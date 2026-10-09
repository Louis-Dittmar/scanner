using System;

namespace Scanner.Core.Imaging;

/// <summary>
/// An uncompressed 32 bit image in BGRA order (as delivered by Windows' <c>BitmapDecoder</c> with
/// <c>BitmapPixelFormat.Bgra8</c>), without row padding. Alpha is ignored by all processing steps.
/// </summary>
public sealed class BgraImage
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// <see cref="Width"/> * <see cref="Height"/> * 4 bytes, row by row from the top.
    /// </summary>
    public byte[] Pixels { get; }

    public int Stride => Width * 4;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public BgraImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != (long)width * height * 4)
            throw new ArgumentException($"Expected {(long)width * height * 4} bytes for {width}x{height}, got {pixels.Length}", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public BgraImage(int width, int height) : this(width, height, new byte[checked(width * height * 4)])
    {
    }

    /// <summary>
    /// Creates an image filled with one opaque color.
    /// </summary>
    public static BgraImage Filled(int width, int height, byte r, byte g, byte b)
    {
        BgraImage image = new(width, height);
        byte[] p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            p[i] = b;
            p[i + 1] = g;
            p[i + 2] = r;
            p[i + 3] = 255;
        }
        return image;
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public BgraImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>
    /// Copies a part of the image. The rectangle is clamped to the image bounds.
    /// </summary>
    public BgraImage Crop(PixelRect rect)
    {
        PixelRect r = rect.ClampTo(Width, Height);
        if (r.IsEmpty)
            throw new ArgumentException("The crop rectangle doesn't overlap the image", nameof(rect));

        BgraImage result = new(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
        {
            Buffer.BlockCopy(Pixels, ((r.Y + y) * Width + r.X) * 4, result.Pixels, y * r.Width * 4, r.Width * 4);
        }
        return result;
    }

    /// <summary>
    /// Copies <paramref name="source"/> (same size as this image) into this image, but only inside <paramref name="rect"/>.
    /// </summary>
    public void CopyRegionFrom(BgraImage source, PixelRect rect)
    {
        if (source.Width != Width || source.Height != Height)
            throw new ArgumentException("Images must have the same size", nameof(source));

        PixelRect r = rect.ClampTo(Width, Height);
        for (int y = r.Y; y < r.Bottom; y++)
        {
            Buffer.BlockCopy(source.Pixels, (y * Width + r.X) * 4, Pixels, (y * Width + r.X) * 4, r.Width * 4);
        }
    }

    /// <summary>
    /// Perceived brightness (0..255) of a pixel, integer approximation of Rec. 601 luma.
    /// </summary>
    public int GetLuma(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Pixels[i] * 29 + Pixels[i + 1] * 150 + Pixels[i + 2] * 77) >> 8;
    }
}

/// <summary>
/// A rectangle in pixel coordinates.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public PixelRect ClampTo(int width, int height)
    {
        int left = Math.Clamp(X, 0, width);
        int top = Math.Clamp(Y, 0, height);
        int right = Math.Clamp(Right, 0, width);
        int bottom = Math.Clamp(Bottom, 0, height);
        return FromEdges(left, top, Math.Max(left, right), Math.Max(top, bottom));
    }

    public PixelRect Inflate(int amount) => FromEdges(X - amount, Y - amount, Right + amount, Bottom + amount);

    /// <summary>
    /// Converts a box normalized to 0..1000 (as used by the AI text recognition) into pixels of an image.
    /// </summary>
    public static PixelRect FromNormalized(NormalizedBox box, int imageWidth, int imageHeight)
    {
        int left = (int)Math.Floor(box.X1 / 1000.0 * imageWidth);
        int top = (int)Math.Floor(box.Y1 / 1000.0 * imageHeight);
        int right = (int)Math.Ceiling(box.X2 / 1000.0 * imageWidth);
        int bottom = (int)Math.Ceiling(box.Y2 / 1000.0 * imageHeight);
        return FromEdges(left, top, right, bottom).ClampTo(imageWidth, imageHeight);
    }
}

/// <summary>
/// A box with coordinates normalized to 0..1000 relative to the page image (the convention of the
/// DeepSeek-OCR/Unlimited-OCR model family).
/// </summary>
public readonly record struct NormalizedBox(int X1, int Y1, int X2, int Y2)
{
    public int Width => X2 - X1;
    public int Height => Y2 - Y1;
    public bool IsValid => X2 > X1 && Y2 > Y1;
}
