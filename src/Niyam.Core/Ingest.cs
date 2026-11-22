using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Niyam;

/// <summary>Reads documents (PDF, HTML, TXT, MD) into pages of plain text, and cuts pages into citable passages.</summary>
public static class Ingest
{
    public static readonly string[] Extensions = [".pdf", ".html", ".htm", ".txt", ".md"];

    /// <summary>Text per page (page numbers start at 1). HTML and text files are one page.</summary>
    public static List<string> ReadPages(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => ReadPdf(path),
            ".html" or ".htm" => [Text.Clean(HtmlToText(File.ReadAllText(path, Encoding.UTF8)))],
            _ => [Text.Clean(File.ReadAllText(path, Encoding.UTF8))],
        };
    }

    static List<string> ReadPdf(string path)
    {
        var pages = new List<string>();
        using var doc = PdfDocument.Open(path);
        foreach (var page in doc.GetPages())
        {
            // Rebuild lines from word positions: PDF content order is not always reading order.
            var words = page.GetWords().OrderByDescending(w => Math.Round(w.BoundingBox.Bottom, 0)).ThenBy(w => w.BoundingBox.Left).ToList();
            var sb = new StringBuilder();
            double? lineY = null;
            foreach (var w in words)
            {
                var y = w.BoundingBox.Bottom;
                var tol = Math.Max(2.0, w.BoundingBox.Height * 0.5);
                if (lineY is null) lineY = y;
                else if (Math.Abs(y - lineY.Value) > tol) { sb.Append('\n'); lineY = y; }
                else sb.Append(' ');
                sb.Append(w.Text);
            }
            pages.Add(Text.Clean(sb.ToString()));
        }
        return pages;
    }

    public static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"<(script|style|noscript|head)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(br|/p|/div|/li|/tr|/h[1-6]|/table)[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        return WebUtility.HtmlDecode(html);
    }

    static readonly Regex HeadingRx = new(
        @"^\s*((section|rule|chapter|article|schedule|para(graph)?|clause|notification|circular)\b.{0,90}|\d{1,3}[A-Z]{0,2}\.\s+\p{Lu}.{0,90}|(धारा|नियम|अध्याय|कलम)\s.{0,90})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Cuts a page into passages of about maxChars, on paragraph or sentence boundaries, remembering the last heading.</summary>
    public static IEnumerable<Chunk> Chunk(string file, int page, string text, string headingSoFar, int maxChars, Action<string> heading)
    {
        var paras = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .SelectMany(p => p.Length <= maxChars ? [p] : SplitLong(p, maxChars)).ToList();
        var current = new StringBuilder();
        var h = headingSoFar; var chunkHeading = h; int n = 0;
        foreach (var p in paras)
        {
            var before = h;
            foreach (var line in p.Split('\n'))
                if (HeadingRx.IsMatch(line) && line.Length < 120) { h = line.Trim(); heading(h); }
            if (current.Length > 0 && current.Length + p.Length + 2 > maxChars)
            {
                yield return Make(); current.Clear();
            }
            // A passage is labelled with the heading in force where it starts: the first heading inside it if it opens one.
            if (current.Length == 0) chunkHeading = FirstHeading(p) ?? before;
            if (current.Length > 0) current.Append("\n\n");
            current.Append(p);
        }
        if (current.ToString().Trim().Length > 0) yield return Make();

        Chunk Make() => new() { Id = $"{file}#p{page}.{++n}", File = file, Page = page, Heading = chunkHeading, Text = current.ToString().Trim() };
    }

    static string? FirstHeading(string p) =>
        p.Split('\n').Take(3).FirstOrDefault(l => HeadingRx.IsMatch(l) && l.Length < 120)?.Trim();

    static IEnumerable<string> SplitLong(string p, int max)
    {
        var sentences = Regex.Split(p, @"(?<=[\.\?!।])\s+");
        var sb = new StringBuilder();
        foreach (var s in sentences)
        {
            if (sb.Length > 0 && sb.Length + s.Length + 1 > max) { yield return sb.ToString(); sb.Clear(); }
            if (s.Length > max) { for (int i = 0; i < s.Length; i += max) yield return s.Substring(i, Math.Min(max, s.Length - i)); continue; }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    public static string Hash(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f))[..16];
    }
}
