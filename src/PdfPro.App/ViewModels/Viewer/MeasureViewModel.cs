using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services.Annotating;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Measuring;
using PdfPro.Core.Overlay;
using PdfPro.Operations.Annotating;

namespace PdfPro.App.ViewModels.Viewer;

/// <summary>A finished measurement on a page (PDF space of the page's source).</summary>
public sealed record Measurement(Guid Id, Guid PageKey, MeasureKind Kind, IReadOnlyList<PdfPoint> Points);

/// <summary>
/// "Đo" of a document tab: distances (two clicks), lengths along a line and areas (a click per corner, a double-click —
/// or for an area a click on the first corner — to finish), in mm, cm, m, in, ft or pt at a drawing's scale ("1 : 100");
/// Shift keeps lines at multiples of 45°. Measurements stay on the pages while the tool is on, until cleared or saved
/// into the document as line and polygon comments labelled with their value (one undoable step).
/// </summary>
public sealed partial class MeasureViewModel : ObservableObject
{
    /// <summary>A click this close (pixels) to an area's first corner closes it.</summary>
    public const double CloseTolerance = 8;

    /// <summary>The colour measurements are drawn and saved in.</summary>
    public static readonly RgbColor Ink = new(0x00, 0x78, 0xD4);

    private readonly DocumentViewerViewModel _viewer;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly List<Measurement> _measurements = [];
    private (Guid PageKey, List<PdfPoint> Points)? _current;
    private PdfPoint? _hover;

    public MeasureViewModel(DocumentViewerViewModel viewer, Func<Guid, PageGeometry?> geometryOf)
    {
        _viewer = viewer;
        _geometryOf = geometryOf;
        UnitOptions =
        [
            new(LengthUnit.Millimetre, Strings.Measure_Unit_Millimetre),
            new(LengthUnit.Centimetre, Strings.Measure_Unit_Centimetre),
            new(LengthUnit.Metre, Strings.Measure_Unit_Metre),
            new(LengthUnit.Inch, Strings.Measure_Unit_Inch),
            new(LengthUnit.Foot, Strings.Measure_Unit_Foot),
            new(LengthUnit.Point, Strings.Measure_Unit_Point),
        ];
    }

    /// <summary>The pages need drawing again.</summary>
    public event EventHandler? Changed;

    /// <summary>Saves annotations by page (1-based page numbers) as one undoable step; true when done.</summary>
    public Func<IReadOnlyList<PageAnnotations>, bool>? AddAnnotations { get; init; }

    /// <summary>The name saved measurements carry as their author.</summary>
    public Func<string> Author { get; init; } = () => string.Empty;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public IReadOnlyList<SettingOption<LengthUnit>> UnitOptions { get; }

    /// <summary>The tool is on: the viewer's mouse measures.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDistanceTool), nameof(IsPerimeterTool), nameof(IsAreaTool), nameof(Hint), nameof(Status))]
    public partial MeasureKind Kind { get; set; }

    public bool IsDistanceTool
    {
        get => Kind == MeasureKind.Distance;
        set => SetKind(value, MeasureKind.Distance);
    }

    public bool IsPerimeterTool
    {
        get => Kind == MeasureKind.Perimeter;
        set => SetKind(value, MeasureKind.Perimeter);
    }

    public bool IsAreaTool
    {
        get => Kind == MeasureKind.Area;
        set => SetKind(value, MeasureKind.Area);
    }

    [ObservableProperty]
    public partial LengthUnit Unit { get; set; } = LengthUnit.Millimetre;

    /// <summary>Real lengths are this many times those on paper (100 for a plan at 1 : 100).</summary>
    [ObservableProperty]
    public partial double? Scale { get; set; } = 1;

    /// <summary>The value of the measurement being made (to the mouse), else of the last one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    public partial string? ResultText { get; private set; }

    /// <summary>What the bar says: the value measured, else how to use the tool.</summary>
    public string Status => ResultText ?? Hint;

    /// <summary>How to use the tool chosen.</summary>
    public string Hint => Kind switch
    {
        MeasureKind.Distance => Strings.Measure_Hint_Distance,
        MeasureKind.Perimeter => Strings.Measure_Hint_Perimeter,
        _ => Strings.Measure_Hint_Area,
    };

    public IReadOnlyList<Measurement> Measurements => _measurements;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand), nameof(SaveCommand))]
    public partial bool HasMeasurements { get; private set; }

    /// <summary>A measurement is being made (its first point is down).</summary>
    public bool IsMeasuring => _current is not null;

    public PageGeometry? GeometryOf(Guid pageKey) => _geometryOf(pageKey);

    public IEnumerable<Measurement> MeasurementsOn(Guid pageKey) => _measurements.Where(m => m.PageKey == pageKey);

    /// <summary>The measurement being made on <paramref name="pageKey"/>, to the mouse; null when none is.</summary>
    public Measurement? PreviewOn(Guid pageKey)
        => _current is { } current && current.PageKey == pageKey
            ? new Measurement(Guid.Empty, pageKey, Kind, [.. current.Points, .. _hover is { } hover ? [hover] : Array.Empty<PdfPoint>()])
            : null;

    /// <summary>The value of a measurement, written out ("125,4 mm", "6 m² · chu vi 10 m"); empty with too few points.</summary>
    public string LabelOf(Measurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (Measure(measurement) is not { } result)
        {
            return string.Empty;
        }

        var length = FormatValue(result.Length, result.Unit, squared: false);
        return result.Kind switch
        {
            MeasureKind.Area => string.Format(CultureInfo.CurrentCulture, Strings.Measure_Label_Area, FormatValue(result.Area!.Value, result.Unit, squared: true), length),
            _ => length,
        };
    }

    /// <summary>A click at <paramref name="viewport"/>: starts, continues or finishes a measurement. True when it measured.</summary>
    /// <param name="snap">Shift is held: the line from the last point keeps to a multiple of 45°.</param>
    public bool Press(ViewPoint viewport, int clickCount, bool snap)
    {
        if (!IsActive)
        {
            return false;
        }

        if (_current is not { } current)
        {
            if (Locate(viewport) is not { } start)
            {
                return false;
            }

            _current = (start.PageKey, [start.Point]);
            _hover = null;
            Update();
            return true;
        }

        if (PointOn(current.PageKey, viewport) is not { } point)
        {
            return true;
        }

        point = snap ? Core.Measuring.Measurements.Snap(current.Points[^1], point) : point;
        switch (Kind)
        {
            case MeasureKind.Distance:
                if (point != current.Points[0])
                {
                    Finish([current.Points[0], point]);
                }

                break;
            case MeasureKind.Perimeter when clickCount >= 2:
                Finish(current.Points);
                break;
            case MeasureKind.Area when clickCount >= 2 || (current.Points.Count >= 3 && IsNearFirst(current.PageKey, current.Points[0], viewport)):
                Finish(current.Points);
                break;
            default:
                if (point != current.Points[^1])
                {
                    current.Points.Add(point);
                }

                Update();
                break;
        }

        return true;
    }

    /// <summary>The mouse moved: the measurement being made follows it.</summary>
    public void MoveTo(ViewPoint viewport, bool snap)
    {
        if (_current is not { } current || PointOn(current.PageKey, viewport) is not { } point)
        {
            return;
        }

        _hover = snap ? Core.Measuring.Measurements.Snap(current.Points[^1], point) : point;
        Update();
    }

    /// <summary>Esc: drops the measurement being made, else turns the tool off.</summary>
    public bool Escape()
    {
        if (_current is not null)
        {
            Cancel();
            return true;
        }

        if (IsActive)
        {
            IsActive = false;
            return true;
        }

        return false;
    }

    /// <summary>Backspace: takes back the last corner of the line or area being made.</summary>
    public bool RemoveLastPoint()
    {
        if (_current is not { } current)
        {
            return false;
        }

        if (current.Points.Count <= 1)
        {
            Cancel();
        }
        else
        {
            current.Points.RemoveAt(current.Points.Count - 1);
            Update();
        }

        return true;
    }

    /// <summary>Measurements of pages that are gone are dropped.</summary>
    public void OnPagesChanged()
    {
        var removed = _measurements.RemoveAll(m => _viewer.Document.IndexOf(m.PageKey) < 0);
        if (_current is { } current && _viewer.Document.IndexOf(current.PageKey) < 0)
        {
            _current = null;
            _hover = null;
        }

        if (removed > 0 || _current is null)
        {
            Update();
        }
    }

    /// <summary>"Xóa các phép đo".</summary>
    [RelayCommand(CanExecute = nameof(HasMeasurements))]
    private void Clear()
    {
        _measurements.Clear();
        _current = null;
        _hover = null;
        Update();
    }

    /// <summary>
    /// "Lưu vào trang": every measurement becomes a comment on its page — a line with arrow heads, or a line or
    /// polygon — and a label with its value; all in one undoable step, then they are cleared.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasMeasurements))]
    private void Save()
    {
        if (AddAnnotations is null)
        {
            return;
        }

        var now = Time.GetLocalNow();
        var author = Author();
        var pages = new List<PageAnnotations>();
        foreach (var group in _measurements.GroupBy(m => m.PageKey))
        {
            var index = _viewer.Document.IndexOf(group.Key);
            if (index < 0 || _geometryOf(group.Key) is not { } geometry)
            {
                continue;
            }

            var items = new List<AnnotationItem>();
            foreach (var measurement in group)
            {
                var label = LabelOf(measurement);
                items.Add(measurement.Kind switch
                {
                    MeasureKind.Distance => new LineAnnotation(NewId(), Ink, 1, author, now, now, label, measurement.Points[0], measurement.Points[^1], 1, LineEnding.ClosedArrow, LineEnding.ClosedArrow),
                    _ => new PolyAnnotation(NewId(), Ink, 1, author, now, now, label, measurement.Kind == MeasureKind.Area, measurement.Points, 1),
                });
                items.Add(Label(geometry, measurement, label, author, now));
            }

            pages.Add(new PageAnnotations(index + 1, items));
        }

        if (pages.Count > 0 && AddAnnotations(pages))
        {
            Clear();
        }
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (!value)
        {
            _current = null;
            _hover = null;
        }

        Update();
    }

    partial void OnKindChanged(MeasureKind value) => Cancel();

    partial void OnUnitChanged(LengthUnit value) => Update();

    partial void OnScaleChanged(double? value)
    {
        if (value is not { } scale || !double.IsFinite(scale) || scale <= 0)
        {
            Scale = 1;
            return;
        }

        Update();
    }

    private static string NewId() => $"pdfpro-{Guid.NewGuid():N}";

    private static string FormatValue(double value, LengthUnit unit, bool squared) => Core.Measuring.Measurements.Format(value, unit, squared);

    /// <summary>A borderless text box with the value, centred on the measurement as shown, upright like the page.</summary>
    private static FreeTextAnnotation Label(PageGeometry geometry, Measurement measurement, string text, string author, DateTimeOffset now)
    {
        var style = TextStyle.Default with { FontSize = 10, Color = Ink };
        var mapper = new PageCoordinateMapper(geometry, 1);
        var at = mapper.PdfToScreen(LabelPoint(measurement));
        var width = Math.Max(40, (text.Length * style.FontSize * 0.6) + (2 * AnnotationDrawing.TextPadding));
        var height = PageEditorViewModel.TextHeight(style, text) + (2 * AnnotationDrawing.TextPadding);
        var box = mapper.ScreenRectToPdf(new ViewRect(at.X - (width / 2), at.Y - height - 2, width, height));
        return new FreeTextAnnotation(NewId(), Ink, 1, author, now, now, text, box, style, null, RgbColor.White, [], geometry.RotationDegrees);
    }

    /// <summary>Where a measurement's value is written: the middle of a distance, the middle of a line, inside an area.</summary>
    public static PdfPoint LabelPoint(Measurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        var points = measurement.Points;
        if (measurement.Kind == MeasureKind.Distance || points.Count == 2)
        {
            return new PdfPoint((points[0].X + points[^1].X) / 2, (points[0].Y + points[^1].Y) / 2);
        }

        if (measurement.Kind == MeasureKind.Perimeter)
        {
            var middle = points.Count / 2;
            return new PdfPoint((points[middle - 1].X + points[middle].X) / 2, (points[middle - 1].Y + points[middle].Y) / 2);
        }

        return new PdfPoint(points.Average(p => p.X), points.Average(p => p.Y));
    }

    private MeasureResult? Measure(Measurement measurement)
    {
        if (_geometryOf(measurement.PageKey) is not { } geometry)
        {
            return null;
        }

        var mapper = new PageCoordinateMapper(geometry, 1);
        return Core.Measuring.Measurements.Measure(measurement.Kind, measurement.Points, Unit, Scale ?? 1, mapper.PdfToScreen);
    }

    private void Finish(IReadOnlyList<PdfPoint> points)
    {
        if (_current is not { } current)
        {
            return;
        }

        var distinct = points.Where((p, i) => i == 0 || p != points[i - 1]).ToList();
        if (distinct.Count < (Kind == MeasureKind.Area ? 3 : 2))
        {
            return; // too few corners yet: keep measuring
        }

        _measurements.Add(new Measurement(Guid.NewGuid(), current.PageKey, Kind, distinct));
        _current = null;
        _hover = null;
        Update();
    }

    private void Cancel()
    {
        _current = null;
        _hover = null;
        Update();
    }

    private void SetKind(bool on, MeasureKind kind)
    {
        if (on)
        {
            Kind = kind;
        }
    }

    private void Update()
    {
        HasMeasurements = _measurements.Count > 0;
        var shown = _current is { } current ? PreviewOn(current.PageKey) : _measurements.LastOrDefault();
        var label = shown is null ? null : LabelOf(shown);
        ResultText = shown is null || string.IsNullOrEmpty(label) ? null : shown.Kind switch
        {
            MeasureKind.Distance => string.Format(CultureInfo.CurrentCulture, Strings.Measure_Result_Distance, label, (Measure(shown)?.Angle ?? 0).ToString("0.#", CultureInfo.CurrentCulture)),
            MeasureKind.Perimeter => string.Format(CultureInfo.CurrentCulture, Strings.Measure_Result_Perimeter, label),
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Measure_Result_Area, label),
        };
        OnPropertyChanged(nameof(IsMeasuring));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsNearFirst(Guid pageKey, PdfPoint first, ViewPoint viewport)
    {
        var index = _viewer.Document.IndexOf(pageKey);
        if (index < 0 || _geometryOf(pageKey) is not { } geometry)
        {
            return false;
        }

        var bounds = _viewer.Layout.GetPageBounds(index);
        var shown = new PageCoordinateMapper(geometry, _viewer.Layout.Scale).PdfToScreen(first);
        var x = bounds.X + shown.X - _viewer.ScrollOffset.X;
        var y = bounds.Y + shown.Y - _viewer.ScrollOffset.Y;
        return Math.Abs(x - viewport.X) <= CloseTolerance && Math.Abs(y - viewport.Y) <= CloseTolerance;
    }

    private (Guid PageKey, PdfPoint Point)? Locate(ViewPoint viewport)
    {
        var extent = new ViewPoint(viewport.X + _viewer.ScrollOffset.X, viewport.Y + _viewer.ScrollOffset.Y);
        if (_viewer.Layout.GetPageAt(extent) is not { } index || index < 0 || index >= _viewer.PageCount)
        {
            return null;
        }

        var key = _viewer.Document.Pages[index].Key;
        return PointOn(key, viewport) is { } point ? (key, point) : null;
    }

    /// <summary>A viewport point on the page <paramref name="pageKey"/> (even outside it), in its PDF space.</summary>
    private PdfPoint? PointOn(Guid pageKey, ViewPoint viewport)
    {
        var index = _viewer.Document.IndexOf(pageKey);
        if (index < 0 || _geometryOf(pageKey) is not { } geometry)
        {
            return null;
        }

        var bounds = _viewer.Layout.GetPageBounds(index);
        var mapper = new PageCoordinateMapper(geometry, _viewer.Layout.Scale);
        return mapper.ScreenToPdf(new ViewPoint(viewport.X + _viewer.ScrollOffset.X - bounds.X, viewport.Y + _viewer.ScrollOffset.Y - bounds.Y));
    }
}
