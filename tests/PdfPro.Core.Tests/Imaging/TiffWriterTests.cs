using System.Buffers.Binary;
using PdfPro.Core.Imaging;

namespace PdfPro.Core.Tests.Imaging;

/// <summary>TIFF 6.0 writing: LZW with early change, the predictor, G4 pages, and a directory chain readers can follow.</summary>
public sealed class TiffWriterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(300_000)]
    public void Lzw_RoundTrips_RandomData_ThroughSeveralTableResets(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        TiffLzw.Decode(TiffLzw.Encode(data)).ShouldBe(data);
    }

    [Fact]
    public void Lzw_RoundTrips_RepetitiveData_AndCompressesIt()
    {
        var data = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 7 == 0 ? 255 : i / 5000)).ToArray();

        var encoded = TiffLzw.Encode(data);

        TiffLzw.Decode(encoded).ShouldBe(data);
        encoded.Length.ShouldBeLessThan(data.Length / 10);
    }

    [Fact]
    public void Lzw_StartsWithClear_AndEndsWithEndOfInformation()
    {
        var encoded = TiffLzw.Encode([7, 7, 7]);

        // Clear = 256 in 9 bits: 1 0000 0000 → first byte 0x80.
        encoded[0].ShouldBe((byte)0x80);
        TiffLzw.Decode(encoded).ShouldBe(new byte[] { 7, 7, 7 });
    }

    [Fact]
    public void Lzw_WidensItsCodes_WhereTiffReadersDo()
    {
        // 254 codes of 9 bits, then 10 bits: distinct pairs make one code per two bytes until the table grows.
        var data = new byte[3000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 37) ^ (i >> 3));
        }

        TiffLzw.Decode(TiffLzw.Encode(data)).ShouldBe(data);
    }

    [Fact]
    public void ARgbPage_IsOneStrip_OfPredictedLzwSamples_WithItsResolution()
    {
        var rgb = Pattern(40, 30, 3);
        using var stream = new MemoryStream();

        new TiffWriter(stream, 1).AddRgb(rgb, 40, 30, 150);

        var pages = Read(stream.ToArray());
        pages.Count.ShouldBe(1);
        var page = pages[0];
        page.Tags[256].ShouldBe([40u]);
        page.Tags[257].ShouldBe([30u]);
        page.Tags[258].ShouldBe([8u, 8u, 8u]);
        page.Tags[259].ShouldBe([5u], "LZW");
        page.Tags[262].ShouldBe([2u], "RGB");
        page.Tags[317].ShouldBe([2u], "horizontal predictor");
        page.Tags[282].ShouldBe([15000u, 100u]);
        page.Tags[296].ShouldBe([2u], "inches");
        Unpredict(TiffLzw.Decode(page.Strip), 40, 3).ShouldBe(rgb);
    }

    [Fact]
    public void SeveralPages_AreChained_AndNumbered()
    {
        using var stream = new MemoryStream();
        var tiff = new TiffWriter(stream, 3);

        tiff.AddRgb(Pattern(9, 5, 3), 9, 5, 72);
        tiff.AddGray(Pattern(7, 3, 1), 7, 3, 72);
        tiff.AddBilevel(Enumerable.Repeat((byte)0xF0, 2 * 4).ToArray(), 12, 4, 300);

        var pages = Read(stream.ToArray());
        pages.Count.ShouldBe(3);
        pages.Select(p => p.Tags[297]).ShouldBe([[0u, 3u], [1u, 3u], [2u, 3u]]);
        pages.ShouldAllBe(p => p.Tags[254].Single() == 2u, "pages of a multi-page file");
        pages[1].Tags[262].ShouldBe([1u], "grey, black is zero");
        Unpredict(TiffLzw.Decode(pages[1].Strip), 7, 1).ShouldBe(Pattern(7, 3, 1));
        pages[2].Tags[259].ShouldBe([4u], "CCITT Group 4");
        pages[2].Tags[262].ShouldBe([0u], "white is zero, as fax readers expect");
        pages[2].Strip.ShouldBe(CcittG4.Encode(Enumerable.Repeat((byte)0xF0, 2 * 4).ToArray(), 12, 4));
    }

    [Fact]
    public void MorePagesThanAnnounced_AreRefused()
    {
        using var stream = new MemoryStream();
        var tiff = new TiffWriter(stream, 1);
        tiff.AddGray(new byte[4], 2, 2, 72);

        Should.Throw<InvalidOperationException>(() => tiff.AddGray(new byte[4], 2, 2, 72));
    }

    private static byte[] Pattern(int width, int height, int channels)
    {
        var samples = new byte[width * height * channels];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)((i * 13) + (i / (width * channels) * 50));
        }

        return samples;
    }

    private static byte[] Unpredict(byte[] data, int width, int channels)
    {
        var row = width * channels;
        for (var y = 0; y < data.Length / row; y++)
        {
            for (var i = channels; i < row; i++)
            {
                data[(y * row) + i] = (byte)(data[(y * row) + i] + data[(y * row) + i - channels]);
            }
        }

        return data;
    }

    /// <summary>The pages of a little-endian TIFF: their tags (values) and their single strip.</summary>
    private static List<(Dictionary<int, uint[]> Tags, byte[] Strip)> Read(byte[] file)
    {
        file[0].ShouldBe((byte)'I');
        BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2)).ShouldBe((ushort)42);
        var pages = new List<(Dictionary<int, uint[]>, byte[])>();
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        while (offset != 0)
        {
            (offset % 2).ShouldBe(0u, "directories are word-aligned");
            var count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)offset));
            var tags = new Dictionary<int, uint[]>();
            var previous = -1;
            for (var i = 0; i < count; i++)
            {
                var entry = file.AsSpan((int)offset + 2 + (12 * i), 12);
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                tag.ShouldBeGreaterThan(previous, "tags are sorted");
                previous = tag;
                var type = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
                var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                var size = type switch { 3 => 2, 4 => 4, 5 => 8, _ => throw new InvalidDataException($"type {type}") };
                var values = (n * size <= 4 ? entry.Slice(8, n * size) : file.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), n * size)).ToArray();
                tags[tag] = type switch
                {
                    3 => [.. Enumerable.Range(0, n).Select(k => (uint)BinaryPrimitives.ReadUInt16LittleEndian(values[(k * 2)..]))],
                    _ => [.. Enumerable.Range(0, values.Length / 4).Select(k => BinaryPrimitives.ReadUInt32LittleEndian(values[(k * 4)..]))],
                };
            }

            var strip = file.AsSpan((int)tags[273][0], (int)tags[279][0]).ToArray();
            pages.Add((tags, strip));
            offset = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)offset + 2 + (12 * count)));
        }

        return pages;
    }
}
