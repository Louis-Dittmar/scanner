using Scanner.Core.Imaging;

namespace ScannerCoreTests;

[TestClass]
public sealed class DocumentDetectorTests
{
    [TestMethod]
    public void FullPageScan_IsDocumentWithoutCrop()
    {
        BgraImage page = TestImages.Page();

        DocumentDetectionResult result = DocumentDetector.Detect(page);

        Assert.IsTrue(result.IsDocument);
        Assert.AreEqual(new PixelRect(0, 0, page.Width, page.Height), result.Bounds);
        Assert.IsFalse(result.IsCropped(page.Width, page.Height));
    }

    [TestMethod]
    public void PageOnDarkLid_IsCroppedToThePaper()
    {
        BgraImage scan = TestImages.PageOnDarkBackground(out PixelRect pageArea);

        DocumentDetectionResult result = DocumentDetector.Detect(scan);

        Assert.IsTrue(result.IsDocument);
        Assert.IsTrue(result.IsCropped(scan.Width, scan.Height));
        // the detection works on blocks of a few pixels and stays slightly inside the paper
        const int tolerance = 12;
        Assert.IsTrue(Math.Abs(result.Bounds.X - pageArea.X) <= tolerance, $"left {result.Bounds.X} vs {pageArea.X}");
        Assert.IsTrue(Math.Abs(result.Bounds.Y - pageArea.Y) <= tolerance, $"top {result.Bounds.Y} vs {pageArea.Y}");
        Assert.IsTrue(Math.Abs(result.Bounds.Right - pageArea.Right) <= tolerance, $"right {result.Bounds.Right} vs {pageArea.Right}");
        Assert.IsTrue(Math.Abs(result.Bounds.Bottom - pageArea.Bottom) <= tolerance, $"bottom {result.Bounds.Bottom} vs {pageArea.Bottom}");
        Assert.IsTrue(result.Bounds.X >= pageArea.X && result.Bounds.Right <= pageArea.Right, "must not include the lid");
    }

    [TestMethod]
    public void PageOnDarkLid_LeftoverLidBecomesWhite()
    {
        BgraImage scan = TestImages.PageOnDarkBackground(out PixelRect pageArea);
        DocumentDetectionResult result = DocumentDetector.Detect(scan);
        Assert.IsNotNull(result.Background);

        // a crop that is deliberately too large still contains some lid
        BgraImage full = scan.Clone();
        result.ClearBackground(full);

        Assert.AreEqual(((byte)255, (byte)255, (byte)255), TestImages.Get(full, 20, 20), "lid");
        Assert.AreEqual(((byte)255, (byte)255, (byte)255), TestImages.Get(full, pageArea.Right + 30, pageArea.Bottom + 30), "lid");
        Assert.AreEqual(TestImages.Get(scan, pageArea.X + 100, pageArea.Y + 62), TestImages.Get(full, pageArea.X + 100, pageArea.Y + 62), "text on the paper stays");
        Assert.AreEqual(TestImages.Get(scan, pageArea.X + 150, pageArea.Y + 350), TestImages.Get(full, pageArea.X + 150, pageArea.Y + 350), "picture on the paper stays");
    }

    [TestMethod]
    public void Photo_IsNotADocument()
    {
        BgraImage photo = TestImages.Photo();

        DocumentDetectionResult result = DocumentDetector.Detect(photo);

        Assert.IsFalse(result.IsDocument);
        Assert.AreEqual(new PixelRect(0, 0, photo.Width, photo.Height), result.Bounds);
    }

    [TestMethod]
    public void BlankWhitePage_IsDocument()
    {
        BgraImage blank = BgraImage.Filled(300, 400, 250, 250, 250);

        DocumentDetectionResult result = DocumentDetector.Detect(blank);

        Assert.IsTrue(result.IsDocument);
        Assert.IsFalse(result.IsCropped(blank.Width, blank.Height));
    }

    [TestMethod]
    public void BlackImage_IsNotADocument()
    {
        Assert.IsFalse(DocumentDetector.Detect(BgraImage.Filled(200, 200, 5, 5, 5)).IsDocument);
    }

    [TestMethod]
    public void TinyImage_DoesNotThrow()
    {
        DocumentDetector.Detect(BgraImage.Filled(1, 1, 255, 255, 255));
        DocumentDetector.Detect(BgraImage.Filled(3, 700, 255, 255, 255));
    }
}

[TestClass]
public sealed class WhiteCorrectionTests
{
    [TestMethod]
    public void YellowedShadowedPaper_BecomesWhite()
    {
        BgraImage page = TestImages.Page();

        BgraImage corrected = WhiteCorrection.Apply(page);

        // paper in the middle, at the top and in the shadow on the right
        foreach ((int x, int y) in new[] { (300, 30), (30, 700), (580, 600), (590, 30), (450, 520) })
        {
            (byte r, byte g, byte b) = TestImages.Get(corrected, x, y);
            Assert.IsTrue(r >= 245 && g >= 245 && b >= 245, $"paper at {x},{y} is ({r},{g},{b})");
        }
    }

    [TestMethod]
    public void Text_StaysDark()
    {
        BgraImage page = TestImages.Page();

        BgraImage corrected = WhiteCorrection.Apply(page);

        foreach ((int x, int y) in new[] { (100, 62), (300, 90), (520, 146) })
        {
            (byte r, byte g, byte b) = TestImages.Get(corrected, x, y);
            Assert.IsTrue(r <= 90 && g <= 90 && b <= 90, $"text at {x},{y} is ({r},{g},{b})");
        }
    }

    [TestMethod]
    public void ProtectedArea_IsOnlyWhiteBalanced()
    {
        BgraImage page = TestImages.Page();

        BgraImage corrected = WhiteCorrection.Apply(page, protectedAreas: [TestImages.PictureArea]);
        BgraImage unprotected = WhiteCorrection.Apply(page);

        int brighterThanBalanced = 0, samples = 0;
        for (int y = TestImages.PictureArea.Y; y < TestImages.PictureArea.Bottom; y += 7)
        {
            for (int x = TestImages.PictureArea.X; x < TestImages.PictureArea.Right; x += 7)
            {
                (byte r, byte g, byte b) original = TestImages.Get(page, x, y);
                (byte r, byte g, byte b) balanced = TestImages.Get(corrected, x, y);
                (byte r, byte g, byte b) bleached = TestImages.Get(unprotected, x, y);

                // only brightened by removing the paper tint (blue is weakest in yellowed paper, so it gains most)
                Assert.IsTrue(balanced.r >= original.r - 1 && balanced.r - original.r <= 40, $"red at {x},{y}: {original.r} -> {balanced.r}");
                Assert.IsTrue(balanced.b >= original.b - 1 && balanced.b - original.b <= 90, $"blue at {x},{y}: {original.b} -> {balanced.b}");
                if (bleached.r + bleached.g + bleached.b > balanced.r + balanced.g + balanced.b)
                    brighterThanBalanced++;
                samples++;
            }
        }
        Assert.IsTrue(brighterThanBalanced > samples / 2, "the full correction would have bleached the picture");
    }

    [TestMethod]
    public void ZeroStrength_KeepsTheImage()
    {
        BgraImage page = TestImages.Page();

        BgraImage corrected = WhiteCorrection.Apply(page, new WhiteCorrectionOptions { Strength = 0 });

        CollectionAssert.AreEqual(page.Pixels, corrected.Pixels);
    }

    [TestMethod]
    public void Input_IsNotModified()
    {
        BgraImage page = TestImages.Page();
        byte[] before = (byte[])page.Pixels.Clone();

        WhiteCorrection.Apply(page);

        CollectionAssert.AreEqual(before, page.Pixels);
    }

    [TestMethod]
    public void SmallAndOddSizes_DoNotThrow()
    {
        WhiteCorrection.Apply(BgraImage.Filled(1, 1, 200, 200, 200));
        WhiteCorrection.Apply(BgraImage.Filled(7, 333, 200, 190, 180));
        WhiteCorrection.Apply(TestImages.Photo(97, 31));
    }
}

[TestClass]
public sealed class ImageHelperTests
{
    [TestMethod]
    public void Crop_CopiesTheArea()
    {
        BgraImage page = TestImages.Page();

        BgraImage crop = page.Crop(TestImages.PictureArea);

        Assert.AreEqual(TestImages.PictureArea.Width, crop.Width);
        Assert.AreEqual(TestImages.PictureArea.Height, crop.Height);
        Assert.AreEqual(TestImages.Get(page, TestImages.PictureArea.X + 5, TestImages.PictureArea.Y + 9), TestImages.Get(crop, 5, 9));
    }

    [TestMethod]
    public void Crop_IsClampedToTheImage()
    {
        BgraImage image = BgraImage.Filled(10, 10, 1, 2, 3);

        BgraImage crop = image.Crop(new PixelRect(5, 5, 100, 100));

        Assert.AreEqual(5, crop.Width);
        Assert.AreEqual(5, crop.Height);
    }

    [TestMethod]
    public void NormalizedBox_ToPixels()
    {
        PixelRect rect = PixelRect.FromNormalized(new NormalizedBox(100, 250, 500, 1000), 2000, 4000);

        Assert.AreEqual(new PixelRect(200, 1000, 800, 3000), rect);
    }

    [TestMethod]
    public void FitWithin_DownscalesLongestSide()
    {
        BgraImage image = BgraImage.Filled(3000, 1500, 10, 20, 30);

        BgraImage small = ImageResize.FitWithin(image, 1000);

        Assert.AreEqual(1000, small.Width);
        Assert.AreEqual(500, small.Height);
        Assert.AreEqual(((byte)10, (byte)20, (byte)30), TestImages.Get(small, 500, 250));
        Assert.AreSame(image, ImageResize.FitWithin(image, 5000));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(-1)]
    public void Rotate_MovesPixelsClockwise(int quarterTurns)
    {
        BgraImage image = BgraImage.Filled(3, 2, 0, 0, 0);
        TestImages.Set(image, 0, 0, 255, 0, 0);  // top left red
        TestImages.Set(image, 2, 1, 0, 0, 255);  // bottom right blue

        BgraImage rotated = ImageResize.Rotate(image, quarterTurns);

        int turns = ((quarterTurns % 4) + 4) % 4;
        (int x, int y) red = turns switch { 1 => (1, 0), 2 => (2, 1), _ => (0, 2) };
        (int x, int y) blue = turns switch { 1 => (0, 2), 2 => (0, 0), _ => (1, 0) };
        Assert.AreEqual(turns % 2 == 1 ? 2 : 3, rotated.Width);
        Assert.AreEqual(((byte)255, (byte)0, (byte)0), TestImages.Get(rotated, red.x, red.y));
        Assert.AreEqual(((byte)0, (byte)0, (byte)255), TestImages.Get(rotated, blue.x, blue.y));
    }

    [TestMethod]
    public void Png_HasValidSignatureAndChunks()
    {
        byte[] png = PngImageEncoder.Encode(TestImages.Page(64, 48, withPicture: false));

        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.AreEqual("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.AreEqual("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }
}
