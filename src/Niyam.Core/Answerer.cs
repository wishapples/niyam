using System.Text;
using System.Text.RegularExpressions;

namespace Niyam;

public sealed record Answer(string Text, List<Hit> Sources, Grounding Check, List<string> Notes, string Mode);

/// <summary>What the automatic check found: every number in the answer must appear in a passage the answer cites.</summary>
public sealed record Grounding(int Sentences, int CitedSentences, List<int> BadReferences, List<string> UnsupportedFacts, List<string> UncitedFacts)
{
    public bool Ok => BadReferences.Count == 0 && UnsupportedFacts.Count == 0 && UncitedFacts.Count == 0;

    public string Summary()
    {
        if (Sentences == 0) return "Check: nothing to verify.";
        if (Ok) return $"Check passed: {CitedSentences} of {Sentences} sentence(s) cite a source, and every number in the answer appears in the passage it cites.";
        var sb = new StringBuilder("CHECK WARNING — verify before relying on this answer:");
        if (BadReferences.Count > 0) sb.Append($"\n  cites passage(s) that do not exist: {string.Join(", ", BadReferences.Select(b => $"[{b}]"))}");
        foreach (var f in UnsupportedFacts) sb.Append($"\n  not found in the cited passage: {f}");
        foreach (var f in UncitedFacts) sb.Append($"\n  stated without a citation: {f}");
        return sb.ToString();
    }
}

/// <summary>Answers a question from the library only, with numbered citations, then checks the answer against the passages.</summary>
public sealed class Answerer
{
    public const string Disclaimer = "This is not legal or tax advice. Check the cited source before acting.";
    const string NotFound = "NOT_FOUND";

    static readonly string System =
        """
        You are a research assistant for chartered accountants and company secretaries in India.
        You answer ONLY from the numbered passages provided. They come from the firm's own library of official documents.
        Rules:
        1. Every sentence that states a fact must end with the passage number(s) it comes from, like [2] or [1][3].
        2. Copy numbers, rates, dates, section numbers and amounts exactly as they appear in the passages. Never compute or guess them.
        3. If the passages do not contain the answer, reply with exactly NOT_FOUND and nothing else.
        4. If passages disagree, say so, cite both, and point out which document is newer by its date.
        5. If a passage is marked superseded or amended, say so.
        6. Reply in the language of the question (English, Hindi or Marathi). Keep the section numbers and citations as they are.
        7. Be short: at most 8 sentences. Do not add advice beyond what the passages say.
        """;

    readonly Library _lib;
    readonly NimClient? _nim;

    public Answerer(Library lib, NimClient? nim) { _lib = lib; _nim = nim; }

    public async Task<Answer> AskAsync(string question, int k = 6, CancellationToken ct = default)
    {
        var (hits, notes) = await _lib.SearchAsync(question, k, _nim, ct);
        if (hits.Count == 0)
            return new Answer("Nothing in your library matches this question. Add the relevant official document and run ingest.",
                              hits, new Grounding(0, 0, [], [], []), notes, "none");

        if (_nim is null)
            return new Answer(Extractive(hits), hits, new Grounding(0, 0, [], [], []), [.. notes, "offline: showing the most relevant passages (set NVIDIA_API_KEY for written answers)"], "offline");

        string reply;
        try { reply = await _nim.ChatAsync(System, Prompt(question, hits), ct); }
        catch (Exception e)
        {
            return new Answer(Extractive(hits), hits, new Grounding(0, 0, [], [], []), [.. notes, "AI answer unavailable, showing passages: " + e.Message], "offline");
        }

        if (reply.Trim().StartsWith(NotFound, StringComparison.Ordinal))
            return new Answer("Your library does not answer this. The closest passages are listed below; add the relevant circular, notification or section and ask again.",
                              hits, new Grounding(0, 0, [], [], []), notes, "not found");

        var check = Check(reply, hits);
        return new Answer(reply, hits, check, notes, _nim.Settings.ChatModel);
    }

    public string Prompt(string question, List<Hit> hits)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < hits.Count; i++)
        {
            var c = hits[i].Chunk;
            sb.AppendLine($"[{i + 1}] Source: {_lib.Source(c.File).Cite(c.Page)}");
            if (c.Heading.Length > 0) sb.AppendLine($"    Heading: {c.Heading}");
            sb.AppendLine(c.Text);
            sb.AppendLine();
        }
        sb.AppendLine($"Question: {question}");
        return sb.ToString();
    }

    string Extractive(List<Hit> hits)
    {
        var sb = new StringBuilder("Most relevant passages in your library:\n");
        for (int i = 0; i < hits.Count; i++)
        {
            var c = hits[i].Chunk;
            var t = c.Text.Replace('\n', ' ');
            sb.AppendLine($"\n[{i + 1}] {(t.Length > 420 ? t[..420] + "…" : t)}");
        }
        return sb.ToString().TrimEnd();
    }

    static readonly Regex CiteRx = new(@"\[(\d{1,2}(?:\s*[,;]\s*\d{1,2})*)\]", RegexOptions.Compiled);
    static readonly Regex FactRx = new(@"(?:₹|Rs\.?|INR)?\s?\d[\d,]*(?:\.\d+)?(?:\s?%|\([0-9a-zA-Z]{1,4}\))*", RegexOptions.Compiled);

    /// <summary>
    /// Checks a written answer: citations must point to retrieved passages, and every number (rate, amount, date,
    /// section) must appear in a passage the sentence cites. Catches the commonest failure of AI answers: a wrong number.
    /// </summary>
    public static Grounding Check(string answer, List<Hit> hits)
    {
        var bad = new SortedSet<int>(); var unsupported = new List<string>(); var uncited = new List<string>();
        int sentences = 0, cited = 0;
        var sentenceList = Regex.Split(answer, @"(?<=[\.\?!।])\s+(?=\S)|\n+")
                                .Select(s => s.Trim()).Where(s => s.Length > 2 && !s.Contains("not legal", StringComparison.OrdinalIgnoreCase));
        var pending = new List<string>();   // facts in sentences whose citation comes at the end of the paragraph
        foreach (var s in sentenceList)
        {
            sentences++;
            var refs = CiteRx.Matches(s).SelectMany(m => m.Groups[1].Value.Split([',', ';']).Select(x => int.Parse(x.Trim()))).Distinct().ToList();
            var body = CiteRx.Replace(s, " ");
            var facts = FactRx.Matches(body).Select(m => m.Value.Trim()).Where(f => f.Any(char.IsDigit) && Norm(f).Length > 0).ToList();
            if (refs.Count == 0) { pending.AddRange(facts); continue; }
            cited++;
            foreach (var r in refs.Where(r => r < 1 || r > hits.Count)) bad.Add(r);
            var pool = Norm(string.Join(" ", refs.Where(r => r >= 1 && r <= hits.Count).Select(r => hits[r - 1].Chunk.Text)));
            foreach (var f in pending.Concat(facts))
                if (!Contains(pool, Norm(f))) unsupported.Add($"\"{f}\" (cites {string.Join("", refs.Select(r => $"[{r}]"))})");
            pending.Clear();
        }
        uncited.AddRange(pending.Distinct().Select(f => $"\"{f}\""));
        return new Grounding(sentences, cited, bad.ToList(), unsupported.Distinct().ToList(), uncited);
    }

    /// <summary>Whole-number match: "500" must not match inside "5000" or "1.500".</summary>
    static bool Contains(string pool, string fact) =>
        Regex.IsMatch(pool, @"(?<![0-9.])" + Regex.Escape(fact) + @"(?![0-9]|\.[0-9])");

    /// <summary>Normalises numbers for comparison: drops currency signs, spaces and thousands separators.</summary>
    static string Norm(string s)
    {
        s = s.Replace("₹", "").Replace("Rs.", "").Replace("Rs", "").Replace("INR", "");
        var sb = new StringBuilder();
        foreach (var ch in s) if (ch != ',' && !char.IsWhiteSpace(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString().TrimEnd('.');
    }
}
