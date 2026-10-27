using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;

namespace PdfPro.App.Services.Editing;

/// <summary>A picture copied into the document's session, ready to be placed: its file and size as shown.</summary>
/// <param name="Path">The copy in the session folder.</param>
/// <param name="Width">Width as shown (EXIF orientation applied), in pixels.</param>
/// <param name="Height">Height as shown, in pixels.</param>
/// <param name="DpiX">Horizontal resolution (96 when the file does not say).</param>
/// <param name="DpiY">Vertical resolution.</param>
public sealed record ImportedImage(string Path, int Width, int Height, double DpiX, double DpiY)
{
    /// <summary>Its size in points at its own resolution, shrunk (keeping proportions) to fit <paramref name="maximum"/>.</summary>
    public PdfSize SizeWithin(PdfSize maximum)
    {
        var width = Width * 72 / DpiX;
        var height = Height * 72 / DpiY;
        var factor = Math.Min(1, Math.Min(maximum.Width / width, maximum.Height / height));
        return new PdfSize(width * factor, height * factor);
    }
}

/// <summary>
/// Copies pictures into a session folder before they are placed on pages, so the document never depends on the
/// original file. JPEG files are kept byte for byte (the PDF embeds them unchanged); every other format WPF can read
/// (PNG, BMP, GIF, TIFF…) is written as an 8-bit RGBA PNG, which PDFsharp embeds with its transparency. Copies are
/// named by a hash of their content, so the same picture is stored once.
/// </summary>
public static class ImageImporter
{
    /// <exception cref="IOException">The file cannot be read or the copy written.</exception>
    /// <exception cref="NotSupportedException">The file is not a picture WPF can decode.</exception>
    public static ImportedImage Import(string sourcePath, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var bytes = File.ReadAllBytes(sourcePath);
        var frame = Decode(bytes);
        if (IsJpeg(bytes))
        {
            var orientation = ExifOrientation.Read(bytes);
            var (width, height) = ExifOrientation.DisplayedSize(orientation, frame.PixelWidth, frame.PixelHeight);
            var swap = ExifOrientation.SwapsSides(orientation);
            return new ImportedImage(Store(bytes, ".jpg", folder), width, height, Dpi(swap ? frame.DpiY : frame.DpiX), Dpi(swap ? frame.DpiX : frame.DpiY));
        }

        return ImportBitmap(frame, folder);
    }

    /// <summary>A picture in memory (pasted from the clipboard) written as a PNG.</summary>
    /// <exception cref="IOException">The copy cannot be written.</exception>
    public static ImportedImage ImportBitmap(BitmapSource bitmap, string folder)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var rgba = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rgba));
        using var png = new MemoryStream();
        encoder.Save(png);
        return new ImportedImage(Store(png.ToArray(), ".png", folder), bitmap.PixelWidth, bitmap.PixelHeight, Dpi(bitmap.DpiX), Dpi(bitmap.DpiY));
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0 ? decoder.Frames[0] : throw new NotSupportedException("The picture has no image.");
        }
        catch (FileFormatException ex)
        {
            throw new NotSupportedException("The file is not a picture.", ex);
        }
    }

    private static string Store(byte[] bytes, string extension, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, Convert.ToHexStringLower(SHA256.HashData(bytes))[..32] + extension);
        if (!File.Exists(path))
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }

        return path;
    }

    private static bool IsJpeg(byte[] bytes) => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static double Dpi(double value) => value is > 1 and < 10000 ? value : 96;
}
