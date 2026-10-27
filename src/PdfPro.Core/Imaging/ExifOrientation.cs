using System.Buffers.Binary;
using PdfPro.Core.Geometry;

namespace PdfPro.Core.Imaging;

/// <summary>
/// The EXIF orientation of photos (tag 0x0112): how the stored pixels must be turned or mirrored to be shown upright.
/// Cameras and phones store pictures sideways and set this tag instead of rotating the pixels; PDF knows nothing of it,
/// so the image is drawn through <see cref="StoredToDisplayed"/> and its data is embedded unchanged.
/// </summary>
public static class ExifOrientation
{
    /// <summary>Upright: stored as shown.</summary>
    public const int Normal = 1;

    /// <summary>The orientation (1–8) of a JPEG file; 1 when it has none or it cannot be read.</summary>
    public static int Read(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return Normal;
        }

        var position = 2;
        while (position + 4 <= jpeg.Length && jpeg[position] == 0xFF)
        {
            var marker = jpeg[position + 1];
            if (marker == 0xD8 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
            {
                position += 2;
                continue;
            }

            // Start of scan or end of image: no metadata after this point.
            if (marker is 0xDA or 0xD9)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 2)..]);
            if (length < 2 || position + 2 + length > jpeg.Length)
            {
                break;
            }

            var segment = jpeg.Slice(position + 4, length - 2);
            if (marker == 0xE1 && segment.Length > 6 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                return FromTiff(segment[6..]);
            }

            position += 2 + length;
        }

        return Normal;
    }

    /// <summary>True when the picture is shown turned a quarter (width and height swap).</summary>
    public static bool SwapsSides(int orientation) => orientation is >= 5 and <= 8;

    /// <summary>The size of the picture as shown.</summary>
    public static (int Width, int Height) DisplayedSize(int orientation, int storedWidth, int storedHeight)
        => SwapsSides(orientation) ? (storedHeight, storedWidth) : (storedWidth, storedHeight);

    /// <summary>
    /// Maps the stored picture drawn at (0, 0)–(width, height) (y down) onto the upright picture at
    /// (0, 0)–(displayed width, displayed height).
    /// </summary>
    public static PdfMatrix StoredToDisplayed(int orientation, double storedWidth, double storedHeight)
    {
        var (w, h) = (storedWidth, storedHeight);
        return orientation switch
        {
            2 => new PdfMatrix(-1, 0, 0, 1, w, 0),
            3 => new PdfMatrix(-1, 0, 0, -1, w, h),
            4 => new PdfMatrix(1, 0, 0, -1, 0, h),
            5 => new PdfMatrix(0, 1, 1, 0, 0, 0),
            6 => new PdfMatrix(0, 1, -1, 0, h, 0),
            7 => new PdfMatrix(0, -1, -1, 0, h, w),
            8 => new PdfMatrix(0, -1, 1, 0, 0, w),
            _ => PdfMatrix.Identity,
        };
    }

    private static int FromTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return Normal;
        }

        var littleEndian = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!littleEndian && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M'))
        {
            return Normal;
        }

        if (U16(tiff, 2, littleEndian) != 42)
        {
            return Normal;
        }

        var directory = U32(tiff, 4, littleEndian);
        if (directory > (uint)(tiff.Length - 2))
        {
            return Normal;
        }

        var entries = U16(tiff, (int)directory, littleEndian);
        for (var i = 0; i < entries; i++)
        {
            var entry = (int)directory + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
            {
                break;
            }

            // Tag 0x0112, type SHORT (3): the value sits in the first two bytes of the value field.
            if (U16(tiff, entry, littleEndian) == 0x0112 && U16(tiff, entry + 2, littleEndian) == 3)
            {
                var value = U16(tiff, entry + 8, littleEndian);
                return value is >= 1 and <= 8 ? value : Normal;
            }
        }

        return Normal;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at, bool littleEndian)
        => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private static uint U32(ReadOnlySpan<byte> data, int at, bool littleEndian)
        => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
}
