using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.Core.Imaging;

namespace PdfPro.App.Services.Editing;

/// <summary>
/// Turns a hand signature into a PNG with a transparent background, cut close to the ink: strokes drawn with the
/// mouse or a pen, a name written in a handwriting font, or a photo or scan of a signature on paper (the paper is made
/// transparent). Drawn signatures and names are rendered at <see cref="Dpi"/> so they stay sharp when printed.
/// </summary>
public static class SignatureRenderer
{
    public const double Dpi = 288;

    /// <summary>Handwriting fonts shipped with the app (assets/fonts, SIL OFL 1.1) for signatures typed by name.</summary>
    public static IReadOnlyList<string> HandwritingFonts { get; } = ["Dancing Script", "Charm"];

    private const double Margin = 6;
    private const double TypedSize = 64;
    private const int MaxPictureSide = 2400;

    /// <summary>A handwriting font from the app's Fonts folder (or the system's fallback font when it is missing).</summary>
    public static FontFamily HandwritingFont(string name)
        => new(new Uri(Path.Combine(AppContext.BaseDirectory, "Fonts") + Path.DirectorySeparatorChar), "./#" + name);

    /// <summary>The strokes as a PNG, or null when nothing is drawn.</summary>
    public static byte[]? FromStrokes(StrokeCollection strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (strokes.Count == 0)
        {
            return null;
        }

        var bounds = strokes.GetBounds();
        return Render(bounds, context => strokes.Draw(context));
    }

    /// <summary>A name written in a handwriting font as a PNG, or null for blank text.</summary>
    public static byte[]? FromText(string text, string font, Color color)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var formatted = new FormattedText(text.Trim().Normalize(System.Text.NormalizationForm.FormC), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(HandwritingFont(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), TypedSize, brush, 1);
        var geometry = formatted.BuildGeometry(new Point(0, 0));
        return geometry.Bounds.IsEmpty ? null : Render(geometry.Bounds, context => context.DrawGeometry(brush, null, geometry));
    }

    /// <summary>
    /// A photo or scan of a signature: the paper (pixels lighter than <paramref name="threshold"/>) made transparent and
    /// the picture cut to the ink. Null when nothing is left.
    /// </summary>
    /// <exception cref="NotSupportedException">The file is not a picture.</exception>
    public static byte[]? FromPicture(byte[] file, byte threshold)
    {
        ArgumentNullException.ThrowIfNull(file);
        BitmapSource source;
        try
        {
            using var stream = new MemoryStream(file, writable: false);
            source = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception ex) when (ex is FileFormatException or ArgumentException or IOException)
        {
            throw new NotSupportedException("The file is not a picture.", ex);
        }

        var orientation = ExifOrientation.Read(file);
        if (orientation != ExifOrientation.Normal)
        {
            var m = ExifOrientation.StoredToDisplayed(orientation, source.PixelWidth, source.PixelHeight);
            source = new TransformedBitmap(source, new MatrixTransform(m.A, m.B, m.C, m.D, 0, 0));
        }

        var side = Math.Max(source.PixelWidth, source.PixelHeight);
        if (side > MaxPictureSide)
        {
            var factor = (double)MaxPictureSide / side;
            source = new TransformedBitmap(source, new ScaleTransform(factor, factor));
        }

        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var (width, height) = (bgra.PixelWidth, bgra.PixelHeight);
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        SignatureImages.MakeWhiteTransparent(pixels, threshold);
        if (SignatureImages.OpaqueBounds(pixels, width, height) is not { } ink)
        {
            return null;
        }

        var (cropped, bounds) = SignatureImages.Crop(pixels, width, height, ink, margin: 4);
        return Png(BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgra32, null, cropped, bounds.Width * 4));
    }

    /// <summary>A PNG decoded for showing (thumbnails, preview).</summary>
    public static BitmapSource Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        using var stream = new MemoryStream(png, writable: false);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    private static byte[] Render(Rect bounds, Action<DrawingContext> draw)
    {
        var scale = Dpi / 96;
        var width = (int)Math.Ceiling((bounds.Width + (2 * Margin)) * scale);
        var height = (int)Math.Ceiling((bounds.Height + (2 * Margin)) * scale);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(Margin - bounds.X, Margin - bounds.Y));
            draw(context);
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Png(bitmap);
    }

    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
