using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Core.Redaction;
using PdfPro.Operations.Security;

namespace PdfPro.App.ViewModels.Security;

/// <summary>What the mouse marks in the redaction mode.</summary>
public enum RedactionTool
{
    /// <summary>A box dragged over the page (text, pictures, drawings — anything under it).</summary>
    Area,

    /// <summary>Text selected with the mouse, line by line.</summary>
    Text,
}

/// <summary>
/// A place marked for redaction on a page of the viewer: rectangles in the page's PDF space (as it is written, the space
/// of its source page). <see cref="Pattern"/> says which search found it (null: marked by hand).
/// </summary>
public sealed record RedactionMark(Guid Id, Guid PageKey, IReadOnlyList<PdfRect> Rects, RedactionPattern? Pattern = null)
{
    public PdfRect Bounds => Rects.Aggregate((a, b) => new PdfRect(Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom), Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top)));

    public bool Contains(PdfPoint point, double tolerance)
        => Rects.Any(r => point.X >= r.Left - tolerance && point.X <= r.Right + tolerance && point.Y >= r.Bottom - tolerance && point.Y <= r.Top + tolerance);
}

/// <summary>
/// "Xóa thông tin" (security.redact) on the viewer: places to remove are marked first — boxes dragged over the page,
/// selected text, or what "Tìm &amp; đánh dấu" finds — shown as red frames and reviewed; nothing changes in the document
/// until they are applied, as one content change (one undoable step) that removes them for good. Marks are kept while
/// the mode is off; they go with the pages they are on. Must be used on the UI thread.
/// </summary>
public sealed partial class RedactionViewModel : ObservableObject
{
    /// <summary>A drag shorter than this (points) marks nothing.</summary>
    public const double DragThreshold = 2;

    private readonly DocumentViewerViewModel _viewer;
    private readonly TextSelectionViewModel _selection;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly List<RedactionMark> _marks = [];
    private (Guid PageKey, int Index, PdfPoint Start, PdfPoint End)? _drag;

    /// <param name="viewer">The viewer whose pages are marked.</param>
    /// <param name="selection">The text selection (marked with the Text tool, or "Đánh dấu vùng chọn").</param>
    /// <param name="geometryOf">The PDF space of a page as it will be written, when known.</param>
    public RedactionViewModel(DocumentViewerViewModel viewer, TextSelectionViewModel selection, Func<Guid, PageGeometry?> geometryOf)
    {
        _viewer = viewer;
        _selection = selection;
        _geometryOf = geometryOf;
        _selection.Changed += (_, _) => MarkSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Marks, the selected mark or the box being dragged changed: pages redraw their frames.</summary>
    public event EventHandler? Changed;

    /// <summary>The mode is on: the viewer's mouse marks.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAreaTool), nameof(IsTextTool))]
    public partial RedactionTool Tool { get; set; }

    public bool IsAreaTool
    {
        get => Tool == RedactionTool.Area;
        set
        {
            if (value)
            {
                Tool = RedactionTool.Area;
            }
        }
    }

    public bool IsTextTool
    {
        get => Tool == RedactionTool.Text;
        set
        {
            if (value)
            {
                Tool = RedactionTool.Text;
            }
        }
    }

    /// <summary>The mark picked with the mouse (Delete removes it), or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial RedactionMark? Selected { get; private set; }

    public IReadOnlyList<RedactionMark> Marks => _marks;

    public bool HasMarks => _marks.Count > 0;

    /// <summary>"3 vùng trên 2 trang", or how to mark when there is none.</summary>
    public string Status => _marks.Count == 0
        ? (Tool == RedactionTool.Area ? Strings.Redaction_Hint_Area : Strings.Redaction_Hint_Text)
        : string.Format(CultureInfo.CurrentCulture, Strings.Redaction_Status, _marks.Count, _marks.Select(m => m.PageKey).Distinct().Count());

    /// <summary>A box is being dragged (the mouse is captured for it).</summary>
    public bool IsDragging => _drag is not null;

    public PageGeometry? GeometryOf(Guid pageKey) => _geometryOf(pageKey);

    public IEnumerable<RedactionMark> MarksOn(Guid pageKey) => _marks.Where(m => m.PageKey == pageKey);

    /// <summary>The box being dragged on a page, or null.</summary>
    public PdfRect? Preview(Guid pageKey)
        => _drag is { } drag && drag.PageKey == pageKey ? Box(drag.Start, drag.End) : null;

    /// <summary>
    /// A press on the viewer: a mark under the point is selected; otherwise the Area tool starts a box. False when the
    /// press is not for the redaction mode (the Text tool hands it to the text selection).
    /// </summary>
    public bool Press(ViewPoint viewport)
    {
        if (!IsActive || Locate(viewport) is not { } found)
        {
            return false;
        }

        var tolerance = 3 / _viewer.Layout.Scale;
        if (MarksOn(found.Page.Key).LastOrDefault(m => m.Contains(found.Point, tolerance)) is { } hit)
        {
            Selected = hit;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        Selected = null;
        if (Tool != RedactionTool.Area)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        _drag = (found.Page.Key, found.Index, found.Point, found.Point);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void DragTo(ViewPoint viewport)
    {
        if (_drag is not { } drag || PointOn(drag.Index, viewport) is not { } point)
        {
            return;
        }

        _drag = drag with { End = point };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The end of a drag: the box becomes a mark (kept inside the page) when it has a size.</summary>
    public void Release()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        var box = Box(drag.Start, drag.End);
        if (_geometryOf(drag.PageKey) is { } geometry)
        {
            box = Clip(box, geometry.Box);
        }

        if (box.Width >= DragThreshold && box.Height >= DragThreshold)
        {
            Add([new RedactionMark(Guid.NewGuid(), drag.PageKey, [box])]);
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The end of a text drag with the Text tool: what is selected becomes marks.</summary>
    public void ReleaseText()
    {
        if (IsActive && Tool == RedactionTool.Text && _selection.HasSelection)
        {
            MarkSelection();
        }
    }

    /// <summary>Esc: a drag is dropped, then the selection. False when there was nothing to drop.</summary>
    public bool Escape()
    {
        if (_drag is not null)
        {
            _drag = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (Selected is not null)
        {
            Selected = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    /// <summary>Adds marks (from the search, or by hand); a mark on a page that is gone is left out.</summary>
    public void Add(IEnumerable<RedactionMark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        var added = marks.Where(m => _viewer.Document.IndexOf(m.PageKey) >= 0 && m.Rects.Count > 0).ToList();
        if (added.Count == 0)
        {
            return;
        }

        _marks.AddRange(added);
        Selected = added[^1];
        NotifyMarks();
    }

    /// <summary>The marks as the parameters of security.redact (pages 1-based as shown), or null without marks.</summary>
    public RedactParams? Parameters(RgbColor fill, string? label, RedactionMode mode = RedactionMode.Raster)
    {
        var areas = _marks
            .Select(m => (Page: _viewer.Document.IndexOf(m.PageKey), Mark: m))
            .Where(m => m.Page >= 0)
            .GroupBy(m => m.Page)
            .OrderBy(g => g.Key)
            .Select(g => new RedactionArea(g.Key + 1, [.. g.SelectMany(m => m.Mark.Rects)]))
            .ToList();
        return areas.Count == 0 ? null : new RedactParams(areas, fill, string.IsNullOrWhiteSpace(label) ? null : label.Trim(), mode);
    }

    /// <summary>Forgets every mark (after they were applied, or on request).</summary>
    [RelayCommand(CanExecute = nameof(HasMarks))]
    public void ClearAll()
    {
        _marks.Clear();
        Selected = null;
        NotifyMarks();
    }

    /// <summary>The pages changed: marks on pages that are gone go too (a content change gives every page a new key).</summary>
    public void OnPagesChanged()
    {
        if (_marks.RemoveAll(m => _viewer.Document.IndexOf(m.PageKey) < 0) > 0)
        {
            if (Selected is { } selected && !_marks.Contains(selected))
            {
                Selected = null;
            }

            NotifyMarks();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Delete()
    {
        if (Selected is { } selected && _marks.Remove(selected))
        {
            Selected = null;
            NotifyMarks();
        }
    }

    private bool HasSelected() => Selected is not null;

    /// <summary>"Đánh dấu vùng chọn": the selected text, page by page, becomes marks; the selection is cleared.</summary>
    [RelayCommand(CanExecute = nameof(HasTextSelection))]
    private void MarkSelection()
    {
        var marks = new List<RedactionMark>();
        foreach (var page in _viewer.Document.Pages)
        {
            var rects = _selection.RectsOn(page.Key);
            if (rects.Count > 0)
            {
                marks.Add(new RedactionMark(Guid.NewGuid(), page.Key, [.. rects]));
            }
        }

        _selection.Clear();
        Add(marks);
    }

    private bool HasTextSelection() => _selection.HasSelection;

    partial void OnIsActiveChanged(bool value)
    {
        _drag = null;
        if (!value)
        {
            Selected = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnToolChanged(RedactionTool value) => OnPropertyChanged(nameof(Status));

    private void NotifyMarks()
    {
        OnPropertyChanged(nameof(Marks));
        OnPropertyChanged(nameof(HasMarks));
        OnPropertyChanged(nameof(Status));
        ClearAllCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static PdfRect Box(PdfPoint a, PdfPoint b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private static PdfRect Clip(PdfRect rect, PdfRect bounds)
        => new(Math.Max(rect.Left, bounds.Left), Math.Max(rect.Bottom, bounds.Bottom), Math.Min(rect.Right, bounds.Right), Math.Min(rect.Top, bounds.Top));

    private (int Index, Services.Documents.VirtualPage Page, PdfPoint Point)? Locate(ViewPoint viewport)
    {
        var extent = new ViewPoint(viewport.X + _viewer.ScrollOffset.X, viewport.Y + _viewer.ScrollOffset.Y);
        if (_viewer.Layout.GetPageAt(extent) is not { } index || index < 0 || index >= _viewer.PageCount)
        {
            return null;
        }

        return PointOn(index, viewport) is { } point ? (index, _viewer.Document.Pages[index], point) : null;
    }

    /// <summary>A viewport point on page <paramref name="index"/> (even outside it), in its PDF space.</summary>
    private PdfPoint? PointOn(int index, ViewPoint viewport)
    {
        if (index < 0 || index >= _viewer.PageCount || _geometryOf(_viewer.Document.Pages[index].Key) is not { } geometry)
        {
            return null;
        }

        var bounds = _viewer.Layout.GetPageBounds(index);
        var mapper = new PageCoordinateMapper(geometry, _viewer.Layout.Scale);
        return mapper.ScreenToPdf(new ViewPoint(viewport.X + _viewer.ScrollOffset.X - bounds.X, viewport.Y + _viewer.ScrollOffset.Y - bounds.Y));
    }
}
