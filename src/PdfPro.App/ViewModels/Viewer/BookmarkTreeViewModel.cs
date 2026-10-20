using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Core.Viewing;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Viewer;

public enum BookmarkTreeState
{
    Loading,
    Ready,
    Empty,
    Failed,
}

/// <summary>An entry of the bookmark tree.</summary>
public sealed partial class BookmarkItemViewModel : ObservableObject
{
    internal BookmarkItemViewModel(string title, PdfDestination? destination, IReadOnlyList<BookmarkItemViewModel> children)
    {
        Title = title;
        Destination = destination;
        Children = children;
    }

    /// <summary>One-line NFC title (control characters removed); a placeholder when the title is empty.</summary>
    public string Title { get; }

    /// <summary>Where the bookmark goes; null for entries that only group others (or use unsupported actions).</summary>
    public PdfDestination? Destination { get; }

    public IReadOnlyList<BookmarkItemViewModel> Children { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>
/// The document outline in the sidebar. It is read once in the background when the tab opens; activating an entry
/// (click, Enter) scrolls the viewer to its destination. Must be used on the UI thread.
/// </summary>
public sealed partial class BookmarkTreeViewModel : ObservableObject, IDisposable
{
    private readonly IPdfDocument _document;
    private readonly DocumentViewerViewModel _viewer;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public BookmarkTreeViewModel(IPdfDocument document, DocumentViewerViewModel viewer, IUiDispatcher ui, ILogger<BookmarkTreeViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(viewer);
        _document = document;
        _viewer = viewer;
        _ui = ui;
        _logger = logger;
        Items = [];
        State = BookmarkTreeState.Loading;
    }

    [ObservableProperty]
    public partial IReadOnlyList<BookmarkItemViewModel> Items { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(HasItems), nameof(IsEmpty), nameof(IsFailed))]
    public partial BookmarkTreeState State { get; private set; }

    public bool IsLoading => State == BookmarkTreeState.Loading;

    public bool HasItems => State == BookmarkTreeState.Ready;

    public bool IsEmpty => State == BookmarkTreeState.Empty;

    public bool IsFailed => State == BookmarkTreeState.Failed;

    /// <summary>Reads the outline and builds the tree off the UI thread; failures only change <see cref="State"/>.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<BookmarkItemViewModel> items;
        try
        {
            var bookmarks = await _document.GetBookmarksAsync(_lifetime.Token).ConfigureAwait(false);
            items = Build(bookmarks);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return; // the document was closed meanwhile
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the bookmarks failed");
            _ui.Post(() =>
            {
                if (!_disposed)
                {
                    State = BookmarkTreeState.Failed;
                }
            });
            return;
        }

        _ui.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            Items = items;
            State = items.Count == 0 ? BookmarkTreeState.Empty : BookmarkTreeState.Ready;
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Turns the outline into item view models; a lone root entry starts expanded.</summary>
    internal static IReadOnlyList<BookmarkItemViewModel> Build(IReadOnlyList<PdfBookmark> bookmarks)
    {
        var items = BuildLevel(bookmarks);
        if (items.Length == 1 && items[0].Children.Count > 0)
        {
            items[0].IsExpanded = true;
        }

        return items;
    }

    private static BookmarkItemViewModel[] BuildLevel(IReadOnlyList<PdfBookmark> bookmarks)
    {
        var items = new BookmarkItemViewModel[bookmarks.Count];
        for (var i = 0; i < items.Length; i++)
        {
            var bookmark = bookmarks[i];
            var title = TextNormalizer.ToSingleLine(bookmark.Title);
            items[i] = new BookmarkItemViewModel(
                title.Length > 0 ? title : Strings.Bookmark_Untitled,
                bookmark.Destination,
                BuildLevel(bookmark.Children));
        }

        return items;
    }

    /// <summary>
    /// Shows the bookmark's destination: the first page of the plan showing its target page, scrolled to the
    /// destination's top when it has one (moved through the page's crop and rotation). Does nothing when the target
    /// page was deleted.
    /// </summary>
    [RelayCommand]
    private async Task NavigateAsync(BookmarkItemViewModel? item)
    {
        if (_disposed || item?.Destination is not { } destination || _viewer.Document.FirstIndexOf(_document, destination.PageIndex) < 0)
        {
            return;
        }

        ViewPoint? pointInSource = null;
        if (destination.Left is not null || destination.Top is not null)
        {
            try
            {
                var geometry = await _document.GetPageGeometryAsync(destination.PageIndex, _lifetime.Token);
                pointInSource = DestinationPlacement.PointInPage(geometry, destination.Left, destination.Top, scale: 1);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The page itself will show its own error; going to its top is still useful.
                _logger.LogWarning(ex, "The page of a bookmark could not be measured; showing the top of the page");
            }
        }

        // The plan may have changed while the page was measured.
        var index = _viewer.Document.FirstIndexOf(_document, destination.PageIndex);
        if (_disposed || index < 0)
        {
            return;
        }

        ViewPoint? pointInPage = null;
        if (pointInSource is { } point)
        {
            var shown = _viewer.Document.Pages[index].FromSourcePoint(point);
            pointInPage = new ViewPoint(shown.X * _viewer.Layout.Scale, shown.Y * _viewer.Layout.Scale);
        }

        _viewer.NavigateTo(index, pointInPage);
    }
}
