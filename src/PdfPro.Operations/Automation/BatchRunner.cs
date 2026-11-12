using System.Diagnostics;
using System.Globalization;
using PdfPro.Core.Tables;
using PdfPro.Core.Text;

namespace PdfPro.Operations.Automation;

/// <summary>A file of a batch and the folder it sits in under the folder that was chosen ("" for a file chosen by itself).</summary>
public sealed record BatchFile(string Path, string RelativeFolder = "");

/// <summary>The files a batch runs on: files as given, folders expanded (recursively when asked) by a name pattern.</summary>
public static class BatchInputs
{
    /// <summary>The pattern of a folder when none is given.</summary>
    public const string DefaultPattern = "*.pdf";

    /// <summary>
    /// Files given stay as they are (one that does not exist fails when it runs); folders give the files matching
    /// <paramref name="pattern"/> ("*.pdf"; several separated by ';'), hidden and temporary ones ("~$x.docx",
    /// ".x.pdf") and the hot folder's "_done" and "_error" left out. Each file once, in natural order: a folder's own
    /// files before those of its subfolders.
    /// </summary>
    public static IReadOnlyList<BatchFile> Expand(IEnumerable<string> paths, bool recursive, string? pattern = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var patterns = (string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = new List<BatchFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var given in paths)
        {
            var full = Path.GetFullPath(given);
            if (!Directory.Exists(full))
            {
                if (seen.Add(full))
                {
                    files.Add(new BatchFile(full));
                }

                continue;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            var found = patterns
                .SelectMany(p => Directory.EnumerateFiles(full, p, options))
                .Where(f => !Skipped(full, f))
                .Select(f => new BatchFile(f, Path.GetRelativePath(full, Path.GetDirectoryName(f)!) is "." ? string.Empty : Path.GetRelativePath(full, Path.GetDirectoryName(f)!)))
                .OrderBy(f => f.RelativeFolder, NaturalComparer.Instance)
                .ThenBy(f => Path.GetFileName(f.Path), NaturalComparer.Instance);
            foreach (var file in found)
            {
                if (seen.Add(file.Path))
                {
                    files.Add(file);
                }
            }
        }

        return files;
    }

    private static bool Skipped(string root, string file)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
        return relative != "." && relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.StartsWith('.') || string.Equals(part, HotFolderNames.Done, StringComparison.OrdinalIgnoreCase) || string.Equals(part, HotFolderNames.Error, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The folders a hot folder moves its files to.</summary>
public static class HotFolderNames
{
    public const string Done = "_done";

    public const string Error = "_error";
}

public enum BatchItemStatus
{
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>Where a file of a batch is.</summary>
/// <param name="File">The file.</param>
/// <param name="Status">Waiting, running, done, failed or cancelled.</param>
/// <param name="Outputs">What it wrote.</param>
/// <param name="Failure">Why it failed.</param>
/// <param name="Step">The step that failed (0-based among all steps).</param>
/// <param name="Operation">Its operation.</param>
/// <param name="ErrorType">The kind of error (the innermost exception's type), for the log.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Unprotected">It was protected and ran as an unprotected copy.</param>
/// <param name="Error">What failed, in memory (for the caller to word; its message is never logged).</param>
public sealed record BatchItemResult(
    BatchFile File,
    BatchItemStatus Status,
    IReadOnlyList<string> Outputs,
    WorkflowFailure? Failure = null,
    int? Step = null,
    string? Operation = null,
    string? ErrorType = null,
    TimeSpan Duration = default,
    bool Unprotected = false,
    Exception? Error = null)
{
    public static BatchItemResult Waiting(BatchFile file) => new(file, BatchItemStatus.Waiting, []);
}

/// <param name="Index">The file (in the order given).</param>
/// <param name="Item">Where it is.</param>
/// <param name="Step">How far its workflow is, while it runs.</param>
public readonly record struct BatchProgress(int Index, BatchItemResult Item, WorkflowProgress? Step = null);

/// <summary>Pauses a batch: the files running finish, the next ones wait until it resumes.</summary>
public sealed class BatchPause
{
    private readonly Lock _lock = new();
    private TaskCompletionSource? _paused;

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused is not null;
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? paused;
        lock (_lock)
        {
            paused = _paused;
            _paused = null;
        }

        paused?.TrySetResult();
    }

    internal Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _paused?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }
    }
}

/// <summary>How a batch runs.</summary>
public sealed record BatchOptions
{
    /// <summary>Files run at once (0 = half the processors, at least one).</summary>
    public int MaxParallel { get; init; }

    /// <summary>The folder the results go to (the folders of the files under it), instead of the workflow's.</summary>
    public string? OutputFolder { get; init; }

    /// <summary>Where the intermediate files go, or null for the system's temporary folder.</summary>
    public string? WorkingFolder { get; init; }

    /// <summary>The owner password of a protected file, or null.</summary>
    public Func<string, string?>? Passwords { get; init; }

    /// <summary>Passwords and certificates of the steps that need them.</summary>
    public IWorkflowSecrets? Secrets { get; init; }

    /// <summary>Pauses the batch between files.</summary>
    public BatchPause? Pause { get; init; }

    /// <summary>The number of files run at once.</summary>
    public int Workers => MaxParallel > 0 ? MaxParallel : Math.Max(1, Environment.ProcessorCount / 2);
}

/// <param name="Items">Each file, in the order given.</param>
/// <param name="Duration">How long it took.</param>
public sealed record BatchResult(IReadOnlyList<BatchItemResult> Items, TimeSpan Duration)
{
    public int Succeeded => Items.Count(i => i.Status == BatchItemStatus.Done);

    public int Failed => Items.Count(i => i.Status == BatchItemStatus.Failed);

    public int Cancelled => Items.Count(i => i.Status is BatchItemStatus.Cancelled or BatchItemStatus.Waiting);

    /// <summary>The files that failed, to run again.</summary>
    public IReadOnlyList<BatchFile> FailedFiles => [.. Items.Where(i => i.Status == BatchItemStatus.Failed).Select(i => i.File)];
}

/// <summary>
/// Runs a workflow on many files (SPEC Phase 12, "Chạy lô"): a few at a time, each in its own run so a failing file
/// never stops the others; paused between files, cancelled at any time (the files not finished are reported as
/// cancelled). The results keep the folders of the files under the folder that was chosen.
/// </summary>
public sealed class BatchRunner(WorkflowRunner runner)
{
    /// <exception cref="WorkflowFailedException">The workflow itself is invalid (no file is run).</exception>
    public async Task<BatchResult> RunAsync(Workflow workflow, IReadOnlyList<BatchFile> files, BatchOptions options, IProgress<BatchProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        var errors = WorkflowValidator.Check(workflow, runner.Catalog).Where(i => i.Severity == WorkflowIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new WorkflowFailedException(WorkflowFailure.Invalid, issues: errors);
        }

        var clock = Stopwatch.StartNew();
        var items = files.Select(BatchItemResult.Waiting).ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            progress?.Report(new BatchProgress(i, items[i]));
        }

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, items.Length),
                new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    if (options.Pause is { } pause)
                    {
                        await pause.WaitAsync(token).ConfigureAwait(false);
                    }

                    items[index] = await RunOneAsync(workflow, index, files[index], options, progress, token).ConfigureAwait(false);
                    progress?.Report(new BatchProgress(index, items[index]));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].Status is BatchItemStatus.Waiting or BatchItemStatus.Running)
                {
                    items[i] = items[i] with { Status = BatchItemStatus.Cancelled };
                    progress?.Report(new BatchProgress(i, items[i]));
                }
            }
        }

        return new BatchResult(items, clock.Elapsed);
    }

    private async Task<BatchItemResult> RunOneAsync(Workflow workflow, int index, BatchFile file, BatchOptions options, IProgress<BatchProgress>? progress, CancellationToken cancellationToken)
    {
        var running = new BatchItemResult(file, BatchItemStatus.Running, []);
        progress?.Report(new BatchProgress(index, running));
        var clock = Stopwatch.StartNew();
        var request = new WorkflowRunRequest(file.Path)
        {
            OutputFolder = options.OutputFolder is { } folder ? Path.Combine(folder, file.RelativeFolder) : null,
            Password = options.Passwords?.Invoke(file.Path),
            WorkingFolder = options.WorkingFolder,
            Secrets = options.Secrets,
        };
        try
        {
            var stepProgress = progress is null ? null : new StepProgress(step => progress.Report(new BatchProgress(index, running, step)));
            var result = await runner.RunAsync(workflow, request, stepProgress, cancellationToken).ConfigureAwait(false);
            return new BatchItemResult(file, BatchItemStatus.Done, result.Outputs, Duration: clock.Elapsed, Unprotected: result.Unprotected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkflowFailedException ex)
        {
            return new BatchItemResult(file, BatchItemStatus.Failed, [], ex.Failure, ex.Step, ex.Operation, Innermost(ex).GetType().Name, clock.Elapsed, Error: ex);
        }
        catch (Exception ex)
        {
            // One file must never stop the others, whatever goes wrong with it.
            return new BatchItemResult(file, BatchItemStatus.Failed, [], WorkflowFailure.Step, ErrorType: Innermost(ex).GetType().Name, Duration: clock.Elapsed, Error: ex);
        }
    }

    /// <summary>
    /// Whether an error says the file is not a PDF the libraries can read (damaged, truncated, not a PDF): PDFium's and
    /// PdfPig's format errors, PDFsharp's reader errors (some of them plain <see cref="Exception"/>s).
    /// </summary>
    public static bool IsUnreadableFile(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var inner = Innermost(error);
        return inner is Pdfium.PdfDocumentException or InvalidDataException or UglyToad.PdfPig.Core.PdfDocumentFormatException
            || inner.GetType() == typeof(Exception)
            || inner.GetType().Namespace?.StartsWith("PdfSharp.Pdf.IO", StringComparison.Ordinal) == true;
    }

    /// <summary>The exception at the bottom of a chain (the one that says what really went wrong).</summary>
    public static Exception Innermost(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException is { } next)
        {
            inner = next;
        }

        return inner;
    }

    private sealed class StepProgress(Action<WorkflowProgress> report) : IProgress<WorkflowProgress>
    {
        public void Report(WorkflowProgress value) => report(value);
    }
}

/// <summary>The log of a batch as CSV (UTF-8 with BOM, one line per file), for Excel.</summary>
public static class BatchLog
{
    /// <summary>The column titles, from the caller's resources.</summary>
    /// <param name="Statuses">The words of <see cref="BatchItemStatus"/>, by its value.</param>
    public sealed record Labels(string File, string Status, string Outputs, string Failure, string Step, string Operation, string Error, string Seconds, IReadOnlyDictionary<BatchItemStatus, string> Statuses);

    public static byte[] Write(IEnumerable<BatchItemResult> items, Labels labels)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(labels);
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { labels.File, labels.Status, labels.Outputs, labels.Failure, labels.Step, labels.Operation, labels.Error, labels.Seconds },
        };
        rows.AddRange(items.Select(i => (IReadOnlyList<string>)
        [
            i.File.Path,
            labels.Statuses.GetValueOrDefault(i.Status, i.Status.ToString()),
            string.Join("; ", i.Outputs),
            i.Failure?.ToString() ?? string.Empty,
            i.Step is { } step ? (step + 1).ToString(CultureInfo.InvariantCulture) : string.Empty,
            i.Operation ?? string.Empty,
            i.ErrorType ?? string.Empty,
            i.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
        ]));
        return CsvWriter.Write(rows);
    }
}
