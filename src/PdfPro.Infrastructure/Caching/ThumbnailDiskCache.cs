using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.IO;

namespace PdfPro.Infrastructure.Caching;

/// <summary>
/// Page thumbnails on disk (SPEC: %LOCALAPPDATA%\PdfPro\cache\thumbnails): one folder per document fingerprint,
/// one PNG per page and pixel width. A folder's last write time marks its last use (set by writes, refreshed on
/// the first read of each session); <see cref="Prune"/> removes the least recently used folders. Safe to delete at
/// any time; failures only cost a re-render, so they are logged and swallowed.
/// </summary>
public sealed class ThumbnailDiskCache(AppPaths paths, ILogger<ThumbnailDiskCache> logger)
{
    public const long DefaultMaxBytes = 500L * 1024 * 1024;
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, byte> _touched = new(StringComparer.Ordinal);

    public string RootDirectory => Path.Combine(paths.CacheDirectory, "thumbnails");

    /// <summary>The cached PNG, or null when there is none (or it cannot be read).</summary>
    public byte[]? TryRead(string fingerprint, int pageIndex, int pixelWidth)
    {
        var path = PathFor(fingerprint, pageIndex, pixelWidth);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (_touched.TryAdd(fingerprint, 0))
            {
                Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(path)!, DateTime.UtcNow);
            }

            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Reading a cached thumbnail failed");
            return null;
        }
    }

    public async Task WriteAsync(string fingerprint, int pageIndex, int pixelWidth, byte[] png, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        var path = PathFor(fingerprint, pageIndex, pixelWidth);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await AtomicFile.WriteAsync(path, (stream, ct) => stream.WriteAsync(png, ct).AsTask(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Writing a cached thumbnail failed");
        }
    }

    /// <summary>
    /// Deletes document folders unused for longer than <paramref name="maxAge"/>, then the least recently used
    /// ones until the cache holds at most <paramref name="maxBytes"/>.
    /// </summary>
    /// <returns>Bytes freed.</returns>
    public long Prune(long maxBytes, TimeSpan maxAge)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var folders = DocumentFolders()
            .Select(d => (Folder: d, Bytes: SizeOf(d), LastUse: d.LastWriteTimeUtc))
            .OrderBy(f => f.LastUse)
            .ToList();
        var total = folders.Sum(f => f.Bytes);
        var cutoff = DateTime.UtcNow - maxAge;
        long freed = 0;
        foreach (var (folder, bytes, lastUse) in folders)
        {
            if (lastUse >= cutoff && total <= maxBytes)
            {
                break; // the remaining folders are newer and the cache is small enough
            }

            if (TryDelete(folder))
            {
                total -= bytes;
                freed += bytes;
            }
        }

        if (freed > 0)
        {
            logger.LogInformation("Thumbnail cache pruned: {FreedBytes} bytes freed, {RemainingBytes} bytes kept", freed, total);
        }

        return freed;
    }

    /// <summary>Removes every cached thumbnail. Returns the bytes freed.</summary>
    public long Clear()
    {
        long freed = 0;
        foreach (var folder in DocumentFolders())
        {
            var bytes = SizeOf(folder);
            if (TryDelete(folder))
            {
                freed += bytes;
            }
        }

        _touched.Clear();
        return freed;
    }

    /// <summary>Total size of the cached thumbnails in bytes.</summary>
    public long GetSize() => DocumentFolders().Sum(SizeOf);

    private string PathFor(string fingerprint, int pageIndex, int pixelWidth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        if (!fingerprint.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A fingerprint is hexadecimal.", nameof(fingerprint));
        }

        return Path.Combine(RootDirectory, fingerprint, string.Create(CultureInfo.InvariantCulture, $"{pageIndex}_{pixelWidth}.png"));
    }

    private DirectoryInfo[] DocumentFolders()
    {
        try
        {
            return Directory.Exists(RootDirectory) ? new DirectoryInfo(RootDirectory).GetDirectories() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Listing the thumbnail cache failed");
            return [];
        }
    }

    private static long SizeOf(DirectoryInfo folder)
    {
        try
        {
            return folder.EnumerateFiles().Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private bool TryDelete(DirectoryInfo folder)
    {
        try
        {
            folder.Delete(recursive: true);
            _touched.TryRemove(folder.Name, out _);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Deleting a thumbnail folder failed");
            return false;
        }
    }
}
