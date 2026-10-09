using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Scanner.Models.AiOcr;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Scanner.Services;

/// <summary>
/// A page image as sent to the AI text recognition, with the page size of the original document if known.
/// </summary>
internal record AiOcrPageImage(byte[] Image, double? WidthPoints, double? HeightPoints);

/// <summary>
/// Turns AI text recognition results into output files.
/// </summary>
internal static partial class AiOcrDocumentWriter
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private const string fontFamily = "Arial";
    private static readonly object fontSetupLock = new();
    private static bool isFontSetupDone;

    [GeneratedRegex(@"<\s*(br|/tr|/p|/li|/h\d)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}.*$", RegexOptions.Multiline)]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^\s*(#{1,6}|>|[-*+]\s)\s*", RegexOptions.Multiline)]
    private static partial Regex LinePrefixRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpacesRegex();


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public static string CreateMarkdown(IReadOnlyList<AiOcrPageResult> pages)
    {
        return string.Join("\n\n---\n\n", pages.Select(x => x.Markdown.Trim())).Trim() + "\n";
    }

    /// <summary>
    /// Creates a PDF from the page images with the recognized text as an invisible layer on top, so the document
    /// can be searched and its text copied. Built from the images instead of the original PDF, so it doesn't
    /// contain the text layer of the regular OCR a second time.
    /// </summary>
    public static byte[] CreateSearchablePdf(IReadOnlyList<AiOcrPageImage> images, IReadOnlyList<AiOcrPageResult> results)
    {
        EnsureFontSetup();

        List<IDisposable> resources = [];
        try
        {
            using PdfDocument document = new();
            for (int i = 0; i < images.Count; i++)
            {
                MemoryStream imageStream = new(images[i].Image);
                resources.Add(imageStream);
                XImage image = XImage.FromStream(imageStream);
                resources.Add(image);

                double width = images[i].WidthPoints ?? image.PointWidth;
                double height = images[i].HeightPoints ?? image.PointHeight;

                PdfPage page = document.AddPage();
                page.Width = XUnit.FromPoint(width);
                page.Height = XUnit.FromPoint(height);

                using XGraphics graphics = XGraphics.FromPdfPage(page);
                graphics.DrawImage(image, 0, 0, width, height);
                DrawInvisibleText(graphics, results[i], width, height);
            }

            using MemoryStream output = new();
            document.Save(output, false);
            return output.ToArray();
        }
        finally
        {
            foreach (IDisposable resource in resources)
                resource.Dispose();
        }
    }

    private static void DrawInvisibleText(XGraphics graphics, AiOcrPageResult result, double pageWidth, double pageHeight)
    {
        // practically invisible, but still a real text layer for search, copy and document management systems
        XBrush brush = new XSolidBrush(XColor.FromArgb(1, 255, 255, 255));
        Dictionary<double, XFont> fonts = [];

        XFont GetFont(double size)
        {
            double rounded = Math.Round(size * 2) / 2;
            if (!fonts.TryGetValue(rounded, out XFont? font))
            {
                font = new XFont(fontFamily, rounded, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
                fonts[rounded] = font;
            }
            return font;
        }

        foreach (AiOcrRegion region in result.Regions)
        {
            string[] lines = ToPlainText(region.Text)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
                continue;

            double x = region.X1 / 1000.0 * pageWidth;
            double y = region.Y1 / 1000.0 * pageHeight;
            double width = (region.X2 - region.X1) / 1000.0 * pageWidth;
            double height = (region.Y2 - region.Y1) / 1000.0 * pageHeight;
            if (width < 2 || height < 2)
                continue;

            double lineHeight = height / lines.Length;
            double baseSize = Math.Clamp(lineHeight * 0.8, 2, 72);

            for (int i = 0; i < lines.Length; i++)
            {
                // shrink lines that would be wider than their region
                XFont font = GetFont(baseSize);
                double measured = graphics.MeasureString(lines[i], font).Width;
                if (measured > width)
                    font = GetFont(Math.Max(1, baseSize * width / measured));

                graphics.DrawString(lines[i], font, brush, new XPoint(x, y + i * lineHeight), XStringFormats.TopLeft);
            }
        }
    }

    /// <summary>
    /// Removes Markdown/HTML markup, so only the words end up in the text layer.
    /// </summary>
    internal static string ToPlainText(string text)
    {
        text = LineBreakTagRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, " ");
        text = TableSeparatorRegex().Replace(text, "");
        text = LinePrefixRegex().Replace(text, "");
        text = text.Replace("|", " ").Replace("**", "").Replace("__", "").Replace("$", "");
        text = SpacesRegex().Replace(text, " ");
        return text.Trim();
    }

    private static void EnsureFontSetup()
    {
        lock (fontSetupLock)
        {
            if (isFontSetupDone)
                return;

            try
            {
                // PDFsharp's core build needs to be told where fonts come from
                if (GlobalFontSettings.FontResolver == null)
                    GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            }
            catch (InvalidOperationException)
            {
                // already configured elsewhere
            }

            isFontSetupDone = true;
        }
    }
}
