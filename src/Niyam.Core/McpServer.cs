using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Niyam;

/// <summary>MCP server over stdio (newline-delimited JSON-RPC 2.0), so Claude, VS Code, Cursor and other AI apps can search the library.</summary>
public sealed class McpServer
{
    public static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    readonly Func<Library> _lib;
    readonly NimClient? _nim;
    readonly TextWriter _log;

    const string Instructions =
        "Niyam searches the user's own library of official Indian compliance documents (GST, company law, notifications, " +
        "circulars). Answer only from returned passages and cite them with their source line. If nothing relevant is returned, " +
        "say the library does not cover it. Never present this as legal advice.";

    public McpServer(Func<Library> lib, NimClient? nim, TextWriter log) { _lib = lib; _nim = nim; _log = log; }

    static JsonObject Tool(string name, string description, JsonObject props, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object", ["properties"] = props,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false, ["openWorldHint"] = false },
    };

    static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };

    public static JsonArray Tools() =>
    [
        Tool("search_library", "Find the passages in the library most relevant to a question. Returns numbered passages with their source (issuer, number, date, page, URL).",
             new JsonObject { ["query"] = Str("The question or keywords, in English, Hindi or Marathi."), ["k"] = new JsonObject { ["type"] = "number", ["description"] = "How many passages (default 6, max 15)." } }, "query"),
        Tool("ask", "Answer a question from the library with citations, plus an automatic check that every number in the answer appears in the cited passage.",
             new JsonObject { ["question"] = Str("The question.") }, "question"),
        Tool("list_sources", "List the documents in the library with their source details and any reading problems.", new JsonObject()),
        Tool("read_passage", "Full text of one passage by its id (from search results).", new JsonObject { ["id"] = Str("Passage id, e.g. 'cgst_act.pdf#p12.2'.") }, "id"),
    ];

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && await input.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var reply = await HandleAsync(line, ct);
            if (reply is null) continue;
            await output.WriteLineAsync(reply);
            await output.FlushAsync(ct);
        }
    }

    public async Task<string?> HandleAsync(string line, CancellationToken ct = default)
    {
        JsonObject msg;
        try { msg = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException e) { return Error(null, -32700, "Parse error: " + e.Message); }
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>();
        if (method is null) return id is null ? null : Error(id, -32600, "Invalid request");
        if (id is null) { _log.WriteLine($"[mcp] notification {method}"); return null; }
        var p = msg["params"] as JsonObject ?? new JsonObject();
        try
        {
            switch (method)
            {
                case "initialize":
                    var asked = p["protocolVersion"]?.GetValue<string>();
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = asked is not null && SupportedVersions.Contains(asked) ? asked : SupportedVersions[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "niyam", ["title"] = "Niyam compliance library", ["version"] = "0.1.0" },
                        ["instructions"] = Instructions,
                    });
                case "ping": return Result(id, new JsonObject());
                case "tools/list": return Result(id, new JsonObject { ["tools"] = Tools() });
                case "resources/list": return Result(id, new JsonObject { ["resources"] = new JsonArray() });
                case "prompts/list": return Result(id, new JsonObject { ["prompts"] = new JsonArray() });
                case "tools/call":
                    var name = p["name"]?.GetValue<string>() ?? "";
                    var args = p["arguments"] as JsonObject ?? new JsonObject();
                    _log.WriteLine($"[mcp] call {name}");
                    var (text, isError) = await CallAsync(name, args, ct);
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                        ["isError"] = isError,
                    });
                default: return Error(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception e) { _log.WriteLine($"[mcp] {method} failed: {e}"); return Error(id, -32603, "Internal error: " + e.Message); }
    }

    async Task<(string, bool)> CallAsync(string name, JsonObject a, CancellationToken ct)
    {
        var lib = _lib();
        string S(string k) => a[k]?.ToString() ?? "";
        switch (name)
        {
            case "search_library":
                var k = int.TryParse(S("k"), out var kk) ? Math.Clamp(kk, 1, 15) : 6;
                var (hits, notes) = await lib.SearchAsync(S("query"), k, _nim, ct);
                return (Format.Passages(lib, hits, notes), false);
            case "ask":
                var ans = await new Answerer(lib, _nim).AskAsync(S("question"), 6, ct);
                return (Format.Answer(lib, ans), false);
            case "list_sources":
                return (Format.Sources(lib), false);
            case "read_passage":
                var c = lib.Chunks.FirstOrDefault(x => x.Id == S("id"));
                return c is null ? ($"No passage with id '{S("id")}'.", true) : ($"{lib.Source(c.File).Cite(c.Page)}\n{lib.Source(c.File).Url}\n\n{c.Text}", false);
            default:
                return ($"Unknown tool '{name}'.", true);
        }
    }

    static string Result(JsonNode id, JsonNode result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();
    static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}

/// <summary>Plain-text rendering shared by the CLI and the MCP server.</summary>
public static class Format
{
    public static string Passages(Library lib, List<Hit> hits, List<string> notes)
    {
        var sb = new StringBuilder();
        if (hits.Count == 0) sb.AppendLine("No matching passages.");
        for (int i = 0; i < hits.Count; i++)
        {
            var c = hits[i].Chunk; var s = lib.Source(c.File);
            sb.AppendLine($"[{i + 1}] {s.Cite(c.Page)}  (id {c.Id})");
            if (s.Url.Length > 0) sb.AppendLine($"    {s.Url}");
            if (c.Heading.Length > 0) sb.AppendLine($"    {c.Heading}");
            sb.AppendLine("    " + c.Text.Replace("\n", "\n    "));
            sb.AppendLine();
        }
        if (hits.Count > 0) sb.AppendLine($"Search: {hits[0].How}.");
        foreach (var n in notes) sb.AppendLine("Note: " + n);
        return sb.ToString().TrimEnd();
    }

    public static string Answer(Library lib, Answer a)
    {
        var sb = new StringBuilder(a.Text.Trim());
        sb.AppendLine("\n");
        if (a.Mode is not ("offline" or "none" or "not found")) sb.AppendLine(a.Check.Summary()).AppendLine();
        if (a.Sources.Count > 0)
        {
            sb.AppendLine("Sources:");
            for (int i = 0; i < a.Sources.Count; i++)
            {
                var c = a.Sources[i].Chunk; var s = lib.Source(c.File);
                sb.AppendLine($"  [{i + 1}] {s.Cite(c.Page)}{(s.Url.Length > 0 ? "  " + s.Url : "")}");
            }
        }
        foreach (var n in a.Notes) sb.AppendLine("Note: " + n);
        sb.AppendLine().Append(Answerer.Disclaimer);
        return sb.ToString();
    }

    public static string Sources(Library lib)
    {
        if (lib.Docs.Count == 0) return "The library is empty. Put official documents in the library folder, list them in sources.csv, and run ingest.";
        var sb = new StringBuilder($"{lib.Docs.Count} document(s), {lib.Chunks.Count} passage(s); search: {(lib.HasVectors ? "keyword + NVIDIA embeddings (" + lib.EmbedModel + ")" : "keyword")}; indexed {lib.BuiltUtc:dd MMM yyyy HH:mm} UTC\n");
        foreach (var d in lib.Docs.OrderBy(d => d.File))
        {
            var s = lib.Source(d.File);
            sb.AppendLine($"- {s.Cite()}  [{d.File}, {d.Pages} page(s)]{(d.Warning.Length > 0 ? "  WARNING: " + d.Warning : "")}");
        }
        return sb.ToString().TrimEnd();
    }
}
