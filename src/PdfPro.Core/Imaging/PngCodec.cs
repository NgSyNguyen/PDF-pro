using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfPro.Core.Imaging;

/// <summary>Pixels of a picture: rows top to bottom, 4 bytes per pixel in B, G, R, A order (alpha not premultiplied).</summary>
public sealed record BgraImage(int Width, int Height, byte[] Pixels)
{
    public const int BytesPerPixel = 4;

    public int Stride => Width * BytesPerPixel;
}

/// <summary>
/// A small PNG codec for the pictures PDF_Pro writes and reads itself (pages' images extracted, pictures imported into a
/// session — always 8-bit, not interlaced): decodes grey, grey + alpha, RGB, RGBA and palette images of 1–8 bits
/// (16-bit samples are reduced to 8), and encodes RGBA. Interlaced files are refused.
/// </summary>
public static class PngCodec
{
    /// <summary>Largest picture decoded (pixels), against hostile files.</summary>
    public const long MaxPixels = 100_000_000;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    /// <summary>Encodes a picture as an RGBA PNG.</summary>
    public static byte[] Encode(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(image.Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(image.Height, 1);
        if (image.Pixels.Length < (long)image.Stride * image.Height)
        {
            throw new ArgumentException("The pixels do not fill the picture.", nameof(image));
        }

        var raw = new byte[(long)(image.Stride + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * (image.Stride + 1); // filter byte 0: none
            for (var x = 0; x < image.Width; x++)
            {
                var from = (y * image.Stride) + (x * 4);
                var to = row + 1 + (x * 4);
                raw[to] = image.Pixels[from + 2];
                raw[to + 1] = image.Pixels[from + 1];
                raw[to + 2] = image.Pixels[from];
                raw[to + 3] = image.Pixels[from + 3];
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Decodes a PNG into BGRA pixels.</summary>
    /// <exception cref="InvalidDataException">Not a PNG this codec reads (damaged, interlaced, too large).</exception>
    public static BgraImage Decode(ReadOnlySpan<byte> data)
    {
        if (!IsPng(data))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int width = 0, height = 0, depth = 0, colorType = -1;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();
        var position = Signature.Length;
        while (position + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data[position..]);
            if (length < 0 || position + 12L + length > data.Length)
            {
                throw new InvalidDataException("A chunk of the PNG is cut off.");
            }

            var type = System.Text.Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            var body = data.Slice(position + 8, length);
            switch (type)
            {
                case "IHDR" when length >= 13:
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8];
                    colorType = body[9];
                    if (body[12] != 0)
                    {
                        throw new InvalidDataException("Interlaced PNG pictures are not supported.");
                    }

                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    transparency = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
            }

            position += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }

        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException("The PNG picture has no size or is too large.");
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"PNG colour type {colorType} is not supported."),
        };
        if (depth is not (1 or 2 or 4 or 8 or 16) || (depth < 8 && colorType is not (0 or 3)) || (depth == 16 && colorType == 3))
        {
            throw new InvalidDataException($"PNG bit depth {depth} is not supported for colour type {colorType}.");
        }

        var bitsPerPixel = channels * depth;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        var stride = (int)(((long)width * bitsPerPixel + 7) / 8);
        var raw = Inflate(idat.ToArray(), (long)(stride + 1) * height);
        var previous = new byte[stride];
        var current = new byte[stride];
        var pixels = new byte[(long)width * height * BgraImage.BytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            var offset = (long)y * (stride + 1);
            var filter = raw[offset];
            Array.Copy(raw, offset + 1, current, 0, stride);
            Unfilter(filter, current, previous, bytesPerPixel);
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = Pixel(current, x, colorType, depth, palette, transparency);
                var to = (((long)y * width) + x) * 4;
                pixels[to] = b;
                pixels[to + 1] = g;
                pixels[to + 2] = r;
                pixels[to + 3] = a;
            }

            (previous, current) = (current, previous);
        }

        return new BgraImage(width, height, pixels);
    }

    private static byte[] Inflate(byte[] compressed, long expected)
    {
        if (expected > int.MaxValue)
        {
            throw new InvalidDataException("The PNG picture is too large.");
        }

        var result = new byte[expected];
        using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var read = 0;
        try
        {
            while (read < result.Length)
            {
                var count = zlib.Read(result, read, result.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The PNG picture data is damaged.", ex);
        }

        return read == result.Length ? result : throw new InvalidDataException("The PNG picture data is cut off.");
    }

    private static void Unfilter(byte filter, byte[] row, byte[] above, int bpp)
    {
        for (var i = 0; i < row.Length; i++)
        {
            var left = i >= bpp ? row[i - bpp] : 0;
            var up = above[i];
            var upLeft = i >= bpp ? above[i - bpp] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + left),
                2 => (byte)(row[i] + up),
                3 => (byte)(row[i] + ((left + up) / 2)),
                4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(byte[] row, int x, int colorType, int depth, byte[]? palette, byte[]? transparency)
    {
        if (depth < 8)
        {
            var perByte = 8 / depth;
            var shift = 8 - depth - ((x % perByte) * depth);
            var value = (row[x / perByte] >> shift) & ((1 << depth) - 1);
            if (colorType == 3)
            {
                return FromPalette(value, palette, transparency);
            }

            var grey = (byte)(value * 255 / ((1 << depth) - 1));
            var transparent = transparency is { Length: >= 2 } && BinaryPrimitives.ReadUInt16BigEndian(transparency) == value;
            return (grey, grey, grey, transparent ? (byte)0 : (byte)255);
        }

        var size = depth / 8; // 1 or 2 bytes per sample; 16-bit samples keep their high byte
        byte Sample(int channel, int channels) => row[((x * channels) + channel) * size];
        return colorType switch
        {
            0 => Grey(Sample(0, 1), transparency, size == 1 ? row[x] : BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(x * 2))),
            2 => (Sample(0, 3), Sample(1, 3), Sample(2, 3), 255),
            3 => FromPalette(row[x], palette, transparency),
            4 => (Sample(0, 2), Sample(0, 2), Sample(0, 2), Sample(1, 2)),
            _ => (Sample(0, 4), Sample(1, 4), Sample(2, 4), Sample(3, 4)),
        };
    }

    private static (byte, byte, byte, byte) Grey(byte grey, byte[]? transparency, int value)
        => (grey, grey, grey, transparency is { Length: >= 2 } && BinaryPrimitives.ReadUInt16BigEndian(transparency) == value ? (byte)0 : (byte)255);

    private static (byte, byte, byte, byte) FromPalette(int index, byte[]? palette, byte[]? transparency)
    {
        if (palette is null || (index * 3) + 2 >= palette.Length)
        {
            throw new InvalidDataException("A PNG palette entry is missing.");
        }

        var alpha = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
        return (palette[index * 3], palette[(index * 3) + 1], palette[(index * 3) + 2], alpha);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(name);
        output.Write(body);
        Span<byte> sum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sum, Crc32(Crc32(uint.MaxValue, name), body) ^ uint.MaxValue);
        output.Write(sum);
    }

    /// <summary>The CRC-32 of PNG chunks (ISO 3309), continued over <paramref name="data"/>.</summary>
    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }

        return c;
    })];
}
