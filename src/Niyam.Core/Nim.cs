using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Niyam;

/// <summary>NVIDIA NIM settings. The key is read from the environment only; it is never written to any file.</summary>
public sealed record NimSettings(
    string ApiKey,
    string ChatModel = NimSettings.DefaultChat,
    string BaseUrl = "https://integrate.api.nvidia.com/v1",
    string EmbedModel = "nvidia/llama-3.2-nv-embedqa-1b-v2",
    string RerankModel = "nvidia/llama-3.2-nv-rerankqa-1b-v2",
    string RerankUrl = "https://ai.api.nvidia.com/v1/retrieval/nvidia/llama-3_2-nv-rerankqa-1b-v2/reranking")
{
    public const string DefaultChat = "meta/llama-3.3-70b-instruct";

    public static NimSettings? FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        string E(string name, string def) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : def;
        var d = new NimSettings(key);
        return d with
        {
            ChatModel = E("NIM_MODEL", d.ChatModel), BaseUrl = E("NIM_BASE_URL", d.BaseUrl).TrimEnd('/'),
            EmbedModel = E("NIM_EMBED_MODEL", d.EmbedModel), RerankModel = E("NIM_RERANK_MODEL", d.RerankModel),
            RerankUrl = E("NIM_RERANK_URL", d.RerankUrl),
        };
    }
}

/// <summary>Plain HttpClient calls to NVIDIA's chat, embedding (NeMo Retriever) and reranking (NeMo Retriever) APIs.</summary>
public sealed class NimClient
{
    readonly NimSettings _s;
    readonly HttpClient _http;
    public NimSettings Settings => _s;

    public NimClient(NimSettings s, HttpClient? http = null)
    {
        _s = s;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    async Task<JsonNode> PostAsync(string url, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _s.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"NVIDIA API {(int)resp.StatusCode} at {new Uri(url).AbsolutePath}: {Trim(text, 300)}");
        return JsonNode.Parse(text)!;
    }

    /// <summary>Embeddings for passages (indexing) or a query (search). NeMo Retriever models need input_type.</summary>
    public async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct = default)
    {
        var all = new List<float[]>();
        const int batch = 32;
        for (int i = 0; i < texts.Count; i += batch)
        {
            var part = texts.Skip(i).Take(batch).ToList();
            var body = new JsonObject
            {
                ["model"] = _s.EmbedModel,
                ["input"] = new JsonArray(part.Select(t => (JsonNode)t).ToArray()),
                ["input_type"] = query ? "query" : "passage",
                ["encoding_format"] = "float",
                ["truncate"] = "END",
            };
            var r = await PostAsync(_s.BaseUrl + "/embeddings", body, ct);
            var data = r["data"]!.AsArray().OrderBy(d => d!["index"]?.GetValue<int>() ?? 0);
            all.AddRange(data.Select(d => d!["embedding"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray()));
        }
        return all;
    }

    /// <summary>Indices of passages, most relevant first, from the NeMo Retriever reranker.</summary>
    public async Task<List<(int Index, double Logit)>> RerankAsync(string query, IReadOnlyList<string> passages, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _s.RerankModel,
            ["query"] = new JsonObject { ["text"] = query },
            ["passages"] = new JsonArray(passages.Select(p => (JsonNode)new JsonObject { ["text"] = p }).ToArray()),
            ["truncate"] = "END",
        };
        var r = await PostAsync(_s.RerankUrl, body, ct);
        return r["rankings"]!.AsArray().Select(x => (x!["index"]!.GetValue<int>(), x["logit"]!.GetValue<double>())).ToList();
    }

    /// <summary>One chat completion. Reasoning models' &lt;think&gt; blocks are removed from the answer.</summary>
    public async Task<string> ChatAsync(string system, string user, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _s.ChatModel,
            ["temperature"] = 0.1,
            ["max_tokens"] = 1200,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        var r = await PostAsync(_s.BaseUrl + "/chat/completions", body, ct);
        var content = r["choices"]![0]!["message"]!["content"]?.ToString() ?? "";
        return Regex.Replace(content, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
    }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
