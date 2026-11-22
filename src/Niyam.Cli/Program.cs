using System.Text;
using Niyam;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var argv = args.ToList();
string? Opt(string name, string? def = null)
{
    var i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return def;
    var v = argv[i + 1]; argv.RemoveRange(i, 2); return v;
}
var library = Path.GetFullPath(Opt("--library", Environment.GetEnvironmentVariable("NIYAM_LIBRARY") ?? "library")!);
var k = int.Parse(Opt("--k", "6")!);
var command = argv.Count > 0 ? argv[0].ToLowerInvariant() : "help";
var rest = string.Join(' ', argv.Skip(1)).Trim();
var settings = NimSettings.FromEnvironment();
var nim = settings is null ? null : new NimClient(settings);

if (command is "help" or "--help" or "-h")
{
    Console.WriteLine($"""
Niyam: answers GST and company-law questions from your own library of official documents, with citations.

  niyam ingest                 index the documents in the library folder (PDF, HTML, TXT, MD)
  niyam ask "question"         answer with numbered citations and an automatic number check
  niyam search "question"      show the most relevant passages only
  niyam chat                   ask several questions
  niyam sources                list the documents and any reading problems
  niyam fetch                  download documents listed in sources.csv (url column) that are not in the folder yet
  niyam mcp                    run as an MCP server for Claude Desktop, VS Code, Cursor

Options: --library <folder> (default ./library, or NIYAM_LIBRARY), --k <passages, default 6>
NVIDIA: set NVIDIA_API_KEY to use NeMo Retriever embeddings and reranking, and a NIM model for written answers
        (NIM_MODEL, default {NimSettings.DefaultChat}). Without a key, search works offline and shows passages.
Library: put official documents in the folder and describe each in sources.csv:
        file,title,issuer,number,date,url,status
""");
    return 0;
}

Directory.CreateDirectory(library);

switch (command)
{
    case "ingest":
    {
        var lib = Library.Load(library);
        Console.WriteLine($"Library: {library}");
        var notes = await lib.UpdateAsync(nim, Console.WriteLine);
        Console.WriteLine($"Indexed {lib.Docs.Count} document(s), {lib.Chunks.Count} passage(s){(lib.HasVectors ? ", with NVIDIA embeddings" : ", keyword search only (set NVIDIA_API_KEY for meaning-based search)")}.");
        foreach (var n in notes) Console.WriteLine("Note: " + n);
        return 0;
    }
    case "search":
    {
        var lib = Library.Load(library);
        var (hits, notes) = await lib.SearchAsync(rest, k, nim);
        Console.WriteLine(Format.Passages(lib, hits, notes));
        return 0;
    }
    case "ask":
    {
        if (rest.Length == 0) { Console.Error.WriteLine("Usage: niyam ask \"your question\""); return 2; }
        var lib = Library.Load(library);
        Console.WriteLine(Format.Answer(lib, await new Answerer(lib, nim).AskAsync(rest, k)));
        return 0;
    }
    case "chat":
    {
        var lib = Library.Load(library);
        var answerer = new Answerer(lib, nim);
        Console.WriteLine($"Niyam · {lib.Docs.Count} document(s) · {(nim is null ? "offline (passages only)" : "AI: " + nim.Settings.ChatModel)} · empty line to quit");
        while (true)
        {
            Console.Write("\nyou › ");
            var q = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(q)) return 0;
            Console.WriteLine("\n" + Format.Answer(lib, await answerer.AskAsync(q, k)));
        }
    }
    case "sources":
        Console.WriteLine(Format.Sources(Library.Load(library)));
        return 0;
    case "fetch":
    {
        var sources = SourceInfo.ReadCsv(Path.Combine(library, Library.SourcesFile));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Niyam/0.1 (+https://github.com/wishapples/niyam)");
        int got = 0;
        foreach (var s in sources.Values.Where(s => s.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            var target = Path.Combine(library, s.File);
            if (File.Exists(target)) continue;
            try
            {
                var bytes = await http.GetByteArrayAsync(s.Url);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes);
                Console.WriteLine($"downloaded {s.File} ({bytes.Length / 1024} KB) from {s.Url}");
                got++;
            }
            catch (Exception e) { Console.WriteLine($"could not download {s.File}: {e.Message}"); }
        }
        Console.WriteLine(got == 0 ? "Nothing to download." : $"Downloaded {got} file(s). Now run: niyam ingest");
        return 0;
    }
    case "mcp":
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        Console.Error.WriteLine($"[mcp] Niyam MCP server, library: {library}");
        await new McpServer(() => Library.Load(library), nim, Console.Error).RunAsync(stdin, stdout);
        return 0;
    }
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Run: niyam help");
        return 2;
}
