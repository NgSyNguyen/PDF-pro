using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.IO;

namespace PdfPro.Infrastructure.Splitting;

/// <summary>A remembered first-page mark: 0-based page of the file and the chosen file name (or null).</summary>
public sealed record RememberedSplitMark(int Page, string? FileName = null);

/// <summary>
/// Remembers the first-page marks (split by marks) of documents between sessions, outside the PDF: reopening the same,
/// unchanged file brings its marks back.
/// </summary>
public interface ISplitMarkMemory
{
    /// <summary>The marks remembered for <paramref name="path"/>, when the file is unchanged since; otherwise none.</summary>
    Task<IReadOnlyList<RememberedSplitMark>> RecallAsync(string path, FileStamp stamp, int pageCount, CancellationToken cancellationToken = default);

    /// <summary>Remembers the marks of <paramref name="path"/> as it is now; no marks forgets the file.</summary>
    Task RememberAsync(string path, FileStamp stamp, int pageCount, IReadOnlyList<RememberedSplitMark> marks, CancellationToken cancellationToken = default);
}

/// <summary>
/// One small JSON file per document under <see cref="AppPaths.SplitMarksDirectory"/>, named by a hash of the
/// document's full path (the path itself is not stored), holding the file's size and time stamp, page count and marks.
/// A file changed elsewhere does not match and its marks are ignored. The newest <see cref="MaxDocuments"/> documents
/// are kept. Failures are logged (never with paths or names) and otherwise ignored: marks are a convenience.
/// </summary>
public sealed class SplitMarkMemory(AppPaths paths, ILogger<SplitMarkMemory> logger) : ISplitMarkMemory
{
    public const int MaxDocuments = 500;

    private const int Version = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<IReadOnlyList<RememberedSplitMark>> RecallAsync(string path, FileStamp stamp, int pageCount, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = FileOf(path);
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            var stream = File.OpenRead(file);
            await using (stream.ConfigureAwait(false))
            {
                var entry = await JsonSerializer.DeserializeAsync<Entry>(stream, Options, cancellationToken).ConfigureAwait(false);
                if (entry is null || entry.Version != Version || entry.Length != stamp.Length || entry.LastWriteTimeUtc != stamp.LastWriteTimeUtc
                    || entry.PageCount != pageCount || entry.Marks is null)
                {
                    return [];
                }

                return [.. entry.Marks
                    .Where(m => m.Page >= 0 && m.Page < pageCount)
                    .DistinctBy(m => m.Page)
                    .Select(m => new RememberedSplitMark(m.Page, string.IsNullOrWhiteSpace(m.FileName) || m.FileName.Length > 255 ? null : m.FileName))];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Remembered split marks could not be read: {ErrorType}", ex.GetType().Name);
            return [];
        }
    }

    public async Task RememberAsync(string path, FileStamp stamp, int pageCount, IReadOnlyList<RememberedSplitMark> marks, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(marks);
        var file = FileOf(path);
        try
        {
            if (marks.Count == 0)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                return;
            }

            Directory.CreateDirectory(paths.SplitMarksDirectory);
            var entry = new Entry(Version, stamp.Length, stamp.LastWriteTimeUtc, pageCount, [.. marks]);
            await AtomicFile.WriteAllTextAsync(file, JsonSerializer.Serialize(entry, Options), cancellationToken).ConfigureAwait(false);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Split marks could not be remembered: {ErrorType}", ex.GetType().Name);
        }
    }

    /// <summary>The memory file of a document: a hash of its full path (case-insensitive, as Windows paths are).</summary>
    private string FileOf(string path)
    {
        var key = Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        return Path.Combine(paths.SplitMarksDirectory, hash + ".json");
    }

    private void Prune()
    {
        var files = new DirectoryInfo(paths.SplitMarksDirectory).GetFiles("*.json");
        foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxDocuments))
        {
            old.Delete();
        }
    }

    private sealed record Entry(int Version, long Length, DateTime LastWriteTimeUtc, int PageCount, IReadOnlyList<RememberedSplitMark>? Marks);
}
