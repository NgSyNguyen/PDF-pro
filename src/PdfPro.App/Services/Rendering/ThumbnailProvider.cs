using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure.Caching;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Rendering;

/// <summary>Size of sidebar thumbnails: pages are fitted into a 160 × 226 box (an A4 page fills it).</summary>
public static class ThumbnailGeometry
{
    public const double MaxWidth = 160;
    public const double MaxHeight = 226;

    /// <summary>View units per point that fit the page into the thumbnail box.</summary>
    public static double ScaleFor(PdfSize pageSize)
        => pageSize.Width <= 0 || pageSize.Height <= 0 ? 0 : Math.Min(MaxWidth / pageSize.Width, MaxHeight / pageSize.Height);

    /// <summary>Thumbnail size in view units.</summary>
    public static ViewSize SizeFor(PdfSize pageSize)
    {
        var scale = ScaleFor(pageSize);
        return new ViewSize(pageSize.Width * scale, pageSize.Height * scale);
    }

    /// <summary>Thumbnail size in device pixels at <paramref name="dpiScale"/>.</summary>
    public static (int Width, int Height) PixelSizeFor(PdfSize pageSize, double dpiScale)
    {
        var scale = ScaleFor(pageSize) * dpiScale;
        return (PixelSize.FromPoints(pageSize.Width, scale), PixelSize.FromPoints(pageSize.Height, scale));
    }
}

/// <summary>A thumbnail to load.</summary>
/// <param name="FilePath">The document's file, used to recognise it in the disk cache.</param>
/// <param name="DpiScale">Device pixels per view unit where the thumbnail is shown.</param>
public sealed record ThumbnailRequest(IPdfDocument Document, string FilePath, int PageIndex, double DpiScale)
{
    public (int Width, int Height) Pixels => ThumbnailGeometry.PixelSizeFor(Document.PageSizes[PageIndex], DpiScale);
}

/// <summary>Page thumbnails for the sidebar (seam for view-model tests).</summary>
public interface IThumbnailProvider
{
    /// <summary>The thumbnail if it is already in memory at the requested size (no I/O).</summary>
    bool TryGetCached(ThumbnailRequest request, out ImageSource image);

    /// <summary>Memory cache, then disk cache, then a render at low priority (which is saved to disk).</summary>
    Task<ImageSource> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken);

    /// <summary>Forgets per-document state when the document closes.</summary>
    void ForgetDocument(Guid documentId);
}

/// <summary>
/// Loads thumbnails from the shared <see cref="RenderCache"/>, the <see cref="ThumbnailDiskCache"/> (keyed by the
/// file's content fingerprint, so reopening a file shows its thumbnails at once) or PDFium. Encrypted documents
/// never reach the disk cache: their pages must not be readable without the password.
/// </summary>
internal sealed class ThumbnailProvider(
    IPageImageRenderer renderer,
    RenderCache cache,
    ThumbnailDiskCache diskCache,
    ILogger<ThumbnailProvider> logger) : IThumbnailProvider
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<string?>>> _fingerprints = new();
    private readonly ConcurrentDictionary<Task, byte> _pendingWrites = new();

    public bool TryGetCached(ThumbnailRequest request, out ImageSource image)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cache.TryGet(RenderKey.Thumbnail(request.Document.Id, request.PageIndex), out var cached)
            && cached is BitmapSource bitmap
            && bitmap.PixelWidth == request.Pixels.Width)
        {
            image = cached;
            return true;
        }

        image = null!;
        return false;
    }

    public async Task<ImageSource> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (TryGetCached(request, out var cached))
        {
            return cached;
        }

        var key = RenderKey.Thumbnail(request.Document.Id, request.PageIndex);
        var size = request.Pixels;
        var fingerprint = await FingerprintOfAsync(request).ConfigureAwait(false);
        if (fingerprint is not null)
        {
            var stored = await Task.Run(() => ReadFromDisk(fingerprint, request.PageIndex, size), cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                cache.Add(key, stored, (long)stored.PixelWidth * stored.PixelHeight * 4);
                return stored;
            }
        }

        var scale = ThumbnailGeometry.ScaleFor(request.Document.PageSizes[request.PageIndex]) * request.DpiScale;
        var rendered = await renderer.RenderAsync(request.Document, request.PageIndex, scale, region: null, PdfiumPriority.Low, cancellationToken).ConfigureAwait(false);
        cache.Add(key, rendered.Image, rendered.ByteSize);
        if (fingerprint is not null && rendered.Image is BitmapSource bitmap)
        {
            TrackWrite(Task.Run(() => SaveToDiskAsync(fingerprint, request.PageIndex, bitmap), CancellationToken.None));
        }

        return rendered.Image;
    }

    public void ForgetDocument(Guid documentId) => _fingerprints.TryRemove(documentId, out _);

    /// <summary>Waits for thumbnails being written to disk (tests).</summary>
    internal Task WhenWritesCompleteAsync() => Task.WhenAll(_pendingWrites.Keys);

    private Task<string?> FingerprintOfAsync(ThumbnailRequest request)
    {
        if (request.Document.Features.IsEncrypted)
        {
            return Task.FromResult<string?>(null);
        }

        var lazy = _fingerprints.GetOrAdd(
            request.Document.Id,
            _ => new Lazy<Task<string?>>(() => ComputeFingerprintAsync(request.FilePath)));
        return lazy.Value;
    }

    private async Task<string?> ComputeFingerprintAsync(string filePath)
    {
        try
        {
            return await Task.Run(() => DocumentFingerprint.ComputeAsync(filePath)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "The document fingerprint could not be computed; thumbnails will not be cached on disk");
            return null;
        }
    }

    private BitmapSource? ReadFromDisk(string fingerprint, int pageIndex, (int Width, int Height) size)
    {
        var png = diskCache.TryRead(fingerprint, pageIndex, size.Width);
        if (png is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame.PixelWidth == size.Width && frame.PixelHeight == size.Height ? frame : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException)
        {
            logger.LogDebug(ex, "A cached thumbnail could not be decoded; it will be rendered again");
            return null;
        }
    }

    private async Task SaveToDiskAsync(string fingerprint, int pageIndex, BitmapSource image)
    {
        byte[] png;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            png = stream.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
        {
            logger.LogDebug(ex, "A thumbnail could not be encoded for the disk cache");
            return;
        }

        await diskCache.WriteAsync(fingerprint, pageIndex, image.PixelWidth, png).ConfigureAwait(false);
    }

    private void TrackWrite(Task write)
    {
        _pendingWrites.TryAdd(write, 0);
        write.ContinueWith(t => _pendingWrites.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
