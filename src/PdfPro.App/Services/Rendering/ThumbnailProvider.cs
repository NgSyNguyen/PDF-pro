using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure.Caching;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Rendering;

/// <summary>
/// Size of thumbnails: pages are fitted into a box of the A4 proportions, 160 × 226 in the sidebar (an A4 page fills
/// it) and as wide as the organizer's tile size there.
/// </summary>
public static class ThumbnailGeometry
{
    public const double MaxWidth = 160;
    public const double MaxHeight = 226;

    /// <summary>Height of a box <paramref name="boxWidth"/> wide.</summary>
    public static double BoxHeightFor(double boxWidth) => boxWidth * MaxHeight / MaxWidth;

    /// <summary>View units per point that fit the page into the sidebar box.</summary>
    public static double ScaleFor(PdfSize pageSize) => ScaleFor(pageSize, MaxWidth);

    /// <summary>View units per point that fit the page into a box <paramref name="boxWidth"/> wide.</summary>
    public static double ScaleFor(PdfSize pageSize, double boxWidth)
        => pageSize.Width <= 0 || pageSize.Height <= 0 || boxWidth <= 0
            ? 0
            : Math.Min(boxWidth / pageSize.Width, BoxHeightFor(boxWidth) / pageSize.Height);

    /// <summary>Sidebar thumbnail size in view units.</summary>
    public static ViewSize SizeFor(PdfSize pageSize) => SizeFor(pageSize, MaxWidth);

    /// <summary>Thumbnail size in view units in a box <paramref name="boxWidth"/> wide.</summary>
    public static ViewSize SizeFor(PdfSize pageSize, double boxWidth)
    {
        var scale = ScaleFor(pageSize, boxWidth);
        return new ViewSize(pageSize.Width * scale, pageSize.Height * scale);
    }

    /// <summary>Sidebar thumbnail size in device pixels at <paramref name="dpiScale"/>.</summary>
    public static (int Width, int Height) PixelSizeFor(PdfSize pageSize, double dpiScale)
    {
        var scale = ScaleFor(pageSize) * dpiScale;
        return (PixelSize.FromPoints(pageSize.Width, scale), PixelSize.FromPoints(pageSize.Height, scale));
    }
}

/// <summary>
/// A thumbnail to load: a page of the virtual document rendered upright (the view turns it), at the scale that makes it
/// fill the thumbnail box once turned.
/// </summary>
/// <param name="Page">The page (not blank).</param>
/// <param name="FilePath">The source file, used to recognise it in the disk cache (empty: no disk cache).</param>
/// <param name="DpiScale">Device pixels per view unit where the thumbnail is shown.</param>
/// <param name="BoxWidth">Width of the box the thumbnail fits in: the sidebar's, or an organizer tile size.</param>
public sealed record ThumbnailRequest(VirtualPage Page, string FilePath, double DpiScale, double BoxWidth = ThumbnailGeometry.MaxWidth)
{
    public IPdfDocument Document => Page.Source ?? throw new InvalidOperationException("Blank pages have no thumbnail image.");

    public int PageIndex => Page.SourcePageIndex;

    /// <summary>Device pixels per point.</summary>
    public double Scale => ThumbnailGeometry.ScaleFor(Page.DisplaySize, BoxWidth) * DpiScale;

    /// <summary>The memory cache entry: the sidebar thumbnail, or the tile of this size.</summary>
    public RenderKey CacheKey => BoxWidth == ThumbnailGeometry.MaxWidth
        ? RenderKey.ThumbnailOf(Page)
        : RenderKey.TileOf(Page, (int)Math.Round(BoxWidth));

    /// <summary>Size of the upright image in device pixels.</summary>
    public (int Width, int Height) Pixels
    {
        get
        {
            var size = Page.Crop?.Size ?? Page.BaseSize;
            return (PixelSize.FromPoints(size.Width, Scale), PixelSize.FromPoints(size.Height, Scale));
        }
    }

    /// <summary>
    /// Only sidebar thumbnails of uncropped pages are cached on disk: cropped pages are rare (their files would need the
    /// crop in their name), and organizer tiles come in many sizes that would multiply the cache for little gain.
    /// </summary>
    public bool CanUseDiskCache => Page.Crop is null && FilePath.Length > 0 && BoxWidth == ThumbnailGeometry.MaxWidth;

    /// <summary>True when both requests produce the same image.</summary>
    public bool IsSameImage(ThumbnailRequest other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(Page.Source, other.Page.Source) && PageIndex == other.PageIndex && Page.Crop == other.Page.Crop && Pixels == other.Pixels;
    }
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
        if (cache.TryGet(request.CacheKey, out var cached)
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

        var key = request.CacheKey;
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

        var options = request.Page.RenderOptions(request.Scale, region: null, PdfRenderFlags.Annotations, extraQuarterTurns: -request.Page.QuarterTurns);
        var rendered = await renderer.RenderAsync(request.Document, request.PageIndex, options, PdfiumPriority.Low, cancellationToken).ConfigureAwait(false);
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
        if (request.Document.Features.IsEncrypted || !request.CanUseDiskCache)
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
            if (frame.PixelWidth != size.Width || frame.PixelHeight != size.Height)
            {
                return null;
            }

            // A decoded frame stays tied to its decoder, which belongs to this pool thread even when frozen; the UI
            // thread could not wrap it for display. Copy the pixels into a bitmap that any thread may use.
            var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            var stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            pixels.CopyPixels(buffer, stride, 0);
            var bitmap = BitmapSource.Create(size.Width, size.Height, 96, 96, PixelFormats.Bgr32, null, buffer, stride);
            bitmap.Freeze();
            return bitmap;
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
