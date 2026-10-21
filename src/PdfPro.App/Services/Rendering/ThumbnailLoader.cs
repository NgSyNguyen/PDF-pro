using System.Windows.Media;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Documents;

namespace PdfPro.App.Services.Rendering;

/// <summary>An item that shows a thumbnail image (a sidebar thumbnail, an organizer tile).</summary>
internal interface IThumbnailItem
{
    /// <summary>The page key: one load per item at a time.</summary>
    Guid Key { get; }

    /// <summary>1-based page number (for the log).</summary>
    int Number { get; }

    ImageSource? Image { get; set; }

    /// <summary>The request the current <see cref="Image"/> was made for.</summary>
    ThumbnailRequest? ImageRequest { get; set; }

    bool LoadFailed { get; set; }
}

/// <summary>
/// Loads the thumbnails of the items a virtualized list shows: memory cache first, otherwise in the background (one
/// load per item: a newer request replaces an older one), the result applied on the UI thread. Released items drop
/// their image, so off-screen items hold none. Must be used on the UI thread.
/// </summary>
internal sealed class ThumbnailLoader(IThumbnailProvider provider, IUiDispatcher ui, ILogger logger) : IDisposable
{
    private readonly Dictionary<Guid, PendingLoad> _loads = [];
    private readonly HashSet<Guid> _documentIds = [];
    private bool _failureLogged;
    private bool _disposed;

    /// <summary>Thumbnails being loaded (tests and diagnostics).</summary>
    public int PendingCount => _loads.Count;

    /// <summary>Shows the thumbnail <paramref name="request"/> describes on <paramref name="item"/>.</summary>
    public void Load(IThumbnailItem item, ThumbnailRequest request)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(request);
        if (_disposed)
        {
            return;
        }

        if (_loads.TryGetValue(item.Key, out var pending))
        {
            if (pending.Request.IsSameImage(request))
            {
                return;
            }

            CancelLoad(item.Key);
        }

        if (item.Image is not null && item.ImageRequest is { } shown && shown.IsSameImage(request))
        {
            return;
        }

        if (provider.TryGetCached(request, out var cached))
        {
            Show(item, cached, request);
            return;
        }

        // Until the new image arrives the old one (turned by the view) stays on screen.
        var cancellation = new CancellationTokenSource();
        _loads[item.Key] = new PendingLoad(request, cancellation);
        _ = LoadAsync(item, request, cancellation, cancellation.Token);
    }

    /// <summary>The item left the screen (or is a blank page): no image, no load.</summary>
    public void Release(IThumbnailItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        CancelLoad(item.Key);
        item.Image = null;
        item.ImageRequest = null;
    }

    /// <summary>Remembers the source documents, to forget their per-document state on <see cref="Dispose"/>.</summary>
    public void Remember(VirtualDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var page in document.Pages)
        {
            if (page.Source is { } source)
            {
                _documentIds.Add(source.Id);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var load in _loads.Values)
        {
            load.Cancellation.Cancel();
            load.Cancellation.Dispose();
        }

        _loads.Clear();
        foreach (var id in _documentIds)
        {
            provider.ForgetDocument(id);
        }
    }

    private static void Show(IThumbnailItem item, ImageSource image, ThumbnailRequest request)
    {
        item.Image = DisplayImage.Of(image);
        item.ImageRequest = request;
        item.LoadFailed = false;
    }

    private async Task LoadAsync(IThumbnailItem item, ThumbnailRequest request, CancellationTokenSource owner, CancellationToken cancellationToken)
    {
        try
        {
            var image = await provider.GetAsync(request, cancellationToken).ConfigureAwait(false);
            ui.Post(() =>
            {
                if (Complete(item.Key, owner))
                {
                    Show(item, image, request);
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
            ui.Post(() =>
            {
                if (!Complete(item.Key, owner))
                {
                    return;
                }

                item.LoadFailed = true;
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    logger.LogWarning(ex, "Loading the thumbnail of page {PageNumber} failed", item.Number);
                }
            });
        }
    }

    /// <summary>Ends a load that is still current; false when it was cancelled or replaced meanwhile.</summary>
    private bool Complete(Guid key, CancellationTokenSource owner)
    {
        if (_disposed || !_loads.TryGetValue(key, out var load) || !ReferenceEquals(load.Cancellation, owner))
        {
            return false;
        }

        _loads.Remove(key);
        owner.Dispose();
        return true;
    }

    private void CancelLoad(Guid key)
    {
        if (_loads.Remove(key, out var load))
        {
            load.Cancellation.Cancel();
            load.Cancellation.Dispose();
        }
    }

    private sealed record PendingLoad(ThumbnailRequest Request, CancellationTokenSource Cancellation);
}
