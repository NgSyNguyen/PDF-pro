using System.Text;

namespace PdfPro.Infrastructure.IO;

/// <summary>Extra steps of an atomic write (documents: verify, keep a temporary backup, record the swap).</summary>
public sealed record AtomicWriteOptions
{
    /// <summary>Receives the previous content during the swap, so an interrupted swap can be undone.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Deletes <see cref="BackupPath"/> once the swap succeeded.</summary>
    public bool DeleteBackupAfterSwap { get; init; }

    /// <summary>Checks the finished temporary file (its path) before the swap; an exception aborts the write.</summary>
    public Func<string, CancellationToken, Task>? VerifyAsync { get; init; }

    /// <summary>
    /// Called with the temporary and backup paths right before the swap: record them so that a crash during the swap
    /// can be repaired with <see cref="AtomicFile.RecoverInterruptedSwap"/>.
    /// </summary>
    public Func<string, string?, CancellationToken, Task>? BeforeSwapAsync { get; init; }
}

/// <summary>What <see cref="AtomicFile.RecoverInterruptedSwap"/> found.</summary>
public enum SwapRecovery
{
    /// <summary>No leftovers: the swap never started or completed cleanly.</summary>
    NothingToDo,

    /// <summary>The swap had completed; leftover temporary or backup files were removed.</summary>
    LeftoversRemoved,

    /// <summary>The file had been moved aside and not replaced: the previous content was put back.</summary>
    BackupRestored,
}

/// <summary>
/// Writes files so that readers see either the old or the new content, never a partial file:
/// write to a temporary file in the same folder, flush to disk, then swap with <see cref="File.Replace(string, string, string?)"/>.
/// </summary>
/// <remarks>
/// I/O failures of the write and the swap are reported as <see cref="FileSaveException"/> (an <see cref="IOException"/>)
/// classified as locked / access denied / disk full; the destination is then unchanged and the temporary file deleted.
/// </remarks>
public static class AtomicFile
{
    private const int ErrorUnableToMoveReplacement2 = unchecked((int)0x80070499);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="path">Destination file. Its folder must exist.</param>
    /// <param name="write">Writes the full content to the temporary stream.</param>
    /// <param name="backupPath">Optional path that receives (and keeps) the previous content when the file is replaced.</param>
    /// <param name="cancellationToken">Cancels before the swap; the destination is then left untouched.</param>
    public static Task WriteAsync(
        string path,
        Func<Stream, CancellationToken, Task> write,
        string? backupPath = null,
        CancellationToken cancellationToken = default)
        => WriteAsync(path, write, new AtomicWriteOptions { BackupPath = backupPath }, cancellationToken);

    /// <param name="path">Destination file. Its folder must exist.</param>
    /// <param name="write">Writes the full content to the temporary stream.</param>
    /// <param name="options">Verification, backup and swap recording.</param>
    /// <param name="cancellationToken">Cancels before the swap; the destination is then left untouched.</param>
    public static async Task WriteAsync(
        string path,
        Func<Stream, CancellationToken, Task> write,
        AtomicWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(options);

        var fullPath = Path.GetFullPath(path);
        var tempPath = TempPathFor(fullPath);
        var backupPath = options.BackupPath is null ? null : Path.GetFullPath(options.BackupPath);

        try
        {
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw FileSaveException.From(ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (options.VerifyAsync is { } verify)
            {
                await verify(tempPath, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (options.BeforeSwapAsync is { } beforeSwap)
            {
                await beforeSwap(tempPath, backupPath, cancellationToken).ConfigureAwait(false);
            }

            Swap(tempPath, fullPath, backupPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        if (options.DeleteBackupAfterSwap && backupPath is not null)
        {
            TryDelete(backupPath);
        }
    }

    /// <summary>Atomically writes <paramref name="contents"/> as UTF-8 without BOM.</summary>
    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var bytes = Utf8NoBom.GetBytes(contents);
        return WriteAsync(path, (stream, ct) => stream.WriteAsync(bytes, ct).AsTask(), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Repairs the leftovers of a swap interrupted by a crash or power loss (paths recorded by
    /// <see cref="AtomicWriteOptions.BeforeSwapAsync"/>): when the destination was moved aside and not replaced, the
    /// previous content is put back; otherwise the temporary and backup files are removed.
    /// </summary>
    public static SwapRecovery RecoverInterruptedSwap(string path, string tempPath, string? backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        var result = SwapRecovery.NothingToDo;
        if (backupPath is not null && File.Exists(backupPath))
        {
            if (File.Exists(path))
            {
                File.Delete(backupPath);
                result = SwapRecovery.LeftoversRemoved;
            }
            else
            {
                File.Move(backupPath, path);
                result = SwapRecovery.BackupRestored;
            }
        }

        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
            if (result == SwapRecovery.NothingToDo)
            {
                result = SwapRecovery.LeftoversRemoved;
            }
        }

        return result;
    }

    /// <summary>The temporary file used for <paramref name="path"/>: hidden-style name in the same folder.</summary>
    internal static string TempPathFor(string path)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The path must point to a file inside a folder.", nameof(path));
        return Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    private static void Swap(string tempPath, string destinationPath, string? backupPath)
    {
        try
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
        catch (IOException ex) when (ex.HResult == ErrorUnableToMoveReplacement2 && backupPath is not null)
        {
            // The destination was renamed to the backup name but the new file could not take its place: put it back.
            if (!File.Exists(destinationPath) && File.Exists(backupPath))
            {
                File.Move(backupPath, destinationPath);
            }

            throw FileSaveException.From(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FileSaveException.From(ex);
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
