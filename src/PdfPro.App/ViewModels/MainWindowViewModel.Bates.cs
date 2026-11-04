using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Operations;
using PdfPro.Core.Stamping;
using PdfPro.Operations.Stamping;

namespace PdfPro.App.ViewModels;

/// <summary>"Số Bates": numbering the pages of several files, written in the background like a save.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The Bates dialog (with the active document first, as displayed), then the numbered copies written in the
    /// background with progress and cancel (all or nothing); their folder opens when asked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNumberBates))]
    private async Task BatesAsync()
    {
        if (_bates is not { } bates)
        {
            return;
        }

        string? workingFolder = null;
        try
        {
            var initial = new List<BatesItemViewModel>();
            if (ActiveDocument is { IsReadOnly: false } document)
            {
                (var file, workingFolder) = await DocumentForBatesAsync(bates, document);
                if (file is not null)
                {
                    initial.Add(new BatesItemViewModel(file));
                }
            }

            var dialog = new BatesViewModel(bates, _fileDialogs, initial);
            if (_pageDialogs.ShowBates(dialog) && dialog.Result is { } result)
            {
                await RunBatesAsync(bates, result);
            }
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }
    }

    private bool CanNumberBates() => _bates is not null && _activeSave is null;

    /// <summary>
    /// The open document as a file to number: its own file when nothing is unsaved, otherwise a copy of what it shows,
    /// written into its session folder under its own name (numbered copies keep their input's name). Null when it
    /// cannot be read; the working folder (to remove afterwards) is returned either way.
    /// </summary>
    private async Task<(BatesFile? File, string? WorkingFolder)> DocumentForBatesAsync(IBatesService bates, DocumentTabViewModel document)
    {
        if (!document.IsDirty && File.Exists(document.FilePath) && !document.Workspace.HasChangedOnDisk())
        {
            return (await bates.AddAsync(document.FilePath, document.FileName), null);
        }

        var folder = Path.Combine(document.Workspace.WorkDirectory, $"bates-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, document.FileName);
            var task = StatusBar.StartTask(Strings.Merge_Preparing);
            try
            {
                await _saver.ExportAsync(document.Workspace, [.. Enumerable.Range(0, document.Viewer.PageCount)], path, new Progress<OperationProgress>(), task.Token);
            }
            finally
            {
                task.Complete(string.Empty);
            }

            return (await bates.AddAsync(path, Format(Strings.Merge_CurrentDocument, document.FileName)), folder);
        }
        catch (Exception ex) when (ex is DocumentSaveException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The open document could not be prepared for Bates numbering: {ErrorType}", ex.GetType().Name);
            return (null, folder);
        }
    }

    private async Task RunBatesAsync(IBatesService bates, BatesDialogResult result)
    {
        await WaitForBackgroundWorkAsync(cancelPrint: false);
        var text = Format(Strings.Bates_Progress, result.Inputs.Count);
        var task = StatusBar.StartTask(text);
        var progress = new Progress<OperationProgress>(p => task.Report(p.Fraction, text));
        var write = bates.WriteAsync(result.Inputs, result.Parameters, result.OutputFolder, progress, task.Token);
        _activeSave = write;
        NotifySaveCommands();
        string error;
        try
        {
            var done = await write;
            var format = new BatesFormat(result.Parameters.Prefix, result.Parameters.Digits, result.Parameters.Suffix);
            task.Complete(Format(Strings.Bates_Done, done.OutputPaths.Count, format.Label(done.Ranges[0].First), format.Label(done.Ranges[^1].Last)));
            if (result.OpenFolderWhenDone)
            {
                _shell.OpenFolder(result.OutputFolder);
            }

            return;
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Bates_Cancelled);
            return;
        }
        catch (BatesFileException ex)
        {
            _logger.LogWarning("Bates numbering failed on file {FileIndex}: {ErrorType}", ex.FileIndex, ex.InnerException?.GetType().Name);
            task.Complete(Strings.Bates_FailedStatus);
            error = Format(Strings.Bates_Error_File, Path.GetFileName(result.Inputs[ex.FileIndex]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationValidationException)
        {
            _logger.LogWarning("Bates numbering failed: {ErrorType} (HResult {HResult:X8})", ex.GetType().Name, ex.HResult);
            task.Complete(Strings.Bates_FailedStatus);
            error = ex switch
            {
                OperationValidationException => Strings.Edit_Error_Invalid,
                InvalidOperationException => Strings.Bates_Error_OutputIsInput,
                _ => SplitFailureMessage(ex, result.OutputFolder),
            };
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
        }

        await _messages.ShowErrorAsync(Strings.Bates_Error_Title, error);
    }

    private void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("A working copy could not be removed: {ErrorType}", ex.GetType().Name);
        }
    }
}
