using Scanner.Core.Imaging;
using System;
using System.Collections.Generic;

namespace Scanner.Core.Ocr;

/// <summary>
/// What a recognized area of a page is, derived from the model's label.
/// </summary>
public enum OcrRegionKind
{
    Text,
    Title,
    List,
    Table,
    Image,
    Formula,
    Caption,
    PageHeader,
    PageFooter,
}

/// <summary>
/// A recognized area of a page.
/// </summary>
/// <param name="Label">The label as the model reported it, e.g. "text", "title", "table", "image".</param>
/// <param name="Box">Position, normalized to 0..1000 relative to the page image.</param>
/// <param name="Text">Recognized content (Markdown/HTML as the model produces it), empty for images.</param>
public record OcrRegion(string Label, NormalizedBox Box, string Text)
{
    public OcrRegionKind Kind => OcrLabels.GetKind(Label);
}

/// <summary>
/// The result of analyzing one page image.
/// </summary>
/// <param name="Width">Width in pixels of the analyzed image.</param>
/// <param name="Height">Height in pixels of the analyzed image.</param>
/// <param name="Markdown">The page's content as Markdown, without position markers.</param>
/// <param name="Regions">Recognized areas in reading order.</param>
/// <param name="Raw">The unprocessed model output.</param>
public record OcrPageResult(int Width, int Height, string Markdown, IReadOnlyList<OcrRegion> Regions, string Raw = "");

public class OcrException : Exception
{
    public OcrException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

public static class OcrLabels
{
    public static OcrRegionKind GetKind(string? label)
    {
        string l = (label ?? "").Trim().ToLowerInvariant();
        if (l.Length == 0)
            return OcrRegionKind.Text;
        if (l.Contains("caption"))
            return OcrRegionKind.Caption;
        if (l.Contains("table"))
            return OcrRegionKind.Table;
        if (l.Contains("image") || l.Contains("figure") || l.Contains("picture") || l.Contains("photo")
            || l.Contains("chart") || l.Contains("seal") || l.Contains("logo") || l.Contains("stamp"))
            return OcrRegionKind.Image;
        if (l.Contains("formula") || l.Contains("equation"))
            return OcrRegionKind.Formula;
        if (l.Contains("title") || l.Contains("heading") || l.Contains("section"))
            return OcrRegionKind.Title;
        if (l.Contains("header"))
            return OcrRegionKind.PageHeader;
        if (l.Contains("footer") || l.Contains("page_number") || l.Contains("footnote"))
            return OcrRegionKind.PageFooter;
        if (l.Contains("list"))
            return OcrRegionKind.List;
        return OcrRegionKind.Text;
    }
}
