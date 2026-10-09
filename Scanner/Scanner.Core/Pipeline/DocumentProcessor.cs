using Scanner.Core.Imaging;
using Scanner.Core.Ocr;
using Scanner.Core.Pdf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Scanner.Core.Pipeline;

/// <summary>
/// Recognizes the text on a page image, e.g. through the local Unlimited-OCR service.
/// </summary>
public interface IOcrEngine
{
    /// <param name="encodedImage">The page as PNG or JPEG.</param>
    /// <param name="width">Width of the encoded image in pixels.</param>
    /// <param name="height">Height of the encoded image in pixels.</param>
    Task<OcrPageResult> AnalyzeAsync(byte[] encodedImage, int width, int height, CancellationToken cancellationToken);
}

public record DocumentProcessingOptions
{
    /// <summary>
    /// Make the paper white (only if the page was recognized as a document).
    /// </summary>
    public bool WhiteCorrection { get; init; } = true;

    public WhiteCorrectionOptions WhiteCorrectionOptions { get; init; } = new();

    /// <summary>
    /// Cut away the scanner background around a smaller sheet of paper.
    /// </summary>
    public bool CropToDocument { get; init; } = true;

    /// <summary>
    /// Pictures found by the recognition only get a gentle white balance instead of the full white correction,
    /// so photos keep their contrast and colors.
    /// </summary>
    public bool ProtectPictures { get; init; } = true;

    public PdfOutputMode PdfMode { get; init; } = PdfOutputMode.Reconstructed;

    public string? Title { get; init; }

    /// <summary>
    /// Pages are downscaled to this size (longest side, in pixels) before recognition. The model works at
    /// 640..1024 pixels internally, larger images only cost time.
    /// </summary>
    public int MaxRecognitionImageSide { get; init; } = 2600;
}

public enum ProcessingStep
{
    Waiting,
    DetectingDocument,
    WhiteCorrection,
    Recognizing,
    Recognized,
    ComposingPdf,
    Done,
    Failed,
}

/// <summary>
/// A page on its way through <see cref="DocumentProcessor"/>. Properties are filled in step by step, so a UI can
/// show the intermediate results while the next step runs.
/// </summary>
public sealed class ProcessedPage
{
    public required int Index { get; init; }
    public required BgraImage Input { get; init; }
    public required double Dpi { get; init; }

    public ProcessingStep Step { get; internal set; } = ProcessingStep.Waiting;
    public DocumentDetectionResult? Detection { get; internal set; }

    /// <summary>
    /// The input, cropped to the document if requested.
    /// </summary>
    public BgraImage? Original { get; internal set; }

    /// <summary>
    /// <see cref="Original"/> after white correction (or the same image if it's off).
    /// </summary>
    public BgraImage? Processed { get; internal set; }

    public OcrPageResult? Ocr { get; internal set; }
    public string? Error { get; internal set; }
    public TimeSpan? RecognitionDuration { get; internal set; }

    public bool IsWhiteCorrected { get; internal set; }
}

public record DocumentProcessingProgress(ProcessedPage Page, int PageCount, ProcessingStep Step);

public record DocumentProcessingResult(IReadOnlyList<ProcessedPage> Pages, byte[] Pdf, string Markdown);

/// <summary>
/// The document pipeline: detect the paper, make it white, recognize the text, protect pictures, create the PDF.
/// </summary>
public sealed class DocumentProcessor
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private readonly IOcrEngine ocrEngine;
    private readonly IImageEncoder encoder;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public DocumentProcessor(IOcrEngine ocrEngine, IImageEncoder? encoder = null)
    {
        this.ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        this.encoder = encoder ?? PngImageEncoder.Instance;
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public static List<ProcessedPage> CreatePages(IEnumerable<(BgraImage Image, double Dpi)> inputs) =>
        inputs.Select((x, i) => new ProcessedPage { Index = i, Input = x.Image, Dpi = x.Dpi }).ToList();

    /// <summary>
    /// Runs all steps for all pages and creates the PDF.
    /// </summary>
    public async Task<DocumentProcessingResult> ProcessAsync(IReadOnlyList<ProcessedPage> pages, DocumentProcessingOptions options,
        IProgress<DocumentProcessingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
            throw new ArgumentException("At least one page is required", nameof(pages));

        foreach (ProcessedPage page in pages)
            await ProcessPageAsync(page, pages.Count, options, progress, cancellationToken);

        foreach (ProcessedPage page in pages)
            Report(page, pages.Count, ProcessingStep.ComposingPdf, progress);
        byte[] pdf = await Task.Run(() => ComposePdf(pages, options), cancellationToken);

        foreach (ProcessedPage page in pages)
            Report(page, pages.Count, ProcessingStep.Done, progress);
        return new DocumentProcessingResult(pages, pdf, CreateMarkdown(pages));
    }

    /// <summary>
    /// Detection, white correction and recognition of one page.
    /// </summary>
    public async Task ProcessPageAsync(ProcessedPage page, int pageCount, DocumentProcessingOptions options,
        IProgress<DocumentProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            // 1. find the sheet of paper
            Report(page, pageCount, ProcessingStep.DetectingDocument, progress);
            DocumentDetectionResult detection = await Task.Run(() => DocumentDetector.Detect(page.Input), cancellationToken);
            page.Detection = detection;
            page.Original = options.CropToDocument && detection.IsDocument && detection.IsCropped(page.Input.Width, page.Input.Height)
                ? page.Input.Crop(detection.Bounds)
                : page.Input;

            // 2. white paper, only for documents (photos etc. stay as they are)
            Report(page, pageCount, ProcessingStep.WhiteCorrection, progress);
            if (options.WhiteCorrection && detection.IsDocument)
            {
                BgraImage original = page.Original;
                page.Processed = await Task.Run(() =>
                {
                    BgraImage corrected = WhiteCorrection.Apply(original, options.WhiteCorrectionOptions);
                    detection.ClearBackground(corrected);
                    return corrected;
                }, cancellationToken);
                page.IsWhiteCorrected = true;
            }
            else
            {
                page.Processed = page.Original;
                page.IsWhiteCorrected = false;
            }

            // 3. text recognition
            Report(page, pageCount, ProcessingStep.Recognizing, progress);
            BgraImage processed = page.Processed;
            (byte[] encoded, int width, int height) = await Task.Run(() =>
            {
                BgraImage forRecognition = ImageResize.FitWithin(processed, options.MaxRecognitionImageSide);
                return (encoder.EncodeLossless(forRecognition), forRecognition.Width, forRecognition.Height);
            }, cancellationToken);

            DateTime started = DateTime.UtcNow;
            OcrPageResult ocr = await ocrEngine.AnalyzeAsync(encoded, width, height, cancellationToken);
            page.RecognitionDuration = DateTime.UtcNow - started;

            // results refer to the full-size processed image from here on (boxes are normalized anyway)
            page.Ocr = ocr with { Width = processed.Width, Height = processed.Height };

            // 4. pictures keep their contrast and colors
            if (page.IsWhiteCorrected && options.ProtectPictures)
            {
                List<PixelRect> pictures = page.Ocr.Regions
                    .Where(r => r.Kind == OcrRegionKind.Image)
                    .Select(r => PixelRect.FromNormalized(r.Box, processed.Width, processed.Height))
                    .Where(r => !r.IsEmpty)
                    .ToList();
                if (pictures.Count > 0)
                {
                    BgraImage original = page.Original;
                    page.Processed = await Task.Run(() =>
                    {
                        BgraImage corrected = WhiteCorrection.Apply(original, options.WhiteCorrectionOptions, pictures);
                        detection.ClearBackground(corrected);
                        return corrected;
                    }, cancellationToken);
                }
            }

            Report(page, pageCount, ProcessingStep.Recognized, progress);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exc)
        {
            page.Error = exc.Message;
            Report(page, pageCount, ProcessingStep.Failed, progress);
            throw;
        }
    }

    /// <summary>
    /// Creates the PDF from pages that went through <see cref="ProcessPageAsync"/>, e.g. again with another mode.
    /// </summary>
    public byte[] ComposePdf(IReadOnlyList<ProcessedPage> pages, DocumentProcessingOptions options)
    {
        List<PdfPageInput> inputs = [];
        foreach (ProcessedPage page in pages)
        {
            if (page.Processed == null || page.Ocr == null)
                throw new InvalidOperationException($"Page {page.Index + 1} hasn't been processed");
            // pictures are cut out of the processed page: white-balanced, but not bleached
            inputs.Add(new PdfPageInput(page.Processed, null, page.Ocr, page.Dpi));
        }

        return DocumentPdfComposer.Create(inputs, new PdfComposeOptions { Mode = options.PdfMode, Title = options.Title }, encoder);
    }

    public static string CreateMarkdown(IEnumerable<ProcessedPage> pages) =>
        string.Join("\n\n---\n\n", pages.Select(p => p.Ocr?.Markdown.Trim() ?? "").Where(x => x.Length > 0)).Trim() + "\n";

    private static void Report(ProcessedPage page, int pageCount, ProcessingStep step, IProgress<DocumentProcessingProgress>? progress)
    {
        page.Step = step;
        progress?.Report(new DocumentProcessingProgress(page, pageCount, step));
    }
}
