using System.Text.Json;
using System.Text.Json.Serialization;

namespace Niyam;

/// <summary>
/// The indexed library: every passage of every document, a BM25 keyword index (works offline, any language), and,
/// when an NVIDIA key is set, NeMo Retriever embeddings for meaning-based search plus reranking.
/// Saved as niyam_index.json next to the documents.
/// </summary>
public sealed class Library
{
    public const string IndexFile = "niyam_index.json";
    public const string SourcesFile = "sources.csv";

    [JsonIgnore] public string Folder { get; private set; } = "";
    public List<DocEntry> Docs { get; set; } = [];
    public List<Chunk> Chunks { get; set; } = [];
    public string EmbedModel { get; set; } = "";
    public DateTime BuiltUtc { get; set; }
    [JsonIgnore] public Dictionary<string, SourceInfo> Sources { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    // BM25 state (rebuilt on load)
    Dictionary<string, Dictionary<int, int>> _postings = new();
    int[] _len = [];
    double _avgLen;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public SourceInfo Source(string file) =>
        Sources.TryGetValue(file, out var s) ? s : new SourceInfo(file, Path.GetFileNameWithoutExtension(file));

    [JsonIgnore] public bool HasVectors => Chunks.Count > 0 && Chunks.All(c => c.Vector is not null);

    public static Library Load(string folder)
    {
        var path = Path.Combine(folder, IndexFile);
        var lib = File.Exists(path) ? JsonSerializer.Deserialize<Library>(File.ReadAllText(path), Json) ?? new Library() : new Library();
        lib.Folder = Path.GetFullPath(folder);
        lib.Sources = SourceInfo.ReadCsv(Path.Combine(folder, SourcesFile));
        lib.BuildKeywordIndex();
        return lib;
    }

    public void Save() => File.WriteAllText(Path.Combine(Folder, IndexFile), JsonSerializer.Serialize(this, Json));

    /// <summary>Indexes new and changed documents, drops removed ones, and embeds passages when a client is given.</summary>
    public async Task<List<string>> UpdateAsync(NimClient? nim, Action<string> log, int maxChars = 1100, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var files = Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories)
            .Where(f => Ingest.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .Select(f => Path.GetRelativePath(Folder, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        var keep = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        int removed = Docs.RemoveAll(d => !keep.Contains(d.File));
        Chunks.RemoveAll(c => !keep.Contains(c.File));
        if (removed > 0) log($"removed {removed} document(s) no longer in the folder");

        foreach (var f in files)
        {
            var full = Path.Combine(Folder, f);
            var hash = Ingest.Hash(full);
            var existing = Docs.FirstOrDefault(d => d.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (existing?.Hash == hash) continue;
            Docs.RemoveAll(d => d.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            Chunks.RemoveAll(c => c.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            var entry = new DocEntry { File = f, Hash = hash };
            try
            {
                var pages = Ingest.ReadPages(full);
                entry.Pages = pages.Count;
                string heading = "";
                for (int i = 0; i < pages.Count; i++)
                {
                    if (pages[i].Length < 25) { entry.EmptyPages++; continue; }
                    Chunks.AddRange(Ingest.Chunk(f, i + 1, pages[i], heading, maxChars, h => heading = h));
                }
                if (entry.Pages > 0 && entry.EmptyPages == entry.Pages)
                    entry.Warning = "no text found: probably a scanned PDF; it needs OCR before it can be searched";
                else if (entry.EmptyPages > 0)
                    entry.Warning = $"{entry.EmptyPages} of {entry.Pages} page(s) have no text (scanned?)";
            }
            catch (Exception e) { entry.Warning = "could not read: " + e.Message; }
            Docs.Add(entry);
            log($"{(existing is null ? "added" : "updated")} {f}: {entry.Pages} page(s){(entry.Warning.Length > 0 ? " — " + entry.Warning : "")}");
            if (entry.Warning.Length > 0) notes.Add($"{f}: {entry.Warning}");
            if (!Sources.ContainsKey(f)) notes.Add($"{f}: no row in {SourcesFile}; citations will show the file name only");
        }

        if (nim is not null)
        {
            if (EmbedModel != nim.Settings.EmbedModel) { foreach (var c in Chunks) c.Vector = null; EmbedModel = nim.Settings.EmbedModel; }
            var todo = Chunks.Where(c => c.Vector is null).ToList();
            if (todo.Count > 0)
            {
                log($"embedding {todo.Count} passage(s) with {nim.Settings.EmbedModel} (NVIDIA NeMo Retriever)…");
                var vecs = await nim.EmbedAsync(todo.Select(EmbedText).ToList(), query: false, ct);
                for (int i = 0; i < todo.Count; i++) todo[i].Vector = Normalise(vecs[i]);
            }
        }
        else if (Chunks.Any(c => c.Vector is null)) { foreach (var c in Chunks) c.Vector = null; EmbedModel = ""; }

        BuiltUtc = DateTime.UtcNow;
        BuildKeywordIndex();
        Save();
        return notes;
    }

    string EmbedText(Chunk c)
    {
        var s = Source(c.File);
        return $"{s.Cite()}\n{c.Heading}\n{c.Text}";
    }

    void BuildKeywordIndex()
    {
        _postings = new();
        _len = new int[Chunks.Count];
        for (int i = 0; i < Chunks.Count; i++)
        {
            var c = Chunks[i];
            var toks = Text.Tokens(Source(c.File).Cite() + " " + c.Heading + " " + c.Text);
            _len[i] = toks.Count;
            foreach (var t in toks)
            {
                if (!_postings.TryGetValue(t, out var p)) _postings[t] = p = new();
                p[i] = p.GetValueOrDefault(i) + 1;
            }
        }
        _avgLen = _len.Length > 0 ? _len.Average() : 1;
    }

    /// <summary>BM25 keyword ranking (k1 = 1.4, b = 0.75).</summary>
    public List<(int Index, double Score)> Keyword(string query, int top)
    {
        var scores = new Dictionary<int, double>();
        int n = Chunks.Count;
        foreach (var t in Text.Tokens(query).Distinct())
        {
            if (!_postings.TryGetValue(t, out var p)) continue;
            double idf = Math.Log(1 + (n - p.Count + 0.5) / (p.Count + 0.5));
            foreach (var (i, tf) in p)
                scores[i] = scores.GetValueOrDefault(i) + idf * tf * 2.4 / (tf + 1.4 * (0.25 + 0.75 * _len[i] / _avgLen));
        }
        return scores.OrderByDescending(kv => kv.Value).Take(top).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public List<(int Index, double Score)> Semantic(float[] q, int top)
    {
        q = Normalise(q);
        return Chunks.Select((c, i) => (i, c.Vector is null ? -1.0 : Dot(q, c.Vector)))
                     .OrderByDescending(x => x.Item2).Take(top).ToList();
    }

    /// <summary>
    /// Hybrid search: keyword and (if available) embedding candidates fused by reciprocal rank, then reranked by
    /// NeMo Retriever when a client is given. Falls back step by step if an NVIDIA call fails, and says so.
    /// </summary>
    public async Task<(List<Hit> Hits, List<string> Notes)> SearchAsync(string query, int k, NimClient? nim, CancellationToken ct = default)
    {
        var notes = new List<string>();
        if (Chunks.Count == 0) return ([], ["the library is empty: add documents and run ingest"]);
        const int pool = 40;
        var lists = new List<List<(int Index, double Score)>> { Keyword(query, pool) };
        string how = "keyword";
        if (nim is not null && HasVectors && EmbedModel == nim.Settings.EmbedModel)
        {
            try
            {
                var qv = (await nim.EmbedAsync([query], query: true, ct))[0];
                lists.Add(Semantic(qv, pool));
                how = "keyword + NeMo Retriever embeddings";
            }
            catch (Exception e) { notes.Add("embedding search unavailable, keyword search only: " + e.Message); }
        }
        else if (nim is not null) notes.Add("index has no embeddings for this model: run ingest with NVIDIA_API_KEY set for meaning-based search");

        var fused = new Dictionary<int, double>();
        foreach (var l in lists)
            for (int r = 0; r < l.Count; r++) fused[l[r].Index] = fused.GetValueOrDefault(l[r].Index) + 1.0 / (60 + r + 1);
        var candidates = fused.OrderByDescending(kv => kv.Value).Take(Math.Max(k * 3, 20)).ToList();
        if (candidates.Count == 0) return ([], notes);

        if (nim is not null)
        {
            try
            {
                var ranked = await nim.RerankAsync(query, candidates.Select(c => EmbedText(Chunks[c.Key])).ToList(), ct);
                return (ranked.Take(k).Select(r => new Hit(Chunks[candidates[r.Index].Key], r.Logit, how + " + NeMo Retriever reranking")).ToList(), notes);
            }
            catch (Exception e) { notes.Add("reranking unavailable: " + e.Message); }
        }
        return (candidates.Take(k).Select(c => new Hit(Chunks[c.Key], c.Value, how)).ToList(), notes);
    }

    static float[] Normalise(float[] v)
    {
        double s = 0; foreach (var x in v) s += x * x;
        var n = (float)Math.Sqrt(s);
        return n == 0 ? v : v.Select(x => x / n).ToArray();
    }

    static double Dot(float[] a, float[] b)
    {
        double s = 0; int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }
}
