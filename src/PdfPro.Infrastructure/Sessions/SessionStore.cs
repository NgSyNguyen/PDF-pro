using System.Globalization;
using Microsoft.Extensions.Logging;
using PdfPro.Core.Organizing;
using PdfPro.Infrastructure.IO;

namespace PdfPro.Infrastructure.Sessions;

/// <summary>
/// One session folder, held open by its lock file: as long as this object lives (and this process runs), no other
/// instance treats the session as abandoned. Dispose releases the lock without deleting anything.
/// </summary>
public sealed class SessionFolder : IDisposable
{
    private readonly FileStream _lock;

    internal SessionFolder(Guid id, string directory, FileStream lockFile)
    {
        Id = id;
        Directory = directory;
        _lock = lockFile;
    }

    public Guid Id { get; }

    public string Directory { get; }

    public string ManifestPath => Path.Combine(Directory, SessionStore.ManifestFileName);

    /// <summary>Scratch space for intermediate files (repaired sources, materialized revisions in progress).</summary>
    public string WorkDirectory => Path.Combine(Directory, SessionStore.WorkFolderName);

    /// <summary>Full path of a file of the session, from its relative name (e.g. a <c>SessionSource.FileName</c>).</summary>
    public string PathOf(string relativeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeName);
        var full = Path.GetFullPath(Path.Combine(Directory, relativeName));
        return full.StartsWith(Path.GetFullPath(Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new ArgumentException("The name must stay inside the session folder.", nameof(relativeName));
    }

    public void Dispose() => _lock.Dispose();
}

/// <summary>A session folder left behind by a process that ended without closing it (crash, power loss, kill).</summary>
/// <param name="Folder">The folder, now locked by this process: restore it, delete it or release it for later.</param>
/// <param name="Manifest">Its manifest, or null when there is none or it cannot be read.</param>
/// <param name="LastWriteUtc">When the session last changed.</param>
public sealed record OrphanSession(SessionFolder Folder, SessionManifest? Manifest, DateTime LastWriteUtc);

/// <summary>
/// The session folders under %LOCALAPPDATA%\PdfPro\sessions (SPEC "DocumentSession và Revision"): one per open
/// document, holding copies of its source files, revisions and manifest.json. Logs name sessions by id only.
/// </summary>
public sealed class SessionStore(AppPaths paths, ILogger<SessionStore> logger)
{
    public const string ManifestFileName = "manifest.json";
    public const string LockFileName = "session.lock";
    public const string SourcesFolderName = "sources";
    public const string RevisionsFolderName = "revisions";
    public const string WorkFolderName = "work";

    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    public string RootDirectory => paths.SessionsDirectory;

    /// <summary>Relative name of a source file inside a session folder.</summary>
    public static string SourceFileName(string sourceId)
        => SourceIds.IsValid(sourceId) && sourceId != SourceIds.Blank
            ? $"{SourcesFolderName}/{sourceId}.pdf"
            : throw new ArgumentException("Invalid source id.", nameof(sourceId));

    /// <summary>Relative name of a revision file: revisions/r0001.pdf, r0002.pdf…</summary>
    public static string RevisionFileName(int revision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{RevisionsFolderName}/r{revision:D4}.pdf");
    }

    /// <summary>Creates a new session folder holding a copy of <paramref name="originalPath"/> as source "s0".</summary>
    /// <exception cref="IOException">The file cannot be read or the copy cannot be written.</exception>
    public async Task<SessionFolder> CreateAsync(string originalPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        var id = Guid.NewGuid();
        var directory = Path.Combine(RootDirectory, id.ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var folder = new SessionFolder(id, directory, OpenLock(directory));
        try
        {
            await CopyFileAsync(originalPath, folder.PathOf(SourceFileName(SourceIds.Original)), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Session {SessionId} created", id);
            return folder;
        }
        catch
        {
            Delete(folder);
            throw;
        }
    }

    /// <summary>Copies a file whose pages are inserted into the session, as source <paramref name="sourceId"/>.</summary>
    public Task CopySourceAsync(SessionFolder folder, string sourceId, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return CopyFileAsync(path, folder.PathOf(SourceFileName(sourceId)), cancellationToken);
    }

    /// <summary>Atomically writes the manifest.</summary>
    public Task WriteManifestAsync(SessionFolder folder, SessionManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(manifest);
        return AtomicFile.WriteAllTextAsync(folder.ManifestPath, SessionManifestSerializer.Serialize(manifest), cancellationToken);
    }

    /// <summary>
    /// Session folders whose owner is gone: their lock could be taken. The caller owns each returned folder and must
    /// restore, delete or release it.
    /// </summary>
    public IReadOnlyList<OrphanSession> FindOrphans()
    {
        var orphans = new List<OrphanSession>();
        DirectoryInfo[] directories;
        try
        {
            directories = System.IO.Directory.Exists(RootDirectory) ? new DirectoryInfo(RootDirectory).GetDirectories() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The sessions folder could not be listed");
            return orphans;
        }

        foreach (var directory in directories)
        {
            if (!Guid.TryParseExact(directory.Name, "N", out var id))
            {
                continue;
            }

            FileStream lockFile;
            try
            {
                lockFile = OpenLock(directory.FullName);
            }
            catch (IOException ex) when (ex.HResult is ErrorSharingViolation or ErrorLockViolation)
            {
                continue; // a running instance owns it
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Session {SessionId} could not be locked", id);
                continue;
            }

            var folder = new SessionFolder(id, directory.FullName, lockFile);
            orphans.Add(new OrphanSession(folder, TryReadManifest(folder), LastWriteUtc(folder)));
        }

        return orphans;
    }

    /// <summary>Releases the lock and removes the folder with everything in it (best effort).</summary>
    public void Delete(SessionFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        folder.Dispose();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.IO.Directory.Delete(folder.Directory, recursive: true);
                logger.LogInformation("Session {SessionId} removed", folder.Id);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    logger.LogWarning(ex, "Session {SessionId} could not be removed; it will be cleaned up at a later start", folder.Id);
                    return;
                }

                Thread.Sleep(100 * attempt); // a file handle may still be closing
            }
        }
    }

    private SessionManifest? TryReadManifest(SessionFolder folder)
    {
        try
        {
            return File.Exists(folder.ManifestPath)
                ? SessionManifestSerializer.Deserialize(File.ReadAllText(folder.ManifestPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionManifestException)
        {
            logger.LogWarning(ex, "The manifest of session {SessionId} cannot be read", folder.Id);
            return null;
        }
    }

    private static DateTime LastWriteUtc(SessionFolder folder)
        => File.Exists(folder.ManifestPath) ? File.GetLastWriteTimeUtc(folder.ManifestPath) : System.IO.Directory.GetLastWriteTimeUtc(folder.Directory);

    private static FileStream OpenLock(string directory)
        => new(Path.Combine(directory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>Copies through a ".part" file, so an interrupted copy never looks complete.</summary>
    private static async Task CopyFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partPath = targetPath + ".part";
        try
        {
            var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (source.ConfigureAwait(false))
            {
                var target = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using (target.ConfigureAwait(false))
                {
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(partPath, targetPath);
        }
        catch
        {
            try
            {
                File.Delete(partPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The whole session folder is deleted by the caller.
            }

            throw;
        }
    }
}
