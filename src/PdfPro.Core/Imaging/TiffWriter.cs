using System.Buffers.Binary;

namespace PdfPro.Core.Imaging;

/// <summary>The colours of a TIFF page.</summary>
public enum TiffColor
{
    /// <summary>8-bit RGB, LZW.</summary>
    Rgb,

    /// <summary>8-bit grey, LZW.</summary>
    Gray,

    /// <summary>1-bit black and white, CCITT Group 4 (scanned text, fax, archives).</summary>
    BlackWhite,
}

/// <summary>
/// Writes baseline TIFF 6.0 files of one or more pages (little-endian): 8-bit RGB or grey compressed with LZW after the
/// horizontal predictor, or 1-bit black and white compressed with CCITT Group 4 — the forms archives and scanners
/// read. Each page is one strip, followed by its directory; the stream must be seekable (directories are chained by
/// patching the previous one's link).
/// </summary>
public sealed class TiffWriter
{
    private const ushort Short = 3;
    private const ushort Long = 4;
    private const ushort Rational = 5;

    private readonly Stream _output;
    private readonly long _start;
    private readonly int _pageCount;
    private long _link;
    private int _pages;

    /// <param name="output">A seekable, writable stream, at the place the file starts.</param>
    /// <param name="pageCount">How many pages will be added (written into each page's PageNumber).</param>
    public TiffWriter(Stream output, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        if (!output.CanSeek || !output.CanWrite)
        {
            throw new ArgumentException("A TIFF is written to a seekable stream.", nameof(output));
        }

        _output = output;
        _start = output.Position;
        _pageCount = pageCount;
        Span<byte> header = [(byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0];
        output.Write(header);
        _link = _start + 4;
    }

    /// <summary>A page of 8-bit RGB samples (3 per pixel, rows top first).</summary>
    public void AddRgb(ReadOnlySpan<byte> rgb, int width, int height, double dpi)
        => AddContinuous(rgb, width, height, 3, dpi);

    /// <summary>A page of 8-bit grey samples (0 black, rows top first).</summary>
    public void AddGray(ReadOnlySpan<byte> gray, int width, int height, double dpi)
        => AddContinuous(gray, width, height, 1, dpi);

    /// <summary>A black and white page: rows packed most significant bit first, each (width + 7) / 8 bytes; 0 = black.</summary>
    public void AddBilevel(ReadOnlySpan<byte> rows, int width, int height, double dpi)
    {
        var data = CcittG4.Encode(rows, width, height);

        // Group 4 codes white and black runs, whatever the bits mean in memory: WhiteIsZero is how fax readers expect it.
        AddPage(data, width, height, dpi, [
            (258, Short, [1]),
            (259, Short, [4]),
            (262, Short, [0]),
            (277, Short, [1]),
            (293, Long, [0]),
        ]);
    }

    private void AddContinuous(ReadOnlySpan<byte> samples, int width, int height, int channels, double dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var rowLength = width * channels;
        if (samples.Length < (long)rowLength * height)
        {
            throw new ArgumentException("The samples are shorter than the image.", nameof(samples));
        }

        // Horizontal differencing (predictor 2): each sample minus the same sample of the pixel on its left.
        var predicted = new byte[rowLength * height];
        for (var y = 0; y < height; y++)
        {
            var row = samples.Slice(y * rowLength, rowLength);
            var target = predicted.AsSpan(y * rowLength, rowLength);
            row[..channels].CopyTo(target);
            for (var i = channels; i < rowLength; i++)
            {
                target[i] = (byte)(row[i] - row[i - channels]);
            }
        }

        var data = TiffLzw.Encode(predicted);
        uint[] bits = channels == 3 ? [8, 8, 8] : [8];
        AddPage(data, width, height, dpi, [
            (258, Short, bits),
            (259, Short, [5]),
            (262, Short, [channels == 3 ? 2u : 1u]),
            (277, Short, [(uint)channels]),
            (317, Short, [2]),
        ]);
    }

    private void AddPage(byte[] data, int width, int height, double dpi, List<(ushort Tag, ushort Type, uint[] Values)> specific)
    {
        if (_pages >= _pageCount)
        {
            throw new InvalidOperationException("All the pages announced have been written.");
        }

        _output.Seek(0, SeekOrigin.End);
        Align();
        var stripOffset = (uint)(_output.Position - _start);
        _output.Write(data);
        Align();

        var resolution = (uint)Math.Clamp(Math.Round(dpi * 100), 1, uint.MaxValue);
        var entries = new List<(ushort Tag, ushort Type, uint[] Values)>(specific)
        {
            (254, Long, [_pageCount > 1 ? 2u : 0u]),
            (256, Long, [(uint)width]),
            (257, Long, [(uint)height]),
            (273, Long, [stripOffset]),
            (278, Long, [(uint)height]),
            (279, Long, [(uint)data.Length]),
            (282, Rational, [resolution, 100]),
            (283, Rational, [resolution, 100]),
            (284, Short, [1]),
            (296, Short, [2]),
            (297, Short, [(uint)_pages, (uint)_pageCount]),
        };
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        var directory = _output.Position - _start;
        var extra = directory + 2 + (12 * entries.Count) + 4;
        using var block = new MemoryStream();
        using var values = new MemoryStream();
        Span<byte> buffer = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)entries.Count);
        block.Write(buffer[..2]);
        foreach (var (tag, type, items) in entries)
        {
            var size = type == Short ? 2 : 4;
            var count = type == Rational ? items.Length / 2 : items.Length;
            var bytes = new byte[items.Length * size];
            for (var i = 0; i < items.Length; i++)
            {
                if (size == 2)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)items[i]);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), items[i]);
                }
            }

            buffer.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], (uint)count);
            if (bytes.Length <= 4)
            {
                bytes.CopyTo(buffer[8..]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], (uint)(extra + values.Length));
                values.Write(bytes);
            }

            block.Write(buffer);
        }

        buffer.Clear();
        block.Write(buffer[..4]); // no next directory yet
        _output.Write(block.ToArray());
        _output.Write(values.ToArray());

        // The previous link (the header's, or the last directory's) points here now.
        var end = _output.Position;
        _output.Seek(_link, SeekOrigin.Begin);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)directory);
        _output.Write(buffer[..4]);
        _output.Seek(end, SeekOrigin.Begin);
        _link = _start + directory + 2 + (12 * entries.Count);
        _pages++;
    }

    /// <summary>TIFF offsets are word-aligned.</summary>
    private void Align()
    {
        if ((_output.Position - _start) % 2 == 1)
        {
            _output.WriteByte(0);
        }
    }
}
