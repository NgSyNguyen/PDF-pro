using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Automation;
using PdfPro.Operations.Automation;

namespace PdfPro.App.ViewModels.Automation;

/// <summary>What a batch can run: a workflow of the library, or one operation with its usual settings.</summary>
public sealed record BatchJob(string Name, Workflow Workflow, bool IsOperation);

/// <summary>A file of the batch, as the table shows it.</summary>
public sealed partial class BatchRowViewModel(BatchFile file) : ObservableObject
{
    public BatchFile File { get; } = file;

    public string Name => Path.GetFileName(File.Path);

    public string Folder => File.RelativeFolder.Length == 0 ? Path.GetDirectoryName(File.Path) ?? string.Empty : File.RelativeFolder;

    [ObservableProperty]
    public partial BatchItemStatus Status { get; set; } = BatchItemStatus.Waiting;

    [ObservableProperty]
    public partial string StatusText { get; set; } = Strings.Batch_Status_Waiting;

    /// <summary>The step running, what was written, or why it failed.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    public BatchItemResult? Result { get; set; }
}

/// <summary>
/// "Chạy lô" (SPEC Phase 12): files and folders (with their subfolders, by name pattern), a workflow or one operation,
/// a few files at a time; a table of every file (waiting, running and its step, done, failed and why); pause between
/// files, cancel, a CSV log, the failed files run again. One failing file never stops the others.
/// </summary>
public sealed partial class BatchViewModel : ObservableObject, IDisposable
{
    private readonly AutomationServices _automation;
    private readonly IFileDialogService _files;
    private readonly IShellLauncher _shell;
    private readonly Func<Workflow, PreparedSecrets?> _prepareSecrets;
    private readonly string _workingFolder;
    private readonly ILogger _logger;
    private readonly BatchPause _pause = new();
    private CancellationTokenSource? _cancel;
    private BatchResult? _last;

    public BatchViewModel(AutomationServices automation, IFileDialogService files, IShellLauncher shell, Func<Workflow, PreparedSecrets?> prepareSecrets, string workingFolder, Workflow? workflow, ILogger logger)
    {
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _prepareSecrets = prepareSecrets ?? throw new ArgumentNullException(nameof(prepareSecrets));
        _workingFolder = workingFolder;
        _logger = logger;
        var jobs = new List<BatchJob>();
        if (workflow is not null)
        {
            jobs.Add(new BatchJob(workflow.Name, workflow, IsOperation: false));
        }

        jobs.AddRange(automation.Library.List().Select(e => new BatchJob(e.Workflow.Name, e.Workflow, IsOperation: false)));
        jobs.AddRange(OperationJobs(automation));
        Jobs = jobs;
        Job = jobs[0];
        Parallel = Math.Max(1, Environment.ProcessorCount / 2);
    }

    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<BatchJob> Jobs { get; }

    public ObservableCollection<BatchRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial BatchJob Job { get; set; }

    [ObservableProperty]
    public partial bool Recursive { get; set; } = true;

    [ObservableProperty]
    public partial string Pattern { get; set; } = BatchInputs.DefaultPattern;

    /// <summary>Where the results go (the folders of the files under it); empty = where the workflow says.</summary>
    [ObservableProperty]
    public partial string OutputFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Parallel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(CancelCommand), nameof(RerunFailedCommand), nameof(ExportLogCommand), nameof(AddFilesCommand), nameof(AddFolderCommand), nameof(ClearCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>Nothing runs: the files and the job can change.</summary>
    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double Fraction { get; private set; }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void AddFiles()
    {
        var picked = _files.PickPdfFiles();
        Add(picked.Select(p => new BatchFile(p)));
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void AddFolder()
    {
        if (_files.PickFolder(Strings.Batch_AddFolder_Title, null) is not { } folder)
        {
            return;
        }

        try
        {
            Add(BatchInputs.Expand([folder], Recursive, Pattern));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Summary = Strings.Batch_Error_Folder;
        }
    }

    [RelayCommand]
    private void PickOutputFolder()
    {
        if (_files.PickFolder(Strings.Batch_Output_Title, OutputFolder.Length > 0 ? OutputFolder : null) is { } folder)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Clear()
    {
        Rows.Clear();
        _last = null;
        Summary = string.Empty;
        StartCommand.NotifyCanExecuteChanged();
        RerunFailedCommand.NotifyCanExecuteChanged();
        ExportLogCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync([.. Rows.Select(r => r.File)]);

    private bool CanStart() => !IsRunning && Rows.Count > 0;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Pause()
    {
        if (_pause.IsPaused)
        {
            _pause.Resume();
            IsPaused = false;
        }
        else
        {
            _pause.Pause();
            IsPaused = true;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        _cancel?.Cancel();
        _pause.Resume();
        IsPaused = false;
    }

    [RelayCommand(CanExecute = nameof(CanRerun))]
    private Task RerunFailedAsync()
    {
        var failed = _last!.FailedFiles;
        var keep = Rows.Where(r => failed.Contains(r.File)).ToList();
        Rows.Clear();
        foreach (var row in keep)
        {
            row.Status = BatchItemStatus.Waiting;
            row.StatusText = Strings.Batch_Status_Waiting;
            row.Detail = string.Empty;
            Rows.Add(row);
        }

        return RunAsync(failed);
    }

    private bool CanRerun() => !IsRunning && _last is { Failed: > 0 };

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void ExportLog()
    {
        if (_last is not { } result || _files.PickTableSavePath(Strings.Batch_Log_FileName, OutputFolder.Length > 0 ? OutputFolder : null, csv: true) is not { } path)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(path, BatchLog.Write(result.Items, LogLabels()));
            Summary = string.Format(CultureInfo.CurrentCulture, Strings.Batch_Log_Written, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = Strings.Batch_Error_Log;
        }
    }

    private bool HasResult() => !IsRunning && _last is not null;

    [RelayCommand]
    private void Close()
    {
        Cancel();
        CloseRequested?.Invoke(this, false);
    }

    /// <summary>The labels of the CSV log, in the user's language.</summary>
    public static BatchLog.Labels LogLabels() => new(
        Strings.Batch_Log_File,
        Strings.Batch_Log_Status,
        Strings.Batch_Log_Outputs,
        Strings.Batch_Log_Failure,
        Strings.Batch_Log_Step,
        Strings.Batch_Log_Operation,
        Strings.Batch_Log_Error,
        Strings.Batch_Log_Seconds,
        new Dictionary<BatchItemStatus, string>
        {
            [BatchItemStatus.Waiting] = Strings.Batch_Status_Cancelled,
            [BatchItemStatus.Running] = Strings.Batch_Status_Cancelled,
            [BatchItemStatus.Done] = Strings.Batch_Status_Done,
            [BatchItemStatus.Failed] = Strings.Batch_Status_Failed,
            [BatchItemStatus.Cancelled] = Strings.Batch_Status_Cancelled,
        });

    public void Dispose()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
    }

    /// <summary>One operation with its usual settings, for those whose settings need nothing typed.</summary>
    private static IEnumerable<BatchJob> OperationJobs(AutomationServices automation)
    {
        foreach (var operation in automation.Catalog.All.Where(o => !WorkflowValidator.SessionOnly.Contains(o.Id)))
        {
            var workflow = new Workflow(OperationNames.Of(operation.Id), [new WorkflowStep(operation.Id, [])]);
            if (!WorkflowValidator.Check(workflow, automation.Catalog).Any() && !WorkflowValidator.AsksWhenRun(operation))
            {
                yield return new BatchJob(string.Format(CultureInfo.CurrentCulture, Strings.Batch_Job_Operation, workflow.Name), workflow, IsOperation: true);
            }
        }
    }

    private void Add(IEnumerable<BatchFile> files)
    {
        var known = Rows.Select(r => r.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (known.Add(file.Path))
            {
                Rows.Add(new BatchRowViewModel(file));
            }
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync(IReadOnlyList<BatchFile> files)
    {
        var workflow = Job.Workflow;
        if (workflow.Destination.Mode == WorkflowOutputMode.Folder && workflow.Destination.Folder is null && OutputFolder.Length == 0)
        {
            Summary = Strings.Batch_Error_NoOutput;
            return;
        }

        if (_prepareSecrets(workflow) is not { } secrets)
        {
            return;
        }

        using (secrets)
        {
            _cancel = new CancellationTokenSource();
            IsRunning = true;
            Fraction = 0;
            Summary = string.Format(CultureInfo.CurrentCulture, Strings.Batch_Running, files.Count);
            var rows = files.Select(f => Rows.First(r => r.File == f)).ToList();
            var live = true;
            var progress = new Progress<BatchProgress>(p =>
            {
                // Reports still queued when the run has ended would put back an older state.
                if (live)
                {
                    Show(rows[p.Index], p.Item, p.Step);
                    Fraction = (double)rows.Count(r => r.Status is BatchItemStatus.Done or BatchItemStatus.Failed) / rows.Count;
                }
            });
            var options = new BatchOptions
            {
                MaxParallel = Math.Clamp(Parallel, 1, 64),
                OutputFolder = OutputFolder.Length > 0 ? OutputFolder : null,
                WorkingFolder = _workingFolder,
                Secrets = secrets,
                Pause = _pause,
            };
            try
            {
                _last = await Task.Run(() => _automation.Batches.RunAsync(workflow, files, options, progress, _cancel.Token));
                live = false;
                for (var i = 0; i < rows.Count; i++)
                {
                    Show(rows[i], _last.Items[i], null);
                }

                Fraction = rows.Count == 0 ? 0 : (double)_last.Items.Count(r => r.Status is BatchItemStatus.Done or BatchItemStatus.Failed) / rows.Count;
                Summary = string.Format(CultureInfo.CurrentCulture, Strings.Batch_Done, _last.Succeeded, _last.Failed, _last.Cancelled);
                _logger.LogInformation("A batch ran: {Succeeded} done, {Failed} failed, {Cancelled} cancelled", _last.Succeeded, _last.Failed, _last.Cancelled);
            }
            catch (WorkflowFailedException ex)
            {
                live = false;
                Summary = MainWindowViewModel.Explain(ex);
            }
            finally
            {
                IsRunning = false;
                IsPaused = false;
                _cancel.Dispose();
                _cancel = null;
                RerunFailedCommand.NotifyCanExecuteChanged();
                ExportLogCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private static void Show(BatchRowViewModel row, BatchItemResult item, WorkflowProgress? step)
    {
        row.Status = item.Status;
        row.Result = item;
        switch (item.Status)
        {
            case BatchItemStatus.Running:
                row.StatusText = Strings.Batch_Status_Running;
                if (step is { } running)
                {
                    row.Detail = string.Format(CultureInfo.CurrentCulture, Strings.Batch_Step, running.Position + 1, running.Count, OperationNames.Of(running.Operation));
                }

                break;
            case BatchItemStatus.Done:
                row.StatusText = Strings.Batch_Status_Done;
                row.Detail = string.Join("; ", item.Outputs.Select(Path.GetFileName));
                break;
            case BatchItemStatus.Failed:
                row.StatusText = Strings.Batch_Status_Failed;
                row.Detail = item.Error is WorkflowFailedException failed ? MainWindowViewModel.Explain(failed) : string.Format(CultureInfo.CurrentCulture, Strings.Workflow_Failure_Step, item.ErrorType);
                break;
            case BatchItemStatus.Cancelled:
                row.StatusText = Strings.Batch_Status_Cancelled;
                row.Detail = string.Empty;
                break;
            default:
                row.StatusText = Strings.Batch_Status_Waiting;
                break;
        }
    }
}
