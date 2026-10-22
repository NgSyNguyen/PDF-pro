using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;

namespace PdfPro.Operations.Splitting;

/// <summary>What <see cref="SplitWriter"/> writes.</summary>
internal sealed record SplitWriteRequest
{
    public required SplitDocument Document { get; init; }

    public required IReadOnlyList<SplitSegment> Segments { get; init; }

    public required SplitNaming Naming { get; init; }

    public required string OutputDirectory { get; init; }

    public bool Overwrite { get; init; }

    /// <summary>Split by size: a written file over this many bytes is cut in two and written again.</summary>
    public long? MaxBytes { get; init; }

    /// <summary>Files that must never be replaced (the document's own file), compared by full path.</summary>
    public IReadOnlyCollection<string> ProtectedPaths { get; init; } = [];
}

/// <summary>
/// Writes the files of a split all or nothing: each file is materialized (keeping bookmarks and links inside it) into a
/// temporary file in the output folder; only when all are written are they renamed, replacing existing files when
/// asked. Cancelling or failing removes every temporary file and restores replaced ones.
/// </summary>
internal sealed class SplitWriter(IPlanMaterializer materializer)
{
    public const string Stage = "Stage_SplitWrite";

    private const string TempSuffix = ".pdfpro-split.tmp";
    private const string BackupSuffix = ".pdfpro-split.bak";

    /// <returns>The written files, in order.</returns>
    public async Task<IReadOnlyList<string>> WriteAsync(SplitWriteRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        if (request.Segments.Count == 0)
        {
            throw new ArgumentException("There is no file to write.", nameof(request));
        }

        var directory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(directory);
        var segments = request.Segments.ToList();
        var temps = new List<string>();
        try
        {
            for (var i = 0; i < segments.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new OperationProgress(i, segments.Count, Stage));
                var temp = Path.Combine(directory, $".{Guid.NewGuid():N}{TempSuffix}");
                temps.Add(temp);
                await WriteFileAsync(request.Document, segments[i], temp, cancellationToken).ConfigureAwait(false);
                if (request.MaxBytes is { } max && segments[i].PageCount > 1 && new FileInfo(temp).Length > max)
                {
                    // The estimate was too low: cut the file in two and write both halves.
                    File.Delete(temp);
                    temps.RemoveAt(temps.Count - 1);
                    var pages = segments[i].Pages;
                    var half = pages.Count / 2;
                    segments[i] = new SplitSegment([.. pages.Take(half)], segments[i].Title, segments[i].FileName);
                    segments.Insert(i + 1, new SplitSegment([.. pages.Skip(half)], segments[i].Title, segments[i].FileName));
                    i--;
                }
            }

            progress.Report(new OperationProgress(segments.Count, segments.Count, Stage));
            var names = request.Naming.Names(segments, name => IsTaken(Path.Combine(directory, name + ".pdf"), request));
            return Publish(temps, names.Select(n => Path.Combine(directory, n + ".pdf")).ToArray());
        }
        finally
        {
            foreach (var temp in temps)
            {
                TryDelete(temp);
            }
        }
    }

    private async Task WriteFileAsync(SplitDocument document, SplitSegment segment, string path, CancellationToken cancellationToken)
    {
        var plan = PagePlan.Create(segment.Pages.Select(i => document.Plan[i]));
        var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
        await using (output.ConfigureAwait(false))
        {
            var materialize = new MaterializeRequest
            {
                Plan = plan,
                BaseSourceId = document.BaseSourceId,
                SourcePaths = document.SourcePaths,
                WorkingDirectory = document.WorkingDirectory,
            };
            await materializer.MaterializeAsync(materialize, output, NoProgress.Instance, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }

    /// <summary>Renames the temporary files to their names; on failure undoes the renames already done.</summary>
    private static string[] Publish(IReadOnlyList<string> temps, string[] targets)
    {
        var done = new List<(string Target, string? Backup)>(targets.Length);
        try
        {
            for (var i = 0; i < targets.Length; i++)
            {
                if (File.Exists(targets[i]))
                {
                    // Only reached when overwriting was asked for (other names were made unique).
                    var backup = Path.Combine(Path.GetDirectoryName(targets[i])!, $".{Guid.NewGuid():N}{BackupSuffix}");
                    File.Replace(temps[i], targets[i], backup, ignoreMetadataErrors: true);
                    done.Add((targets[i], backup));
                }
                else
                {
                    File.Move(temps[i], targets[i]);
                    done.Add((targets[i], null));
                }
            }
        }
        catch
        {
            for (var i = done.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (done[i].Backup is { } backup)
                    {
                        File.Move(backup, done[i].Target, overwrite: true);
                    }
                    else
                    {
                        File.Delete(done[i].Target);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the original error is what the user needs to see.
                }
            }

            throw;
        }

        foreach (var (_, backup) in done)
        {
            if (backup is not null)
            {
                TryDelete(backup);
            }
        }

        return targets;
    }

    private static bool IsTaken(string path, SplitWriteRequest request)
        => request.ProtectedPaths.Any(p => string.Equals(Path.GetFullPath(p), path, StringComparison.OrdinalIgnoreCase))
            || (!request.Overwrite && File.Exists(path));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless; it is named so it can be recognised.
        }
    }
}

/// <summary>Discards progress reports (a stage whose own progress is not shown).</summary>
internal sealed class NoProgress : IProgress<OperationProgress>
{
    public static readonly NoProgress Instance = new();

    public void Report(OperationProgress value)
    {
    }
}
