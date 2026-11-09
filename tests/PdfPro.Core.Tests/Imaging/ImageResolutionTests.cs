using System.Buffers.Binary;
using PdfPro.Core.Imaging;

namespace PdfPro.Core.Tests.Imaging;

/// <summary>The resolution pictures say they were made at.</summary>
public sealed class ImageResolutionTests
{
    [Theory]
    [InlineData(1, 300, 200, 300.0, 200.0)]
    [InlineData(2, 118, 118, 299.72, 299.72)]
    public void Jfif_GivesTheDensity_InDotsPerInch(byte units, int x, int y, double dpiX, double dpiY)
    {
        var resolution = ImageResolution.ReadJpeg(Jpeg(Jfif(units, x, y))).ShouldNotBeNull();

        resolution.X.ShouldBe(dpiX, 0.01);
        resolution.Y.ShouldBe(dpiY, 0.01);
    }

    [Fact]
    public void AnAspectRatioOnly_IsNoResolution_AndExifIsReadInstead()
    {
        ImageResolution.ReadJpeg(Jpeg(Jfif(0, 1, 1))).ShouldBeNull();

        var resolution = ImageResolution.ReadJpeg(Jpeg(Jfif(0, 1, 1), Exif(240))).ShouldNotBeNull();

        resolution.X.ShouldBe(240);
        resolution.Y.ShouldBe(240);
    }

    [Fact]
    public void PngPhys_InPixelsPerMetre_IsConverted()
    {
        var png = Png(("pHYs", [.. Big32(11811), .. Big32(11811), 1]));

        var resolution = ImageResolution.ReadPng(png).ShouldNotBeNull();

        resolution.X.ShouldBe(300, 0.01);
    }

    [Fact]
    public void APngWithoutPhys_OrInUnknownUnits_HasNone()
    {
        ImageResolution.ReadPng(Png()).ShouldBeNull();
        ImageResolution.ReadPng(Png(("pHYs", [.. Big32(1), .. Big32(1), 0]))).ShouldBeNull();
        ImageResolution.ReadJpeg("not a picture"u8).ShouldBeNull();
    }

    private static byte[] Jpeg(params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(s => s), 0xFF, 0xD9];

    private static byte[] Jfif(byte units, int x, int y)
    {
        byte[] body = [.. "JFIF\0"u8, 1, 2, units, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 0, 0];
        return Segment(0xE0, body);
    }

    /// <summary>An EXIF block whose first directory gives XResolution = YResolution = <paramref name="dpi"/> inches.</summary>
    private static byte[] Exif(int dpi)
    {
        var tiff = new byte[8 + 2 + (3 * 12) + 4 + 16];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 3);
        var rationals = 8 + 2 + (3 * 12) + 4;
        Entry(10, 282, 5, 1, (uint)rationals);
        Entry(22, 283, 5, 1, (uint)rationals + 8);
        Entry(34, 296, 3, 1, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(rationals), (uint)dpi);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(rationals + 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(rationals + 8), (uint)dpi);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(rationals + 12), 1);
        return Segment(0xE1, [.. "Exif\0\0"u8, .. tiff]);

        void Entry(int at, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8), value);
        }
    }

    private static byte[] Segment(byte marker, byte[] body) => [0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];

    private static byte[] Png(params (string Type, byte[] Data)[] chunks)
    {
        var bytes = new List<byte>([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        foreach (var (type, data) in new[] { ("IHDR", new byte[13]) }.Concat(chunks).Append(("IDAT", new byte[2])))
        {
            bytes.AddRange(Big32(data.Length));
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            bytes.AddRange(data);
            bytes.AddRange(new byte[4]); // the CRC is not read
        }

        return [.. bytes];
    }

    private static byte[] Big32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
