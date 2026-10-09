using Scanner.Core.Imaging;
using Scanner.Core.Ocr;
using Scanner.Core.Pdf;
using Scanner.Core.Pipeline;
using UglyToad.PdfPig;
using PigPage = UglyToad.PdfPig.Content.Page;

namespace ScannerCoreTests;

internal static class SampleOcr
{
    public static OcrPageResult ForTestPage(int width, int height) => UnlimitedOcrParser.Parse("""
        <|det|>title [100, 50, 900, 75]<|/det|># Rechnung für Müller
        <|det|>text [100, 80, 900, 330]<|/det|>Größe der Lieferung: zwölf Kartons
        Zahlbar innerhalb von 14 Tagen.
        <|det|>image [200, 375, 467, 525]<|/det|>
        <|det|>table [100, 600, 900, 700]<|/det|><table><tr><td>Pos</td><td>Betrag</td></tr><tr><td>1</td><td>99,00 EUR</td></tr></table>
        <|det|>page_footer [100, 950, 900, 980]<|/det|>Seite 1
        """, width, height);
}

[TestClass]
public sealed class PdfComposerTests
{
    private static string ExtractText(byte[] pdf, out int imageCount, out double width, out double height)
    {
        using PdfDocument document = PdfDocument.Open(pdf);
        PigPage page = document.GetPage(1);
        imageCount = page.GetImages().Count();
        width = page.Width;
        height = page.Height;
        return string.Join(" ", page.GetWords().Select(w => w.Text));
    }

    [TestMethod]
    public void Reconstructed_TextIsSelectableAndPictureIsKept()
    {
        BgraImage page = TestImages.Page();
        OcrPageResult ocr = SampleOcr.ForTestPage(page.Width, page.Height);

        byte[] pdf = DocumentPdfComposer.Create([new PdfPageInput(page, page, ocr, 72)], new PdfComposeOptions { Title = "Test" });

        string text = ExtractText(pdf, out int images, out _, out _);
        foreach (string word in new[] { "Rechnung", "Müller", "Größe", "zwölf", "Kartons", "99,00", "EUR", "Seite" })
            StringAssert.Contains(text, word);
        Assert.IsFalse(text.Contains('#'), "Markdown markup must not end up in the PDF");
        Assert.AreEqual(1, images, "only the picture region is an image, the text is real text");
    }

    [TestMethod]
    public void ScanWithTextLayer_HasScanAndText()
    {
        BgraImage page = TestImages.Page();
        OcrPageResult ocr = SampleOcr.ForTestPage(page.Width, page.Height);

        byte[] pdf = DocumentPdfComposer.Create([new PdfPageInput(page, null, ocr, 72)], new PdfComposeOptions { Mode = PdfOutputMode.ScanWithTextLayer });

        string text = ExtractText(pdf, out int images, out _, out _);
        StringAssert.Contains(text, "Rechnung");
        StringAssert.Contains(text, "Kartons");
        Assert.AreEqual(1, images, "the whole scan");
    }

    [TestMethod]
    public void A4At300Dpi_GetsExactA4Size()
    {
        BgraImage page = BgraImage.Filled(2480, 3508, 255, 255, 255);

        byte[] pdf = DocumentPdfComposer.Create([new PdfPageInput(page, null, new OcrPageResult(2480, 3508, "", []), 300)]);

        ExtractText(pdf, out _, out double width, out double height);
        Assert.AreEqual(595.276, width, 0.01);
        Assert.AreEqual(841.89, height, 0.01);
    }

    [TestMethod]
    public void OddSize_KeepsItsSize()
    {
        (double width, double height) = DocumentPdfComposer.GetPageSize(new PdfPageInput(BgraImage.Filled(300, 600, 0, 0, 0), null, new OcrPageResult(300, 600, "", []), 100), 0.04);

        Assert.AreEqual(216, width, 0.01);
        Assert.AreEqual(432, height, 0.01);
    }

    [TestMethod]
    public void MultiplePages()
    {
        BgraImage page = TestImages.Page(200, 300, withPicture: false);
        OcrPageResult ocr = UnlimitedOcrParser.Parse("<|det|>text [100, 100, 900, 300]<|/det|>Seite", 200, 300);

        byte[] pdf = DocumentPdfComposer.Create([new PdfPageInput(page, null, ocr), new PdfPageInput(page, null, ocr)]);

        using PdfDocument document = PdfDocument.Open(pdf);
        Assert.AreEqual(2, document.NumberOfPages);
    }

    [TestMethod]
    public void LongTextInSmallBox_DoesNotThrowAndKeepsAllWords()
    {
        BgraImage page = BgraImage.Filled(400, 400, 255, 255, 255);
        string longText = string.Join(" ", Enumerable.Range(1, 200).Select(i => $"Wort{i}"));
        OcrPageResult ocr = UnlimitedOcrParser.Parse($"<|det|>text [10, 10, 60, 20]<|/det|>{longText}", 400, 400);

        byte[] pdf = DocumentPdfComposer.Create([new PdfPageInput(page, null, ocr, 72)]);

        string text = ExtractText(pdf, out _, out _, out _);
        StringAssert.Contains(text, "Wort1");
        StringAssert.Contains(text, "Wort200");
    }
}

[TestClass]
public sealed class DocumentProcessorTests
{
    private sealed class FakeOcrEngine(Func<int, int, OcrPageResult> result) : IOcrEngine
    {
        public int Calls;
        public (int Width, int Height) LastSize;

        public Task<OcrPageResult> AnalyzeAsync(byte[] encodedImage, int width, int height, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastSize = (width, height);
            Assert.IsTrue(encodedImage.Length > 8 && encodedImage[1] == (byte)'P', "a PNG is sent");
            return Task.FromResult(result(width, height));
        }
    }

    private sealed class ListProgress : IProgress<DocumentProcessingProgress>
    {
        public readonly List<ProcessingStep> Steps = [];
        public void Report(DocumentProcessingProgress value)
        {
            lock (Steps)
                Steps.Add(value.Step);
        }
    }

    [TestMethod]
    public async Task FullPipeline()
    {
        BgraImage scan = TestImages.PageOnDarkBackground(out PixelRect pageArea);
        FakeOcrEngine engine = new(SampleOcr.ForTestPage);
        DocumentProcessor processor = new(engine);
        List<ProcessedPage> pages = DocumentProcessor.CreatePages([(scan, 72.0)]);
        ListProgress progress = new();

        DocumentProcessingResult result = await processor.ProcessAsync(pages, new DocumentProcessingOptions { Title = "Rechnung" }, progress);

        ProcessedPage page = result.Pages.Single();
        Assert.IsTrue(page.Detection!.IsDocument);
        Assert.IsTrue(page.IsWhiteCorrected);
        Assert.IsTrue(Math.Abs(page.Original!.Width - pageArea.Width) <= 24, "cropped to the paper");
        Assert.AreEqual(page.Original.Width, page.Ocr!.Width, "results refer to the processed image");
        Assert.AreEqual(1, engine.Calls);
        CollectionAssert.AreEqual(
            new[] { ProcessingStep.DetectingDocument, ProcessingStep.WhiteCorrection, ProcessingStep.Recognizing, ProcessingStep.Recognized, ProcessingStep.ComposingPdf, ProcessingStep.Done },
            progress.Steps);
        Assert.AreEqual(ProcessingStep.Done, page.Step);

        StringAssert.StartsWith(result.Markdown, "# Rechnung für Müller");
        using PdfDocument document = PdfDocument.Open(result.Pdf);
        Assert.AreEqual("Rechnung", document.Information.Title);
        StringAssert.Contains(string.Join(" ", document.GetPage(1).GetWords().Select(w => w.Text)), "Kartons");
    }

    [TestMethod]
    public async Task PicturesAreNotBleached()
    {
        BgraImage scan = TestImages.Page();
        DocumentProcessor processor = new(new FakeOcrEngine(SampleOcr.ForTestPage));
        List<ProcessedPage> pages = DocumentProcessor.CreatePages([(scan, 72.0)]);
        List<ProcessedPage> bleachedPages = DocumentProcessor.CreatePages([(scan, 72.0)]);

        await processor.ProcessAsync(pages, new DocumentProcessingOptions());
        await processor.ProcessAsync(bleachedPages, new DocumentProcessingOptions { ProtectPictures = false });

        ProcessedPage page = pages[0];
        PixelRect picture = PixelRect.FromNormalized(page.Ocr!.Regions.Single(r => r.Kind == OcrRegionKind.Image).Box, page.Processed!.Width, page.Processed.Height);
        (byte r, byte g, byte b) protectedPixel = TestImages.Get(page.Processed, picture.X + 10, picture.Y + 10);
        (byte r, byte g, byte b) bleachedPixel = TestImages.Get(bleachedPages[0].Processed!, picture.X + 10, picture.Y + 10);
        Assert.IsTrue(protectedPixel.r + protectedPixel.g + protectedPixel.b < bleachedPixel.r + bleachedPixel.g + bleachedPixel.b,
            $"{protectedPixel} should be darker than {bleachedPixel}");
        // paper outside of it is white
        Assert.AreEqual(((byte)255, (byte)255, (byte)255), TestImages.Get(page.Processed, 580, 780));
    }

    [TestMethod]
    public async Task WhiteCorrectionOff_KeepsTheScan()
    {
        BgraImage scan = TestImages.Page();
        DocumentProcessor processor = new(new FakeOcrEngine(SampleOcr.ForTestPage));
        List<ProcessedPage> pages = DocumentProcessor.CreatePages([(scan, 72.0)]);

        await processor.ProcessAsync(pages, new DocumentProcessingOptions { WhiteCorrection = false });

        Assert.IsFalse(pages[0].IsWhiteCorrected);
        CollectionAssert.AreEqual(scan.Pixels, pages[0].Processed!.Pixels);
    }

    [TestMethod]
    public async Task Photo_IsNeitherCroppedNorWhitened()
    {
        BgraImage photo = TestImages.Photo();
        DocumentProcessor processor = new(new FakeOcrEngine((w, h) => new OcrPageResult(w, h, "", [])));
        List<ProcessedPage> pages = DocumentProcessor.CreatePages([(photo, 72.0)]);

        await processor.ProcessAsync(pages, new DocumentProcessingOptions());

        Assert.IsFalse(pages[0].IsWhiteCorrected);
        Assert.AreSame(photo, pages[0].Processed);
    }

    [TestMethod]
    public async Task LargePages_AreDownscaledForRecognition()
    {
        BgraImage scan = BgraImage.Filled(3000, 2000, 250, 250, 250);
        FakeOcrEngine engine = new((w, h) => new OcrPageResult(w, h, "", []));
        DocumentProcessor processor = new(engine);
        List<ProcessedPage> pages = DocumentProcessor.CreatePages([(scan, 300.0)]);

        await processor.ProcessAsync(pages, new DocumentProcessingOptions { MaxRecognitionImageSide = 1500 });

        Assert.AreEqual((1500, 1000), engine.LastSize);
        Assert.AreEqual(3000, pages[0].Ocr!.Width, "but the result refers to the full page");
    }

    [TestMethod]
    public async Task RecognitionError_MarksThePage()
    {
        DocumentProcessor processor = new(new FakeOcrEngine((_, _) => throw new OcrException("GPU weg")));
        List<ProcessedPage> pages = DocumentProcessor.CreatePages([(TestImages.Page(), 72.0)]);
        ListProgress progress = new();

        await Assert.ThrowsExactlyAsync<OcrException>(() => processor.ProcessAsync(pages, new DocumentProcessingOptions(), progress));

        Assert.AreEqual(ProcessingStep.Failed, pages[0].Step);
        Assert.AreEqual("GPU weg", pages[0].Error);
        Assert.AreEqual(ProcessingStep.Failed, progress.Steps[^1]);
    }

    [TestMethod]
    public async Task Cancellation_IsPassedThrough()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        DocumentProcessor processor = new(new FakeOcrEngine(SampleOcr.ForTestPage));
        List<ProcessedPage> pages = DocumentProcessor.CreatePages([(TestImages.Page(), 72.0)]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(pages, new DocumentProcessingOptions(), null, cancellation.Token));
    }

    [TestMethod]
    public async Task ComposeAgain_WithOtherMode()
    {
        DocumentProcessor processor = new(new FakeOcrEngine(SampleOcr.ForTestPage));
        List<ProcessedPage> pages = DocumentProcessor.CreatePages([(TestImages.Page(), 72.0)]);
        await processor.ProcessAsync(pages, new DocumentProcessingOptions());

        byte[] pdf = processor.ComposePdf(pages, new DocumentProcessingOptions { PdfMode = PdfOutputMode.ScanWithTextLayer });

        using PdfDocument document = PdfDocument.Open(pdf);
        Assert.AreEqual(1, document.GetPage(1).GetImages().Count());
    }
}
