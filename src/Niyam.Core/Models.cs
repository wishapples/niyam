using System.Globalization;
using System.Text;

namespace Niyam;

/// <summary>Where a document came from. Filled from the library's sources.csv; the file name is used when a row is missing.</summary>
public sealed record SourceInfo(string File, string Title, string Issuer = "", string Number = "", string Date = "",
                                string Url = "", string Status = "")
{
    /// <summary>One-line citation, e.g. "CBIC Circular No. 199/11/2023-GST, 17 Jul 2023, p. 2".</summary>
    public string Cite(int? page = null)
    {
        var parts = new List<string>();
        var head = string.Join(" ", new[] { Issuer, Title }.Where(s => s.Length > 0));
        parts.Add(head.Length > 0 ? head : File);
        if (Number.Length > 0 && !Title.Contains(Number, StringComparison.OrdinalIgnoreCase)) parts.Add(Number);
        if (Date.Length > 0) parts.Add(Date);
        if (page is > 0) parts.Add($"p. {page}");
        if (Status.Length > 0 && !Status.Equals("in force", StringComparison.OrdinalIgnoreCase)) parts.Add($"status: {Status}");
        return string.Join(", ", parts);
    }

    /// <summary>Reads sources.csv: file,title,issuer,number,date,url,status (header row required; extra columns ignored).</summary>
    public static Dictionary<string, SourceInfo> ReadCsv(string path)
    {
        var map = new Dictionary<string, SourceInfo>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.File.Exists(path)) return map;
        var rows = Csv.Parse(System.IO.File.ReadAllText(path, Encoding.UTF8));
        if (rows.Count == 0) return map;
        var h = rows[0].Select(c => c.Trim().ToLowerInvariant()).ToList();
        string Col(List<string> r, string name) { var i = h.IndexOf(name); return i >= 0 && i < r.Count ? r[i].Trim() : ""; }
        foreach (var r in rows.Skip(1))
        {
            var file = Col(r, "file");
            if (file.Length == 0) continue;
            map[file] = new SourceInfo(file, Col(r, "title"), Col(r, "issuer"), Col(r, "number"), Col(r, "date"), Col(r, "url"), Col(r, "status"));
        }
        return map;
    }
}

/// <summary>A passage of a document: the unit that is searched and cited.</summary>
public sealed class Chunk
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public int Page { get; set; }
    public string Heading { get; set; } = "";
    public string Text { get; set; } = "";
    public float[]? Vector { get; set; }
}

public sealed class DocEntry
{
    public string File { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Pages { get; set; }
    public int EmptyPages { get; set; }
    public string Warning { get; set; } = "";
}

/// <summary>A search hit with the scores that produced it.</summary>
public sealed record Hit(Chunk Chunk, double Score, string How);

public static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (q)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') q = false;
                else cell.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
        if (rows.Count > 0 && rows[0].Count > 0) rows[0][0] = rows[0][0].TrimStart('﻿');
        return rows;
    }

    public static string Escape(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

public static class Text
{
    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the","a","an","of","to","in","and","or","is","are","was","be","by","for","on","as","at","with","from","that","this",
        "it","its","which","what","when","how","do","does","can","i","we","my","our","any","shall","such","under","into",
        "के","की","का","में","है","और","से","को","पर","कि","एक","यह","हैं","भी","या","तो","ही","ला","चे","ची","आहे","व","हे","त्या",
    };

    /// <summary>Lower-cased word tokens; letters, combining marks (Devanagari vowel signs) and digits stay together.</summary>
    public static List<string> Tokens(string s)
    {
        var list = new List<string>(); var sb = new StringBuilder();
        foreach (var ch in s)
        {
            var cat = char.GetUnicodeCategory(ch);
            bool word = char.IsLetterOrDigit(ch) || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            if (word) sb.Append(char.ToLowerInvariant(ch));
            else Flush();
        }
        Flush();
        return list;
        void Flush()
        {
            if (sb.Length == 0) return;
            var t = sb.ToString(); sb.Clear();
            if (!Stop.Contains(t)) list.Add(t);
        }
    }

    public static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var ch in s)
        {
            if (ch == '\n') { sb.Append('\n'); space = false; continue; }
            if (char.IsWhiteSpace(ch)) { if (!space) sb.Append(' '); space = true; continue; }
            sb.Append(ch); space = false;
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @" *\n[ \n]*\n *", "\n\n").Trim();
    }
}
