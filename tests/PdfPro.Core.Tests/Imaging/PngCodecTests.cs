using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PdfPro.Core.Imaging;

namespace PdfPro.Core.Tests.Imaging;

public sealed class PngCodecTests
{
    [Fact]
    public void Rgba_RoundTrips()
    {
        // B, G, R, A per pixel: red, half-transparent green, blue / white, black, clear.
        byte[] pixels =
        [
            0, 0, 255, 255, 0, 255, 0, 128, 255, 0, 0, 255,
            255, 255, 255, 255, 0, 0, 0, 255, 10, 20, 30, 0,
        ];
        var image = new BgraImage(3, 2, pixels);

        var png = PngCodec.Encode(image);
        var decoded = PngCodec.Decode(png);

        PngCodec.IsPng(png).ShouldBeTrue();
        decoded.Width.ShouldBe(3);
        decoded.Height.ShouldBe(2);
        decoded.Pixels.ShouldBe(pixels);
    }

    [Fact]
    public void GreyRows_WithEveryFilter_AreDecoded()
    {
        // 3×5 grey, one row per filter (none, sub, up, average, paeth); every row decodes to 10, 20, 30.
        byte[][] rows =
        [
            [0, 10, 20, 30],
            [1, 10, 10, 10],
            [2, 0, 0, 0],
            [3, 5, 5, 5], // left/up averages: (0+10)/2=5→10, (10+20)/2=15→20, (20+30)/2=25→30
            [4, 0, 0, 0], // paeth picks up (10, 20, 30)
        ];
        var png = Build(3, 5, 8, 0, rows);

        var image = PngCodec.Decode(png);

        for (var y = 0; y < 5; y++)
        {
            Enumerable.Range(0, 3).Select(x => image.Pixels[((y * 3) + x) * 4]).ShouldBe(new byte[] { 10, 20, 30 }, $"row {y}");
        }

        image.Pixels.Where((_, i) => i % 4 == 3).ShouldAllBe(a => a == 255);
    }

    [Fact]
    public void APalette_OfTwoBits_WithTransparency_IsDecoded()
    {
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255];
        byte[] alpha = [255, 0];
        var png = Build(3, 1, 2, 3, [[0, 0b00_01_10_00]], palette, alpha);

        var image = PngCodec.Decode(png);

        image.Pixels.ShouldBe(new byte[] { 0, 0, 255, 255, 0, 255, 0, 0, 255, 0, 0, 255 });
    }

    [Fact]
    public void InterlacedOrDamagedFiles_AreRefused()
    {
        Should.Throw<InvalidDataException>(() => PngCodec.Decode(Build(1, 1, 8, 0, [[0, 7]], interlaced: true)));
        Should.Throw<InvalidDataException>(() => PngCodec.Decode("not a png"u8));
        var png = PngCodec.Encode(new BgraImage(2, 2, new byte[16]));
        Should.Throw<InvalidDataException>(() => PngCodec.Decode(png.AsSpan(0, png.Length - 20)));
    }

    private static byte[] Build(int width, int height, byte depth, byte colorType, byte[][] rows, byte[]? palette = null, byte[]? transparency = null, bool interlaced = false)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = depth;
        header[9] = colorType;
        header[12] = interlaced ? (byte)1 : (byte)0;
        Chunk(output, "IHDR", header);
        if (palette is not null)
        {
            Chunk(output, "PLTE", palette);
        }

        if (transparency is not null)
        {
            Chunk(output, "tRNS", transparency);
        }

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                foreach (var row in rows)
                {
                    zlib.Write(row);
                }
            }

            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>A chunk with a zero CRC: the decoder does not check CRCs.</summary>
    private static void Chunk(Stream output, string type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        output.Write(length);
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(body);
        output.Write(new byte[4]);
    }
}
