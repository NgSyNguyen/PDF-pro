using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfPro.Core.Imaging;

/// <summary>The colours of TIFF samples as they go into a PDF.</summary>
public enum TiffColorModel
{
    Gray,
    Rgb,
    Cmyk,

    /// <summary>Indexes into <see cref="TiffSamples.Palette"/> (RGB triples).</summary>
    Palette,
}

/// <summary>A page of a TIFF file: its size, resolution (null when the file gives none) and orientation (1–8, as EXIF).</summary>
public sealed record TiffImage(int Width, int Height, double? DpiX, double? DpiY, int Orientation, TiffImageData Data);

/// <summary>What a TIFF page holds, in a form a PDF image can take as it is.</summary>
public abstract record TiffImageData;

/// <summary>
/// Samples, chunky and with rows starting on a byte, as a PDF image wants them: <paramref name="Bits"/> per component
/// (1, 2, 4 or 8); <paramref name="InvertGray"/> when 0 is white (PDF: /Decode [1 0]); an 8-bit alpha channel apart.
/// </summary>
public sealed record TiffSamples(byte[] Data, int Bits, TiffColorModel Model, byte[]? Palette, bool InvertGray, byte[]? Alpha) : TiffImageData;

/// <summary>
/// CCITT Group 4 data as coded, one band of rows per strip (each strip is coded on its own, from a white line).
/// <paramref name="Invert"/> when the page's black is its coded white (BlackIsZero).
/// </summary>
public sealed record TiffFax(IReadOnlyList<TiffStrip> Strips, bool Invert) : TiffImageData;

/// <summary>JPEG data, one whole JPEG file per strip (the shared tables put back in); <paramref name="Rgb"/>: not YCbCr.</summary>
public sealed record TiffJpeg(IReadOnlyList<TiffStrip> Strips, int Components, bool Rgb) : TiffImageData;

/// <summary>A band of <paramref name="Rows"/> rows starting at row <paramref name="FirstRow"/>.</summary>
public sealed record TiffStrip(byte[] Data, int FirstRow, int Rows);

/// <summary>
/// Reads the pages of baseline TIFF files (and the common extensions scanners write): uncompressed, LZW, Deflate and
/// PackBits samples of 1–8 bits (16 bits brought to 8), grey, RGB, CMYK and palette, with the horizontal predictor,
/// either planar configuration, fill order and an alpha channel; CCITT Group 4 and JPEG strips are kept as coded.
/// Tiled images, Group 3, old-style JPEG and floating-point samples are refused (<see cref="InvalidDataException"/>).
/// </summary>
public static class TiffReader
{
    private const int MaxPages = 10_000;
    private const long MaxPixels = 400_000_000;

    public static bool IsTiff(ReadOnlySpan<byte> data)
        => data.Length >= 8 && ((data[0] == 'I' && data[1] == 'I' && data[2] == 42 && data[3] == 0) || (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && data[3] == 42));

    /// <summary>The pages of a TIFF file, in order.</summary>
    /// <exception cref="InvalidDataException">Not a TIFF, damaged, or a page of a kind not handled (the message says which).</exception>
    public static IReadOnlyList<TiffImage> Read(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!IsTiff(file))
        {
            throw new InvalidDataException("Not a TIFF file (or a BigTIFF one).");
        }

        var reader = new Reader(file, file[0] == 'M');
        var pages = new List<TiffImage>();
        var seen = new HashSet<long>();
        var offset = (long)reader.UInt32(4);
        while (offset != 0)
        {
            if (!seen.Add(offset) || pages.Count >= MaxPages)
            {
                throw new InvalidDataException("The directories of the TIFF file loop.");
            }

            var tags = reader.Directory(offset, out var next);
            pages.Add(Page(reader, tags));
            offset = next;
        }

        return pages.Count > 0 ? pages : throw new InvalidDataException("The TIFF file has no page.");
    }

    private static TiffImage Page(Reader reader, Dictionary<int, long[]> tags)
    {
        long One(int tag, long fallback) => tags.TryGetValue(tag, out var values) && values.Length > 0 ? values[0] : fallback;
        var width = (int)One(256, 0);
        var height = (int)One(257, 0);
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException("A TIFF page has no size or is too large.");
        }

        if (tags.ContainsKey(322))
        {
            throw new InvalidDataException("Tiled TIFF images are not supported.");
        }

        var compression = (int)One(259, 1);
        var photometric = (int)One(262, -1);
        var samples = (int)One(277, 1);
        var bits = tags.TryGetValue(258, out var bitList) && bitList.Length > 0 ? (int)bitList[0] : 1;
        var planar = (int)One(284, 1);
        var fillOrder = (int)One(266, 1);
        var orientation = (int)One(274, 1) is var o and >= 1 and <= 8 ? o : 1;
        var (dpiX, dpiY) = Resolution(reader, tags);
        var offsets = tags.GetValueOrDefault(273) ?? throw new InvalidDataException("A TIFF page has no strips.");
        var counts = tags.GetValueOrDefault(279) ?? throw new InvalidDataException("A TIFF page has no strip lengths.");
        if (offsets.Length != counts.Length || offsets.Length == 0)
        {
            throw new InvalidDataException("The strips of a TIFF page do not match their lengths.");
        }

        if (One(339, 1) is not 1)
        {
            throw new InvalidDataException("TIFF samples that are not unsigned integers are not supported.");
        }

        var rowsPerStrip = (int)Math.Clamp(One(278, height), 1, height);
        var strips = new List<byte[]>(offsets.Length);
        for (var i = 0; i < offsets.Length; i++)
        {
            strips.Add(reader.Bytes(offsets[i], counts[i]));
        }

        TiffImageData data = compression switch
        {
            4 => Fax(strips, rowsPerStrip, height, fillOrder, photometric == 1),
            7 => Jpeg(reader, tags, strips, rowsPerStrip, height, samples, photometric),
            1 or 5 or 8 or 32946 or 32773 => Samples(tags, strips, width, height, rowsPerStrip, compression, photometric, samples, bits, planar, fillOrder),
            2 or 3 => throw new InvalidDataException("CCITT Group 3 TIFF pages are not supported."),
            6 => throw new InvalidDataException("Old-style JPEG TIFF pages are not supported."),
            _ => throw new InvalidDataException($"TIFF compression {compression} is not supported."),
        };
        return new TiffImage(width, height, dpiX, dpiY, orientation, data);
    }

    private static (double? X, double? Y) Resolution(Reader reader, Dictionary<int, long[]> tags)
    {
        var unit = tags.TryGetValue(296, out var u) && u.Length > 0 ? u[0] : 2;
        double? Of(int tag)
        {
            if (!tags.TryGetValue(tag, out var r) || r.Length < 2 || r[1] == 0 || r[0] <= 0)
            {
                return null;
            }

            var value = (double)r[0] / r[1];
            return unit switch { 2 => value, 3 => value * 2.54, _ => null };
        }

        return (Of(282), Of(283));
    }

    private static TiffFax Fax(List<byte[]> strips, int rowsPerStrip, int height, int fillOrder, bool invert)
    {
        var bands = new List<TiffStrip>(strips.Count);
        for (var i = 0; i < strips.Count && i * rowsPerStrip < height; i++)
        {
            var data = fillOrder == 2 ? Reversed(strips[i]) : strips[i];
            bands.Add(new TiffStrip(data, i * rowsPerStrip, Math.Min(rowsPerStrip, height - (i * rowsPerStrip))));
        }

        return new TiffFax(bands, invert);
    }

    private static TiffJpeg Jpeg(Reader reader, Dictionary<int, long[]> tags, List<byte[]> strips, int rowsPerStrip, int height, int samples, int photometric)
    {
        if (samples is not (1 or 3))
        {
            throw new InvalidDataException("JPEG TIFF pages of other than 1 or 3 samples are not supported.");
        }

        var tables = reader.Undefined(tags, 347);
        var bands = new List<TiffStrip>(strips.Count);
        for (var i = 0; i < strips.Count && i * rowsPerStrip < height; i++)
        {
            var strip = strips[i];

            // Abbreviated strips: the shared tables (SOI … EOI) go in front, without their EOI and the strip's SOI.
            var whole = tables is { Length: > 4 } && strip.Length > 2
                ? [.. tables.AsSpan(0, tables.Length - 2), .. strip.AsSpan(2)]
                : strip;
            bands.Add(new TiffStrip(whole, i * rowsPerStrip, Math.Min(rowsPerStrip, height - (i * rowsPerStrip))));
        }

        return new TiffJpeg(bands, samples, photometric == 2);
    }

    private static TiffSamples Samples(
        Dictionary<int, long[]> tags,
        List<byte[]> strips,
        int width,
        int height,
        int rowsPerStrip,
        int compression,
        int photometric,
        int samples,
        int bits,
        int planar,
        int fillOrder)
    {
        var (model, colorSamples) = photometric switch
        {
            0 or 1 => (TiffColorModel.Gray, 1),
            2 => (TiffColorModel.Rgb, 3),
            3 => (TiffColorModel.Palette, 1),
            5 => (TiffColorModel.Cmyk, 4),
            _ => throw new InvalidDataException($"TIFF photometric interpretation {photometric} is not supported."),
        };
        if (bits is not (1 or 2 or 4 or 8 or 16) || samples < colorSamples || samples > colorSamples + 4)
        {
            throw new InvalidDataException("These TIFF samples are not supported.");
        }

        if (bits != 8 && bits != 16 && samples != 1)
        {
            throw new InvalidDataException("TIFF samples of less than 8 bits with several components are not supported.");
        }

        var predictor = tags.TryGetValue(317, out var p) && p.Length > 0 ? (int)p[0] : 1;
        if (predictor is not (1 or 2) || (predictor == 2 && bits != 8))
        {
            throw new InvalidDataException("This TIFF predictor is not supported.");
        }

        // The decoded strips, as one block per plane (planar configuration 2) or one for all (1).
        var planes = planar == 2 ? samples : 1;
        var perPlane = planes == 1 ? samples : 1;
        var rowBytes = ((width * perPlane * bits) + 7) / 8;
        var stripsPerPlane = (height + rowsPerStrip - 1) / rowsPerStrip;
        if (strips.Count < stripsPerPlane * planes)
        {
            throw new InvalidDataException("A TIFF page has fewer strips than rows.");
        }

        var blocks = new byte[planes][];
        for (var plane = 0; plane < planes; plane++)
        {
            var block = new byte[(long)rowBytes * height];
            for (var s = 0; s < stripsPerPlane; s++)
            {
                var decoded = Decompress(strips[(plane * stripsPerPlane) + s], compression);
                var start = (long)s * rowsPerStrip * rowBytes;
                var length = (int)Math.Min(decoded.Length, block.Length - start);
                if (length > 0)
                {
                    decoded.AsSpan(0, length).CopyTo(block.AsSpan((int)start));
                }
            }

            if (predictor == 2)
            {
                Unpredict(block, rowBytes, perPlane);
            }

            if (fillOrder == 2 && bits < 8)
            {
                block = Reversed(block);
            }

            blocks[plane] = block;
        }

        // Interleaved 8-bit samples (16 bits brought to 8, high byte first or last as the file's byte order says).
        byte[] chunky;
        if (bits == 16)
        {
            var big = tags.ContainsKey(-1);
            chunky = new byte[width * height * samples];
            for (var plane = 0; plane < planes; plane++)
            {
                for (var i = 0; i < width * height * perPlane; i++)
                {
                    var target = planes == 1 ? i : (i * samples) + plane;
                    chunky[target] = blocks[plane][(i * 2) + (big ? 0 : 1)];
                }
            }

            bits = 8;
        }
        else if (planes > 1)
        {
            chunky = new byte[width * height * samples];
            for (var plane = 0; plane < planes; plane++)
            {
                for (var i = 0; i < width * height; i++)
                {
                    chunky[(i * samples) + plane] = blocks[plane][i];
                }
            }
        }
        else
        {
            chunky = blocks[0];
        }

        byte[]? alpha = null;
        if (samples > colorSamples)
        {
            var extra = tags.TryGetValue(338, out var e) && e.Length > 0 ? e[0] : 0;
            var color = new byte[width * height * colorSamples];
            alpha = extra is 1 or 2 ? new byte[width * height] : null;
            for (var i = 0; i < width * height; i++)
            {
                chunky.AsSpan(i * samples, colorSamples).CopyTo(color.AsSpan(i * colorSamples));
                if (alpha is not null)
                {
                    alpha[i] = chunky[(i * samples) + colorSamples];
                }
            }

            chunky = color;
        }

        byte[]? palette = null;
        if (model == TiffColorModel.Palette)
        {
            var map = tags.GetValueOrDefault(320) ?? throw new InvalidDataException("A palette TIFF page has no colour map.");
            var entries = 1 << bits;
            if (map.Length < entries * 3)
            {
                throw new InvalidDataException("The colour map of a TIFF page is too short.");
            }

            palette = new byte[entries * 3];
            for (var i = 0; i < entries; i++)
            {
                palette[i * 3] = (byte)(map[i] >> 8);
                palette[(i * 3) + 1] = (byte)(map[entries + i] >> 8);
                palette[(i * 3) + 2] = (byte)(map[(2 * entries) + i] >> 8);
            }
        }

        return new TiffSamples(chunky, bits, model, palette, photometric == 0, alpha);
    }

    private static byte[] Decompress(byte[] data, int compression)
    {
        try
        {
            return compression switch
            {
                5 => TiffLzw.Decode(data),
                8 or 32946 => Inflate(data),
                32773 => PackBits(data),
                _ => data,
            };
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
        {
            throw new InvalidDataException("A TIFF strip cannot be decompressed.", ex);
        }
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>PackBits (Apple, TIFF 6.0 section 9): a count byte n, then n + 1 literal bytes, or one byte repeated 1 − n times.</summary>
    public static byte[] PackBits(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length * 2);
        var at = 0;
        while (at < data.Length)
        {
            var n = (sbyte)data[at++];
            if (n >= 0)
            {
                var count = Math.Min(n + 1, data.Length - at);
                for (var i = 0; i < count; i++)
                {
                    output.Add(data[at + i]);
                }

                at += count;
            }
            else if (n != -128 && at < data.Length)
            {
                var b = data[at++];
                for (var i = 0; i < 1 - n; i++)
                {
                    output.Add(b);
                }
            }
        }

        return [.. output];
    }

    /// <summary>Undoes the horizontal predictor: each sample plus the same sample of the pixel on its left.</summary>
    private static void Unpredict(byte[] block, int rowBytes, int bytesPerPixel)
    {
        var rows = block.Length / rowBytes;
        for (var y = 0; y < rows; y++)
        {
            var row = y * rowBytes;
            for (var i = bytesPerPixel; i < rowBytes; i++)
            {
                block[row + i] = (byte)(block[row + i] + block[row + i - bytesPerPixel]);
            }
        }
    }

    private static byte[] Reversed(byte[] data)
    {
        var reversed = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
            b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
            reversed[i] = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
        }

        return reversed;
    }

    /// <summary>Reads numbers of either byte order out of the file, bounds checked.</summary>
    private sealed class Reader(byte[] file, bool bigEndian)
    {
        public uint UInt32(long at)
        {
            Check(at, 4);
            return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)at)) : BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)at));
        }

        public ushort UInt16(long at)
        {
            Check(at, 2);
            return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan((int)at)) : BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)at));
        }

        public byte[] Bytes(long at, long length)
        {
            Check(at, length);
            return file.AsSpan((int)at, (int)length).ToArray();
        }

        /// <summary>The raw bytes of an UNDEFINED or BYTE tag (JPEGTables), kept by <see cref="Directory"/> as offset and count.</summary>
        public byte[]? Undefined(Dictionary<int, long[]> tags, int tag)
            => tags.TryGetValue(-tag, out var place) && place.Length == 2 ? Bytes(place[0], place[1]) : null;

        /// <summary>The tags of the directory at <paramref name="offset"/> (integer values; rationals as numerator, denominator).</summary>
        public Dictionary<int, long[]> Directory(long offset, out long next)
        {
            var count = UInt16(offset);
            var tags = new Dictionary<int, long[]>();
            if (bigEndian)
            {
                tags[-1] = []; // marks the byte order for 16-bit samples
            }

            for (var i = 0; i < count; i++)
            {
                var entry = offset + 2 + (12L * i);
                int tag = UInt16(entry);
                var type = UInt16(entry + 2);
                var n = UInt32(entry + 4);
                var size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 0 };
                if (size == 0 || n > 100_000_000)
                {
                    continue;
                }

                var length = size * (long)n;
                var at = length <= 4 ? entry + 8 : UInt32(entry + 8);
                Check(at, length);
                if (type is 1 or 7 && tag == 347)
                {
                    tags[-tag] = [at, length];
                    continue;
                }

                tags[tag] = type switch
                {
                    1 or 2 or 6 or 7 => [.. Enumerable.Range(0, (int)n).Select(k => (long)file[at + k])],
                    3 or 8 => [.. Enumerable.Range(0, (int)n).Select(k => (long)UInt16(at + (2 * k)))],
                    4 or 9 or 13 => [.. Enumerable.Range(0, (int)n).Select(k => (long)UInt32(at + (4 * k)))],
                    5 or 10 => [.. Enumerable.Range(0, (int)n * 2).Select(k => (long)UInt32(at + (4 * k)))],
                    _ => [],
                };
            }

            next = UInt32(offset + 2 + (12L * count));
            return tags;
        }

        private void Check(long at, long length)
        {
            if (at < 0 || length < 0 || at + length > file.Length)
            {
                throw new InvalidDataException("The TIFF file is cut short.");
            }
        }
    }
}
