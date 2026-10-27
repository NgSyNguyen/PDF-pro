using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Stamping;

/// <summary>
/// Paints overlay objects into a document as form XObjects placed on their page. A boxed object is drawn upright in its
/// own box and turned into place by <see cref="OverlayBox.LocalToPdf"/>; a line is drawn in the page's displayed space.
/// Text is laid out by <see cref="TextLayout"/> with the metrics of the very face PDFsharp embeds.
/// </summary>
internal static class OverlayPainter
{
    public static FormPlacement Paint(PdfDocument document, PageGeometry page, OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            TextOverlay text => Boxed(document, page, text.Box, Margin(text), g => DrawText(g, text), text.Opacity),
            ImageOverlay image => Boxed(document, page, image.Box, 1, g => DrawImage(g, image.ImagePath, image.Box.Size), image.Opacity),
            ShapeOverlay shape => Boxed(document, page, shape.Box, (shape.EffectiveStroke?.Width ?? 0) + 1, g => DrawShape(g, shape), shape.Opacity),
            LineOverlay line => Line(document, page, line),
            _ => throw new ArgumentException($"Unknown overlay object {item.GetType().Name}.", nameof(item)),
        };
    }

    /// <summary>Draws a picture file stretched over (0, 0)–size, upright according to its EXIF orientation.</summary>
    public static void DrawImage(XGraphics graphics, string path, PdfSize size)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        var bytes = File.ReadAllBytes(path);
        var orientation = ExifOrientation.Read(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        using var image = XImage.FromStream(stream);
        var (storedWidth, storedHeight) = (image.PixelWidth, image.PixelHeight);
        var (shownWidth, shownHeight) = ExifOrientation.DisplayedSize(orientation, storedWidth, storedHeight);
        var transform = ExifOrientation.StoredToDisplayed(orientation, storedWidth, storedHeight)
            .Then(PdfMatrix.Scaling(size.Width / shownWidth, size.Height / shownHeight));

        var state = graphics.Save();
        graphics.MultiplyTransform(new XMatrix(transform.A, transform.B, transform.C, transform.D, transform.E, transform.F));
        graphics.DrawImage(image, 0, 0, storedWidth, storedHeight);
        graphics.Restore(state);
    }

    /// <summary>The pixel size of a picture file as shown (EXIF orientation applied).</summary>
    public static (int Width, int Height) ImageSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, writable: false);
        using var image = XImage.FromStream(stream);
        return ExifOrientation.DisplayedSize(ExifOrientation.Read(bytes), image.PixelWidth, image.PixelHeight);
    }

    public static XColor Color(RgbColor color) => XColor.FromArgb(color.R, color.G, color.B);

    public static XFont Font(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        FontLibrary.Register();
        var weight = (style.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (style.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        return new XFont(style.FontFamily, style.FontSize, weight);
    }

    /// <summary>Draws laid-out text: each line piece at its place, baselines from the top of <paramref name="origin"/>.</summary>
    public static void DrawLayout(XGraphics graphics, TextLayoutResult layout, XFont font, XBrush brush, XPoint origin)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(layout);
        foreach (var line in layout.Lines)
        {
            foreach (var segment in line.Segments)
            {
                graphics.DrawString(segment.Text, font, brush, origin.X + segment.X, origin.Y + line.Baseline, XStringFormats.BaseLineLeft);
            }
        }
    }

    private static FormPlacement Boxed(PdfDocument document, PageGeometry page, OverlayBox box, double margin, Action<XGraphics> draw, double opacity)
    {
        var form = StampForms.Draw(document, box.Size, margin, draw);
        return new FormPlacement(form.Form, form.FormToLocal.Then(box.LocalToPdf(page)), opacity);
    }

    private static FormPlacement Line(PdfDocument document, PageGeometry page, LineOverlay line)
    {
        var mapper = new PageCoordinateMapper(page, 1);
        var start = mapper.PdfToScreen(line.Start);
        var end = mapper.PdfToScreen(line.End);
        var shown = page.DisplaySize;
        var margin = LineOverlay.ArrowLength(line.Stroke.Width) + line.Stroke.Width;
        var form = StampForms.Draw(document, shown, margin, g => DrawLine(g, line, start, end));
        return new FormPlacement(form.Form, form.FormToLocal.Then(mapper.ScreenToPdfMatrix), line.Opacity);
    }

    private static double Margin(TextOverlay text)
        => (text.Border?.Width ?? 0) + text.Style.FontSize; // room for overhanging letters and a border on the edge

    private static void DrawText(XGraphics graphics, TextOverlay text)
    {
        var (width, height) = (text.Box.Size.Width, text.Box.Size.Height);
        if (text.Fill is { } fill)
        {
            graphics.DrawRectangle(new XSolidBrush(Color(fill)), 0, 0, width, height);
        }

        if (text.Border is { Width: > 0 } border)
        {
            graphics.DrawRectangle(new XPen(Color(border.Color), border.Width), 0, 0, width, height);
        }

        var style = text.Style;
        var metrics = FontLibrary.Instance.Metrics(style.FontFamily, style.Bold, style.Italic);
        var layout = TextLayout.Layout(text.Text, metrics, style.FontSize, width - (2 * text.Padding), style.Alignment, style.LineSpacing);
        DrawLayout(graphics, layout, Font(style), new XSolidBrush(Color(style.Color)), new XPoint(text.Padding, text.Padding));
    }

    private static void DrawShape(XGraphics graphics, ShapeOverlay shape)
    {
        var (width, height) = (shape.Box.Size.Width, shape.Box.Size.Height);
        var pen = shape.EffectiveStroke is { Width: > 0 } stroke ? new XPen(Color(stroke.Color), stroke.Width) : null;
        var brush = shape.EffectiveFill is { } fill ? new XSolidBrush(Color(fill)) : null;
        if (shape.Kind == ShapeKind.Ellipse)
        {
            if (brush is not null)
            {
                graphics.DrawEllipse(brush, 0, 0, width, height);
            }

            if (pen is not null)
            {
                graphics.DrawEllipse(pen, 0, 0, width, height);
            }

            return;
        }

        if (brush is not null)
        {
            graphics.DrawRectangle(brush, 0, 0, width, height);
        }

        if (pen is not null)
        {
            graphics.DrawRectangle(pen, 0, 0, width, height);
        }
    }

    private static void DrawLine(XGraphics graphics, LineOverlay line, ViewPoint start, ViewPoint end)
    {
        var color = Color(line.Stroke.Color);
        var pen = new XPen(color, line.Stroke.Width) { LineCap = XLineCap.Round };
        var brush = new XSolidBrush(color);
        var (dx, dy) = (end.X - start.X, end.Y - start.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        var (ux, uy) = (dx / length, dy / length);
        var head = Math.Min(LineOverlay.ArrowLength(line.Stroke.Width), length / 2);

        // The stroke stops where an arrow head begins, so a thick line does not poke through its tip.
        var from = line.ArrowAtStart ? new XPoint(start.X + (ux * head), start.Y + (uy * head)) : new XPoint(start.X, start.Y);
        var to = line.ArrowAtEnd ? new XPoint(end.X - (ux * head), end.Y - (uy * head)) : new XPoint(end.X, end.Y);
        graphics.DrawLine(pen, from, to);
        if (line.ArrowAtEnd)
        {
            graphics.DrawPolygon(brush, ArrowHead(end, ux, uy, head), XFillMode.Winding);
        }

        if (line.ArrowAtStart)
        {
            graphics.DrawPolygon(brush, ArrowHead(start, -ux, -uy, head), XFillMode.Winding);
        }
    }

    private static XPoint[] ArrowHead(ViewPoint tip, double ux, double uy, double length)
    {
        var half = length * 0.45;
        var (bx, by) = (tip.X - (ux * length), tip.Y - (uy * length));
        return [new XPoint(tip.X, tip.Y), new XPoint(bx - (uy * half), by + (ux * half)), new XPoint(bx + (uy * half), by - (ux * half))];
    }
}
