using System.Collections.Concurrent;

namespace PdfPro.Operations.Automation;

/// <summary>How a hot folder works.</summary>
/// <param name="Folder">The folder watched (its own files; subfolders are not).</param>
/// <param name="Workflow">What runs on each file.</param>
/// <param name="OutputFolder">Where the results go, or null for "_output" inside the watched folder (not watched).</param>
/// <param name="Pattern">The names that are picked up ("*.pdf"; several separated by ';').</param>
/// <param name="StableFor">A file is taken once its size has not changed for this long and it can be opened alone.</param>
/// <param name="PollInterval">The folder is also looked through this often (file system events can be lost).</param>
/// <param name="WorkingFolder">Where intermediate files go, or null for the system's temporary folder.</param>
public sealed record HotFolderOptions(
    string Folder,
    Workflow Workflow,
    string? OutputFolder = null,
    string Pattern = BatchInputs.DefaultPattern,
    TimeSpan? StableFor = null,
    TimeSpan? PollInterval = null,
    string? WorkingFolder = null)
{
    public TimeSpan Stable => StableFor ?? TimeSpan.FromSeconds(2);

    public TimeSpan Poll => PollInterval ?? TimeSpan.FromSeconds(10);
}

/// <summary>A file a hot folder has dealt with.</summary>
/// <param name="File">Where the file is now (in "_done" or "_error").</param>
/// <param name="Succeeded">The workflow ran.</param>
/// <param name="Outputs">What it wrote.</param>
/// <param name="Error">Why it failed (in memory only), or null.</param>
public sealed record HotFolderEvent(string File, bool Succeeded, IReadOnlyList<string> Outputs, Exception? Error);

/// <summary>
/// "Hot folder" (SPEC Phase 12): every file dropped into a folder goes through a workflow, once it is completely
/// written — its size steady for a moment and the file openable with nobody else holding it — so a file still being
/// copied is never taken. File system events and a regular look through the folder (events can be lost) both find
/// files; they run one at a time. The original then moves to "_done" (or "_error" when the workflow failed), so it is
/// never taken twice; results go to the output folder ("_output" by default).
/// </summary>
public sealed class HotFolder : IAsyncDisposable
{
    private readonly IWorkflowFileRunner _runner;
    private readonly HotFolderOptions _options;
    private readonly ConcurrentDictionary<string, (long Size, DateTime Since)> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private FileSystemWatcher? _watcher;
    private Task? _polling;

    public HotFolder(IWorkflowFileRunner runner, HotFolderOptions options)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Folder = Path.GetFullPath(options.Folder);
        OutputFolder = options.OutputFolder is { Length: > 0 } output ? Path.GetFullPath(output) : Path.Combine(Folder, HotFolderNames.Output);
    }

    /// <summary>A file was dealt with (raised on a background thread).</summary>
    public event EventHandler<HotFolderEvent>? Processed;

    public string Folder { get; }

    public string OutputFolder { get; }

    public bool IsRunning => _polling is { IsCompleted: false };

    /// <summary>Starts watching.</summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="WorkflowFailedException">The workflow cannot run.</exception>
    public void Start()
    {
        if (!Directory.Exists(Folder))
        {
            throw new DirectoryNotFoundException("The hot folder does not exist.");
        }

        var errors = WorkflowValidator.Check(_options.Workflow, _runner.Catalog).Where(i => i.Severity == WorkflowIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new WorkflowFailedException(WorkflowFailure.Invalid, issues: errors);
        }

        if (IsRunning)
        {
            return;
        }

        _watcher = new FileSystemWatcher(Folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.EnableRaisingEvents = true;
        _polling = Task.Run(PollAsync);
    }

    /// <summary>Stops watching; a file being processed finishes first.</summary>
    public async Task StopAsync()
    {
        _watcher?.Dispose();
        _watcher = null;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_polling is { } polling)
        {
            try
            {
                await polling.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }

        _polling = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _busy.Dispose();
    }

    /// <summary>
    /// Looks through the folder once: files that are ready go through the workflow (one at a time), the others are
    /// remembered and looked at again later. The number of files dealt with.
    /// </summary>
    public async Task<int> ProcessReadyAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        foreach (var file in Candidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsReady(file))
            {
                continue;
            }

            await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(file) && IsReady(file))
                {
                    await ProcessAsync(file, cancellationToken).ConfigureAwait(false);
                    processed++;
                }
            }
            finally
            {
                _busy.Release();
            }
        }

        return processed;
    }

    /// <summary>
    /// Ready when it has had the same size for <see cref="HotFolderOptions.Stable"/> and can be opened with nobody else
    /// holding it (a copy in progress keeps it open, or keeps growing).
    /// </summary>
    internal bool IsReady(string file)
    {
        long size;
        try
        {
            size = new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var seen = _seen.AddOrUpdate(file, (size, now), (_, old) => old.Size == size ? old : (size, now));
        if (now - seen.Since < _options.Stable)
        {
            return false;
        }

        try
        {
            using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IEnumerable<string> Candidates()
    {
        var patterns = _options.Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            return [.. patterns
                .SelectMany(p => Directory.EnumerateFiles(Folder, p, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }))
                .Where(f => Path.GetFileName(f) is var name && !name.StartsWith('.') && !name.StartsWith("~$", StringComparison.Ordinal) && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task ProcessAsync(string file, CancellationToken cancellationToken)
    {
        _seen.TryRemove(file, out _);
        IReadOnlyList<string> outputs = [];
        Exception? error = null;
        try
        {
            var result = await _runner.RunAsync(_options.Workflow, new WorkflowRunRequest(file) { OutputFolder = OutputFolder, WorkingFolder = _options.WorkingFolder }, null, cancellationToken).ConfigureAwait(false);
            outputs = result.Outputs;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Whatever happens to one file, the folder goes on.
            error = ex;
        }

        var moved = Move(file, error is null ? HotFolderNames.Done : HotFolderNames.Error);
        Processed?.Invoke(this, new HotFolderEvent(moved, error is null, outputs, error));
    }

    /// <summary>Moves a file into a subfolder of the hot folder ("name (2).pdf" when taken); where it is now.</summary>
    private string Move(string file, string subfolder)
    {
        var folder = Path.Combine(Folder, subfolder);
        try
        {
            Directory.CreateDirectory(folder);
            var name = Core.Splitting.FileNames.MakeUnique([Path.GetFileNameWithoutExtension(file)], n => File.Exists(Path.Combine(folder, n + Path.GetExtension(file))))[0];
            var target = Path.Combine(folder, name + Path.GetExtension(file));
            File.Move(file, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left where it is; it will be tried again (and fail the same way) — better than losing it.
            return file;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => _seen.TryAdd(e.FullPath, (-1, DateTime.UtcNow));

    private async Task PollAsync()
    {
        var token = _stop.Token;
        var interval = TimeSpan.FromMilliseconds(Math.Min(_options.Poll.TotalMilliseconds, Math.Max(250, _options.Stable.TotalMilliseconds / 2)));
        var lastFullLook = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            // Files the watcher reported are looked at often; the whole folder at the poll interval.
            if (!_seen.IsEmpty || DateTime.UtcNow - lastFullLook >= _options.Poll)
            {
                lastFullLook = DateTime.UtcNow;
                try
                {
                    await ProcessReadyAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The folder went away for a moment, a disk error…: look again next time.
                }
            }

            await Task.Delay(interval, token).ConfigureAwait(false);
        }
    }
}
