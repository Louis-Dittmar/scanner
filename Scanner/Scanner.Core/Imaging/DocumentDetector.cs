using System;
using System.Collections.Generic;

namespace Scanner.Core.Imaging;

/// <summary>
/// The result of looking for a sheet of paper in a scan.
/// </summary>
/// <param name="IsDocument">Whether the scan looks like a document (a large, bright, low-saturation area).</param>
/// <param name="Bounds">The area of the document in the scan, the whole image if no clear edge was found.</param>
/// <param name="PaperBrightness">Average brightness (0..255) of the paper area.</param>
/// <param name="PaperFraction">Share of the scan covered by the paper area, 0..1.</param>
/// <param name="Background">Where the scanner lid shows around the paper, if it could be told apart.</param>
public record DocumentDetectionResult(bool IsDocument, PixelRect Bounds, double PaperBrightness, double PaperFraction,
    BackgroundMask? Background = null)
{
    public bool IsCropped(int imageWidth, int imageHeight) => Bounds != new PixelRect(0, 0, imageWidth, imageHeight);

    /// <summary>
    /// Paints the remains of the scanner lid white, e.g. the corners left over by a slightly rotated sheet.
    /// </summary>
    /// <param name="image">The scan cropped to <see cref="Bounds"/> (or the full scan if it wasn't cropped).</param>
    public void ClearBackground(BgraImage image)
    {
        if (Background is not { } mask)
            return;

        int offsetX = image.Width == Bounds.Width ? Bounds.X : 0;
        int offsetY = image.Height == Bounds.Height ? Bounds.Y : 0;
        byte[] p = image.Pixels;
        for (int y = 0; y < image.Height; y++)
        {
            int cy = Math.Min((y + offsetY) / mask.CellSize, mask.Height - 1);
            for (int x = 0; x < image.Width; x++)
            {
                int cx = Math.Min((x + offsetX) / mask.CellSize, mask.Width - 1);
                if (!mask.Cells[cy * mask.Width + cx])
                    continue;
                int i = (y * image.Width + x) * 4;
                p[i] = p[i + 1] = p[i + 2] = p[i + 3] = 255;
            }
        }
    }
}

/// <summary>
/// A coarse grid over the scan, <see langword="true"/> where the scanner background shows.
/// </summary>
public record BackgroundMask(bool[] Cells, int Width, int Height, int CellSize);

/// <summary>
/// Finds the sheet of paper in a scan, e.g. a receipt on a dark scanner lid. Works on a downscaled copy:
/// the brightest large connected area with little color saturation is taken as paper.
/// </summary>
public static class DocumentDetector
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    private const int analysisSize = 256;
    private const int maxPaperChroma = 70;
    private const double minPaperFraction = 0.12;
    private const double minPaperBrightness = 140;

    /// <summary>
    /// Growing the paper area continues into neighbors at least this bright that differ by at most
    /// <see cref="maxGrowStep"/> (on the downscaled image, so a shadow is a gentle slope while a paper edge is a cliff).
    /// </summary>
    private const int minGrowBrightness = 80;
    private const int maxGrowStep = 12;

    /// <summary>
    /// If the paper reaches this close to the image edges on all sides, the whole scan is the document.
    /// </summary>
    private const double fullPageTolerance = 0.06;

    /// <summary>
    /// Paper and surrounding must differ by at least this much brightness for a crop.
    /// </summary>
    private const double minEdgeContrast = 30;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public static DocumentDetectionResult Detect(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        PixelRect full = new(0, 0, image.Width, image.Height);

        // 1. downscale by averaging blocks
        int block = Math.Max(1, (int)Math.Ceiling(Math.Max(image.Width, image.Height) / (double)analysisSize));
        int w = (image.Width + block - 1) / block;
        int h = (image.Height + block - 1) / block;
        byte[] luma = new byte[w * h];
        byte[] chroma = new byte[w * h];
        Downscale(image, block, w, h, luma, chroma);

        // 2. seeds: clearly bright, hardly colored cells (above Otsu's split between ink/lid and paper)
        int threshold = Math.Max(OtsuThreshold(luma), (int)minPaperBrightness - 20);
        bool[] seeds = new bool[w * h];
        for (int i = 0; i < seeds.Length; i++)
            seeds[i] = luma[i] > threshold && chroma[i] <= maxPaperChroma;

        // 3. grow the paper from the seeds over gradual changes only: shadows and a book fold belong to the paper,
        //    the hard edge to a dark lid doesn't; text holes don't matter for the bounding box
        (int count, int left, int top, int right, int bottom, double brightnessSum, int[] labels, int paperLabel) = LargestGrownArea(seeds, luma, chroma, w, h);
        if (count == 0)
            return new DocumentDetectionResult(false, full, 0, 0);

        double paperFraction = count / (double)(w * h);
        double paperBrightness = brightnessSum / count;
        bool isDocument = paperFraction >= minPaperFraction && paperBrightness >= minPaperBrightness;
        if (!isDocument)
            return new DocumentDetectionResult(false, full, paperBrightness, paperFraction);

        // 5. paper fills (almost) the whole scan: nothing to crop
        int toleranceX = (int)Math.Ceiling(w * fullPageTolerance);
        int toleranceY = (int)Math.Ceiling(h * fullPageTolerance);
        bool touchesAllEdges = left <= toleranceX && top <= toleranceY && right >= w - 1 - toleranceX && bottom >= h - 1 - toleranceY;
        if (touchesAllEdges)
            return new DocumentDetectionResult(true, full, paperBrightness, paperFraction);

        // 6. only crop if the surrounding is clearly darker than the paper (a white lid can't be told apart)
        double outsideBrightness = AverageOutside(luma, w, h, left, top, right, bottom);
        if (double.IsNaN(outsideBrightness) || paperBrightness - outsideBrightness < minEdgeContrast)
            return new DocumentDetectionResult(true, full, paperBrightness, paperFraction);

        // back to full resolution, one block inwards to drop the shadowy edge of the sheet
        PixelRect bounds = PixelRect.FromEdges(
            Math.Min((left + 1) * block, image.Width),
            Math.Min((top + 1) * block, image.Height),
            Math.Max(right * block, 0),
            Math.Max(bottom * block, 0)).ClampTo(image.Width, image.Height);

        if (bounds.IsEmpty || bounds.Area < full.Area * minPaperFraction)
            return new DocumentDetectionResult(true, full, paperBrightness, paperFraction);

        return new DocumentDetectionResult(true, bounds, paperBrightness, paperFraction,
            new BackgroundMask(FindBackground(labels, paperLabel, w, h), w, h, block));
    }

    private static void Downscale(BgraImage image, int block, int w, int h, byte[] luma, byte[] chroma)
    {
        byte[] p = image.Pixels;
        for (int by = 0; by < h; by++)
        {
            int y0 = by * block;
            int y1 = Math.Min(y0 + block, image.Height);
            for (int bx = 0; bx < w; bx++)
            {
                int x0 = bx * block;
                int x1 = Math.Min(x0 + block, image.Width);
                long sumB = 0, sumG = 0, sumR = 0;
                int n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = (y * image.Width + x0) * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        sumB += p[i];
                        sumG += p[i + 1];
                        sumR += p[i + 2];
                        n++;
                    }
                }
                int b = (int)(sumB / n), g = (int)(sumG / n), r = (int)(sumR / n);
                luma[by * w + bx] = (byte)((b * 29 + g * 150 + r * 77) >> 8);
                chroma[by * w + bx] = (byte)(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)));
            }
        }
    }

    internal static int OtsuThreshold(byte[] values)
    {
        Span<int> histogram = stackalloc int[256];
        foreach (byte v in values)
            histogram[v]++;

        long total = values.Length;
        double sumAll = 0;
        for (int i = 0; i < 256; i++)
            sumAll += i * (double)histogram[i];

        double sumBackground = 0;
        long weightBackground = 0;
        double bestVariance = -1;
        int best = 127;
        for (int t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
                continue;
            long weightForeground = total - weightBackground;
            if (weightForeground == 0)
                break;

            sumBackground += t * (double)histogram[t];
            double meanBackground = sumBackground / weightBackground;
            double meanForeground = (sumAll - sumBackground) / weightForeground;
            double variance = weightBackground * (double)weightForeground * (meanBackground - meanForeground) * (meanBackground - meanForeground);
            if (variance > bestVariance)
            {
                bestVariance = variance;
                best = t;
            }
        }

        // an (almost) uniform image has no meaningful split, treat everything as one class
        int min = 255, max = 0;
        foreach (byte v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (max - min < 24)
            return min - 1;

        return best;
    }

    private static (int Count, int Left, int Top, int Right, int Bottom, double BrightnessSum, int[] Labels, int Label) LargestGrownArea(bool[] seeds, byte[] luma, byte[] chroma, int w, int h)
    {
        int[] labels = new int[seeds.Length];
        Queue<int> queue = new();
        (int Count, int Left, int Top, int Right, int Bottom, double BrightnessSum) best = default;
        int bestLabel = 0;
        int label = 0;

        for (int start = 0; start < seeds.Length; start++)
        {
            if (!seeds[start] || labels[start] != 0)
                continue;

            label++;
            int count = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            double brightness = 0;
            labels[start] = label;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % w, y = i / w;
                count++;
                brightness += luma[i];
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;

                if (x > 0) Visit(i, i - 1);
                if (x < w - 1) Visit(i, i + 1);
                if (y > 0) Visit(i, i - w);
                if (y < h - 1) Visit(i, i + w);
            }

            if (count > best.Count)
            {
                best = (count, left, top, right, bottom, brightness);
                bestLabel = label;
            }
        }
        return (best.Count, best.Left, best.Top, best.Right, best.Bottom, best.BrightnessSum, labels, bestLabel);

        void Visit(int from, int to)
        {
            if (labels[to] != 0)
                return;
            bool isPaper = seeds[to]
                || (luma[to] >= minGrowBrightness && chroma[to] <= maxPaperChroma && Math.Abs(luma[to] - luma[from]) <= maxGrowStep);
            if (isPaper)
            {
                labels[to] = label;
                queue.Enqueue(to);
            }
        }
    }

    /// <summary>
    /// Everything reachable from the image border without crossing the paper is background; text and pictures
    /// on the paper are enclosed by it and stay.
    /// </summary>
    private static bool[] FindBackground(int[] labels, int paperLabel, int w, int h)
    {
        bool[] background = new bool[labels.Length];
        Queue<int> queue = new();
        void Add(int i)
        {
            if (!background[i] && labels[i] != paperLabel)
            {
                background[i] = true;
                queue.Enqueue(i);
            }
        }

        for (int x = 0; x < w; x++)
        {
            Add(x);
            Add((h - 1) * w + x);
        }
        for (int y = 0; y < h; y++)
        {
            Add(y * w);
            Add(y * w + w - 1);
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, y = i / w;
            if (x > 0) Add(i - 1);
            if (x < w - 1) Add(i + 1);
            if (y > 0) Add(i - w);
            if (y < h - 1) Add(i + w);
        }
        return background;
    }

    private static double AverageOutside(byte[] luma, int w, int h, int left, int top, int right, int bottom)
    {
        double sum = 0;
        long n = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (x >= left && x <= right && y >= top && y <= bottom)
                    continue;
                sum += luma[y * w + x];
                n++;
            }
        }
        return n == 0 ? double.NaN : sum / n;
    }
}
