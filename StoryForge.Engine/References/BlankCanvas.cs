using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace StoryForge.Engine.References;

/// <summary>
/// A plain light square PNG. An image made from its look alone is painted on it, so an image-edit
/// workflow does not edit the picture it was saved with instead.
/// </summary>
internal static class BlankCanvas
{
    /// <summary>The light grey of "a plain light background".</summary>
    public const byte Shade = 236;

    public static byte[] Png(int side)
    {
        using var pixels = new MemoryStream();
        using (var packed = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            // Each row: no filter, then red, green and blue of every pixel.
            var row = new byte[1 + side * 3];
            Array.Fill(row, Shade, 1, side * 3);
            for (var y = 0; y < side; y++)
            {
                packed.Write(row);
            }
        }
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, side);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), side);
        header[8] = 8;   // bits per channel
        header[9] = 2;   // red, green, blue
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", pixels.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(MemoryStream png, string type, byte[] data)
    {
        var number = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typed));
        png.Write(number);
    }

    private static uint Crc(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }
}
