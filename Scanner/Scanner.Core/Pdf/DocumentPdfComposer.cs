using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Scanner.Core.Imaging;
using Scanner.Core.Ocr;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Scanner.Core.Pdf;

public enum PdfOutputMode
{
    /// <summary>
    /// A clean digital document: white pages, the recognized text as real (visible, selectable) text at its
    /// position, tables as tables, and pictures cut out of the scan.
    /// </summary>
    Reconstructed,

    /// <summary>
    /// The (white-corrected) scan as it is, with the recognized text as an invisible layer on top.
    /// </summary>
    ScanWithTextLayer,
}

/// <summary>
/// One page to put into the PDF.
/// </summary>
/// <param name="Scan">The processed page image (cropped and white-corrected) the recognition ran on.</param>
/// <param name="Original">The same page before white correction (same size as <paramref name="Scan"/>); pictures
/// are taken from it so photos keep their colors. <see langword="null"/> uses <paramref name="Scan"/>.</param>
/// <param name="Ocr">The recognition result for <paramref name="Scan"/>.</param>
/// <param name="Dpi">Resolution of the scan, used for the page size.</param>
public record PdfPageInput(BgraImage Scan, BgraImage? Original, OcrPageResult Ocr, double Dpi = 300);

public record PdfComposeOptions
{
    public PdfOutputMode Mode { get; init; } = PdfOutputMode.Reconstructed;
    public string? Title { get; init; }
    public string Creator { get; init; } = "Scanner Paperless";

    /// <summary>
    /// Pages whose size is within this tolerance of A4 or US Letter get exactly that size.
    /// </summary>
    public double PaperSizeSnapTolerance { get; init; } = 0.04;
}

/// <summary>
/// Creates a PDF from recognized pages, see <see cref="PdfOutputMode"/>.
/// </summary>
public static class DocumentPdfComposer
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private static readonly (double Width, double Height)[] knownPaperSizes =
    [
        (595.276, 841.89),  // A4
        (612, 792),         // US Letter
        (419.528, 595.276), // A5
    ];

    private const double minFontSize = 4;
    private const double maxBodyFontSize = 24;
    private const double maxTitleFontSize = 48;
    private const double lineSpacing = 1.18;

    private static readonly XColor textColor = XColor.FromArgb(255, 20, 20, 20);
    private static readonly XColor secondaryTextColor = XColor.FromArgb(255, 90, 90, 90);
    private static readonly XColor tableLineColor = XColor.FromArgb(255, 160, 160, 160);


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public static byte[] Create(IReadOnlyList<PdfPageInput> pages, PdfComposeOptions? options = null, IImageEncoder? encoder = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
            throw new ArgumentException("At least one page is required", nameof(pages));

        options ??= new PdfComposeOptions();
        encoder ??= PngImageEncoder.Instance;
        EmbeddedFontResolver.EnsureInstalled();

        List<IDisposable> resources = [];
        try
        {
            using PdfDocument document = new();
            document.Info.Title = options.Title ?? "";
            document.Info.Creator = options.Creator;
            document.Info.CreationDate = DateTime.Now;

            foreach (PdfPageInput input in pages)
            {
                (double width, double height) = GetPageSize(input, options.PaperSizeSnapTolerance);
                PdfPage page = document.AddPage();
                page.Width = XUnit.FromPoint(width);
                page.Height = XUnit.FromPoint(height);

                using XGraphics graphics = XGraphics.FromPdfPage(page);
                FontCache fonts = new();
                if (options.Mode == PdfOutputMode.ScanWithTextLayer)
                {
                    XImage scan = LoadImage(encoder.EncodePhoto(input.Scan), resources);
                    graphics.DrawImage(scan, 0, 0, width, height);
                    DrawTextLayer(graphics, fonts, input.Ocr, width, height);
                }
                else
                {
                    DrawReconstructed(graphics, fonts, input, width, height, encoder, resources);
                }
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

    internal static (double Width, double Height) GetPageSize(PdfPageInput input, double snapTolerance)
    {
        double dpi = input.Dpi > 0 ? input.Dpi : 300;
        double width = input.Scan.Width / dpi * 72;
        double height = input.Scan.Height / dpi * 72;

        foreach ((double w, double h) in knownPaperSizes)
        {
            if (Near(width, w) && Near(height, h))
                return (w, h);
            if (Near(width, h) && Near(height, w))
                return (h, w);
        }
        return (width, height);

        bool Near(double value, double target) => Math.Abs(value - target) <= target * snapTolerance;
    }

    private static XImage LoadImage(byte[] encoded, List<IDisposable> resources)
    {
        MemoryStream stream = new(encoded);
        resources.Add(stream);
        XImage image = XImage.FromStream(stream);
        resources.Add(image);
        return image;
    }

    private static XRect ToPageRect(NormalizedBox box, double pageWidth, double pageHeight) => new(
        box.X1 / 1000.0 * pageWidth,
        box.Y1 / 1000.0 * pageHeight,
        box.Width / 1000.0 * pageWidth,
        box.Height / 1000.0 * pageHeight);

    #region Scan with text layer
    /// <summary>
    /// Writes the recognized lines practically invisible over the scan, each stretched to the width of its region,
    /// so search hits and selections line up with the printed text.
    /// </summary>
    private static void DrawTextLayer(XGraphics graphics, FontCache fonts, OcrPageResult ocr, double pageWidth, double pageHeight)
    {
        XBrush brush = new XSolidBrush(XColor.FromArgb(1, 255, 255, 255));
        foreach (OcrRegion region in ocr.Regions)
        {
            if (region.Kind == OcrRegionKind.Image)
                continue;

            string[] lines = OcrText.ToLines(region.Text);
            XRect rect = ToPageRect(region.Box, pageWidth, pageHeight);
            if (lines.Length == 0 || rect.Width < 2 || rect.Height < 2)
                continue;

            double lineHeight = rect.Height / lines.Length;
            XFont font = fonts.Get(Math.Clamp(lineHeight / lineSpacing, 1, 72), false);
            for (int i = 0; i < lines.Length; i++)
            {
                double measured = graphics.MeasureString(lines[i], font).Width;
                if (measured <= 0)
                    continue;

                XGraphicsState state = graphics.Save();
                graphics.TranslateTransform(rect.X, rect.Y + i * lineHeight);
                graphics.ScaleTransform(rect.Width / measured, 1);
                graphics.DrawString(lines[i], font, brush, new XPoint(0, 0), XStringFormats.TopLeft);
                graphics.Restore(state);
            }
        }
    }
    #endregion

    #region Reconstructed
    private static void DrawReconstructed(XGraphics graphics, FontCache fonts, PdfPageInput input, double pageWidth, double pageHeight,
        IImageEncoder encoder, List<IDisposable> resources)
    {
        graphics.DrawRectangle(XBrushes.White, 0, 0, pageWidth, pageHeight);
        BgraImage pictureSource = input.Original is { } original && original.Width == input.Scan.Width && original.Height == input.Scan.Height
            ? original
            : input.Scan;

        // body text gets one consistent size per page: the median of what fits, so small boxes don't shrink everything
        Dictionary<OcrRegion, double> fittedSizes = [];
        foreach (OcrRegion region in input.Ocr.Regions)
        {
            if (region.Kind is OcrRegionKind.Image or OcrRegionKind.Table)
                continue;
            XRect rect = ToPageRect(region.Box, pageWidth, pageHeight);
            bool bold = region.Kind == OcrRegionKind.Title;
            fittedSizes[region] = FitFontSize(graphics, fonts, OcrText.ToLines(region.Text), rect, bold,
                region.Kind == OcrRegionKind.Title ? maxTitleFontSize : maxBodyFontSize);
        }
        List<double> bodySizes = fittedSizes.Where(x => x.Key.Kind is OcrRegionKind.Text or OcrRegionKind.List).Select(x => x.Value).Order().ToList();
        double bodySize = bodySizes.Count > 0 ? bodySizes[bodySizes.Count / 2] : maxBodyFontSize;

        foreach (OcrRegion region in input.Ocr.Regions)
        {
            XRect rect = ToPageRect(region.Box, pageWidth, pageHeight);
            if (rect.Width < 1 || rect.Height < 1)
                continue;

            switch (region.Kind)
            {
                case OcrRegionKind.Image:
                    DrawPicture(graphics, pictureSource, region.Box, rect, encoder, resources);
                    break;

                case OcrRegionKind.Table when OcrText.ParseTable(region.Text) is { } rows:
                    DrawTable(graphics, fonts, rows, rect, bodySize);
                    break;

                default:
                    double size = fittedSizes.TryGetValue(region, out double fitted) ? fitted : bodySize;
                    if (region.Kind is OcrRegionKind.Text or OcrRegionKind.List)
                        size = Math.Min(size, bodySize * 1.1);
                    else if (region.Kind is OcrRegionKind.PageHeader or OcrRegionKind.PageFooter or OcrRegionKind.Caption)
                        size = Math.Min(size, bodySize);
                    bool bold = region.Kind == OcrRegionKind.Title;
                    XColor color = region.Kind is OcrRegionKind.PageHeader or OcrRegionKind.PageFooter or OcrRegionKind.Caption
                        ? secondaryTextColor
                        : textColor;
                    DrawWrappedText(graphics, fonts.Get(size, bold), OcrText.ToLines(region.Text), rect, new XSolidBrush(color));
                    break;
            }
        }
    }

    private static void DrawPicture(XGraphics graphics, BgraImage source, NormalizedBox box, XRect rect, IImageEncoder encoder, List<IDisposable> resources)
    {
        PixelRect pixels = PixelRect.FromNormalized(box, source.Width, source.Height);
        if (pixels.Width < 2 || pixels.Height < 2)
            return;

        XImage picture = LoadImage(encoder.EncodePhoto(source.Crop(pixels)), resources);
        graphics.DrawImage(picture, rect);
    }

    private static void DrawTable(XGraphics graphics, FontCache fonts, List<List<string>> rows, XRect rect, double bodySize)
    {
        int columns = rows.Max(r => r.Count);
        double columnWidth = rect.Width / columns;
        double rowHeight = rect.Height / rows.Count;
        XPen pen = new(tableLineColor, 0.5);
        XBrush brush = new XSolidBrush(textColor);
        const double padding = 1.5;

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                XRect cell = new(rect.X + c * columnWidth, rect.Y + r * rowHeight, columnWidth, rowHeight);
                graphics.DrawRectangle(pen, cell);

                string text = c < rows[r].Count ? rows[r][c] : "";
                if (text.Length == 0)
                    continue;

                XRect inner = new(cell.X + padding, cell.Y + padding, Math.Max(1, cell.Width - 2 * padding), Math.Max(1, cell.Height - 2 * padding));
                double size = Math.Min(FitFontSize(graphics, fonts, [text], inner, r == 0, maxBodyFontSize), bodySize);
                DrawWrappedText(graphics, fonts.Get(size, r == 0), [text], inner, brush);
            }
        }
    }

    /// <summary>
    /// The largest font size at which the wrapped lines fit into <paramref name="rect"/>.
    /// </summary>
    private static double FitFontSize(XGraphics graphics, FontCache fonts, string[] lines, XRect rect, bool bold, double maxSize)
    {
        if (lines.Length == 0)
            return minFontSize;

        double low = minFontSize, high = Math.Max(minFontSize, Math.Min(maxSize, rect.Height / lineSpacing));
        for (int i = 0; i < 12; i++)
        {
            double mid = (low + high) / 2;
            XFont font = fonts.Get(mid, bold);
            int wrappedLines = lines.Sum(line => Wrap(graphics, font, line, rect.Width).Count);
            if (wrappedLines * mid * lineSpacing <= rect.Height + 0.5)
                low = mid;
            else
                high = mid;
        }
        return Math.Round(low * 4) / 4;
    }

    private static void DrawWrappedText(XGraphics graphics, XFont font, string[] lines, XRect rect, XBrush brush)
    {
        double lineHeight = font.Size * lineSpacing;
        double y = rect.Y;
        foreach (string line in lines)
        {
            foreach (string wrapped in Wrap(graphics, font, line, rect.Width))
            {
                // never drop text: overflow continues below the box rather than disappearing
                graphics.DrawString(wrapped, font, brush, new XPoint(rect.X, y), XStringFormats.TopLeft);
                y += lineHeight;
            }
        }
    }

    internal static List<string> Wrap(XGraphics graphics, XFont font, string line, double maxWidth)
    {
        List<string> result = [];
        string current = "";
        foreach (string word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && graphics.MeasureString(candidate, font).Width > maxWidth)
            {
                result.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0)
            result.Add(current);
        return result;
    }
    #endregion

    private sealed class FontCache
    {
        private readonly Dictionary<(double, bool), XFont> fonts = [];

        public XFont Get(double size, bool bold)
        {
            double rounded = Math.Max(1, Math.Round(size * 4) / 4);
            if (!fonts.TryGetValue((rounded, bold), out XFont? font))
            {
                font = new XFont(EmbeddedFontResolver.FamilyName, rounded, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular,
                    new XPdfFontOptions(PdfFontEncoding.Unicode));
                fonts[(rounded, bold)] = font;
            }
            return font;
        }
    }
}
