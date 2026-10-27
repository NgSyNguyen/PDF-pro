using System.Buffers.Binary;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;

namespace PdfPro.Core.Tests.Imaging;

public sealed class ExifOrientationTests
{
    [Theory]
    [InlineData(6, false)]
    [InlineData(8, true)]
    [InlineData(3, false)]
    public void TheTag_IsReadInEitherByteOrder(int orientation, bool bigEndian)
        => ExifOrientation.Read(Jpeg(orientation, bigEndian)).ShouldBe(orientation);

    [Fact]
    public void WithoutATag_ThePictureIsUpright()
    {
        ExifOrientation.Read([0xFF, 0xD8, 0xFF, 0xDA, 0, 2]).ShouldBe(1);
        ExifOrientation.Read("not a jpeg"u8).ShouldBe(1);
        ExifOrientation.Read(Jpeg(9, false)).ShouldBe(1, "values outside 1-8 are ignored");
        ExifOrientation.Read(Jpeg(6, false)[..20]).ShouldBe(1, "a cut file is read safely");
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0)]
    [InlineData(2, 0, 0, 40, 0)]
    [InlineData(3, 0, 0, 40, 20)]
    [InlineData(4, 0, 0, 0, 20)]
    [InlineData(5, 40, 0, 0, 40)]
    [InlineData(6, 0, 0, 20, 0)]
    [InlineData(7, 0, 0, 20, 40)]
    [InlineData(8, 0, 0, 0, 40)]
    public void StoredCorners_GoWhereTheTagSays(int orientation, double x, double y, double shownX, double shownY)
    {
        // A 40 × 20 stored picture.
        var shown = ExifOrientation.StoredToDisplayed(orientation, 40, 20).Transform(new PdfPoint(x, y));

        shown.X.ShouldBe(shownX, 1e-9);
        shown.Y.ShouldBe(shownY, 1e-9);
        ExifOrientation.DisplayedSize(orientation, 40, 20).ShouldBe(orientation >= 5 ? (20, 40) : (40, 20));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryOrientation_CoversTheShownPictureExactly(int orientation)
    {
        var (w, h) = ExifOrientation.DisplayedSize(orientation, 40, 20);
        var bounds = ExifOrientation.StoredToDisplayed(orientation, 40, 20).TransformBounds(new PdfRect(0, 0, 40, 20));

        bounds.ShouldBe(new PdfRect(0, 0, w, h));
    }

    /// <summary>SOI, an APP1 Exif segment with the orientation, then the start of scan.</summary>
    private static byte[] Jpeg(int orientation, bool bigEndian)
    {
        var tiff = new byte[26];
        if (bigEndian)
        {
            "MM"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), (ushort)orientation);
        }
        else
        {
            "II"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), (ushort)orientation);
        }

        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));

        // A JFIF APP0 segment first, as most files have.
        return [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xE1, .. length, .. payload, 0xFF, 0xDA, 0, 2];
    }
}
