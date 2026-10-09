using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Scanner.Core.Imaging;

/// <summary>
/// Encodes images for embedding into PDFs and for sending them to the text recognition. The app supplies a
/// JPEG encoder based on Windows' imaging; <see cref="PngImageEncoder"/> works everywhere.
/// </summary>
public interface IImageEncoder
{
    /// <summary>
    /// Encodes a photo-like image (lossy is fine).
    /// </summary>
    byte[] EncodePhoto(BgraImage image);

    /// <summary>
    /// Encodes an image losslessly or with high quality, e.g. a page sent to the text recognition.
    /// </summary>
    byte[] EncodeLossless(BgraImage image);
}

/// <summary>
/// A small, dependency-free PNG encoder (8 bit RGB, no alpha).
/// </summary>
public sealed class PngImageEncoder : IImageEncoder
{
    public static readonly PngImageEncoder Instance = new();

    public byte[] EncodePhoto(BgraImage image) => Encode(image);

    public byte[] EncodeLossless(BgraImage image) => Encode(image);

    public static byte[] Encode(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        using MemoryStream output = new();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;      // bit depth
        header[9] = 2;      // color type RGB
        header[10] = 0;     // compression
        header[11] = 0;     // filter
        header[12] = 0;     // no interlace
        WriteChunk(output, "IHDR", header);

        using (MemoryStream compressed = new())
        {
            using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] row = new byte[1 + image.Width * 3];
                byte[] previous = new byte[row.Length];
                byte[] p = image.Pixels;
                for (int y = 0; y < image.Height; y++)
                {
                    int i = y * image.Width * 4;
                    for (int x = 0; x < image.Width; x++, i += 4)
                    {
                        row[1 + x * 3] = p[i + 2];
                        row[2 + x * 3] = p[i + 1];
                        row[3 + x * 3] = p[i];
                    }

                    // "up" filter: documents have many identical rows, which then compress to almost nothing
                    row[0] = 2;
                    for (int k = 1; k < row.Length; k++)
                    {
                        byte value = row[k];
                        row[k] = (byte)(value - previous[k]);
                        previous[k] = value;
                    }
                    zlib.Write(row);
                }
            }
            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            typeBytes[i] = (byte)type[i];
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        stream.Write(buffer);
    }

    private static readonly uint[] crcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data)
            crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
