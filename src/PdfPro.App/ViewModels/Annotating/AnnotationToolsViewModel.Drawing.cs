using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services.Annotating;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Operations.Annotating;

namespace PdfPro.App.ViewModels.Annotating;

/// <summary>
/// The drawing tools: free-hand strokes, rectangles and ellipses, lines and arrows, polygons and polylines, text boxes
/// and stamps — their colour and width, and the colour of the selected annotation.
/// </summary>
public sealed partial class AnnotationToolsViewModel
{
    /// <summary>A drag shorter than this (screen pixels) counts as a click: the shape gets its default size.</summary>
    public const double ClickTolerancePixels = 3;

    /// <summary>Length of a line made with a click, in points.</summary>
    public const double DefaultLineLength = 120;

    /// <summary>Width of a text box made with a click, in points.</summary>
    public const double DefaultTextWidth = 200;

    /// <summary>Smallest side of a drawn box, in points.</summary>
    public const double MinBoxSide = 4;

    /// <summary>Size of a rectangle or an ellipse made with a click (as shown), in points.</summary>
    public static readonly PdfSize DefaultShapeSize = new(120, 80);

    /// <summary>Size of a stamp (as shown), in points.</summary>
    public static readonly PdfSize StampSize = new(170, 56);

    public static readonly RgbColor DefaultDrawColor = new(220, 38, 38);

    /// <summary>The colours offered on the tool bar.</summary>
    public IReadOnlyList<ColorSwatch> Swatches => StampDialogViewModel.StandardSwatches;

    /// <summary>Line widths offered on the tool bar, in points.</summary>
    public IReadOnlyList<double> StrokeWidths { get; } = [1, 2, 3, 5, 8];

    /// <summary>The words of the stamps offered.</summary>
    public IReadOnlyList<string> StampTexts { get; } =
    [
        Strings.AnnotationStamp_Approved, Strings.AnnotationStamp_Seen, Strings.AnnotationStamp_NotApproved, Strings.AnnotationStamp_Urgent,
        Strings.AnnotationStamp_Confidential, Strings.AnnotationStamp_Draft, Strings.AnnotationStamp_Copy,
    ];

    /// <summary>Colour of new drawings, text boxes and stamps.</summary>
    [ObservableProperty]
    public partial RgbColor DrawColor { get; set; } = DefaultDrawColor;

    /// <summary>Line width of new strokes, shapes and lines, in points.</summary>
    [ObservableProperty]
    public partial double StrokeWidth { get; set; } = 2;

    /// <summary>The words of the next stamp.</summary>
    [ObservableProperty]
    public partial string StampText { get; set; } = Strings.AnnotationStamp_Approved;

    /// <summary>The next stamp has the date under its words.</summary>
    [ObservableProperty]
    public partial bool StampWithDate { get; set; } = true;

    /// <summary>Font of new text boxes (their colour is <see cref="DrawColor"/>).</summary>
    public TextStyle TextStyle { get; set; } = TextStyle.Default with { FontSize = 12 };

    /// <summary>A tool that draws on the page is chosen.</summary>
    public bool IsDrawingTool => Tool is AnnotationTool.FreeText or AnnotationTool.Ink or AnnotationTool.Rectangle or AnnotationTool.Ellipse
        or AnnotationTool.Line or AnnotationTool.Arrow or AnnotationTool.Polygon or AnnotationTool.Polyline or AnnotationTool.Stamp;

    /// <summary>The chosen tool draws strokes (the width box applies).</summary>
    public bool HasWidth => Tool is AnnotationTool.Ink or AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Line
        or AnnotationTool.Arrow or AnnotationTool.Polygon or AnnotationTool.Polyline;

    /// <summary>Something is being drawn (the mouse is captured for it; a polygon waits for more clicks).</summary>
    public bool IsDrawing => _draw is not null;

    /// <summary>What is being drawn on a page, for the page layer to show; null when nothing is.</summary>
    public AnnotationItem? PreviewOn(Guid pageKey)
        => _draw is { } draw && draw.PageKey == pageKey ? Build(draw, preview: true) : null;

    /// <summary>A swatch: the colour of the selected annotation (one undoable step), or of the next drawings.</summary>
    [RelayCommand]
    public void SetColor(string? hex)
    {
        if (!RgbColor.TryParse(hex, out var parsed))
        {
            return;
        }

        var color = parsed.Value;
        if (Selected is { } target && Find(target) is { } item && IndexOf(target.PageKey) is { } index)
        {
            var changed = item is FreeTextAnnotation box ? box with { Color = color, Style = box.Style with { Color = color } } : item with { Color = color };
            _apply(_operations.Get<UpdateAnnotationsOperation>(), new UpdateAnnotationsParams(index + 1, [changed with { Modified = _time.GetLocalNow() }]));
            return;
        }

        DrawColor = color;
    }

    /// <summary>"Bình luận…": opens the comment (the text of a text box) of the selected annotation.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void EditComment()
    {
        if (Selected is { } target)
        {
            BeginEditContents(target);
        }
    }

    private bool PressDrawing(int pageIndex, VirtualPage page, PdfPoint point, int clickCount)
    {
        switch (Tool)
        {
            case AnnotationTool.Stamp:
                AddStamp(page, point);
                return true;
            case AnnotationTool.Polygon or AnnotationTool.Polyline when _draw is { } current && current.PageKey == page.Key:
                if (clickCount >= 2)
                {
                    FinishDrawing(); // the double-click's first click already placed the last corner
                }
                else
                {
                    _draw = current with { Points = [.. current.Points, point], Current = point };
                }

                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            default:
                _draw = new Draw(page.Key, pageIndex, point, [point], point);
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
        }
    }

    private void DrawTo(ViewPoint viewport)
    {
        if (_draw is not { } draw || PointOn(draw.PageIndex, viewport) is not { } point)
        {
            return;
        }

        if (Tool == AnnotationTool.Ink)
        {
            // A new point once the pen has moved a pixel or so: smooth enough, not too many points.
            var last = draw.Points[^1];
            var step = 1.5 / _viewer.Layout.Scale;
            if (Math.Abs(point.X - last.X) < step && Math.Abs(point.Y - last.Y) < step)
            {
                return;
            }

            _draw = draw with { Points = [.. draw.Points, point], Current = point };
        }
        else
        {
            _draw = draw with { Current = point };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseDrawing()
    {
        if (Tool is AnnotationTool.Polygon or AnnotationTool.Polyline)
        {
            Changed?.Invoke(this, EventArgs.Empty); // more corners to come; a double-click ends it
            return;
        }

        FinishDrawing();
    }

    /// <summary>Turns what was drawn into an annotation (one undoable step); a text box first asks for its text.</summary>
    private void FinishDrawing()
    {
        if (_draw is not { } draw)
        {
            return;
        }

        _draw = null;
        if (Build(draw, preview: false) is not { } item || IndexOf(draw.PageKey) is not { } index)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (item is FreeTextAnnotation box)
        {
            // Typed in the comment box; added with its text when kept.
            _newTextBox = box;
            ContentsDraft = string.Empty;
            ContentsHeader = Strings.Annotation_TextBoxHeader;
            EditingContents = new AnnotationTarget(draw.PageKey, box.Id);
            Tool = AnnotationTool.None;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_apply(_operations.Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(index + 1, [item])) && Tool != AnnotationTool.Ink)
        {
            // Pens keep drawing; shapes and lines are selected for adjusting and the tool goes back to selecting.
            Tool = AnnotationTool.None;
            Selected = new AnnotationTarget(draw.PageKey, item.Id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The annotation a gesture makes; null while it cannot make one yet (or never: too few corners).</summary>
    private AnnotationItem? Build(Draw draw, bool preview)
    {
        if (_geometryOf(draw.PageKey) is not { } geometry)
        {
            return null;
        }

        // Shapes are measured on the page as shown (upright for the user), then turned into its PDF space.
        var mapper = new PageCoordinateMapper(geometry, 1);
        var start = mapper.PdfToScreen(draw.Start);
        var end = mapper.PdfToScreen(draw.Current);
        var tolerance = ClickTolerancePixels / _viewer.Layout.Scale;
        var isClick = Math.Abs(end.X - start.X) < tolerance && Math.Abs(end.Y - start.Y) < tolerance;
        var now = _time.GetLocalNow();
        var id = preview ? "preview" : NewId();
        var author = preview ? string.Empty : _author();
        PdfRect ShownBox(PdfSize clickSize, double minHeight = MinBoxSide)
        {
            var area = isClick ? new ViewRect(start.X, start.Y, clickSize.Width, clickSize.Height) : ViewRect.FromCorners(start, end);
            return mapper.ScreenRectToPdf(new ViewRect(area.X, area.Y, Math.Max(area.Width, MinBoxSide), Math.Max(area.Height, minHeight)));
        }

        switch (Tool)
        {
            case AnnotationTool.Ink:
                return new InkAnnotation(id, DrawColor, 1, author, now, now, string.Empty, StrokeWidth, [draw.Points]);
            case AnnotationTool.Rectangle or AnnotationTool.Ellipse:
                var shape = Tool == AnnotationTool.Ellipse ? NoteShape.Circle : NoteShape.Square;
                return new ShapeAnnotation(id, DrawColor, 1, author, now, now, string.Empty, shape, ShownBox(DefaultShapeSize), StrokeWidth, null);
            case AnnotationTool.Line or AnnotationTool.Arrow:
                var to = isClick ? mapper.ScreenToPdf(new ViewPoint(start.X + DefaultLineLength, start.Y)) : draw.Current;
                var ending = Tool == AnnotationTool.Arrow ? LineEnding.OpenArrow : LineEnding.None;
                return new LineAnnotation(id, DrawColor, 1, author, now, now, string.Empty, draw.Start, to, StrokeWidth, LineEnding.None, ending);
            case AnnotationTool.Polygon or AnnotationTool.Polyline:
                // The preview follows the mouse; the annotation keeps the clicked corners (a double-click repeats one).
                var closed = Tool == AnnotationTool.Polygon;
                List<PdfPoint> vertices = preview ? [.. draw.Points, draw.Current] : [.. draw.Points.Where((p, i) => i == 0 || p != draw.Points[i - 1])];
                if (vertices.Count < (closed && !preview ? 3 : 2))
                {
                    return null;
                }

                return new PolyAnnotation(id, DrawColor, 1, author, now, now, string.Empty, closed && vertices.Count >= 3, vertices, StrokeWidth);
            case AnnotationTool.FreeText:
                var style = TextStyle with { Color = DrawColor };
                var lineHeight = PageEditorViewModel.TextHeight(style, string.Empty) + (2 * AnnotationDrawing.TextPadding);
                return new FreeTextAnnotation(id, DrawColor, 1, author, now, now, string.Empty, ShownBox(new PdfSize(DefaultTextWidth, lineHeight), lineHeight), style, null, null, [], geometry.RotationDegrees);
            default:
                return null;
        }
    }

    private void AddStamp(VirtualPage page, PdfPoint at)
    {
        if (_geometryOf(page.Key) is not { } geometry || IndexOf(page.Key) is not { } index)
        {
            return;
        }

        var mapper = new PageCoordinateMapper(geometry, 1);
        var shown = mapper.PdfToScreen(at);
        var box = mapper.ScreenRectToPdf(new ViewRect(shown.X, shown.Y, StampSize.Width, StampSize.Height));
        var now = _time.GetLocalNow();
        var words = StampWithDate ? $"{StampText}\n{now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}" : StampText;
        var stamp = new StampAnnotation(NewId(), DrawColor, 1, _author(), now, now, string.Empty, box, words, null, geometry.RotationDegrees);
        if (_apply(_operations.Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(index + 1, [stamp])))
        {
            Tool = AnnotationTool.None;
            Selected = new AnnotationTarget(page.Key, stamp.Id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a new text box with its text (nothing when none was typed), tall enough for it.</summary>
    private void AddTextBox(Guid pageKey, FreeTextAnnotation draft, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IndexOf(pageKey) is not { } index)
        {
            return;
        }

        var now = _time.GetLocalNow();
        var box = Fit(pageKey, draft with { Author = _author(), Created = now, Modified = now }, text);
        if (_apply(_operations.Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(index + 1, [box])))
        {
            Selected = new AnnotationTarget(pageKey, box.Id);
        }
    }

    /// <summary>A text box with <paramref name="text"/>, grown downwards (as shown) to hold every line.</summary>
    private FreeTextAnnotation Fit(Guid pageKey, FreeTextAnnotation box, string text)
    {
        if (_geometryOf(pageKey) is not { } geometry)
        {
            return box with { Contents = text };
        }

        var mapper = new PageCoordinateMapper(geometry, 1);
        var shown = mapper.PdfRectToScreen(box.Box);
        var needed = PageEditorViewModel.TextHeight(box.Style, text, shown.Width - (2 * AnnotationDrawing.TextPadding)) + (2 * AnnotationDrawing.TextPadding);
        var area = new ViewRect(shown.X, shown.Y, shown.Width, Math.Max(shown.Height, needed));
        return box with { Contents = text, Box = mapper.ScreenRectToPdf(area) };
    }

    /// <summary>A gesture of a drawing tool: its page, where it started, the points so far, where the mouse is.</summary>
    private sealed record Draw(Guid PageKey, int PageIndex, PdfPoint Start, IReadOnlyList<PdfPoint> Points, PdfPoint Current);
}
