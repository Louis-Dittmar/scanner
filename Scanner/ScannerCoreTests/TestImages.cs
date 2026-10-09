using Scanner.Core.Imaging;

namespace ScannerCoreTests;

/// <summary>
/// Synthetic scans: yellowed paper with a shadow, dark "text" bars and a colorful picture, optionally on a dark lid.
/// </summary>
internal static class TestImages
{
    public static readonly PixelRect PictureArea = new(120, 300, 160, 120);

    /// <summary>
    /// A page of <paramref name="width"/> x <paramref name="height"/> filling the whole scan.
    /// </summary>
    public static BgraImage Page(int width = 600, int height = 800, bool withPicture = true)
    {
        BgraImage image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // yellowish paper, darker towards the right edge (book fold shadow)
                double shadow = 1.0 - 0.35 * Math.Pow(x / (double)width, 3);
                Set(image, x, y, (byte)(232 * shadow), (byte)(224 * shadow), (byte)(190 * shadow));
            }
        }

        // text lines
        for (int line = 0; line < 8; line++)
        {
            int top = 60 + line * 28;
            FillRect(image, new PixelRect(60, top, width - 120, 6), 30, 30, 35, shade: true);
        }

        if (withPicture)
        {
            for (int y = PictureArea.Y; y < PictureArea.Bottom; y++)
                for (int x = PictureArea.X; x < PictureArea.Right; x++)
                    Set(image, x, y, (byte)(40 + (x * 7) % 200), (byte)(120 + (y * 3) % 100), (byte)(200 - (x + y) % 120));
        }
        return image;
    }

    /// <summary>
    /// A page placed on a dark scanner lid.
    /// </summary>
    public static BgraImage PageOnDarkBackground(out PixelRect pageArea, int width = 900, int height = 1100)
    {
        BgraImage page = Page();
        pageArea = new PixelRect(150, 120, page.Width, page.Height);
        BgraImage scan = BgraImage.Filled(width, height, 45, 45, 50);
        for (int y = 0; y < page.Height; y++)
            Buffer.BlockCopy(page.Pixels, y * page.Stride, scan.Pixels, ((pageArea.Y + y) * width + pageArea.X) * 4, page.Stride);
        return scan;
    }

    /// <summary>
    /// Colorful noise without any paper, e.g. a photo.
    /// </summary>
    public static BgraImage Photo(int width = 400, int height = 300)
    {
        Random random = new(42);
        BgraImage image = new(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Set(image, x, y, (byte)random.Next(0, 160), (byte)random.Next(60, 200), (byte)random.Next(0, 255));
        return image;
    }

    public static void Set(BgraImage image, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * image.Width + x) * 4;
        image.Pixels[i] = b;
        image.Pixels[i + 1] = g;
        image.Pixels[i + 2] = r;
        image.Pixels[i + 3] = 255;
    }

    public static (byte R, byte G, byte B) Get(BgraImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return (image.Pixels[i + 2], image.Pixels[i + 1], image.Pixels[i]);
    }

    private static void FillRect(BgraImage image, PixelRect rect, byte r, byte g, byte b, bool shade)
    {
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                double factor = shade ? 1.0 - 0.35 * Math.Pow(x / (double)image.Width, 3) : 1;
                Set(image, x, y, (byte)(r * factor), (byte)(g * factor), (byte)(b * factor));
            }
        }
    }
}
