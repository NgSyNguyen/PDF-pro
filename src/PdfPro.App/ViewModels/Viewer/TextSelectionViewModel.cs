using System.Text;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Viewer;

/// <summary>
/// Selecting text in the viewer: press on a character and drag (across pages too), double-click for a word,
/// triple-click for a line (dragging then goes word by word or line by line), Ctrl+A for the whole document. Points
/// arrive in viewport coordinates of the viewer and become page points through <see cref="PageCoordinateMapper"/>.
/// The selection is cleared when the pages change. Must be used on the UI thread.
/// </summary>
public sealed class TextSelectionViewModel
{
    /// <summary>How near a character a press must be to start a selection, in points.</summary>
    public const double PressTolerance = 2;

    private readonly DocumentViewerViewModel _viewer;
    private readonly TextLayerCache _layers;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly Func<Guid, Task<PageGeometry?>> _loadGeometry;
    private readonly ILogger _logger;
    private Position? _anchor;
    private Position? _focus;
    private Unit _unit;
    private bool _dragging;

    /// <param name="viewer">The viewer whose pages are selected.</param>
    /// <param name="layers">The pages' characters.</param>
    /// <param name="geometryOf">The page space of a page as it will be written, when known.</param>
    /// <param name="loadGeometry">Reads that page space.</param>
    /// <param name="logger">Logs pages whose text cannot be read.</param>
    public TextSelectionViewModel(DocumentViewerViewModel viewer, TextLayerCache layers, Func<Guid, PageGeometry?> geometryOf, Func<Guid, Task<PageGeometry?>> loadGeometry, ILogger logger)
    {
        _viewer = viewer;
        _layers = layers;
        _geometryOf = geometryOf;
        _loadGeometry = loadGeometry;
        _logger = logger;
    }

    /// <summary>The selection changed: pages redraw it.</summary>
    public event EventHandler? Changed;

    private enum Unit
    {
        Character,
        Word,
        Line,
    }

    public bool HasSelection => _anchor is not null && _focus is not null;

    /// <summary>A drag that started on text is under way.</summary>
    public bool IsDragging => _dragging;

    /// <summary>
    /// The page space of a page as it will be written (for drawing the selection), when known; otherwise it is read and
    /// <see cref="Changed"/> follows.
    /// </summary>
    public PageGeometry? GeometryOf(Guid pageKey)
    {
        if (_geometryOf(pageKey) is { } geometry)
        {
            return geometry;
        }

        _ = LoadGeometryAsync(pageKey);
        return null;
    }

    /// <summary>
    /// Mouse pressed at <paramref name="viewport"/>: on text it starts a selection (1 click: from this character, 2: the
    /// word, 3: the line) and returns true; elsewhere it clears the selection and returns false.
    /// </summary>
    public async Task<bool> PressAsync(ViewPoint viewport, int clickCount)
    {
        var hit = await LocateAsync(viewport, nearest: false);
        if (hit is not { } found || await LayerAsync(found.Index) is not { } layer || layer.CharAt(found.Point, PressTolerance) is not { } index)
        {
            _dragging = false;
            Clear();
            return false;
        }

        _unit = clickCount switch
        {
            2 => Unit.Word,
            >= 3 => Unit.Line,
            _ => Unit.Character,
        };
        _anchor = new Position(found.Index, Expand(layer, index));
        _focus = _unit == Unit.Character ? null : _anchor;
        _dragging = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The mouse moved to <paramref name="viewport"/> during a selection: it now reaches the nearest text there.</summary>
    public void DragTo(ViewPoint viewport)
    {
        if (!_dragging || _anchor is null || Locate(viewport, nearest: true) is not { } found)
        {
            return;
        }

        var page = _viewer.Document.Pages[found.Index];
        if (_layers.TryGet(page) is not { } layer)
        {
            _ = LoadLayerAsync(found.Index); // the next move will reach it
            return;
        }

        if (layer.CaretAt(found.Point) is not { } caret)
        {
            return; // a page without text: the selection stays as it is
        }

        var focus = new Position(found.Index, Expand(layer, caret));
        if (focus != _focus)
        {
            _focus = focus;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The mouse was released: the selection stays.</summary>
    public void Release() => _dragging = false;

    public void Clear()
    {
        if (_anchor is null && _focus is null)
        {
            return;
        }

        _anchor = null;
        _focus = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects <paramref name="range"/> of page <paramref name="pageIndex"/> (a search result).</summary>
    public void Select(int pageIndex, TextRange range)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        _dragging = false;
        _unit = Unit.Character;
        _anchor = new Position(pageIndex, range);
        _focus = _anchor;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+A: every character of the document.</summary>
    public void SelectAll()
    {
        if (_viewer.PageCount == 0)
        {
            return;
        }

        _dragging = false;
        _anchor = new Position(0, new TextRange(0, 0));
        _focus = new Position(_viewer.PageCount - 1, new TextRange(int.MaxValue, int.MaxValue));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The characters of page <paramref name="pageIndex"/> in the selection; null when none are.</summary>
    public TextRange? RangeOn(int pageIndex)
    {
        if (_anchor is not { } anchor || _focus is not { } focus)
        {
            return null;
        }

        var (first, last) = anchor.CompareTo(focus) <= 0 ? (anchor, focus) : (focus, anchor);
        var start = Math.Min(first.Range.Start, last.Page == first.Page ? last.Range.Start : int.MaxValue);
        var end = Math.Max(last.Range.End, last.Page == first.Page ? first.Range.End : 0);
        if (pageIndex < first.Page || pageIndex > last.Page)
        {
            return null;
        }

        return new TextRange(pageIndex == first.Page ? start : 0, pageIndex == last.Page ? end : int.MaxValue);
    }

    /// <summary>Where the selection is on a page, in its PDF space (one rectangle per line); empty when it is not there.</summary>
    public IReadOnlyList<PdfRect> RectsOn(Guid pageKey)
    {
        var index = _viewer.Document.IndexOf(pageKey);
        if (index < 0 || RangeOn(index) is not { } range || _layers.TryGet(_viewer.Document.Pages[index]) is not { } layer)
        {
            return [];
        }

        return layer.Rects(Clamp(range, layer));
    }

    /// <summary>
    /// The selected text as it is copied: NFC, lines of a paragraph joined, pages one after another; empty without a
    /// selection. Pages whose text cannot be read are left out.
    /// </summary>
    public async Task<string> GetTextAsync(CancellationToken cancellationToken = default)
    {
        if (!HasSelection)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var page = 0; page < _viewer.PageCount; page++)
        {
            if (RangeOn(page) is not { } range || await LayerAsync(page, cancellationToken) is not { } layer)
            {
                continue;
            }

            var text = layer.CopyText(Clamp(range, layer));
            if (text.Length > 0)
            {
                builder.Append(builder.Length > 0 ? "\r\n" : string.Empty).Append(text);
            }
        }

        return builder.ToString();
    }

    /// <summary>Whether <paramref name="viewport"/> is over text already read (for the text cursor); starts reading the page otherwise.</summary>
    public bool IsOverText(ViewPoint viewport)
    {
        if (Locate(viewport, nearest: false) is not { } found)
        {
            return false;
        }

        if (_layers.TryGet(_viewer.Document.Pages[found.Index]) is { } layer)
        {
            return layer.CharAt(found.Point, PressTolerance) is not null;
        }

        _ = LoadLayerAsync(found.Index);
        return false;
    }

    private static TextRange Clamp(TextRange range, PageText layer)
        => new(Math.Clamp(range.Start, 0, layer.Count), Math.Clamp(range.End, 0, layer.Count));

    private TextRange Expand(PageText layer, int index) => _unit switch
    {
        Unit.Word => layer.WordAt(index),
        Unit.Line => layer.LineAt(index),
        _ => new TextRange(index, index + 1),
    };

    /// <summary>The page under (or, with <paramref name="nearest"/>, nearest) a viewport point and the point on it in PDF space.</summary>
    private (int Index, PdfPoint Point)? Locate(ViewPoint viewport, bool nearest)
    {
        var layout = _viewer.Layout;
        var extent = new ViewPoint(viewport.X + _viewer.ScrollOffset.X, viewport.Y + _viewer.ScrollOffset.Y);
        var index = nearest ? layout.GetNearestPage(extent) : layout.GetPageAt(extent);
        if (index is not { } page || page < 0 || page >= _viewer.PageCount || GeometryOf(_viewer.Document.Pages[page].Key) is not { } geometry)
        {
            return null;
        }

        var bounds = layout.GetPageBounds(page);
        var mapper = new PageCoordinateMapper(geometry, layout.Scale);
        return (page, mapper.ScreenToPdf(new ViewPoint(extent.X - bounds.X, extent.Y - bounds.Y)));
    }

    private async Task<(int Index, PdfPoint Point)?> LocateAsync(ViewPoint viewport, bool nearest)
    {
        if (Locate(viewport, nearest) is { } found)
        {
            return found;
        }

        var extent = new ViewPoint(viewport.X + _viewer.ScrollOffset.X, viewport.Y + _viewer.ScrollOffset.Y);
        if (_viewer.Layout.GetPageAt(extent) is { } page && page >= 0 && page < _viewer.PageCount)
        {
            await _loadGeometry(_viewer.Document.Pages[page].Key);
            return Locate(viewport, nearest);
        }

        return null;
    }

    private async Task<PageText?> LayerAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _layers.GetAsync(_viewer.Document.Pages[pageIndex], PdfiumPriority.High, cancellationToken);
        }
        catch (PdfDocumentException ex)
        {
            _logger.LogWarning("The text of a page could not be read: {Failure}", ex.Failure);
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null; // the document closed meanwhile
        }
    }

    private async Task LoadGeometryAsync(Guid pageKey)
    {
        if (await _loadGeometry(pageKey) is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadLayerAsync(int pageIndex)
    {
        if (await LayerAsync(pageIndex) is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A place in the document: a page (by index while the pages do not change) and characters on it.</summary>
    private readonly record struct Position(int Page, TextRange Range) : IComparable<Position>
    {
        public int CompareTo(Position other)
            => Page != other.Page ? Page.CompareTo(other.Page) : Range.Start.CompareTo(other.Range.Start);
    }
}
