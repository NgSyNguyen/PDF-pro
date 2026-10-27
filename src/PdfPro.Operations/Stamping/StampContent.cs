using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Organizing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Stamping;

/// <summary>A drawing made once (as a form) and placed on pages: its form and its own size in points.</summary>
internal sealed record StampDrawing(StampForm Form, PdfSize Size);

/// <summary>What stamps are made of and how they are placed: text and pictures drawn upright, then turned into place.</summary>
internal static class StampContent
{
    public static readonly StampLayer WatermarkLayer = new("Watermark", nameof(StampKind.Watermark));
    public static readonly StampLayer BackgroundLayer = new("Background", nameof(StampKind.Background));
    public static readonly StampLayer HeaderFooterLayer = new("Header/Footer", nameof(StampKind.HeaderFooter));
    public static readonly StampLayer PageNumbersLayer = new("Header/Footer", nameof(StampKind.PageNumbers));
    public static readonly StampLayer BatesLayer = new("Header/Footer", nameof(StampKind.Bates));

    /// <summary>Text drawn upright, its lines aligned by the style; null when there is nothing to draw.</summary>
    public static StampDrawing? Text(PdfDocument document, string text, TextStyle style)
    {
        var metrics = FontLibrary.Instance.Metrics(style.FontFamily, style.Bold, style.Italic);
        var natural = TextLayout.Layout(text, metrics, style.FontSize, 0, TextAlignment.Left, style.LineSpacing);
        var width = natural.Lines.Max(l => l.Width);
        if (width <= 0)
        {
            return null;
        }

        // Laid out again in the widest line's width so shorter lines align; nothing wraps.
        var layout = TextLayout.Layout(text, metrics, style.FontSize, width + 0.01, style.Alignment, style.LineSpacing);
        var size = new PdfSize(width + 0.01, layout.Height);
        var font = OverlayPainter.Font(style);
        var brush = new XSolidBrush(OverlayPainter.Color(style.Color));
        var form = StampForms.Draw(document, size, style.FontSize / 2, g => OverlayPainter.DrawLayout(g, layout, font, brush, new XPoint(0, 0)));
        return new StampDrawing(form, size);
    }

    /// <summary>A picture drawn upright at its shown pixel size (one point per pixel; placements scale it).</summary>
    public static StampDrawing Picture(PdfDocument document, string path)
    {
        var (width, height) = OverlayPainter.ImageSize(path);
        var size = new PdfSize(width, height);
        return new StampDrawing(StampForms.Draw(document, size, 0, g => OverlayPainter.DrawImage(g, path, size)), size);
    }

    /// <summary>A filled rectangle of the given size.</summary>
    public static StampDrawing Fill(PdfDocument document, PdfSize size, RgbColor color)
        => new(StampForms.Draw(document, size, 0, g => g.DrawRectangle(new XSolidBrush(OverlayPainter.Color(color)), 0, 0, size.Width, size.Height)), size);

    /// <summary>
    /// Places a drawing with its centre at <paramref name="center"/> of the page as shown, scaled by
    /// <paramref name="scaleX"/> × <paramref name="scaleY"/> and turned <paramref name="angle"/> degrees clockwise.
    /// </summary>
    public static PdfMatrix Placement(StampDrawing drawing, PageGeometry page, ViewPoint center, double angle = 0, double scaleX = 1, double scaleY = 1)
        => drawing.Form.FormToLocal
            .Then(PdfMatrix.Translation(-drawing.Size.Width / 2, -drawing.Size.Height / 2))
            .Then(PdfMatrix.Scaling(scaleX, scaleY))
            .Then(PdfMatrix.Rotation(angle))
            .Then(PdfMatrix.Translation(center.X, center.Y))
            .Then(new PageCoordinateMapper(page, 1).ScreenToPdfMatrix);

    /// <summary>The chosen pages (all when <paramref name="pages"/> is empty), 0-based and sorted.</summary>
    public static IReadOnlyList<int> Pages(string? pages, int pageCount)
        => string.IsNullOrWhiteSpace(pages) ? [.. Enumerable.Range(0, pageCount)] : PageRanges.Parse(pages, pageCount);

    public static OperationValidationResult CheckPages(string? pages, DocumentInfo document, string member)
        => string.IsNullOrWhiteSpace(pages) ? OperationValidationResult.Success : ParameterChecks.Pages(pages, document.PageCount, member);

    public static OperationValidationResult CheckStyle(TextStyle? style, string member)
    {
        if (style is null)
        {
            return OperationValidationResult.Success;
        }

        var errors = new List<OperationValidationError>();
        if (string.IsNullOrWhiteSpace(style.FontFamily) || !FontLibrary.Instance.Contains(style.FontFamily))
        {
            errors.Add(new OperationValidationError(member, "The font is not available on this computer."));
        }

        if (!(style.FontSize is >= OverlayLimits.MinFontSize and <= OverlayLimits.MaxFontSize))
        {
            errors.Add(new OperationValidationError(member, "Font size must be between 1 and 400 points."));
        }

        if (!(style.LineSpacing is >= OverlayLimits.MinLineSpacing and <= OverlayLimits.MaxLineSpacing))
        {
            errors.Add(new OperationValidationError(member, "Line spacing must be between 0.5 and 5."));
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    public static OperationValidationResult CheckImage(string? path, string member)
        => path is null || File.Exists(path)
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(member, "The image file does not exist."));

    /// <summary>Exactly one of two alternatives must be given.</summary>
    public static OperationValidationResult CheckOneOf(bool first, bool second, string member)
        => first ^ second
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(member, "Give exactly one of the two."));
}
