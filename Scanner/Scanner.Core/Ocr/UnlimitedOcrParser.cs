using Scanner.Core.Imaging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Scanner.Core.Ocr;

/// <summary>
/// Parses the output of baidu/Unlimited-OCR (and the DeepSeek-OCR family it is based on) into regions and
/// Markdown. Both grounding formats are understood:
/// <list type="bullet">
/// <item>Unlimited-OCR: <c>&lt;|det|&gt;text [x1, y1, x2, y2]&lt;|/det|&gt;content</c>, content may continue on the following lines</item>
/// <item>DeepSeek-OCR: <c>&lt;|ref|&gt;text&lt;|/ref|&gt;&lt;|det|&gt;[[x1, y1, x2, y2]]&lt;|/det|&gt;content</c></item>
/// </list>
/// Coordinates are normalized to 0..1000; if the model reports pixel values instead, they are converted.
/// </summary>
public static partial class UnlimitedOcrParser
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    [GeneratedRegex(@"(?:<\|ref\|>(?<ref>.*?)<\|/ref\|>\s*)?<\|det\|>(?<det>.*?)<\|/det\|>", RegexOptions.Singleline)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    /// <summary>
    /// Special tokens such as &lt;|grounding|&gt; or &lt;｜end▁of▁sentence｜&gt; (with full-width bars).
    /// </summary>
    [GeneratedRegex(@"<[|｜][^<>]{0,40}?[|｜]>")]
    private static partial Regex SpecialTokenRegex();

    [GeneratedRegex(@"^\s*```[a-zA-Z]*\s*$", RegexOptions.Multiline)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlinesRegex();


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Parses the raw model output for a page image of <paramref name="imageWidth"/> x <paramref name="imageHeight"/> pixels.
    /// </summary>
    public static OcrPageResult Parse(string? raw, int imageWidth, int imageHeight)
    {
        raw ??= "";
        string text = raw.Replace("\r\n", "\n");

        List<(string Label, List<double[]> Boxes, string Content)> elements = [];
        string leading = "";

        MatchCollection markers = MarkerRegex().Matches(text);
        if (markers.Count == 0)
        {
            leading = text;
        }
        else
        {
            leading = text[..markers[0].Index];
            for (int i = 0; i < markers.Count; i++)
            {
                Match marker = markers[i];
                int contentStart = marker.Index + marker.Length;
                int contentEnd = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
                string content = text[contentStart..contentEnd];

                string detText = marker.Groups["det"].Value;
                string label = marker.Groups["ref"].Success ? marker.Groups["ref"].Value.Trim() : ExtractLabel(detText);
                elements.Add((label.Length == 0 ? "text" : label, ExtractBoxes(detText), content));
            }
        }

        // pixel coordinates instead of normalized ones?
        double maxCoordinate = elements.SelectMany(e => e.Boxes).SelectMany(b => b).DefaultIfEmpty(0).Max();
        bool isPixels = maxCoordinate > 1000 && imageWidth > 0 && imageHeight > 0;

        List<OcrRegion> regions = [];
        StringBuilder markdown = new();
        AppendBlock(markdown, Clean(leading));

        foreach ((string label, List<double[]> boxes, string content) in elements)
        {
            bool isImage = OcrLabels.GetKind(label) == OcrRegionKind.Image;
            string cleaned = isImage ? "" : Clean(content);
            AppendBlock(markdown, cleaned);

            foreach (double[] box in boxes)
            {
                NormalizedBox normalized = Normalize(box, isPixels, imageWidth, imageHeight);
                if (normalized.IsValid)
                    regions.Add(new OcrRegion(label, normalized, cleaned));
            }
        }

        string result = ManyNewlinesRegex().Replace(markdown.ToString(), "\n\n").Trim();
        return new OcrPageResult(imageWidth, imageHeight, result, regions, raw);
    }

    /// <summary>
    /// The label is the first word inside the det marker of the Unlimited-OCR format ("text [1, 2, 3, 4]").
    /// </summary>
    private static string ExtractLabel(string detText)
    {
        int bracket = detText.IndexOf('[');
        string before = (bracket >= 0 ? detText[..bracket] : detText).Trim();
        int space = before.IndexOfAny([' ', '\t', '\n']);
        return space >= 0 ? before[..space] : before;
    }

    private static List<double[]> ExtractBoxes(string detText)
    {
        int bracket = detText.IndexOf('[');
        List<double[]> boxes = [];
        if (bracket < 0)
            return boxes;

        List<double> numbers = [];
        foreach (Match match in NumberRegex().Matches(detText[bracket..]))
        {
            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                numbers.Add(value);
        }

        for (int i = 0; i + 3 < numbers.Count; i += 4)
            boxes.Add([numbers[i], numbers[i + 1], numbers[i + 2], numbers[i + 3]]);
        return boxes;
    }

    private static NormalizedBox Normalize(double[] box, bool isPixels, int imageWidth, int imageHeight)
    {
        double x1 = box[0], y1 = box[1], x2 = box[2], y2 = box[3];
        if (isPixels)
        {
            x1 = x1 * 1000 / imageWidth;
            x2 = x2 * 1000 / imageWidth;
            y1 = y1 * 1000 / imageHeight;
            y2 = y2 * 1000 / imageHeight;
        }

        static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 1000);
        return new NormalizedBox(Clamp(Math.Min(x1, x2)), Clamp(Math.Min(y1, y2)), Clamp(Math.Max(x1, x2)), Clamp(Math.Max(y1, y2)));
    }

    /// <summary>
    /// Removes special tokens and code fences around the content.
    /// </summary>
    internal static string Clean(string text)
    {
        text = SpecialTokenRegex().Replace(text, "");
        text = CodeFenceRegex().Replace(text, "");
        return text.Trim();
    }

    private static void AppendBlock(StringBuilder builder, string block)
    {
        if (block.Length == 0)
            return;
        if (builder.Length > 0)
            builder.Append("\n\n");
        builder.Append(block);
    }
}
