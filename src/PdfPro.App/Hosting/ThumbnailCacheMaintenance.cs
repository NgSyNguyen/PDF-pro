using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.Caching;

namespace PdfPro.App.Hosting;

/// <summary>
/// Trims the thumbnail disk cache once per run (entries unused for 30 days, then least recently used ones beyond
/// 500 MB). It waits a little after startup so it never competes with opening the first document.
/// </summary>
internal sealed class ThumbnailCacheMaintenance(ThumbnailDiskCache cache, ILogger<ThumbnailCacheMaintenance> logger) : BackgroundService
{
    internal static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);
            await Task.Run(() => cache.Prune(ThumbnailDiskCache.DefaultMaxBytes, ThumbnailDiskCache.DefaultMaxAge), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The app is closing.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The thumbnail cache could not be trimmed");
        }
    }
}
