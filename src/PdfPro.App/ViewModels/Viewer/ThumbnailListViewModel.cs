using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Viewer;

/// <summary>A page thumbnail in the sidebar. Its image is loaded only while the list shows it.</summary>
public sealed partial class ThumbnailViewModel : ObservableObject, IRealizable
{
    private readonly ThumbnailListViewModel _owner;

    internal ThumbnailViewModel(ThumbnailListViewModel owner, int index, ViewSize size)
    {
        _owner = owner;
        Index = index;
        Number = index + 1;
        Width = size.Width;
        Height = size.Height;
        AutomationName = string.Format(CultureInfo.CurrentCulture, Strings.Page_AutomationName, Number);
    }

    public int Index { get; }

    /// <summary>1-based page number shown under the thumbnail.</summary>
    public int Number { get; }

    /// <summary>Thumbnail size in view units, known before the image loads so the list does not jump.</summary>
    public double Width { get; }

    public double Height { get; }

    public string AutomationName { get; }

    [ObservableProperty]
    public partial ImageSource? Image { get; internal set; }

    /// <summary>The page could not be rendered; the view shows a warning icon.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; internal set; }

    /// <summary>DPI the current <see cref="Image"/> was made for.</summary>
    internal double? ImageDpiScale { get; set; }

    public void Realize(double dpiScale) => _owner.Realize(this, dpiScale);

    public void Release() => _owner.Release(this);
}

/// <summary>
/// Sidebar thumbnails of one document (SPEC P2: 160 px wide, low priority, disk cache). The selection follows the
/// viewer's current page; selecting another thumbnail (click, arrow keys) shows that page in the viewer.
/// Must be used on the UI thread.
/// </summary>
public sealed partial class ThumbnailListViewModel : ObservableObject, IDisposable
{
    private readonly IPdfDocument _document;
    private readonly string _filePath;
    private readonly DocumentViewerViewModel _viewer;
    private readonly IThumbnailProvider _provider;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly Dictionary<int, PendingLoad> _loads = [];
    private bool _syncingSelection;
    private bool _failureLogged;
    private bool _disposed;

    public ThumbnailListViewModel(
        IPdfDocument document,
        string filePath,
        DocumentViewerViewModel viewer,
        IThumbnailProvider provider,
        IUiDispatcher ui,
        ILogger<ThumbnailListViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(viewer);
        _document = document;
        _filePath = filePath;
        _viewer = viewer;
        _provider = provider;
        _ui = ui;
        _logger = logger;
        Items = document.PageSizes.Select((size, i) => new ThumbnailViewModel(this, i, ThumbnailGeometry.SizeFor(size))).ToArray();
        _viewer.PropertyChanged += OnViewerPropertyChanged;
        SyncSelection();
    }

    public IReadOnlyList<ThumbnailViewModel> Items { get; }

    /// <summary>The thumbnail of the viewer's current page; set by the list when the user picks another one.</summary>
    [ObservableProperty]
    public partial ThumbnailViewModel? SelectedItem { get; set; }

    /// <summary>Thumbnails being loaded (tests and diagnostics).</summary>
    public int PendingCount => _loads.Count;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewer.PropertyChanged -= OnViewerPropertyChanged;
        foreach (var load in _loads.Values)
        {
            load.Cancellation.Cancel();
            load.Cancellation.Dispose();
        }

        _loads.Clear();
        _provider.ForgetDocument(_document.Id);
    }

    internal void Realize(ThumbnailViewModel item, double dpiScale)
    {
        if (_disposed || dpiScale <= 0)
        {
            return;
        }

        if (_loads.TryGetValue(item.Index, out var pending))
        {
            if (SameDpi(pending.DpiScale, dpiScale))
            {
                return;
            }

            CancelLoad(item.Index);
        }

        if (item.Image is not null && item.ImageDpiScale is { } loaded && SameDpi(loaded, dpiScale))
        {
            return;
        }

        var request = new ThumbnailRequest(_document, _filePath, item.Index, dpiScale);
        if (_provider.TryGetCached(request, out var cached))
        {
            Show(item, cached, dpiScale);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _loads[item.Index] = new PendingLoad(dpiScale, cancellation);
        _ = LoadAsync(item, request, cancellation, cancellation.Token);
    }

    internal void Release(ThumbnailViewModel item)
    {
        CancelLoad(item.Index);
        item.Image = null;
        item.ImageDpiScale = null;
    }

    partial void OnSelectedItemChanged(ThumbnailViewModel? value)
    {
        if (_syncingSelection || _disposed)
        {
            return;
        }

        if (value is null)
        {
            // The list clears its selection while it switches documents: restore it once that settles.
            _ui.Post(SyncSelection);
            return;
        }

        if (value.Index != _viewer.CurrentPageIndex)
        {
            _viewer.NavigateTo(value.Index);
        }
    }

    private static bool SameDpi(double a, double b) => Math.Abs(a - b) < 1e-6;

    /// <summary>Activating a thumbnail (click, Enter) shows the top of its page, even when it is already selected.</summary>
    [RelayCommand]
    private void ShowPage(ThumbnailViewModel? item)
    {
        if (item is not null && !_disposed)
        {
            _viewer.NavigateTo(item.Index);
        }
    }

    private static void Show(ThumbnailViewModel item, ImageSource image, double dpiScale)
    {
        item.Image = image;
        item.ImageDpiScale = dpiScale;
        item.LoadFailed = false;
    }

    private async Task LoadAsync(ThumbnailViewModel item, ThumbnailRequest request, CancellationTokenSource owner, CancellationToken cancellationToken)
    {
        try
        {
            var image = await _provider.GetAsync(request, cancellationToken).ConfigureAwait(false);
            _ui.Post(() =>
            {
                if (Complete(item.Index, owner))
                {
                    Show(item, image, request.DpiScale);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Scrolled away, or the document was closed.
        }
        catch (ObjectDisposedException)
        {
            // The document was closed while the thumbnail was queued.
        }
        catch (Exception ex)
        {
            _ui.Post(() =>
            {
                if (!Complete(item.Index, owner))
                {
                    return;
                }

                item.LoadFailed = true;
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    _logger.LogWarning(ex, "Loading the thumbnail of page {PageNumber} failed", item.Number);
                }
            });
        }
    }

    /// <summary>Ends a load that is still current; false when it was cancelled or replaced meanwhile.</summary>
    private bool Complete(int index, CancellationTokenSource owner)
    {
        if (_disposed || !_loads.TryGetValue(index, out var load) || !ReferenceEquals(load.Cancellation, owner))
        {
            return false;
        }

        _loads.Remove(index);
        owner.Dispose();
        return true;
    }

    private void CancelLoad(int index)
    {
        if (_loads.Remove(index, out var load))
        {
            load.Cancellation.Cancel();
            load.Cancellation.Dispose();
        }
    }

    private void OnViewerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewerViewModel.CurrentPageIndex))
        {
            SyncSelection();
        }
    }

    private void SyncSelection()
    {
        if (_disposed || Items.Count == 0)
        {
            return;
        }

        var current = Items[Math.Clamp(_viewer.CurrentPageIndex, 0, Items.Count - 1)];
        if (ReferenceEquals(SelectedItem, current))
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            SelectedItem = current;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private sealed record PendingLoad(double DpiScale, CancellationTokenSource Cancellation);
}
