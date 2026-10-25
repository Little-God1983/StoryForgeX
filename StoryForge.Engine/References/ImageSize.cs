using System.Buffers.Binary;

namespace StoryForge.Engine.References;

/// <summary>
/// A picture's size and kind, from the first bytes of its file: PNG, JPEG or WebP, the ones a
/// reference is made from. Enough to tell "too small" without loading the picture.
/// </summary>
internal static class ImageSize
{
    /// <summary>Width and height; null when it is none of the three, or the header is cut short.</summary>
    public static (int Width, int Height)? Of(ReadOnlySpan<byte> file) => Read(file) is { } found ? (found.Width, found.Height) : null;

    /// <summary>".png", ".jpg" or ".webp" for a picture of that kind; null for anything else.</summary>
    public static string? Ending(ReadOnlySpan<byte> file) => Read(file)?.Ending;

    private static (int Width, int Height, string Ending)? Read(ReadOnlySpan<byte> file)
    {
        if (file.Length >= 24 && file[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]) && file[12..16].SequenceEqual("IHDR"u8))
        {
            return (BinaryPrimitives.ReadInt32BigEndian(file[16..]), BinaryPrimitives.ReadInt32BigEndian(file[20..]), ".png");
        }
        if (file.Length >= 4 && file[0] == 0xFF && file[1] == 0xD8)
        {
            return Jpeg(file) is { } jpeg ? (jpeg.Width, jpeg.Height, ".jpg") : null;
        }
        if (file.Length >= 30 && file[..4].SequenceEqual("RIFF"u8) && file[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP(file) is { } webp ? (webp.Width, webp.Height, ".webp") : null;
        }
        return null;
    }

    /// <summary>The frame header (SOF0–SOF15, not DHT, JPG or DAC) carries the size.</summary>
    private static (int Width, int Height)? Jpeg(ReadOnlySpan<byte> file)
    {
        var at = 2;
        while (at + 9 <= file.Length)
        {
            if (file[at] != 0xFF)
            {
                return null;
            }
            var marker = file[at + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(file[(at + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(file[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(file[(at + 5)..]));
            }
            at += 2 + length;
        }
        return null;
    }

    private static (int Width, int Height)? WebP(ReadOnlySpan<byte> file)
    {
        var chunk = file[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
        {
            return (1 + (file[24] | file[25] << 8 | file[26] << 16), 1 + (file[27] | file[28] << 8 | file[29] << 16));
        }
        if (chunk.SequenceEqual("VP8 "u8))
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(file[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(file[28..]) & 0x3FFF);
        }
        if (chunk.SequenceEqual("VP8L"u8) && file.Length >= 25)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(file[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        return null;
    }
}
