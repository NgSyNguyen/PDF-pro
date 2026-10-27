using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;

namespace PdfPro.App.Services.Editing;

/// <summary>A picture file decoded for the screen, with its EXIF orientation (WPF does not apply it).</summary>
public sealed record ScreenImage(BitmapSource Bitmap, int Orientation);

/// <summary>Pictures of the page editor, decoded once per file.</summary>
public sealed class ScreenImages
{
    private readonly ConcurrentDictionary<string, ScreenImage> _images = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="NotSupportedException">The file is not a picture WPF can decode.</exception>
    public ScreenImage Get(string path) => _images.GetOrAdd(path, static p =>
    {
        var bytes = File.ReadAllBytes(p);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes, writable: false);
        bitmap.EndInit();
        bitmap.Freeze();
        return new ScreenImage(bitmap, ExifOrientation.Read(bytes));
    });
}

/// <summary>
/// Draws overlay objects on screen as the PDF writer will write them (same layout, same faces, same geometry): the
/// page editor's preview. Sizes in points are multiplied by the page's scale; places come from
/// <see cref="PageCoordinateMapper"/>.
/// </summary>
public static class OverlayDrawing
{
    private static readonly ConcurrentDictionary<Uri, GlyphTypeface> Slanted = new();

    public static void Draw(DrawingContext context, OverlayItem item, PageCoordinateMapper mapper, ScreenFonts fonts, ScreenImages images, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(mapper);
        context.PushOpacity(item.Opacity);
        if (item is LineOverlay line)
        {
            DrawLine(context, line, mapper);
        }
        else
        {
            var (box, size) = item switch
            {
                TextOverlay t => (t.Box.ToView(mapper), t.Box.Size),
                ImageOverlay i => (i.Box.ToView(mapper), i.Box.Size),
                ShapeOverlay s => (s.Box.ToView(mapper), s.Box.Size),
                _ => throw new ArgumentException("Unknown overlay object.", nameof(item)),
            };

            // Local space: the box's top-left at the origin, x along its top edge, in points; then scaled and turned.
            var local = PdfMatrix.Translation(-size.Width / 2, -size.Height / 2)
                .Then(PdfMatrix.Scaling(mapper.Scale, mapper.Scale))
                .Then(PdfMatrix.Rotation(box.Angle))
                .Then(PdfMatrix.Translation(box.Center.X, box.Center.Y));
            context.PushTransform(new MatrixTransform(ToMatrix(local)));
            switch (item)
            {
                case TextOverlay text:
                    DrawText(context, text, fonts, pixelsPerDip, mapper.Scale);
                    break;
                case ImageOverlay image:
                    DrawImage(context, images.Get(image.ImagePath), size);
                    break;
                case ShapeOverlay shape:
                    DrawShape(context, shape, mapper.Scale);
                    break;
            }

            context.Pop();
        }

        context.Pop();
    }

    public static Color ToColor(RgbColor color) => Color.FromRgb(color.R, color.G, color.B);

    public static Matrix ToMatrix(PdfMatrix m) => new(m.A, m.B, m.C, m.D, m.E, m.F);

    /// <summary>
    /// Text laid out exactly like the PDF writer does it, drawn glyph by glyph with the font's own advance widths.
    /// Coordinates are local points; <paramref name="scale"/> only sets the pen width of outlines.
    /// </summary>
    public static void DrawText(DrawingContext context, TextOverlay text, ScreenFonts fonts, double pixelsPerDip, double scale)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fonts);
        var (width, height) = (text.Box.Size.Width, text.Box.Size.Height);
        var border = text.Border is { Width: > 0 } b ? new Pen(new SolidColorBrush(ToColor(b.Color)), b.Width) : null;
        if (text.Fill is { } fill || border is not null)
        {
            context.DrawRectangle(text.Fill is { } f ? new SolidColorBrush(ToColor(f)) : null, border, new Rect(0, 0, width, height));
        }

        var style = text.Style;
        var metrics = FontLibrary.Instance.Metrics(style.FontFamily, style.Bold, style.Italic);
        var layout = TextLayout.Layout(text.Text, metrics, style.FontSize, width - (2 * text.Padding), style.Alignment, style.LineSpacing);
        var face = fonts.Face(style);
        var brush = new SolidColorBrush(ToColor(style.Color));
        brush.Freeze();
        foreach (var line in layout.Lines)
        {
            foreach (var segment in line.Segments)
            {
                // One local unit (a point) is `scale` device-independent pixels.
                if (Run(face, segment.Text, style.FontSize, new Point(text.Padding + segment.X, text.Padding + line.Baseline), pixelsPerDip * scale) is { } run)
                {
                    context.DrawGlyphRun(brush, run);
                }
            }
        }
    }

    /// <summary>One run of glyphs at the font's advance widths (what PDFsharp writes), or null for blank text.</summary>
    public static GlyphRun? Run(ScreenFace face, string text, double emSize, Point baselineOrigin, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(face);
        var glyphs = new List<ushort>();
        var advances = new List<double>();
        foreach (var rune in TextNormalizer.ToNfc(text).EnumerateRunes())
        {
            var glyph = face.Typeface.CharacterToGlyphMap.TryGetValue(rune.Value, out var found) ? found : (ushort)0;
            glyphs.Add(glyph);
            advances.Add(face.Typeface.AdvanceWidths[glyph] * emSize);
        }

        if (glyphs.Count == 0)
        {
            return null;
        }

        var typeface = face.SimulateItalic ? SimulatedItalic(face.Typeface) : face.Typeface;
        return new GlyphRun(typeface, 0, false, emSize, (float)Math.Max(0.01, pixelsPerDip), glyphs, baselineOrigin, advances, null, null, null, null, null, null);
    }

    private static GlyphTypeface SimulatedItalic(GlyphTypeface upright)
        => Slanted.GetOrAdd(upright.FontUri, uri => new GlyphTypeface(uri, StyleSimulations.ItalicSimulation));

    private static void DrawImage(DrawingContext context, ScreenImage image, PdfSize size)
    {
        var (storedWidth, storedHeight) = (image.Bitmap.PixelWidth, image.Bitmap.PixelHeight);
        var (shownWidth, shownHeight) = ExifOrientation.DisplayedSize(image.Orientation, storedWidth, storedHeight);
        var transform = ExifOrientation.StoredToDisplayed(image.Orientation, storedWidth, storedHeight)
            .Then(PdfMatrix.Scaling(size.Width / shownWidth, size.Height / shownHeight));
        context.PushTransform(new MatrixTransform(ToMatrix(transform)));
        context.DrawImage(image.Bitmap, new Rect(0, 0, storedWidth, storedHeight));
        context.Pop();
    }

    private static void DrawShape(DrawingContext context, ShapeOverlay shape, double scale)
    {
        var (width, height) = (shape.Box.Size.Width, shape.Box.Size.Height);
        var pen = shape.EffectiveStroke is { Width: > 0 } stroke ? new Pen(new SolidColorBrush(ToColor(stroke.Color)), stroke.Width) : null;
        var brush = shape.EffectiveFill is { } fill ? new SolidColorBrush(ToColor(fill)) : null;
        if (pen is null && brush is null)
        {
            // Keep an empty shape visible while it is being edited.
            pen = new Pen(Brushes.Gray, 1 / scale);
        }

        if (shape.Kind == ShapeKind.Ellipse)
        {
            context.DrawEllipse(brush, pen, new Point(width / 2, height / 2), width / 2, height / 2);
        }
        else
        {
            context.DrawRectangle(brush, pen, new Rect(0, 0, width, height));
        }
    }

    private static void DrawLine(DrawingContext context, LineOverlay line, PageCoordinateMapper mapper)
    {
        var start = mapper.PdfToScreen(line.Start);
        var end = mapper.PdfToScreen(line.End);
        var (dx, dy) = (end.X - start.X, end.Y - start.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-6)
        {
            return;
        }

        var brush = new SolidColorBrush(ToColor(line.Stroke.Color));
        var pen = new Pen(brush, line.Stroke.Width * mapper.Scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var (ux, uy) = (dx / length, dy / length);
        var head = Math.Min(LineOverlay.ArrowLength(line.Stroke.Width) * mapper.Scale, length / 2);
        var from = line.ArrowAtStart ? new Point(start.X + (ux * head), start.Y + (uy * head)) : new Point(start.X, start.Y);
        var to = line.ArrowAtEnd ? new Point(end.X - (ux * head), end.Y - (uy * head)) : new Point(end.X, end.Y);
        context.DrawLine(pen, from, to);
        if (line.ArrowAtEnd)
        {
            context.DrawGeometry(brush, null, ArrowHead(end, ux, uy, head));
        }

        if (line.ArrowAtStart)
        {
            context.DrawGeometry(brush, null, ArrowHead(start, -ux, -uy, head));
        }
    }

    private static StreamGeometry ArrowHead(ViewPoint tip, double ux, double uy, double length)
    {
        var half = length * 0.45;
        var (bx, by) = (tip.X - (ux * length), tip.Y - (uy * length));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(tip.X, tip.Y), isFilled: true, isClosed: true);
            context.LineTo(new Point(bx - (uy * half), by + (ux * half)), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(bx + (uy * half), by - (ux * half)), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }
}
