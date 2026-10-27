using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Editing;
using PdfPro.Operations.Fonts;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Stamping;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Editing;

/// <summary>
/// The page editor ("Thêm chữ, ảnh, hình…"): objects placed on the pages of a document and not written yet. Edits made
/// here (add, move, resize, turn, delete, paste) have their own undo; "Áp dụng" writes them all as one content change,
/// so one undo of the document removes them again. Coordinates follow <see cref="OverlayBox"/>: points in the PDF space
/// the page will have once written (<see cref="MaterializedGeometry"/>), converted through
/// <see cref="PageCoordinateMapper"/>. Must be used on the UI thread.
/// </summary>
public sealed partial class PageEditorViewModel : ObservableObject
{
    /// <summary>Size of an object made with a click instead of a drag, in points.</summary>
    public static readonly PdfSize DefaultShapeSize = new(120, 80);

    public static readonly PdfSize DefaultWhiteoutSize = new(120, 30);

    public const double DefaultTextWidth = 200;

    public const double DefaultLineLength = 120;

    /// <summary>A drag shorter than this (points) counts as a click.</summary>
    public const double ClickTolerance = 3;

    private readonly Func<VirtualDocument> _pages;
    private readonly ILogger _logger;
    private readonly Dictionary<(Guid Document, int Page), PageGeometry> _sourceGeometries = [];
    private readonly HashSet<(Guid Document, int Page)> _loading = [];
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly Dictionary<Guid, int> _pastes = [];
    private List<(PageGeometry Geometry, OverlayItem Item)> _clipboard = [];
    private Guid? _copiedFrom;

    /// <param name="pages">The document as it is shown now.</param>
    /// <param name="fonts">Faces for drawing text on screen (null where nothing is drawn, as in tests).</param>
    /// <param name="images">Pictures for drawing on screen.</param>
    /// <param name="logger">Logs geometry that cannot be read.</param>
    public PageEditorViewModel(Func<VirtualDocument> pages, ScreenFonts? fonts, ScreenImages images, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _pages = pages;
        Fonts = fonts;
        Images = images;
        _logger = logger;
        Objects.CollectionChanged += (_, _) => OnObjectsChanged();
    }

    public ScreenFonts? Fonts { get; }

    public ScreenImages Images { get; }

    /// <summary>The objects not applied yet, in drawing order.</summary>
    public ObservableCollection<OverlayObjectViewModel> Objects { get; } = [];

    /// <summary>The editor is shown (tool bar, layers on the pages take the mouse).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial OverlayTool Tool { get; set; }

    /// <summary>Style of new text boxes.</summary>
    public TextStyle TextStyle { get; set; } = TextStyle.Default with { FontSize = 14 };

    /// <summary>Border of new rectangles and ellipses.</summary>
    public StrokeStyle ShapeStroke { get; set; } = new(RgbColor.Red, 2);

    /// <summary>Fill of new rectangles and ellipses (none by default).</summary>
    public RgbColor? ShapeFill { get; set; }

    public StrokeStyle LineStroke { get; set; } = new(RgbColor.Red, 2);

    public bool HasObjects => Objects.Count > 0;

    public IReadOnlyList<OverlayObjectViewModel> Selection => [.. Objects.Where(o => o.IsSelected)];

    public bool HasSelection => Objects.Any(o => o.IsSelected);

    /// <summary>Objects, selection or page geometry changed: the page layers redraw.</summary>
    public event EventHandler? Invalidated;

    /// <summary>"Áp dụng": the objects to write, page indices filled in. The document runs the content change.</summary>
    public event EventHandler<OverlayParams>? ApplyRequested;

    public IEnumerable<OverlayObjectViewModel> ObjectsOn(Guid pageKey) => Objects.Where(o => o.PageKey == pageKey);

    /// <summary>The page's geometry once written, or null while it is still being read (<see cref="Invalidated"/> follows).</summary>
    public PageGeometry? GeometryOf(Guid pageKey)
    {
        var document = _pages();
        var index = document.IndexOf(pageKey);
        if (index < 0)
        {
            return null;
        }

        var page = document.Pages[index];
        if (page.Source is not { } source)
        {
            return MaterializedGeometry.Of(page.Ref, null);
        }

        if (_sourceGeometries.TryGetValue((source.Id, page.SourcePageIndex), out var geometry))
        {
            return MaterializedGeometry.Of(page.Ref, geometry);
        }

        _ = LoadGeometryAsync(source, page.SourcePageIndex);
        return null;
    }

    /// <summary>The page's geometry once written, reading it when needed.</summary>
    public async Task<PageGeometry?> GetGeometryAsync(Guid pageKey)
    {
        var document = _pages();
        var index = document.IndexOf(pageKey);
        if (index >= 0 && document.Pages[index].Source is { } source)
        {
            await LoadGeometryAsync(source, document.Pages[index].SourcePageIndex);
        }

        return GeometryOf(pageKey);
    }

    /// <summary>
    /// A new object made with <paramref name="tool"/> by dragging from <paramref name="start"/> to <paramref name="end"/>
    /// (points of the page as shown); a click gives the default size. Null for tools that do not draw (select, image,
    /// signature — they need a file).
    /// </summary>
    public OverlayItem? NewItem(OverlayTool tool, PageGeometry geometry, ViewPoint start, ViewPoint end)
    {
        var mapper = new PageCoordinateMapper(geometry, 1);
        var isClick = Math.Abs(end.X - start.X) < ClickTolerance && Math.Abs(end.Y - start.Y) < ClickTolerance;
        OverlayBox BoxFor(PdfSize size)
        {
            var area = isClick ? new ViewRect(start.X, start.Y, size.Width, size.Height) : ViewRect.FromCorners(start, end);
            return new OverlayBox(mapper.ScreenToPdf(new ViewPoint(area.X + (area.Width / 2), area.Y + (area.Height / 2))), new PdfSize(area.Width, area.Height));
        }

        switch (tool)
        {
            case OverlayTool.Text:
                // At least one line high; a drag sets the width (and may make it taller).
                var height = TextHeight(TextStyle, string.Empty) + (2 * OverlayLimits.DefaultPadding);
                var area = isClick ? new ViewRect(start.X, start.Y, DefaultTextWidth, height) : ViewRect.FromCorners(start, end);
                var size = new PdfSize(Math.Max(area.Width, 10), Math.Max(area.Height, height));
                var center = new ViewPoint(area.X + (size.Width / 2), area.Y + (size.Height / 2));
                return new TextOverlay(0, new OverlayBox(mapper.ScreenToPdf(center), size), string.Empty, TextStyle);
            case OverlayTool.Rectangle:
            case OverlayTool.Ellipse:
                // A shape needs a border or a fill to be seen (and written).
                var stroke = ShapeStroke is { Width: > 0 } || ShapeFill is not null ? ShapeStroke : new StrokeStyle(ShapeStroke.Color, 2);
                return new ShapeOverlay(0, BoxFor(DefaultShapeSize), tool == OverlayTool.Ellipse ? ShapeKind.Ellipse : ShapeKind.Rectangle, stroke, ShapeFill);
            case OverlayTool.Whiteout:
                return new ShapeOverlay(0, BoxFor(DefaultWhiteoutSize), ShapeKind.Whiteout);
            case OverlayTool.Line:
            case OverlayTool.Arrow:
                var to = isClick ? new ViewPoint(start.X + DefaultLineLength, start.Y) : end;
                return new LineOverlay(0, mapper.ScreenToPdf(start), mapper.ScreenToPdf(to), LineStroke, ArrowAtEnd: tool == OverlayTool.Arrow);
            default:
                return null;
        }
    }

    /// <summary>Adds an object (one undoable step) and selects only it.</summary>
    public OverlayObjectViewModel Add(Guid pageKey, OverlayItem item)
    {
        Record();
        var added = new OverlayObjectViewModel(pageKey, item);
        foreach (var other in Objects)
        {
            other.IsSelected = false;
        }

        added.IsSelected = true;
        Objects.Add(added);
        return added;
    }

    /// <summary>A mouse gesture (move, resize, turn) starts: its changes become one undoable step.</summary>
    public void BeginGesture() => Record();

    /// <summary>Changes an object during a gesture (not a step of its own).</summary>
    public void Update(OverlayObjectViewModel target, OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Item = item;
        Invalidate();
    }

    /// <summary>Selects an object; with <paramref name="add"/> it toggles in the selection instead of replacing it.</summary>
    public void Select(OverlayObjectViewModel? target, bool add = false)
    {
        if (!add)
        {
            foreach (var other in Objects)
            {
                other.IsSelected = false;
            }
        }

        if (target is not null)
        {
            target.IsSelected = !add || !target.IsSelected;
        }

        OnSelectionChanged();
    }

    public void ClearSelection() => Select(null);

    /// <summary>Selects every object of a page (Ctrl+A).</summary>
    public void SelectAll(Guid pageKey)
    {
        foreach (var item in Objects)
        {
            item.IsSelected = item.PageKey == pageKey;
        }

        OnSelectionChanged();
    }

    /// <summary>Moves the selection by points of the page as shown (arrow keys: 1, with Shift 10); false when nothing is selected.</summary>
    public bool Nudge(double dx, double dy)
    {
        var selection = Selection;
        if (selection.Count == 0)
        {
            return false;
        }

        Record();
        foreach (var target in selection)
        {
            if (GeometryOf(target.PageKey) is { } geometry)
            {
                target.Item = target.Item.MoveToPage(geometry, geometry, target.Item.PageIndex, new ViewPoint(dx, dy));
            }
        }

        Invalidate();
        return true;
    }

    /// <summary>Pastes what was copied onto a page (shifted a little when it lands where it came from).</summary>
    public void PasteOn(Guid pageKey)
    {
        if (_clipboard.Count == 0 || GeometryOf(pageKey) is not { } target)
        {
            return;
        }

        Record();
        foreach (var other in Objects)
        {
            other.IsSelected = false;
        }

        // Each paste onto a page steps 10 points further down and right, so copies never hide one another; the first
        // paste onto another page keeps the shown place.
        var pastes = _pastes.GetValueOrDefault(pageKey) + 1;
        _pastes[pageKey] = pastes;
        foreach (var (geometry, item) in _clipboard)
        {
            var step = 10 * (geometry == target && pageKey == _copiedFrom ? pastes : pastes - 1);
            Objects.Add(new OverlayObjectViewModel(pageKey, item.MoveToPage(geometry, target, 0, new ViewPoint(step, step))) { IsSelected = true });
        }

        OnSelectionChanged();
    }

    /// <summary>The text box being typed in, on the page (null when none).</summary>
    [ObservableProperty]
    public partial OverlayObjectViewModel? EditingText { get; private set; }

    /// <summary>Starts typing in a text box (double-click, or a new box); its edits become one undoable step.</summary>
    public void BeginTextEdit(OverlayObjectViewModel target, bool record = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Item is not TextOverlay || EditingText == target)
        {
            return;
        }

        EndTextEdit();
        if (record)
        {
            Record();
        }

        Select(target);
        EditingText = target;
        Invalidate();
    }

    /// <summary>The typed text so far: the box grows (downwards, as shown) to hold every line.</summary>
    public void UpdateEditingText(string text)
    {
        if (EditingText is { Item: TextOverlay item } target)
        {
            target.Item = FitHeight(target.PageKey, item with { Text = text });
            Invalidate();
        }
    }

    /// <summary>Stops typing; a box left empty is removed.</summary>
    public void EndTextEdit()
    {
        if (EditingText is not { } target)
        {
            return;
        }

        EditingText = null;
        if (target.Item is TextOverlay { Text: var text } && string.IsNullOrWhiteSpace(text))
        {
            Objects.Remove(target);
        }

        Invalidate();
    }

    /// <summary>A picture placed in the middle of a page, at its own size or shrunk to half the page.</summary>
    public OverlayObjectViewModel? AddImage(Guid pageKey, ImportedImage image, bool isSignature = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (GeometryOf(pageKey) is not { } geometry)
        {
            return null;
        }

        var shown = geometry.DisplaySize;
        var size = image.SizeWithin(new PdfSize(shown.Width / 2, shown.Height / 2));
        var center = new PageCoordinateMapper(geometry, 1).ScreenToPdf(new ViewPoint(shown.Width / 2, shown.Height / 2));
        Tool = OverlayTool.Select;
        return Add(pageKey, new ImageOverlay(0, new OverlayBox(center, size), image.Path, isSignature));
    }

    /// <summary>Changes the selected objects (one undoable step); text boxes are refitted to their text.</summary>
    public void ApplyToSelection(Func<OverlayItem, OverlayItem> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var selection = Selection;
        if (selection.Count == 0)
        {
            return;
        }

        Record();
        foreach (var target in selection)
        {
            var changed = change(target.Item);
            target.Item = changed is TextOverlay text ? FitHeight(target.PageKey, text) : changed;
        }

        Invalidate();
    }

    /// <summary>A text box made tall enough for its text, its top edge (as shown) staying put.</summary>
    public TextOverlay FitHeight(Guid pageKey, TextOverlay text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (GeometryOf(pageKey) is not { } geometry)
        {
            return text;
        }

        var size = text.Box.Size;
        var needed = TextHeight(text.Style, text.Text, size.Width - (2 * text.Padding)) + (2 * text.Padding);
        if (Math.Abs(needed - size.Height) < 0.01)
        {
            return text;
        }

        var mapper = new PageCoordinateMapper(geometry, 1);
        var shown = text.Box.ToView(mapper);
        var center = shown.ToView(0, (needed - size.Height) / 2);
        return text with { Box = OverlayBox.FromView(new RotatedBox(center, shown.Width, needed, shown.Angle), mapper) };
    }

    /// <summary>The pages changed (moved, turned, cropped): the layers redraw.</summary>
    public void Refresh() => Invalidate();

    /// <summary>Empties the editor (after applying, or on cancel).</summary>
    public void Clear()
    {
        EditingText = null;
        Objects.Clear();
        _undo.Clear();
        _redo.Clear();
        NotifyHistory();
    }

    /// <summary>The objects to write, in order, on the pages they are on now (objects of removed pages are left out).</summary>
    public OverlayParams? BuildParameters()
    {
        var document = _pages();
        var items = Objects
            .Select(o => (o.Item, Index: document.IndexOf(o.PageKey)))
            .Where(x => x.Index >= 0 && !IsEmptyText(x.Item))
            .Select(x => x.Item with { PageIndex = x.Index })
            .ToArray();
        return items.Length == 0 ? null : new OverlayParams(items);
    }

    /// <summary>Height of laid-out text in a style, for new and growing text boxes (at least one line).</summary>
    public static double TextHeight(TextStyle style, string text, double width = 0)
    {
        ArgumentNullException.ThrowIfNull(style);
        var metrics = Operations.Fonts.FontLibrary.Instance.Metrics(style.FontFamily, style.Bold, style.Italic);
        return TextLayout.Layout(text, metrics, style.FontSize, width, style.Alignment, style.LineSpacing).Height;
    }

    [RelayCommand]
    private void SetTool(OverlayTool tool) => Tool = tool;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        var selection = Selection;
        if (selection.Count == 0)
        {
            return;
        }

        Record();
        foreach (var target in selection)
        {
            Objects.Remove(target);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        var selection = Selection;
        _clipboard = [.. selection.Select(o => (GeometryOf(o.PageKey), o.Item)).Where(x => x.Item1 is not null).Select(x => (x.Item1!.Value, x.Item))];
        _copiedFrom = selection.Count > 0 ? selection[0].PageKey : null;
        _pastes.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        EndTextEdit();
        if (_undo.TryPop(out var previous))
        {
            _redo.Push(Capture());
            Restore(previous);
        }
    }

    public bool CanUndo => _undo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        EndTextEdit();
        if (_redo.TryPop(out var next))
        {
            _undo.Push(Capture());
            Restore(next);
        }
    }

    public bool CanRedo => _redo.Count > 0;

    [RelayCommand(CanExecute = nameof(HasObjects))]
    private void Apply()
    {
        EndTextEdit();
        if (BuildParameters() is { } parameters)
        {
            ApplyRequested?.Invoke(this, parameters);
        }
        else
        {
            Clear();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Clear();
        Tool = OverlayTool.Select;
    }

    partial void OnToolChanged(OverlayTool value) => Invalidate();

    partial void OnIsActiveChanged(bool value)
    {
        if (!value)
        {
            EndTextEdit();
            Tool = OverlayTool.Select;
        }

        Invalidate();
    }

    private static bool IsEmptyText(OverlayItem item) => item is TextOverlay { Text: var text } && string.IsNullOrWhiteSpace(text);

    private async Task LoadGeometryAsync(IPdfDocument source, int index)
    {
        if (_sourceGeometries.ContainsKey((source.Id, index)) || !_loading.Add((source.Id, index)))
        {
            return;
        }

        try
        {
            _sourceGeometries[(source.Id, index)] = await source.GetPageGeometryAsync(index);
            Invalidate();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("A page's geometry could not be read for the page editor: {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            _loading.Remove((source.Id, index));
        }
    }

    private void Record()
    {
        _undo.Push(Capture());
        _redo.Clear();
        NotifyHistory();
    }

    private Snapshot Capture() => new([.. Objects.Select(o => new SnapshotItem(o.Id, o.PageKey, o.Item))]);

    private void Restore(Snapshot snapshot)
    {
        Objects.Clear();
        foreach (var item in snapshot.Items)
        {
            Objects.Add(new OverlayObjectViewModel(item.PageKey, item.Item, item.Id));
        }

        NotifyHistory();
    }

    private void OnObjectsChanged()
    {
        OnPropertyChanged(nameof(HasObjects));
        ApplyCommand.NotifyCanExecuteChanged();
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(HasSelection));
        DeleteCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        Invalidate();
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void Invalidate() => Invalidated?.Invoke(this, EventArgs.Empty);

    private sealed record SnapshotItem(Guid Id, Guid PageKey, OverlayItem Item);

    private sealed record Snapshot(IReadOnlyList<SnapshotItem> Items);
}
