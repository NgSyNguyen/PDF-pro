using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GongSolutions.Wpf.DragDrop;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Organizer;

/// <summary>What the organizer grid asks its document to do; positions are 0-based.</summary>
public interface IPageOrganizerHost
{
    /// <summary>The document can be edited now (not read-only, not saving).</summary>
    bool CanEditPages { get; }

    /// <summary>Moves pages (one undoable step) so they land before the page now at <paramref name="insertAt"/>.</summary>
    /// <returns>False when nothing moved.</returns>
    bool MovePages(IReadOnlyList<int> indices, int insertAt);

    /// <summary>Inserts every page of PDF files (dropped from Explorer) before the page now at <paramref name="insertAt"/>.</summary>
    Task InsertFilesAsync(IReadOnlyList<string> paths, int insertAt);

    /// <summary>Leaves the organizer and shows the page in the viewer.</summary>
    void OpenPage(int index);
}

/// <summary>
/// The page organizer grid of a document (SPEC P3 "Sắp xếp trang"): large thumbnails with an adjustable size, a
/// multiple selection kept by page key (so it follows moved pages and survives undo), selection by page range, and
/// dragging pages to reorder them or PDF files from Explorer to insert them. After every plan change the selection is:
/// the pages that appeared (duplicates, inserted pages, pages brought back by undo); otherwise the selected pages that
/// remain; otherwise the page that took the place of the first selected one. Must be used on the UI thread.
/// </summary>
public sealed partial class PageOrganizerViewModel : ObservableObject, IItemSelectionHost, IDropTarget, IDisposable
{
    public const double MinTileSize = 120;
    public const double MaxTileSize = 360;
    public const double TileSizeStep = 40;
    public const double DefaultTileSize = 200;

    /// <summary>Margin and padding around a tile's box, both sides together.</summary>
    public const double TileChrome = 28;

    /// <summary>Height of the page number under a tile's box.</summary>
    public const double LabelHeight = 24;

    private readonly Func<IPdfDocument, string?> _filePathOf;
    private readonly IPageOrganizerHost _host;
    private readonly ThumbnailLoader _loader;
    private readonly Dictionary<PageTileViewModel, double> _realized = [];
    private Dictionary<Guid, PageTileViewModel> _tilesByKey;
    private HashSet<Guid> _selectedKeys = [];
    private IReadOnlyList<int> _selectedIndices = [];
    private Guid? _focusKey;
    private double _tileSize = DefaultTileSize;
    private bool _updating;
    private bool _disposed;

    /// <param name="document">The pages to show.</param>
    /// <param name="filePathOf">The file of a source document, to recognise it in the disk cache.</param>
    /// <param name="host">The document the grid edits.</param>
    /// <param name="provider">Loads the images.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="logger">Logs the first failure.</param>
    public PageOrganizerViewModel(
        VirtualDocument document,
        Func<IPdfDocument, string?> filePathOf,
        IPageOrganizerHost host,
        IThumbnailProvider provider,
        IUiDispatcher ui,
        ILogger<PageOrganizerViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(filePathOf);
        ArgumentNullException.ThrowIfNull(host);
        _filePathOf = filePathOf;
        _host = host;
        _loader = new ThumbnailLoader(provider, ui, logger);
        Tiles = document.Pages.Select((page, i) => new PageTileViewModel(this, page, i, _tileSize)).ToArray();
        _tilesByKey = Tiles.ToDictionary(t => t.Key);
        _loader.Remember(document);
        RangeText = string.Empty;
    }

    /// <summary>Raised when the view model selects tiles itself (after an edit, from the page range box).</summary>
    public event EventHandler<ItemSelectionRequest>? SelectionRequested;

    /// <summary>The selection changed (by the user or after an edit): page commands should be re-evaluated.</summary>
    public event EventHandler? SelectionChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    public partial IReadOnlyList<PageTileViewModel> Tiles { get; private set; }

    /// <summary>Width of a tile's page box (the size slider): 120–360 in steps of 40.</summary>
    public double TileSize
    {
        get => _tileSize;
        set
        {
            var size = SnapTileSize(value);
            if (!SetProperty(ref _tileSize, size))
            {
                return;
            }

            OnPropertyChanged(nameof(CellWidth));
            OnPropertyChanged(nameof(CellHeight));
            ZoomTilesInCommand.NotifyCanExecuteChanged();
            ZoomTilesOutCommand.NotifyCanExecuteChanged();
            foreach (var tile in Tiles)
            {
                tile.Resize(size);
            }

            foreach (var (tile, dpiScale) in _realized.ToList())
            {
                Realize(tile, dpiScale); // the shown tiles at the new size; the old image stays until then
            }
        }
    }

    /// <summary>Width of a grid cell (box plus margins).</summary>
    public double CellWidth => _tileSize + TileChrome;

    /// <summary>Height of a grid cell (box, margins and page number).</summary>
    public double CellHeight => ThumbnailGeometry.BoxHeightFor(_tileSize) + TileChrome + LabelHeight;

    /// <summary>The page range box: shows the selection, and selects what the user types on Enter.</summary>
    [ObservableProperty]
    public partial string RangeText { get; set; }

    /// <summary>Why the typed range could not be selected, or null.</summary>
    [ObservableProperty]
    public partial string? RangeError { get; private set; }

    /// <summary>"Đã chọn 3/120 trang".</summary>
    public string SelectionSummary
        => string.Format(CultureInfo.CurrentCulture, Strings.Organizer_SelectionSummary, _selectedIndices.Count, Tiles.Count);

    /// <summary>The selected pages, in document order.</summary>
    public IReadOnlyList<int> SelectedIndices => _selectedIndices;

    public bool HasSelection => _selectedIndices.Count > 0;

    /// <summary>The page the user last selected (or the first selected one), or null without a selection.</summary>
    public int? FocusIndex
        => _focusKey is { } key && _tilesByKey.TryGetValue(key, out var tile) && _selectedKeys.Contains(key)
            ? tile.Index
            : _selectedIndices.Count > 0 ? _selectedIndices[0] : null;

    public ItemSelectionRequest CurrentSelection
        => new([.. _selectedIndices.Select(i => (object)Tiles[i])], FocusIndex is { } focus ? Tiles[focus] : null);

    /// <summary>Thumbnails being loaded (tests and diagnostics).</summary>
    public int PendingCount => _loader.PendingCount;

    /// <summary>The page plan changed: tiles follow their pages and the selection follows the rules above.</summary>
    public void SetDocument(VirtualDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var previous = Tiles;
        var previousFirst = _selectedIndices.Count > 0 ? _selectedIndices[0] : (int?)null;
        var existing = new Dictionary<Guid, PageTileViewModel>(_tilesByKey);
        var tiles = new PageTileViewModel[document.PageCount];
        var added = new List<int>();
        for (var i = 0; i < tiles.Length; i++)
        {
            var page = document.Pages[i];
            if (existing.Remove(page.Key, out var tile))
            {
                tile.Update(page, i, _tileSize);
            }
            else
            {
                tile = new PageTileViewModel(this, page, i, _tileSize);
                added.Add(i);
            }

            tiles[i] = tile;
        }

        foreach (var removed in existing.Values)
        {
            Release(removed);
        }

        IReadOnlyList<int> selection;
        if (added.Count > 0 && added.Count == tiles.Length && previous.Count > 0)
        {
            // Every page is new (a content change rebuilt the document): keep the same positions.
            selection = [.. _selectedIndices.Where(i => i < tiles.Length)];
        }
        else if (added.Count > 0)
        {
            selection = added;
        }
        else if (tiles.Where(t => _selectedKeys.Contains(t.Key)).Select(t => t.Index).ToList() is { Count: > 0 } kept)
        {
            selection = kept;
        }
        else
        {
            selection = previousFirst is { } first && tiles.Length > 0 ? [Math.Min(first, tiles.Length - 1)] : [];
        }

        // Turning or cropping pages keeps the order: the list keeps its items and its selection (no reset, which
        // would make it select a thousand pages again).
        var sameOrder = added.Count == 0 && tiles.Length == previous.Count && tiles.Zip(previous).All(p => ReferenceEquals(p.First, p.Second));
        var selectedKeys = selection.Select(i => tiles[i].Key).ToHashSet();
        var sameSelection = sameOrder && selectedKeys.SetEquals(_selectedKeys);
        _updating = true;
        try
        {
            _loader.Remember(document);
            _tilesByKey = tiles.ToDictionary(t => t.Key);
            var focusKey = _focusKey is { } key && selectedKeys.Contains(key) ? key : (Guid?)null;
            SetSelection(selectedKeys, focusKey ?? (selection.Count > 0 ? tiles[selection[0]].Key : null), tiles);
            if (!sameOrder)
            {
                Tiles = tiles;
            }
        }
        finally
        {
            _updating = false;
        }

        foreach (var (tile, dpiScale) in _realized.ToList())
        {
            Realize(tile, dpiScale); // turned pages keep their image; cropped ones reload
        }

        if (!sameSelection)
        {
            SelectionRequested?.Invoke(this, CurrentSelection);
        }
    }

    /// <summary>Selects pages (and shows <paramref name="focusIndex"/>), as if the user had.</summary>
    public void Select(IEnumerable<int> indices, int? focusIndex = null)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var valid = indices.Where(i => i >= 0 && i < Tiles.Count).ToList();
        var focus = focusIndex is { } index && index >= 0 && index < Tiles.Count ? Tiles[index].Key : (Guid?)null;
        SetSelection(valid.Select(i => Tiles[i].Key), focus, Tiles);
        SelectionRequested?.Invoke(this, CurrentSelection);
    }

    public void OnViewSelectionChanged(IReadOnlyList<object> selectedItems)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        if (_updating || _disposed)
        {
            return;
        }

        var keys = selectedItems.OfType<PageTileViewModel>().Where(t => _tilesByKey.ContainsKey(t.Key)).Select(t => t.Key).ToList();

        // The tile the user just added is where the next keyboard or range action continues.
        var focus = keys.LastOrDefault(k => !_selectedKeys.Contains(k)) is var newest && newest != Guid.Empty ? newest : _focusKey;
        SetSelection(keys, focus, Tiles);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _realized.Clear();
        _loader.Dispose();
    }

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        ArgumentNullException.ThrowIfNull(dropInfo);
        dropInfo.Effects = DragDropEffects.None;
        if (!_host.CanEditPages)
        {
            return;
        }

        if (DraggedTiles(dropInfo).Count > 0)
        {
            dropInfo.Effects = DragDropEffects.Move;
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        }
        else if (DroppedPdfFiles(dropInfo).Count > 0)
        {
            dropInfo.Effects = DragDropEffects.Copy;
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        }
    }

    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        ArgumentNullException.ThrowIfNull(dropInfo);
        if (!_host.CanEditPages)
        {
            return;
        }

        var insertAt = Math.Clamp(dropInfo.InsertIndex, 0, Tiles.Count);
        var tiles = DraggedTiles(dropInfo);
        if (tiles.Count > 0)
        {
            _focusKey = tiles[0].Key;
            _host.MovePages([.. tiles.Select(t => t.Index).Order()], insertAt);
            return;
        }

        var files = DroppedPdfFiles(dropInfo);
        if (files.Count > 0)
        {
            _ = _host.InsertFilesAsync(files, insertAt);
        }
    }

    internal void Realize(PageTileViewModel tile, double dpiScale)
    {
        if (_disposed || dpiScale <= 0 || !_tilesByKey.TryGetValue(tile.Key, out var current) || !ReferenceEquals(current, tile))
        {
            return;
        }

        _realized[tile] = dpiScale;
        if (tile.Page.Source is not { } source)
        {
            _loader.Release(tile); // blank page: the tile itself is the page
            return;
        }

        _loader.Load(tile, new ThumbnailRequest(tile.Page, _filePathOf(source) ?? string.Empty, dpiScale, _tileSize));
    }

    internal void Release(PageTileViewModel tile)
    {
        _realized.Remove(tile);
        _loader.Release(tile);
    }

    /// <summary>The nearest tile size step within the allowed range.</summary>
    internal static double SnapTileSize(double value)
    {
        if (!double.IsFinite(value))
        {
            return DefaultTileSize;
        }

        var steps = Math.Round((value - MinTileSize) / TileSizeStep, MidpointRounding.AwayFromZero);
        return Math.Clamp(MinTileSize + (steps * TileSizeStep), MinTileSize, MaxTileSize);
    }

    /// <summary>Enter in the page range box: selects the typed pages, or explains what is wrong.</summary>
    [RelayCommand]
    private void SelectRange()
    {
        if (Tiles.Count == 0)
        {
            return;
        }

        if (!PageRanges.TryParse(RangeText, Tiles.Count, out var indices, out var error))
        {
            RangeError = error.Kind switch
            {
                PageRangeErrorKind.Empty => Strings.Organizer_Range_Empty,
                PageRangeErrorKind.OutOfRange => string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_OutOfRange, error.Token, Tiles.Count),
                _ => string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_Invalid, error.Token),
            };
            return;
        }

        Select(indices, indices[0]);
    }

    [RelayCommand]
    private void SelectAll() => Select(Enumerable.Range(0, Tiles.Count));

    [RelayCommand(CanExecute = nameof(CanZoomTilesIn))]
    private void ZoomTilesIn() => TileSize += TileSizeStep;

    private bool CanZoomTilesIn() => _tileSize < MaxTileSize;

    [RelayCommand(CanExecute = nameof(CanZoomTilesOut))]
    private void ZoomTilesOut() => TileSize -= TileSizeStep;

    private bool CanZoomTilesOut() => _tileSize > MinTileSize;

    /// <summary>Double-click or Enter on a tile: back to the viewer, at that page.</summary>
    [RelayCommand]
    private void OpenPage(PageTileViewModel? tile)
    {
        if (tile is not null && !_disposed && _tilesByKey.ContainsKey(tile.Key))
        {
            _host.OpenPage(tile.Index);
        }
    }

    private void SetSelection(IEnumerable<Guid> keys, Guid? focusKey, IReadOnlyList<PageTileViewModel> tiles)
    {
        _selectedKeys = [.. keys];
        _selectedIndices = [.. tiles.Where(t => _selectedKeys.Contains(t.Key)).Select(t => t.Index)];
        _focusKey = focusKey is { } key && _selectedKeys.Contains(key) ? key : null;
        RangeText = PageRanges.Format(_selectedIndices);
        RangeError = null;
        OnPropertyChanged(nameof(SelectedIndices));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The tiles of this grid being dragged (one, or the selection).</summary>
    private List<PageTileViewModel> DraggedTiles(IDropInfo dropInfo)
    {
        var items = dropInfo.Data switch
        {
            PageTileViewModel tile => [tile],
            System.Collections.IEnumerable many and not string and not IDataObject => many.OfType<PageTileViewModel>(),
            _ => [],
        };

        return [.. items.Where(t => _tilesByKey.TryGetValue(t.Key, out var current) && ReferenceEquals(current, t)).Distinct()];
    }

    /// <summary>PDF files dragged from Explorer.</summary>
    private static List<string> DroppedPdfFiles(IDropInfo dropInfo)
        => dropInfo.Data is IDataObject data && data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? [.. files.Where(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase))]
            : [];
}
