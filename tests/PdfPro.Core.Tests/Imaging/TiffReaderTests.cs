using System.Buffers.Binary;
using System.IO.Compression;
using PdfPro.Core.Imaging;

namespace PdfPro.Core.Tests.Imaging;

/// <summary>Reading TIFF pages: what <see cref="TiffWriter"/> writes, and hand-made files of the other kinds scanners write.</summary>
public sealed class TiffReaderTests
{
    [Fact]
    public void ThePagesOfTiffWriter_ComeBackAsTheyWent()
    {
        var rgb = Pattern(30, 20, 3);
        var gray = Pattern(17, 9, 1);
        var bilevel = Enumerable.Repeat((byte)0xF0, 3 * 12).ToArray();
        using var stream = new MemoryStream();
        var writer = new TiffWriter(stream, 3);
        writer.AddRgb(rgb, 30, 20, 150);
        writer.AddGray(gray, 17, 9, 200);
        writer.AddBilevel(bilevel, 24, 12, 300);

        var pages = TiffReader.Read(stream.ToArray());

        pages.Count.ShouldBe(3);
        pages[0].ShouldSatisfyAllConditions(
            p => p.Width.ShouldBe(30),
            p => p.Height.ShouldBe(20),
            p => p.DpiX.ShouldBe(150),
            p => p.Orientation.ShouldBe(1));
        var colour = pages[0].Data.ShouldBeOfType<TiffSamples>();
        colour.Model.ShouldBe(TiffColorModel.Rgb);
        colour.Data.ShouldBe(rgb, "LZW and the predictor undone");
        var grey = pages[1].Data.ShouldBeOfType<TiffSamples>();
        grey.Model.ShouldBe(TiffColorModel.Gray);
        grey.InvertGray.ShouldBeFalse();
        grey.Data.ShouldBe(gray);
        var fax = pages[2].Data.ShouldBeOfType<TiffFax>();
        fax.Strips.ShouldHaveSingleItem().Data.ShouldBe(CcittG4.Encode(bilevel, 24, 12), "Group 4 is kept as coded");
        fax.Strips[0].Rows.ShouldBe(12);
        fax.Invert.ShouldBeFalse("white is zero, as fax readers expect");
    }

    [Fact]
    public void BigEndianUncompressedGrey_WithSeveralStrips_IsRead()
    {
        var gray = Pattern(5, 7, 1);
        var file = new TiffBuilder(bigEndian: true)
            .Strips([gray[..15], gray[15..30], gray[30..]])
            .Tag(256, 5).Tag(257, 7).Tag(258, 8).Tag(259, 1).Tag(262, 1).Tag(278, 3)
            .Build();

        var page = TiffReader.Read(file).ShouldHaveSingleItem();

        page.Data.ShouldBeOfType<TiffSamples>().Data.ShouldBe(gray);
        page.DpiX.ShouldBeNull("the file gives no resolution");
    }

    [Fact]
    public void PackBitsRgb_WithTheRowsInPlanes_IsInterleaved()
    {
        var (width, height) = (4, 2);
        var red = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var green = new byte[] { 10, 10, 10, 10, 10, 10, 10, 10 };
        var blue = new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 };
        var file = new TiffBuilder()
            .Strips([PackBitsLiteral(red), [0xF9, 10], [0xFD, 0, 0xFD, 255]])
            .Tag(256, width).Tag(257, height).Tag(258, 8, 8, 8).Tag(259, 32773).Tag(262, 2).Tag(277, 3).Tag(284, 2)
            .Build();

        var samples = TiffReader.Read(file).Single().Data.ShouldBeOfType<TiffSamples>();

        samples.Data.ShouldBe(Enumerable.Range(0, 8).SelectMany(i => new[] { red[i], green[i], blue[i] }).ToArray());
    }

    [Fact]
    public void DeflateWithThePredictor_AndAnAlphaChannel_IsReadWithTheAlphaApart()
    {
        var rgba = Pattern(6, 4, 4);
        var predicted = (byte[])rgba.Clone();
        for (var y = 0; y < 4; y++)
        {
            for (var i = 23; i >= 4; i--)
            {
                predicted[(y * 24) + i] = (byte)(rgba[(y * 24) + i] - rgba[(y * 24) + i - 4]);
            }
        }

        var file = new TiffBuilder()
            .Strips([Deflate(predicted)])
            .Tag(256, 6).Tag(257, 4).Tag(258, 8, 8, 8, 8).Tag(259, 8).Tag(262, 2).Tag(277, 4).Tag(317, 2).Tag(338, 2)
            .Rational(282, 300, 1).Rational(283, 300, 1).Tag(296, 2)
            .Build();

        var page = TiffReader.Read(file).Single();
        var samples = page.Data.ShouldBeOfType<TiffSamples>();

        samples.Data.ShouldBe(Enumerable.Range(0, 24).SelectMany(i => rgba.Skip(i * 4).Take(3)).ToArray());
        samples.Alpha.ShouldBe(Enumerable.Range(0, 24).Select(i => rgba[(i * 4) + 3]).ToArray());
        page.DpiX.ShouldBe(300);
    }

    [Fact]
    public void APaletteOfFourBits_BecomesRgbTriples()
    {
        var map = new long[3 * 16];
        map[1] = 0xFFFF;              // entry 1: red
        map[16 + 2] = 0x8000;         // entry 2: half green
        var file = new TiffBuilder()
            .Strips([[0x12, 0x00]])
            .Tag(256, 4).Tag(257, 1).Tag(258, 4).Tag(259, 1).Tag(262, 3).Tag(320, map)
            .Build();

        var samples = TiffReader.Read(file).Single().Data.ShouldBeOfType<TiffSamples>();

        samples.Model.ShouldBe(TiffColorModel.Palette);
        samples.Bits.ShouldBe(4);
        samples.Data.ShouldBe(new byte[] { 0x12, 0x00 });
        samples.Palette![3..6].ShouldBe(new byte[] { 255, 0, 0 });
        samples.Palette[6..9].ShouldBe(new byte[] { 0, 128, 0 });
    }

    [Fact]
    public void WhiteIsZeroBits_InReversedFillOrder_AreReadInPdfOrder()
    {
        var file = new TiffBuilder()
            .Strips([[0b0000_0001]]) // fill order 2: the first pixel is the lowest bit
            .Tag(256, 8).Tag(257, 1).Tag(258, 1).Tag(259, 1).Tag(262, 0).Tag(266, 2).Tag(274, 6)
            .Build();

        var page = TiffReader.Read(file).Single();
        var samples = page.Data.ShouldBeOfType<TiffSamples>();

        samples.Data.ShouldBe(new byte[] { 0b1000_0000 });
        samples.InvertGray.ShouldBeTrue("0 is white");
        page.Orientation.ShouldBe(6);
    }

    [Fact]
    public void SixteenBitGrey_IsBroughtToEightBits()
    {
        var file = new TiffBuilder()
            .Strips([[0x34, 0x12, 0xCD, 0xAB]])
            .Tag(256, 2).Tag(257, 1).Tag(258, 16).Tag(259, 1).Tag(262, 1)
            .Build();

        TiffReader.Read(file).Single().Data.ShouldBeOfType<TiffSamples>().Data.ShouldBe(new byte[] { 0x12, 0xAB });
    }

    [Fact]
    public void JpegStrips_AreKept_WithTheSharedTablesPutBack()
    {
        byte[] tables = [0xFF, 0xD8, 0xFF, 0xDB, 0, 3, 7, 0xFF, 0xD9];
        byte[] strip = [0xFF, 0xD8, 0xFF, 0xDA, 1, 2, 0xFF, 0xD9];
        var file = new TiffBuilder()
            .Strips([strip])
            .Tag(256, 8).Tag(257, 8).Tag(258, 8, 8, 8).Tag(259, 7).Tag(262, 6).Tag(277, 3).Bytes(347, tables)
            .Build();

        var jpeg = TiffReader.Read(file).Single().Data.ShouldBeOfType<TiffJpeg>();

        jpeg.Strips.Single().Data.ShouldBe(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0, 3, 7, 0xFF, 0xDA, 1, 2, 0xFF, 0xD9 });
        jpeg.Rgb.ShouldBeFalse("YCbCr");
    }

    [Theory]
    [InlineData(3, "Group 3")]
    [InlineData(6, "Old-style JPEG")]
    [InlineData(34712, "compression 34712")]
    public void UnhandledCompressions_AreRefused_SayingWhich(int compression, string said)
    {
        var file = new TiffBuilder().Strips([[0]]).Tag(256, 8).Tag(257, 1).Tag(258, 1).Tag(259, compression).Tag(262, 0).Build();

        Should.Throw<InvalidDataException>(() => TiffReader.Read(file)).Message.ShouldContain(said);
    }

    [Fact]
    public void TiledPages_AndOtherFiles_AreRefused()
    {
        var tiled = new TiffBuilder().Strips([[0]]).Tag(256, 8).Tag(257, 1).Tag(258, 8).Tag(262, 1).Tag(322, 16).Build();

        Should.Throw<InvalidDataException>(() => TiffReader.Read(tiled)).Message.ShouldContain("Tiled");
        Should.Throw<InvalidDataException>(() => TiffReader.Read("%PDF-1.7"u8.ToArray()));
        TiffReader.IsTiff("II*\0\0\0\0\0"u8).ShouldBeTrue();
    }

    [Fact]
    public void PackBits_DecodesTheAppleExample()
    {
        byte[] packed = [0xFE, 0xAA, 0x02, 0x80, 0x00, 0x2A, 0xFD, 0xAA, 0x03, 0x80, 0x00, 0x2A, 0x22, 0xF7, 0xAA];

        TiffReader.PackBits(packed).ShouldBe(new byte[]
        {
            0xAA, 0xAA, 0xAA, 0x80, 0x00, 0x2A, 0xAA, 0xAA, 0xAA, 0xAA, 0x80, 0x00, 0x2A, 0x22,
            0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA,
        });
    }

    private static byte[] Pattern(int width, int height, int channels)
        => [.. Enumerable.Range(0, width * height * channels).Select(i => (byte)((i * 37) + (i / 7)))];

    private static byte[] PackBitsLiteral(byte[] data) => [(byte)(data.Length - 1), .. data];

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>A one-page TIFF of either byte order: its strips first, then a directory with the tags given.</summary>
    private sealed class TiffBuilder(bool bigEndian = false)
    {
        private readonly SortedDictionary<int, (ushort Type, long[] Values, byte[]? Raw)> _tags = [];
        private List<byte[]> _strips = [];

        public TiffBuilder Strips(List<byte[]> strips)
        {
            _strips = strips;
            return this;
        }

        public TiffBuilder Tag(int tag, params long[] values)
        {
            _tags[tag] = (values.Any(v => v > ushort.MaxValue) || tag is 273 or 279 ? (ushort)4 : (ushort)3, values, null);
            return this;
        }

        public TiffBuilder Rational(int tag, long numerator, long denominator)
        {
            _tags[tag] = (5, [numerator, denominator], null);
            return this;
        }

        public TiffBuilder Bytes(int tag, byte[] raw)
        {
            _tags[tag] = (7, [], raw);
            return this;
        }

        public byte[] Build()
        {
            using var file = new MemoryStream();
            file.Write(bigEndian ? "MM\0*"u8 : "II*\0"u8);
            file.Write(new byte[4]);
            var offsets = new List<long>();
            foreach (var strip in _strips)
            {
                offsets.Add(file.Position);
                file.Write(strip);
            }

            _tags[273] = (4, [.. offsets], null);
            _tags[279] = (4, [.. _strips.Select(s => (long)s.Length)], null);
            if (file.Position % 2 == 1)
            {
                file.WriteByte(0);
            }

            var directory = file.Position;
            var extra = directory + 2 + (12 * _tags.Count) + 4;
            var block = new MemoryStream();
            var values = new MemoryStream();
            Write16(block, _tags.Count);
            foreach (var (tag, (type, items, raw)) in _tags)
            {
                var bytes = raw ?? Encode(type, items);
                Write16(block, tag);
                Write16(block, type);
                Write32(block, raw?.Length ?? (type == 5 ? items.Length / 2 : items.Length));
                if (bytes.Length <= 4)
                {
                    block.Write([.. bytes, .. new byte[4 - bytes.Length]]);
                }
                else
                {
                    Write32(block, extra + values.Length);
                    values.Write(bytes);
                    if (values.Length % 2 == 1)
                    {
                        values.WriteByte(0);
                    }
                }
            }

            Write32(block, 0);
            file.Write(block.ToArray());
            file.Write(values.ToArray());
            var bytesOut = file.ToArray();
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytesOut.AsSpan(4), (uint)directory);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytesOut.AsSpan(4), (uint)directory);
            }

            return bytesOut;
        }

        private byte[] Encode(ushort type, long[] items)
        {
            var stream = new MemoryStream();
            foreach (var item in items)
            {
                if (type == 3)
                {
                    Write16(stream, item);
                }
                else
                {
                    Write32(stream, item);
                }
            }

            return stream.ToArray();
        }

        private void Write16(Stream stream, long value)
        {
            Span<byte> buffer = stackalloc byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)value);
            }

            stream.Write(buffer);
        }

        private void Write32(Stream stream, long value)
        {
            Span<byte> buffer = stackalloc byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)value);
            }

            stream.Write(buffer);
        }
    }
}
