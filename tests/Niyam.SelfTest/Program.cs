// Self-test with no test framework. Uses FICTIONAL documents (marked as such) and a local mock of the NVIDIA APIs,
// so it runs offline and in CI with no key.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Niyam;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

int passed = 0, failed = 0;
void Check(bool ok, string what) { if (ok) passed++; else failed++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); }

var dir = Path.Combine(Path.GetTempPath(), "niyam_selftest_" + Environment.ProcessId);
if (Directory.Exists(dir)) Directory.Delete(dir, true);
Directory.CreateDirectory(dir);

// --- fixtures: fictional documents ---------------------------------------------------------------------------
var pdf = new PdfDocumentBuilder();
var font = pdf.AddStandard14Font(Standard14Font.Helvetica);
var p1 = pdf.AddPage(PageSize.A4);
string[] page1 =
[
    "FICTIONAL TEST DOCUMENT - NOT A REAL CIRCULAR",
    "Test Circular No. 7/2099-XYZ",
    "Section 12. Late fee for delayed returns",
    "A registered person who files the monthly return after the due date",
    "shall pay a late fee of Rs. 50 for each day of delay, subject to",
    "a maximum of Rs. 5,000 per return.",
];
for (int i = 0; i < page1.Length; i++) p1.AddText(page1[i], 11, new PdfPoint(40, 780 - i * 18), font);
var p2 = pdf.AddPage(PageSize.A4);
string[] page2 =
[
    "Section 13. Interest on delayed payment",
    "Interest at the rate of 18% per annum is payable on tax paid late,",
    "counted from the day after the due date.",
];
for (int i = 0; i < page2.Length; i++) p2.AddText(page2[i], 11, new PdfPoint(40, 780 - i * 18), font);
pdf.AddPage(PageSize.A4);   // a blank page, like a scanned page without text
File.WriteAllBytes(Path.Combine(dir, "test_circular.pdf"), pdf.Build());

File.WriteAllText(Path.Combine(dir, "test_notice.html"),
    "<html><head><title>x</title><style>body{}</style></head><body><h1>FICTIONAL TEST NOTICE</h1>" +
    "<p>Rule 4. Startup recognition: an entity is treated as a startup for up to 10 years from incorporation &amp; " +
    "if its turnover has not exceeded Rs. 100 crore in any year.</p><script>alert(1)</script></body></html>");
File.WriteAllText(Path.Combine(dir, "test_hindi.txt"),
    "काल्पनिक परीक्षण दस्तावेज़\n\nनियम 9. विलंब शुल्क: मासिक रिटर्न देर से भरने पर प्रतिदिन 50 रुपये का विलंब शुल्क देय होगा।", Encoding.UTF8);
File.WriteAllText(Path.Combine(dir, "sources.csv"),
    "file,title,issuer,number,date,url,status\n" +
    "test_circular.pdf,\"Test Circular, fictional\",Test Board,7/2099-XYZ,01 Jan 2099,https://example.invalid/c7.pdf,in force\n" +
    "test_notice.html,Test Notice,Test Department,N-4,02 Feb 2099,https://example.invalid/n4.html,superseded\n");

// --- ingest ---------------------------------------------------------------------------------------------------
Console.WriteLine("Ingest");
var lib = Library.Load(dir);
var log = new List<string>();
var notes = await lib.UpdateAsync(null, log.Add, maxChars: 400);
Check(lib.Docs.Count == 3, "three documents indexed (PDF, HTML, TXT)");
var pdfDoc = lib.Docs.First(d => d.File == "test_circular.pdf");
Check(pdfDoc.Pages == 3 && pdfDoc.EmptyPages == 1 && pdfDoc.Warning.Contains("1 of 3"), "PDF: 3 pages read, the blank page reported");
Check(lib.Chunks.Any(c => c.File == "test_circular.pdf" && c.Page == 2 && c.Text.Contains("18% per annum")), "PDF text extracted on the right page");
Check(lib.Chunks.Any(c => c.Heading.StartsWith("Section 13")), "section headings remembered for citations");
var html = lib.Chunks.First(c => c.File == "test_notice.html").Text;
Check(html.Contains("10 years from incorporation & if") && !html.Contains("alert") && !html.Contains("body{}"), "HTML: entities decoded, scripts and styles dropped");
Check(lib.Source("test_circular.pdf").Title == "Test Circular, fictional", "sources.csv: quoted comma read correctly");
Check(lib.Source("test_notice.html").Cite(1).Contains("status: superseded"), "superseded status shown in citations");
Check(notes.Any(n => n.Contains("test_hindi.txt") && n.Contains("no row in sources.csv")), "warns when a document has no source details");
Check(File.Exists(Path.Combine(dir, Library.IndexFile)) && !File.ReadAllText(Path.Combine(dir, Library.IndexFile)).Contains(dir), "index saved, without local folder paths");

log.Clear();
lib = Library.Load(dir);
await lib.UpdateAsync(null, log.Add, maxChars: 400);
Check(log.Count == 0, "second ingest skips unchanged documents");
File.Delete(Path.Combine(dir, "test_notice.html"));
await lib.UpdateAsync(null, log.Add, maxChars: 400);
Check(lib.Docs.Count == 2 && !lib.Chunks.Any(c => c.File == "test_notice.html"), "removed documents leave the index");
File.WriteAllText(Path.Combine(dir, "test_notice.html"),
    "<p>FICTIONAL TEST NOTICE. Rule 4. Startup recognition: an entity is treated as a startup for up to 10 years from incorporation.</p>");
await lib.UpdateAsync(null, log.Add, maxChars: 400);

// --- keyword search -------------------------------------------------------------------------------------------
Console.WriteLine("Keyword search (offline)");
var (h1, _) = await lib.SearchAsync("late fee per day for a delayed return", 3, null);
Check(h1.Count > 0 && h1[0].Chunk.File == "test_circular.pdf" && h1[0].Chunk.Page == 1, "English question finds the late-fee section");
var (h2, _) = await lib.SearchAsync("विलंब शुल्क कितना है", 3, null);
Check(h2.Count > 0 && h2[0].Chunk.File == "test_hindi.txt", "Hindi question finds the Hindi document");
var (h3, _) = await lib.SearchAsync("interest rate delayed payment", 3, null);
Check(h3[0].Chunk.Text.Contains("18%"), "interest question finds the interest section");
var off = await new Answerer(lib, null).AskAsync("What is the late fee?", 3);
Check(off.Mode == "offline" && off.Text.Contains("Rs. 50"), "offline mode shows the passages instead of writing an answer");

// --- the number check -----------------------------------------------------------------------------------------
Console.WriteLine("Number check");
var hitsForCheck = h1;
var good = Answerer.Check("The late fee is Rs. 50 per day [1]. It is capped at Rs. 5,000 per return [1].", hitsForCheck);
Check(good.Ok && good.CitedSentences == 2, "correct answer passes");
var wrong = Answerer.Check("The late fee is Rs. 100 per day [1].", hitsForCheck);
Check(!wrong.Ok && wrong.UnsupportedFacts.Any(f => f.Contains("100")), "a wrong amount is caught");
var badRef = Answerer.Check("The late fee is Rs. 50 per day [9].", hitsForCheck);
Check(badRef.BadReferences.Contains(9), "a citation to a passage that does not exist is caught");
var uncited = Answerer.Check("The cap is Rs. 5,000.", hitsForCheck);
Check(uncited.UncitedFacts.Count == 1, "a number stated without a citation is caught");
var spread = Answerer.Check("The late fee is Rs. 50 per day,\nup to Rs. 5000 [1].", hitsForCheck);
Check(spread.Ok, "numbers match with or without thousands separators");

// --- NVIDIA path, against a local mock ------------------------------------------------------------------------
Console.WriteLine("NVIDIA NeMo Retriever + NIM path (local mock)");
int port = Random.Shared.Next(42000, 52000);
var calls = new List<(string Path, JsonObject Body)>();
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
string chatReply = "<think>reasoning</think>The late fee is Rs. 50 for each day of delay [1], up to Rs. 5,000 per return [1].";
_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); } catch { return; }
        var body = (JsonObject)JsonNode.Parse(await new StreamReader(ctx.Request.InputStream).ReadToEndAsync())!;
        var path = ctx.Request.Url!.AbsolutePath;
        lock (calls) calls.Add((path, body));
        JsonObject resp;
        if (path.EndsWith("/embeddings"))
        {
            var inputs = body["input"]!.AsArray().Select(x => x!.ToString()).ToList();
            resp = new JsonObject { ["data"] = new JsonArray(inputs.Select((t, i) => (JsonNode)new JsonObject
                { ["index"] = i, ["embedding"] = new JsonArray(Embed(t).Select(f => (JsonNode)f).ToArray()) }).ToArray()) };
        }
        else if (path.EndsWith("/rerank"))
        {
            var q = Text.Tokens(body["query"]!["text"]!.ToString()).ToHashSet();
            var ps = body["passages"]!.AsArray().Select(x => x!["text"]!.ToString()).ToList();
            resp = new JsonObject { ["rankings"] = new JsonArray(ps.Select((t, i) => (i, Text.Tokens(t).Count(q.Contains)))
                .OrderByDescending(x => x.Item2).Select(x => (JsonNode)new JsonObject { ["index"] = x.i, ["logit"] = (double)x.Item2 }).ToArray()) };
        }
        else
        {
            var user = body["messages"]![1]!["content"]!.ToString();
            var content = user.Contains("Question: What is the capital") ? "NOT_FOUND" : chatReply;
            resp = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) };
        }
        var bytes = Encoding.UTF8.GetBytes(resp.ToJsonString());
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
});

var mock = new NimClient(new NimSettings("test-key", BaseUrl: $"http://127.0.0.1:{port}/v1", RerankUrl: $"http://127.0.0.1:{port}/rerank"));
await lib.UpdateAsync(mock, _ => { }, maxChars: 400);
Check(lib.HasVectors && lib.EmbedModel == "nvidia/llama-3.2-nv-embedqa-1b-v2", "ingest stores NeMo Retriever embeddings for every passage");
Check(calls.Any(c => c.Path.EndsWith("/embeddings") && c.Body["input_type"]!.ToString() == "passage"), "passages embedded with input_type=passage");
calls.Clear();
var ans = await new Answerer(lib, mock).AskAsync("How much is the late fee for a delayed return?", 4);
Check(calls.Any(c => c.Path.EndsWith("/embeddings") && c.Body["input_type"]!.ToString() == "query"), "question embedded with input_type=query");
Check(calls.Any(c => c.Path.EndsWith("/rerank")), "candidates reranked by the NeMo Retriever reranker");
Check(ans.Sources[0].How.Contains("embeddings") && ans.Sources[0].How.Contains("reranking"), "search reports hybrid + rerank");
Check(!ans.Text.Contains("<think>") && ans.Text.StartsWith("The late fee"), "reasoning text removed from the answer");
Check(ans.Check.Ok, "AI answer passes the number check");
var prompt = calls.First(c => c.Path.EndsWith("/chat/completions")).Body["messages"]![1]!["content"]!.ToString();
Check(prompt.Contains("[1] Source: Test Board Test Circular, fictional, 7/2099-XYZ, 01 Jan 2099, p. 1"), "prompt gives the model numbered passages with full source lines");
chatReply = "The late fee is Rs. 500 per day [1].";
var wrongAns = await new Answerer(lib, mock).AskAsync("How much is the late fee for a delayed return?", 4);
var rendered = Format.Answer(lib, wrongAns);
Check(!wrongAns.Check.Ok && rendered.Contains("CHECK WARNING") && rendered.Contains("500"), "a wrong AI number is flagged to the user");
var nf = await new Answerer(lib, mock).AskAsync("What is the capital of France?", 4);
Check(nf.Mode == "not found" && nf.Text.Contains("does not answer"), "questions the library cannot answer say so");
var bad = new NimClient(new NimSettings("k", BaseUrl: "http://127.0.0.1:1/v1", RerankUrl: "http://127.0.0.1:1/rerank"));
var fb = await new Answerer(lib, bad).AskAsync("late fee", 3);
Check(fb.Mode == "offline" && fb.Notes.Any(n => n.Contains("unavailable")), "NVIDIA unreachable: falls back to passages and says why");

// --- MCP ------------------------------------------------------------------------------------------------------
Console.WriteLine("MCP server");
var mcp = new McpServer(() => Library.Load(dir), null, new StringWriter());
async Task<JsonObject> Rpc(string l) => (JsonObject)JsonNode.Parse((await mcp.HandleAsync(l))!)!;
var init = await Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
Check(init["result"]!["serverInfo"]!["name"]!.ToString() == "niyam", "initialize");
var tools = (JsonArray)(await Rpc("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"))["result"]!["tools"]!;
Check(tools.Count == 4 && tools.All(t => t!["annotations"]!["readOnlyHint"]!.GetValue<bool>()), "4 read-only tools");
var sr = await Rpc("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"search_library\",\"arguments\":{\"query\":\"interest delayed payment\"}}}");
Check(sr["result"]!["content"]![0]!["text"]!.ToString().Contains("18% per annum"), "search_library returns the passage with its source");
Check(await mcp.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") is null, "notifications get no reply");

listener.Stop();
Directory.Delete(dir, true);
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static float[] Embed(string t)
{
    var v = new float[64];
    foreach (var tok in Text.Tokens(t)) v[(tok.GetHashCode() & 0x7fffffff) % 64] += 1;
    return v;
}
