using Microsoft.UI.Xaml.Media.Imaging;
using Scanner.Core.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Scanner.Services.Pipeline;

/// <summary>
/// A decoded page image and its resolution.
/// </summary>
public record DecodedPage(BgraImage Image, double Dpi);

/// <summary>
/// Bridges Scanner.Core's <see cref="BgraImage"/> and Windows' imaging APIs.
/// </summary>
internal static class WinRtImaging
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// PDF pages are rendered at this resolution; enough for recognition and print, without exhausting memory.
    /// </summary>
    public const double PdfRenderDpi = 200;

    /// <summary>
    /// Larger images are downscaled before processing (a 600 dpi A4 scan would need about 140 MB per copy).
    /// </summary>
    public const int MaxImageSide = 4000;

    private const double defaultDpi = 300;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    #region Decoding
    /// <summary>
    /// Decodes the pages of a saved project file (PDF or image).
    /// </summary>
    public static async Task<List<DecodedPage>> DecodeFileAsync(string fileName, byte[] content)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() == ".pdf"
            ? await RenderPdfAsync(content)
            : [await DecodeImageAsync(content)];
    }

    /// <summary>
    /// Decodes an image, applies its EXIF orientation and <paramref name="rotation"/>, and limits its size to
    /// <see cref="MaxImageSide"/>. Rotation and scaling are done in managed code, where their order is unambiguous.
    /// </summary>
    public static async Task<DecodedPage> DecodeImageAsync(byte[] content, BitmapRotation rotation = BitmapRotation.None)
    {
        using InMemoryRandomAccessStream stream = await ToStreamAsync(content);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

        // scans carry their resolution; screenshots and the like report 96, which is treated as unknown
        double dpi = decoder.DpiX > 96.5 ? decoder.DpiX : defaultDpi;

        PixelDataProvider pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        byte[] data = pixels.DetachPixelData();

        int width = (int)decoder.OrientedPixelWidth;
        int height = (int)decoder.OrientedPixelHeight;
        if (data.Length != (long)width * height * 4)
            throw new InvalidDataException("Unexpected pixel data size");

        BgraImage image = await Task.Run(() =>
        {
            BgraImage decoded = new(width, height, data);
            int quarterTurns = rotation switch
            {
                BitmapRotation.Clockwise90Degrees => 1,
                BitmapRotation.Clockwise180Degrees => 2,
                BitmapRotation.Clockwise270Degrees => 3,
                _ => 0,
            };
            decoded = ImageResize.Rotate(decoded, quarterTurns);
            return ImageResize.FitWithin(decoded, MaxImageSide);
        });

        dpi *= image.Width / (double)(rotation is BitmapRotation.Clockwise90Degrees or BitmapRotation.Clockwise270Degrees ? height : width);
        return new DecodedPage(image, dpi);
    }

    private static async Task<List<DecodedPage>> RenderPdfAsync(byte[] pdf)
    {
        using InMemoryRandomAccessStream source = await ToStreamAsync(pdf);
        Windows.Data.Pdf.PdfDocument document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(source);

        List<DecodedPage> pages = [];
        for (uint i = 0; i < document.PageCount; i++)
        {
            using Windows.Data.Pdf.PdfPage page = document.GetPage(i);

            // Size is in device independent pixels (1/96 inch)
            Windows.Foundation.Size size = page.Size;
            Windows.Data.Pdf.PdfPageRenderOptions options = new()
            {
                DestinationWidth = (uint)Math.Round(size.Width * PdfRenderDpi / 96),
                DestinationHeight = (uint)Math.Round(size.Height * PdfRenderDpi / 96),
                BitmapEncoderId = BitmapEncoder.PngEncoderId,
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };

            using InMemoryRandomAccessStream target = new();
            await page.RenderToStreamAsync(target, options);
            DecodedPage decoded = await DecodeImageAsync(await ReadAllBytesAsync(target));
            pages.Add(decoded with { Dpi = PdfRenderDpi * decoded.Image.Width / Math.Max(1, options.DestinationWidth) });
        }
        return pages;
    }
    #endregion

    #region Encoding
    public static async Task<byte[]> EncodeAsync(BgraImage image, Guid encoderId, double dpi = 96, double? jpegQuality = null)
    {
        using InMemoryRandomAccessStream stream = new();
        BitmapEncoder encoder;
        if (jpegQuality is double quality)
        {
            BitmapPropertySet properties = new()
            {
                { "ImageQuality", new BitmapTypedValue((float)quality, Windows.Foundation.PropertyType.Single) },
            };
            encoder = await BitmapEncoder.CreateAsync(encoderId, stream, properties);
        }
        else
        {
            encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        }

        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)image.Width, (uint)image.Height, dpi, dpi, image.Pixels);
        await encoder.FlushAsync();
        return await ReadAllBytesAsync(stream);
    }
    #endregion

    #region UI
    /// <summary>
    /// Creates a bitmap for display, downscaled to <paramref name="maxSide"/>. Call on the UI thread.
    /// </summary>
    public static WriteableBitmap ToBitmap(BgraImage image, int maxSide = 1600)
    {
        BgraImage display = ImageResize.FitWithin(image, maxSide);
        WriteableBitmap bitmap = new(display.Width, display.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(display.Pixels, 0, display.Pixels.Length);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// Downscales off the UI thread, so <see cref="ToBitmap"/> only has to copy.
    /// </summary>
    public static Task<BgraImage> PrepareForDisplayAsync(BgraImage image, int maxSide = 1600) =>
        Task.Run(() => ImageResize.FitWithin(image, maxSide));
    #endregion

    #region Streams
    private static async Task<InMemoryRandomAccessStream> ToStreamAsync(byte[] content)
    {
        InMemoryRandomAccessStream stream = new();
        using (DataWriter writer = new(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(content);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        stream.Seek(0);
        return stream;
    }

    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        using Stream netStream = stream.CloneStream().AsStreamForRead();
        using MemoryStream memoryStream = new();
        await netStream.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
    }
    #endregion
}

/// <summary>
/// JPEG for pictures and scans in the PDF, PNG for the recognition. Called from background threads only.
/// </summary>
internal sealed class WinRtImageEncoder : IImageEncoder
{
    private const double jpegQuality = 0.88;

    public byte[] EncodePhoto(BgraImage image) =>
        Task.Run(() => WinRtImaging.EncodeAsync(image, BitmapEncoder.JpegEncoderId, jpegQuality: jpegQuality)).GetAwaiter().GetResult();

    public byte[] EncodeLossless(BgraImage image) =>
        Task.Run(() => WinRtImaging.EncodeAsync(image, BitmapEncoder.PngEncoderId)).GetAwaiter().GetResult();
}
