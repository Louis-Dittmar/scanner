using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Scanner.Core.Ocr;

/// <summary>
/// Turns the Markdown/HTML content of recognized regions into plain text and tables for the PDF.
/// </summary>
public static partial class OcrText
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    [GeneratedRegex(@"<\s*(br|/tr|/p|/li|/h\d|/div)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Multiline)]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^\s*(#{1,6}|>)\s*", RegexOptions.Multiline)]
    private static partial Regex LinePrefixRegex();

    [GeneratedRegex(@"(\*\*|__|`)")]
    private static partial Regex EmphasisRegex();

    [GeneratedRegex(@"\\[\(\)\[\]]|\$\$?")]
    private static partial Regex MathDelimiterRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlRowRegex();

    [GeneratedRegex(@"<t[dh][^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlCellRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImageRegex();


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Removes Markdown/HTML markup, so only the words remain. Line breaks are kept.
    /// </summary>
    public static string ToPlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        text = text.Replace("\r\n", "\n");
        text = MarkdownImageRegex().Replace(text, "");
        text = LineBreakTagRegex().Replace(text, "\n");
        text = Regex.Replace(text, @"</t[dh]\s*>", "  ", RegexOptions.IgnoreCase);
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = TableSeparatorRegex().Replace(text, "");
        text = LinePrefixRegex().Replace(text, "");
        text = EmphasisRegex().Replace(text, "");
        text = MathDelimiterRegex().Replace(text, "");
        text = string.Join("\n", text.Split('\n').Select(line => line.Trim().Trim('|').Replace("|", "  ").Trim()));
        text = SpacesRegex().Replace(text, " ");
        return Regex.Replace(text, @"\n{2,}", "\n").Trim();
    }

    /// <summary>
    /// The non-empty lines of <see cref="ToPlainText"/>.
    /// </summary>
    public static string[] ToLines(string? text) =>
        ToPlainText(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Reads an HTML or Markdown table into rows of cell texts.
    /// </summary>
    /// <returns>The rows, or <see langword="null"/> if <paramref name="text"/> doesn't contain a table.</returns>
    public static List<List<string>>? ParseTable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        List<List<string>> rows = [];
        MatchCollection htmlRows = HtmlRowRegex().Matches(text);
        if (htmlRows.Count > 0)
        {
            foreach (Match row in htmlRows)
            {
                List<string> cells = HtmlCellRegex().Matches(row.Groups["row"].Value)
                    .Select(c => CellText(c.Groups["cell"].Value))
                    .ToList();
                if (cells.Count > 0)
                    rows.Add(cells);
            }
        }
        else
        {
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith('|') || TableSeparatorRegex().IsMatch(trimmed))
                    continue;
                rows.Add(trimmed.Trim('|').Split('|').Select(CellText).ToList());
            }
        }

        return rows.Count > 0 ? rows : null;
    }

    private static string CellText(string cell)
    {
        cell = LineBreakTagRegex().Replace(cell, " ");
        cell = TagRegex().Replace(cell, "");
        cell = WebUtility.HtmlDecode(cell);
        cell = EmphasisRegex().Replace(cell, "");
        return SpacesRegex().Replace(cell, " ").Trim();
    }
}
