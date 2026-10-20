using System.Windows.Media;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Caching;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Rendering;

/// <summary>
/// Identifies one rendered image: a page (or a region of it) of an opened source document at a scale, turned and
/// cropped as the page plan shows it. Moving a page keeps its key; turning or cropping it gives a new one.
/// </summary>
/// <param name="DocumentId">The source document (<see cref="IPdfDocument.Id"/>).</param>
/// <param name="PageIndex">The page in the source document.</param>
/// <param name="ScaleKey">
/// Device pixels per point × 1000, rounded (renders within 0.1 % are interchangeable), or
/// <see cref="ThumbnailScaleKey"/> for the page's sidebar thumbnail.
/// </param>
/// <param name="Region">Part of the displayed page, in pixels at that scale; null for the whole page.</param>
/// <param name="QuarterTurns">The plan's extra rotation (thumbnails are always upright: 0).</param>
/// <param name="Crop">The plan's crop, if any.</param>
public readonly record struct RenderKey(Guid DocumentId, int PageIndex, int ScaleKey, PixelRect? Region = null, int QuarterTurns = 0, PageCrop? Crop = null)
{
    /// <summary>
    /// Thumbnails have one entry per page whatever their resolution, so the viewer can find one to show as a
    /// placeholder while the page renders.
    /// </summary>
    public const int ThumbnailScaleKey = -1;

    public bool IsThumbnail => ScaleKey == ThumbnailScaleKey;

    public static int ToScaleKey(double scale) => (int)Math.Round(scale * 1000, MidpointRounding.AwayFromZero);

    public static RenderKey Thumbnail(Guid documentId, int pageIndex, PageCrop? crop = null) => new(documentId, pageIndex, ThumbnailScaleKey, Crop: crop);

    /// <summary>The key of a page of the virtual document (not for blank pages, which are never rendered).</summary>
    public static RenderKey For(VirtualPage page, int scaleKey, PixelRect? region = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        var source = page.Source ?? throw new ArgumentException("Blank pages are not rendered.", nameof(page));
        return new RenderKey(source.Id, page.SourcePageIndex, scaleKey, region, page.QuarterTurns, page.Crop);
    }

    /// <summary>The sidebar thumbnail of a page of the virtual document (upright: the view turns it).</summary>
    public static RenderKey ThumbnailOf(VirtualPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var source = page.Source ?? throw new ArgumentException("Blank pages are not rendered.", nameof(page));
        return Thumbnail(source.Id, page.SourcePageIndex, page.Crop);
    }
}

/// <summary>
/// Rendered page images shared by every open tab, bounded by the memory budget from the settings
/// (SPEC: LRU by bytes, default 512 MB). Only the cache keeps images of pages that are off screen.
/// </summary>
/// <remarks>
/// Cached bitmaps are never drawn themselves (pages show a <see cref="DisplayImage"/> view of them), so WPF keeps
/// no GPU copy of them and the budget matches the memory the cache holds.
/// </remarks>
public sealed class RenderCache : IDisposable
{
    private const long BytesPerMegabyte = 1024 * 1024;

    private readonly ByteBudgetLruCache<RenderKey, ImageSource> _cache;
    private readonly ISettingsService _settings;

    public RenderCache(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _cache = new ByteBudgetLruCache<RenderKey, ImageSource>(settings.Current.RenderCacheMegabytes * BytesPerMegabyte);
        settings.Changed += OnSettingsChanged;
    }

    public long BudgetBytes => _cache.BudgetBytes;

    public long TotalBytes => _cache.TotalBytes;

    public int Count => _cache.Count;

    public bool TryGet(RenderKey key, out ImageSource image) => _cache.TryGet(key, out image);

    /// <param name="byteSize">Size of the image pixels.</param>
    public bool Add(RenderKey key, ImageSource image, long byteSize) => _cache.Set(key, image, byteSize);

    /// <summary>Drops every image of a closed document.</summary>
    public int RemoveDocument(Guid documentId) => _cache.RemoveWhere(key => key.DocumentId == documentId);

    public void Clear() => _cache.Clear();

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, AppSettings settings)
        => _cache.BudgetBytes = settings.RenderCacheMegabytes * BytesPerMegabyte;
}
