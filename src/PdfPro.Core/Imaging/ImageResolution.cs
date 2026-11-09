using System.Buffers.Binary;

namespace PdfPro.Core.Imaging;

/// <summary>The resolution a picture file says it was made at: JPEG (JFIF density, else EXIF) and PNG (pHYs).</summary>
public static class ImageResolution
{
    /// <summary>Dots per inch across and down, or null when the JPEG gives none (or only an aspect ratio).</summary>
    public static (double X, double Y)? ReadJpeg(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return null;
        }

        (double, double)? exif = null;
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            var marker = jpeg[at + 1];
            if (marker is 0xD9 or 0xDA)
            {
                break; // end of image, start of scan: no more headers
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 2)..]);
            if (length < 2 || at + 2 + length > jpeg.Length)
            {
                break;
            }

            var segment = jpeg.Slice(at + 4, length - 2);
            if (marker == 0xE0 && segment.Length >= 12 && segment[..5].SequenceEqual("JFIF\0"u8))
            {
                var units = segment[7];
                double x = BinaryPrimitives.ReadUInt16BigEndian(segment[8..]);
                double y = BinaryPrimitives.ReadUInt16BigEndian(segment[10..]);
                if (units is 1 or 2 && x > 0 && y > 0)
                {
                    return units == 1 ? (x, y) : (x * 2.54, y * 2.54);
                }
            }
            else if (marker == 0xE1 && segment.Length > 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                exif ??= FromTiff(segment[6..]);
            }

            at += 2 + length;
        }

        return exif;
    }

    /// <summary>Dots per inch from a PNG's pHYs chunk (pixels per metre), or null.</summary>
    public static (double X, double Y)? ReadPng(ReadOnlySpan<byte> png)
    {
        if (!PngCodec.IsPng(png))
        {
            return null;
        }

        var at = 8;
        while (at + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[at..]);
            var type = png.Slice(at + 4, 4);
            if (length < 0 || at + 12 + length > png.Length || type.SequenceEqual("IDAT"u8))
            {
                break;
            }

            if (type.SequenceEqual("pHYs"u8) && length >= 9)
            {
                var data = png.Slice(at + 8, length);
                double x = BinaryPrimitives.ReadUInt32BigEndian(data);
                double y = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                return data[8] == 1 && x > 0 && y > 0 ? (x * 0.0254, y * 0.0254) : null;
            }

            at += 12 + length;
        }

        return null;
    }

    /// <summary>XResolution, YResolution and ResolutionUnit of an EXIF block's first directory.</summary>
    private static (double, double)? FromTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return null;
        }

        var big = tiff[0] == 'M';
        uint U32(ReadOnlySpan<byte> s) => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
        ushort U16(ReadOnlySpan<byte> s) => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
        var directory = (int)U32(tiff[4..]);
        if (directory < 8 || directory + 2 > tiff.Length)
        {
            return null;
        }

        var count = U16(tiff[directory..]);
        double? x = null;
        double? y = null;
        var unit = 2;
        for (var i = 0; i < count && directory + 2 + (12 * (i + 1)) <= tiff.Length; i++)
        {
            var entry = tiff.Slice(directory + 2 + (12 * i), 12);
            var tag = U16(entry);
            if (tag == 296)
            {
                unit = U16(entry[8..]);
            }
            else if (tag is 282 or 283)
            {
                var offset = (int)U32(entry[8..]);
                if (offset < 0 || offset + 8 > tiff.Length)
                {
                    continue;
                }

                var denominator = U32(tiff[(offset + 4)..]);
                double? value = denominator == 0 ? null : (double)U32(tiff[offset..]) / denominator;
                if (tag == 282)
                {
                    x = value;
                }
                else
                {
                    y = value;
                }
            }
        }

        if (x is not > 0 || y is not > 0 || unit is not (2 or 3))
        {
            return null;
        }

        return unit == 2 ? (x.Value, y.Value) : (x.Value * 2.54, y.Value * 2.54);
    }
}
