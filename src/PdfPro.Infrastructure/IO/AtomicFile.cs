using System.Text;

namespace PdfPro.Infrastructure.IO;

/// <summary>
/// Writes files so that readers see either the old or the new content, never a partial file:
/// write to a temporary file in the same folder, flush to disk, then swap with <see cref="File.Replace(string, string, string?)"/>.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="path">Destination file. Its folder must exist.</param>
    /// <param name="write">Writes the full content to the temporary stream.</param>
    /// <param name="backupPath">Optional path that receives the previous content when the file is replaced.</param>
    /// <param name="cancellationToken">Cancels before the swap; the destination is then left untouched.</param>
    public static async Task WriteAsync(
        string path,
        Func<Stream, CancellationToken, Task> write,
        string? backupPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The path must point to a file inside a folder.", nameof(path));
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Swap(tempPath, fullPath, backupPath is null ? null : Path.GetFullPath(backupPath));
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Atomically writes <paramref name="contents"/> as UTF-8 without BOM.</summary>
    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var bytes = Utf8NoBom.GetBytes(contents);
        return WriteAsync(path, (stream, ct) => stream.WriteAsync(bytes, ct).AsTask(), cancellationToken: cancellationToken);
    }

    private static void Swap(string tempPath, string destinationPath, string? backupPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(tempPath, destinationPath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, destinationPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover hidden temp file is harmless and gets a unique name next time.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
