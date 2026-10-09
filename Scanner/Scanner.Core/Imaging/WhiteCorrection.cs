using System;
using System.Collections.Generic;

namespace Scanner.Core.Imaging;

public record WhiteCorrectionOptions
{
    /// <summary>
    /// 0 keeps the original, 1 applies the full correction.
    /// </summary>
    public double Strength { get; init; } = 1.0;

    /// <summary>
    /// After evening out the paper, everything at least this bright (0..255) becomes pure white.
    /// </summary>
    public int WhitePoint { get; init; } = 225;

    /// <summary>
    /// Darkens text slightly so it stays crisp after brightening, 1 = linear.
    /// </summary>
    public double Gamma { get; init; } = 1.15;
}

/// <summary>
/// Makes the paper of a scanned document white: shadows, yellowed paper and uneven lighting are evened out by
/// estimating the paper color in a coarse grid and dividing every pixel by it. Text and colors are kept, only
/// the background changes. Photos inside the document can be protected so they stay untouched.
/// </summary>
public static class WhiteCorrection
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private const int gridCells = 48;
    private const double paperPercentile = 0.9;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Returns a corrected copy of <paramref name="image"/>.
    /// </summary>
    /// <param name="protectedAreas">Areas (e.g. photos) that only get a gentle white balance: the paper tint is
    /// removed, but contrast and colors are kept instead of being pushed towards white.</param>
    public static BgraImage Apply(BgraImage image, WhiteCorrectionOptions? options = null, IEnumerable<PixelRect>? protectedAreas = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new WhiteCorrectionOptions();
        double strength = Math.Clamp(options.Strength, 0, 1);

        BgraImage result = image.Clone();
        if (strength <= 0)
            return result;

        int cell = Math.Max(8, (int)Math.Round(Math.Max(image.Width, image.Height) / (double)gridCells));
        float[] background = EstimateBackground(image, cell, out int gw, out int gh);
        Normalize(image, result, background, gw, gh, cell, options.WhitePoint, options.Gamma, strength);

        if (protectedAreas != null)
        {
            // plain division by the paper color: white balance without levels or gamma
            BgraImage balanced = image.Clone();
            Normalize(image, balanced, background, gw, gh, cell, 255, 1.0, strength);
            foreach (PixelRect area in protectedAreas)
                result.CopyRegionFrom(balanced, area);
        }
        return result;
    }

    /// <summary>
    /// Estimates the paper color (B, G, R) per grid cell: the average of the brightest pixels in the cell, then
    /// spread from bright cells into dark ones (cells full of ink or photos) and smoothed.
    /// </summary>
    private static float[] EstimateBackground(BgraImage image, int cell, out int gw, out int gh)
    {
        gw = (image.Width + cell - 1) / cell;
        gh = (image.Height + cell - 1) / cell;
        float[] bg = new float[gw * gh * 3];
        float[] brightness = new float[gw * gh];
        byte[] p = image.Pixels;
        int[] histogram = new int[256];

        for (int cy = 0; cy < gh; cy++)
        {
            int y0 = cy * cell, y1 = Math.Min(y0 + cell, image.Height);
            for (int cx = 0; cx < gw; cx++)
            {
                int x0 = cx * cell, x1 = Math.Min(x0 + cell, image.Width);
                Array.Clear(histogram);
                int n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = (y * image.Width + x0) * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        histogram[(p[i] * 29 + p[i + 1] * 150 + p[i + 2] * 77) >> 8]++;
                        n++;
                    }
                }

                // brightness that paperPercentile of the pixels don't exceed
                int target = (int)(n * paperPercentile), seen = 0, limit = 255;
                for (int v = 0; v < 256; v++)
                {
                    seen += histogram[v];
                    if (seen > target)
                    {
                        limit = v;
                        break;
                    }
                }

                double sb = 0, sg = 0, sr = 0;
                int m = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = (y * image.Width + x0) * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        if (((p[i] * 29 + p[i + 1] * 150 + p[i + 2] * 77) >> 8) < limit)
                            continue;
                        sb += p[i];
                        sg += p[i + 1];
                        sr += p[i + 2];
                        m++;
                    }
                }

                int c = cy * gw + cx;
                bg[c * 3] = (float)(sb / m);
                bg[c * 3 + 1] = (float)(sg / m);
                bg[c * 3 + 2] = (float)(sr / m);
                brightness[c] = (float)((bg[c * 3] * 29 + bg[c * 3 + 1] * 150 + bg[c * 3 + 2] * 77) / 256);
            }
        }

        // cells inside large dark areas don't show any paper: take the brightest neighbor (max filter), twice
        for (int pass = 0; pass < 2; pass++)
            MaxFilter(bg, brightness, gw, gh);

        // smooth transitions
        for (int pass = 0; pass < 2; pass++)
            BoxBlur(bg, gw, gh);

        return bg;
    }

    private static void MaxFilter(float[] bg, float[] brightness, int gw, int gh)
    {
        float[] sourceBg = (float[])bg.Clone();
        float[] sourceBrightness = (float[])brightness.Clone();
        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++)
            {
                int best = y * gw + x;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= gh)
                        continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= gw)
                            continue;
                        int j = ny * gw + nx;
                        if (sourceBrightness[j] > sourceBrightness[best])
                            best = j;
                    }
                }

                int c = y * gw + x;
                bg[c * 3] = sourceBg[best * 3];
                bg[c * 3 + 1] = sourceBg[best * 3 + 1];
                bg[c * 3 + 2] = sourceBg[best * 3 + 2];
                brightness[c] = sourceBrightness[best];
            }
        }
    }

    private static void BoxBlur(float[] bg, int gw, int gh)
    {
        float[] source = (float[])bg.Clone();
        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++)
            {
                float b = 0, g = 0, r = 0;
                int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = Math.Clamp(y + dy, 0, gh - 1);
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int j = ny * gw + Math.Clamp(x + dx, 0, gw - 1);
                        b += source[j * 3];
                        g += source[j * 3 + 1];
                        r += source[j * 3 + 2];
                        n++;
                    }
                }

                int c = y * gw + x;
                bg[c * 3] = b / n;
                bg[c * 3 + 1] = g / n;
                bg[c * 3 + 2] = r / n;
            }
        }
    }

    private static void Normalize(BgraImage source, BgraImage target, float[] bg, int gw, int gh, int cell,
        int whitePoint, double gamma, double strength)
    {
        // lookup table for levels + gamma on the normalized value (0..255 where 255 = paper)
        whitePoint = Math.Clamp(whitePoint, 64, 255);
        byte[] levels = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            double t = Math.Min(1.0, v / (double)whitePoint);
            levels[v] = (byte)Math.Round(255 * Math.Pow(t, gamma));
        }

        byte[] s = source.Pixels;
        byte[] d = target.Pixels;
        int width = source.Width;
        int strength256 = (int)Math.Round(strength * 256);
        float half = cell / 2f;

        for (int y = 0; y < source.Height; y++)
        {
            // bilinear interpolation between cell centers
            float gy = Math.Clamp((y - half) / cell, 0, gh - 1);
            int y0 = (int)gy, y1 = Math.Min(y0 + 1, gh - 1);
            float fy = gy - y0;

            for (int x = 0; x < width; x++)
            {
                float gx = Math.Clamp((x - half) / cell, 0, gw - 1);
                int x0 = (int)gx, x1 = Math.Min(x0 + 1, gw - 1);
                float fx = gx - x0;

                int i = (y * width + x) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    float top = bg[(y0 * gw + x0) * 3 + ch] * (1 - fx) + bg[(y0 * gw + x1) * 3 + ch] * fx;
                    float bottom = bg[(y1 * gw + x0) * 3 + ch] * (1 - fx) + bg[(y1 * gw + x1) * 3 + ch] * fx;
                    float paper = Math.Max(top * (1 - fy) + bottom * fy, 16f);

                    int normalized = (int)Math.Min(255f, s[i + ch] * 255f / paper);
                    int corrected = levels[normalized];
                    d[i + ch] = (byte)(s[i + ch] + (((corrected - s[i + ch]) * strength256) >> 8));
                }
                d[i + 3] = 255;
            }
        }
    }
}
