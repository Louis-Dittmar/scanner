using Scanner.Core.Imaging;
using Scanner.Core.Ocr;

namespace ScannerCoreTests;

[TestClass]
public sealed class UnlimitedOcrParserTests
{
    /// <summary>
    /// The format documented on the Unlimited-OCR model card: one marker per block, content may span lines.
    /// </summary>
    private const string unlimitedOcrOutput = """
        <|det|>title [80, 40, 920, 90]<|/det|># Rechnung Nr. 2026-117
        <|det|>text [80, 120, 600, 220]<|/det|>Sehr geehrte Damen und Herren,
        vielen Dank für Ihren Auftrag. Größe: 12 m²
        <|det|>image [100, 300, 400, 500]<|/det|>
        <|det|>table [80, 550, 920, 700]<|/det|><table><tr><td>Pos.</td><td>Betrag</td></tr><tr><td>1</td><td>99,00 €</td></tr></table>
        <|det|>page_footer [80, 950, 920, 980]<|/det|>Seite 1 von 1
        """;

    [TestMethod]
    public void UnlimitedOcrFormat_RegionsAndLabels()
    {
        OcrPageResult result = UnlimitedOcrParser.Parse(unlimitedOcrOutput, 1000, 1400);

        CollectionAssert.AreEqual(new[] { "title", "text", "image", "table", "page_footer" }, result.Regions.Select(r => r.Label).ToArray());
        CollectionAssert.AreEqual(
            new[] { OcrRegionKind.Title, OcrRegionKind.Text, OcrRegionKind.Image, OcrRegionKind.Table, OcrRegionKind.PageFooter },
            result.Regions.Select(r => r.Kind).ToArray());
        Assert.AreEqual(new NormalizedBox(80, 120, 600, 220), result.Regions[1].Box);
        Assert.AreEqual(1000, result.Width);
        Assert.AreEqual(1400, result.Height);
    }

    [TestMethod]
    public void UnlimitedOcrFormat_ContentSpansLines()
    {
        OcrPageResult result = UnlimitedOcrParser.Parse(unlimitedOcrOutput, 1000, 1400);

        Assert.AreEqual("Sehr geehrte Damen und Herren,\nvielen Dank für Ihren Auftrag. Größe: 12 m²", result.Regions[1].Text);
        Assert.AreEqual("", result.Regions[2].Text, "pictures carry no text");
    }

    [TestMethod]
    public void UnlimitedOcrFormat_Markdown()
    {
        OcrPageResult result = UnlimitedOcrParser.Parse(unlimitedOcrOutput, 1000, 1400);

        Assert.IsTrue(result.Markdown.StartsWith("# Rechnung Nr. 2026-117\n\nSehr geehrte Damen und Herren,"), result.Markdown);
        Assert.IsFalse(result.Markdown.Contains("<|det|>"));
        Assert.IsTrue(result.Markdown.EndsWith("Seite 1 von 1"));
        Assert.IsFalse(result.Markdown.Contains("\n\n\n"));
    }

    [TestMethod]
    public void DeepSeekOcrFormat()
    {
        const string raw = "<|ref|>sub_title<|/ref|><|det|>[[10, 20, 300, 60]]<|/det|>\n## Einleitung\n\n"
            + "<|ref|>text<|/ref|><|det|>[[10, 80, 900, 200], [10, 220, 900, 300]]<|/det|>\nZwei Boxen, ein Absatz.<｜end▁of▁sentence｜>";

        OcrPageResult result = UnlimitedOcrParser.Parse(raw, 500, 500);

        Assert.AreEqual(3, result.Regions.Count);
        Assert.AreEqual(OcrRegionKind.Title, result.Regions[0].Kind);
        Assert.AreEqual("## Einleitung", result.Regions[0].Text);
        Assert.AreEqual(new NormalizedBox(10, 220, 900, 300), result.Regions[2].Box);
        Assert.AreEqual("Zwei Boxen, ein Absatz.", result.Regions[2].Text);
        Assert.AreEqual("## Einleitung\n\nZwei Boxen, ein Absatz.", result.Markdown);
    }

    [TestMethod]
    public void PixelCoordinates_AreNormalized()
    {
        OcrPageResult result = UnlimitedOcrParser.Parse("<|det|>text [200, 400, 1800, 2000]<|/det|>Hallo", 2000, 4000);

        Assert.AreEqual(new NormalizedBox(100, 100, 900, 500), result.Regions.Single().Box);
    }

    [TestMethod]
    public void InvalidAndSwappedBoxes()
    {
        OcrPageResult result = UnlimitedOcrParser.Parse(
            "<|det|>text [500, 500, 100, 100]<|/det|>gedreht\n<|det|>text [5, 5, 5, 9]<|/det|>leer\n<|det|>text<|/det|>ohne Box", 100, 100);

        Assert.AreEqual(1, result.Regions.Count, "zero-width and missing boxes are skipped");
        Assert.AreEqual(new NormalizedBox(100, 100, 500, 500), result.Regions[0].Box);
        StringAssert.Contains(result.Markdown, "ohne Box", "text without a box still ends up in the Markdown");
    }

    [TestMethod]
    public void PlainTextWithoutMarkers()
    {
        OcrPageResult result = UnlimitedOcrParser.Parse("```markdown\nNur Text\n```", 10, 10);

        Assert.AreEqual(0, result.Regions.Count);
        Assert.AreEqual("Nur Text", result.Markdown);
    }

    [TestMethod]
    public void EmptyOutput()
    {
        OcrPageResult result = UnlimitedOcrParser.Parse(null, 10, 10);

        Assert.AreEqual(0, result.Regions.Count);
        Assert.AreEqual("", result.Markdown);
    }

    [TestMethod]
    [DataRow("text", OcrRegionKind.Text)]
    [DataRow("title", OcrRegionKind.Title)]
    [DataRow("section_header", OcrRegionKind.Title)]
    [DataRow("page_header", OcrRegionKind.PageHeader)]
    [DataRow("figure", OcrRegionKind.Image)]
    [DataRow("image_caption", OcrRegionKind.Caption)]
    [DataRow("table_caption", OcrRegionKind.Caption)]
    [DataRow("equation", OcrRegionKind.Formula)]
    [DataRow("page_number", OcrRegionKind.PageFooter)]
    [DataRow("list_item", OcrRegionKind.List)]
    [DataRow("", OcrRegionKind.Text)]
    public void LabelKinds(string label, OcrRegionKind kind)
    {
        Assert.AreEqual(kind, OcrLabels.GetKind(label));
    }
}

[TestClass]
public sealed class OcrTextTests
{
    [TestMethod]
    public void HtmlTable()
    {
        List<List<string>>? rows = OcrText.ParseTable("<table><tr><th>A</th><th>B &amp; C</th></tr><tr><td>1</td><td><b>2</b></td></tr></table>");

        Assert.IsNotNull(rows);
        Assert.AreEqual(2, rows.Count);
        CollectionAssert.AreEqual(new[] { "A", "B & C" }, rows[0]);
        CollectionAssert.AreEqual(new[] { "1", "2" }, rows[1]);
    }

    [TestMethod]
    public void MarkdownTable()
    {
        List<List<string>>? rows = OcrText.ParseTable("| Pos | Betrag |\n|---|---:|\n| 1 | **5 €** |");

        Assert.IsNotNull(rows);
        Assert.AreEqual(2, rows.Count);
        CollectionAssert.AreEqual(new[] { "1", "5 €" }, rows[1]);
    }

    [TestMethod]
    public void NoTable()
    {
        Assert.IsNull(OcrText.ParseTable("Nur ein Satz."));
    }

    [TestMethod]
    public void PlainText_RemovesMarkup()
    {
        string text = OcrText.ToPlainText("## Titel\n**fett** und `code`<br>neue Zeile\n$E=mc^2$\n![Bild](x.png)");

        Assert.AreEqual("Titel\nfett und code\nneue Zeile\nE=mc^2", text);
    }

    [TestMethod]
    public void Lines_SkipEmpty()
    {
        CollectionAssert.AreEqual(new[] { "a", "b" }, OcrText.ToLines("a\n\n\n  b  \n"));
    }
}
