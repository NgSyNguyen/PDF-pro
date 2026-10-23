using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Viewer;

/// <summary>
/// A page thumbnail in the sidebar. Its image is loaded only while the list shows it. The image is upright; the view
/// turns it by <see cref="Angle"/>, so turning a page shows at once.
/// </summary>
public sealed partial class ThumbnailViewModel : ObservableObject, IRealizable, IThumbnailItem
{
    private readonly ThumbnailListViewModel _owner;

    internal ThumbnailViewModel(ThumbnailListViewModel owner, VirtualPage page, int index, SplitMarkBadge badge)
    {
        _owner = owner;
        Key = page.Key;
        Update(page, index, badge);
    }

    public Guid Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number), nameof(AutomationName))]
    public partial int Index { get; private set; }

    /// <summary>1-based page number shown under the thumbnail.</summary>
    public int Number => Index + 1;

    public string AutomationName => string.Format(CultureInfo.CurrentCulture, Strings.Page_AutomationName, Number);

    /// <summary>Thumbnail size in view units (as displayed, turned), known before the image loads so the list does not jump.</summary>
    [ObservableProperty]
    public partial double Width { get; private set; }

    [ObservableProperty]
    public partial double Height { get; private set; }

    /// <summary>Clockwise angle the view turns the upright image by (the plan's rotation).</summary>
    [ObservableProperty]
    public partial double Angle { get; private set; }

    /// <summary>Blank pages have no image: the white tile is the page.</summary>
    [ObservableProperty]
    public partial bool IsBlank { get; private set; }

    [ObservableProperty]
    public partial ImageSource? Image { get; internal set; }

    /// <summary>The page could not be rendered; the view shows a warning icon.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; internal set; }

    /// <summary>"Tệp 3" when the page starts a file of a split by marks; null otherwise.</summary>
    [ObservableProperty]
    public partial string? SplitBadge { get; private set; }

    [ObservableProperty]
    public partial string? SplitToolTip { get; private set; }

    internal VirtualPage Page { get; private set; } = null!;

    /// <summary>The request the current <see cref="Image"/> was made for.</summary>
    internal ThumbnailRequest? ImageRequest { get; set; }

    ImageSource? IThumbnailItem.Image { get => Image; set => Image = value; }

    ThumbnailRequest? IThumbnailItem.ImageRequest { get => ImageRequest; set => ImageRequest = value; }

    bool IThumbnailItem.LoadFailed { get => LoadFailed; set => LoadFailed = value; }

    public void Realize(double dpiScale) => _owner.Realize(this, dpiScale);

    public void Release() => _owner.Release(this);

    internal void Update(VirtualPage page, int index, SplitMarkBadge badge)
    {
        Page = page;
        Index = index;
        SplitBadge = badge.Text;
        SplitToolTip = badge.ToolTip;
        var size = ThumbnailGeometry.SizeFor(page.DisplaySize);
        Width = size.Width;
        Height = size.Height;
        Angle = page.QuarterTurns * 90;
        IsBlank = page.IsBlank;
    }
}

/// <summary>
/// Sidebar thumbnails of one document (SPEC P2: 160 px wide, low priority, disk cache), following the page plan. The
/// selection follows the viewer's current page; selecting another thumbnail (click, arrow keys) shows that page.
/// Must be used on the UI thread.
/// </summary>
public sealed partial class ThumbnailListViewModel : ObservableObject, IDisposable
{
    private readonly Func<IPdfDocument, string?> _filePathOf;
    private readonly DocumentViewerViewModel _viewer;
    private readonly IUiDispatcher _ui;
    private readonly ThumbnailLoader _loader;
    private bool _syncingSelection;
    private bool _disposed;

    /// <param name="document">The pages to show.</param>
    /// <param name="filePathOf">The file of a source document, to recognise it in the disk cache.</param>
    /// <param name="viewer">The viewer whose current page the selection follows.</param>
    /// <param name="provider">Loads the images.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="logger">Logs the first failure.</param>
    public ThumbnailListViewModel(
        VirtualDocument document,
        Func<IPdfDocument, string?> filePathOf,
        DocumentViewerViewModel viewer,
        IThumbnailProvider provider,
        IUiDispatcher ui,
        ILogger<ThumbnailListViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(filePathOf);
        ArgumentNullException.ThrowIfNull(viewer);
        _filePathOf = filePathOf;
        _viewer = viewer;
        _ui = ui;
        _loader = new ThumbnailLoader(provider, ui, logger);
        var badges = SplitMarkBadge.For(document.Plan);
        Items = document.Pages.Select((page, i) => new ThumbnailViewModel(this, page, i, badges[i])).ToArray();
        _loader.Remember(document);
        _viewer.PropertyChanged += OnViewerPropertyChanged;
        SyncSelection();
    }

    [ObservableProperty]
    public partial IReadOnlyList<ThumbnailViewModel> Items { get; private set; }

    /// <summary>The thumbnail of the viewer's current page; set by the list when the user picks another one.</summary>
    [ObservableProperty]
    public partial ThumbnailViewModel? SelectedItem { get; set; }

    /// <summary>Thumbnails being loaded (tests and diagnostics).</summary>
    public int PendingCount => _loader.PendingCount;

    /// <summary>The page plan changed: thumbnails follow their pages; pages that turned or were cropped reload.</summary>
    public void SetDocument(VirtualDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var existing = Items.ToDictionary(i => i.Key);
        var badges = SplitMarkBadge.For(document.Plan);
        var items = new ThumbnailViewModel[document.PageCount];
        for (var i = 0; i < items.Length; i++)
        {
            var page = document.Pages[i];
            if (existing.Remove(page.Key, out var item))
            {
                item.Update(page, i, badges[i]);
            }
            else
            {
                item = new ThumbnailViewModel(this, page, i, badges[i]);
            }

            items[i] = item;
        }

        foreach (var removed in existing.Values)
        {
            Release(removed);
        }

        _loader.Remember(document);
        _syncingSelection = true;
        try
        {
            Items = items;
        }
        finally
        {
            _syncingSelection = false;
        }

        SyncSelection();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewer.PropertyChanged -= OnViewerPropertyChanged;
        _loader.Dispose();
    }

    internal void Realize(ThumbnailViewModel item, double dpiScale)
    {
        if (_disposed || dpiScale <= 0)
        {
            return;
        }

        if (item.Page.Source is not { } source)
        {
            Release(item); // blank page: the tile itself is the page
            return;
        }

        _loader.Load(item, new ThumbnailRequest(item.Page, _filePathOf(source) ?? string.Empty, dpiScale));
    }

    internal void Release(ThumbnailViewModel item) => _loader.Release(item);

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

    /// <summary>Activating a thumbnail (click, Enter) shows the top of its page, even when it is already selected.</summary>
    [RelayCommand]
    private void ShowPage(ThumbnailViewModel? item)
    {
        if (item is not null && !_disposed)
        {
            _viewer.NavigateTo(item.Index);
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
}
