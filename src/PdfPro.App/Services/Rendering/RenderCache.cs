using System.Windows.Media;
using PdfPro.Core.Caching;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Services.Rendering;

/// <summary>Identifies one rendered image: a page (or a region of it) of an opened document at a scale.</summary>
/// <param name="ScaleKey">Device pixels per point × 1000, rounded (renders within 0.1 % are interchangeable).</param>
public readonly record struct RenderKey(Guid DocumentId, int PageIndex, int ScaleKey, PixelRect? Region = null)
{
    public static int ToScaleKey(double scale) => (int)Math.Round(scale * 1000, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Rendered page images shared by every open tab, bounded by the memory budget from the settings
/// (SPEC: LRU by bytes, default 512 MB). Only the cache keeps images of pages that are off screen.
/// </summary>
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

    public bool Add(RenderKey key, ImageSource image, long byteSize) => _cache.Set(key, image, byteSize);

    /// <summary>Drops every image of a closed document.</summary>
    public int RemoveDocument(Guid documentId) => _cache.RemoveWhere(key => key.DocumentId == documentId);

    public void Clear() => _cache.Clear();

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, AppSettings settings)
        => _cache.BudgetBytes = settings.RenderCacheMegabytes * BytesPerMegabyte;
}
