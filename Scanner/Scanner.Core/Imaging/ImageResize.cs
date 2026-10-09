using System;

namespace Scanner.Core.Imaging;

public static class ImageResize
{
    /// <summary>
    /// Downscales <paramref name="image"/> so its longest side is at most <paramref name="maxSide"/> pixels
    /// (area averaging). Returns the image itself if it's small enough.
    /// </summary>
    public static BgraImage FitWithin(BgraImage image, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(image);
        int longest = Math.Max(image.Width, image.Height);
        if (maxSide <= 0 || longest <= maxSide)
            return image;

        double scale = maxSide / (double)longest;
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return Resize(image, width, height);
    }

    /// <summary>
    /// Downscales by averaging all source pixels that fall into a target pixel.
    /// </summary>
    public static BgraImage Resize(BgraImage image, int width, int height)
    {
        BgraImage result = new(width, height);
        byte[] s = image.Pixels;
        byte[] d = result.Pixels;
        double scaleX = image.Width / (double)width;
        double scaleY = image.Height / (double)height;

        for (int y = 0; y < height; y++)
        {
            int y0 = (int)(y * scaleY);
            int y1 = Math.Max(y0 + 1, Math.Min(image.Height, (int)((y + 1) * scaleY)));
            for (int x = 0; x < width; x++)
            {
                int x0 = (int)(x * scaleX);
                int x1 = Math.Max(x0 + 1, Math.Min(image.Width, (int)((x + 1) * scaleX)));
                long b = 0, g = 0, r = 0;
                int n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int i = (sy * image.Width + x0) * 4;
                    for (int sx = x0; sx < x1; sx++, i += 4)
                    {
                        b += s[i];
                        g += s[i + 1];
                        r += s[i + 2];
                        n++;
                    }
                }

                int o = (y * width + x) * 4;
                d[o] = (byte)(b / n);
                d[o + 1] = (byte)(g / n);
                d[o + 2] = (byte)(r / n);
                d[o + 3] = 255;
            }
        }
        return result;
    }
}
