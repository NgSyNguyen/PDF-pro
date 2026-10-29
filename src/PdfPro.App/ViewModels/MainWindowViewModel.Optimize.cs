using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Operations;
using PdfPro.Operations.Optimizing;

namespace PdfPro.App.ViewModels;

/// <summary>"Tối ưu cho web" (optimize.linearize) and "Sửa tệp hỏng" (optimize.repair).</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// A copy of the active document laid out for Fast Web View (qpdf), as it is shown: its own file when nothing is
    /// unsaved, otherwise a copy of what it shows. Written in the background to a file the user names.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLinearize))]
    private async Task LinearizeAsync()
    {
        if (_optimizing is not { } optimizing || ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.Linearize_Error_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var output = _fileDialogs.PickSavePath(Format(Strings.Linearize_Name, Path.GetFileNameWithoutExtension(document.FileName)) + ".pdf", Path.GetDirectoryName(document.FilePath));
        if (output is null)
        {
            return;
        }

        if (Documents.Any(d => d.IsSameFile(output)))
        {
            await _messages.ShowErrorAsync(Strings.Linearize_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(output)));
            return;
        }

        var (input, workingFolder) = await DocumentFileAsync(document, "web");
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.Linearize_Error_Title, Strings.Optimize_Error_Unreadable);
                return;
            }

            var fileName = Path.GetFileName(output);
            await RunOptimizingAsync(
                Format(Strings.Linearize_Progress, fileName),
                token => optimizing.Linearize.WriteAsync(input, output, token),
                Format(Strings.Linearize_Done, fileName),
                Strings.Linearize_Error_Title);
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }
    }

    private bool CanLinearize() => _optimizing is not null && HasActiveDocument && _activeSave is null && !StatusBar.IsTaskRunning;

    /// <summary>
    /// A damaged file (one that may not even open) read back — by PDFium, qpdf, a rebuilt cross-reference section or
    /// PdfPig — into a new file, which then opens; the user is told which way worked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairAsync()
    {
        if (_optimizing is not { } optimizing || _fileDialogs.PickPdfFile(Strings.Repair_Pick_Title) is not { } input)
        {
            return;
        }

        var output = _fileDialogs.PickSavePath(Format(Strings.Repair_Name, Path.GetFileNameWithoutExtension(input)) + ".pdf", Path.GetDirectoryName(input));
        if (output is null)
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase) || Documents.Any(d => d.IsSameFile(output)))
        {
            await _messages.ShowErrorAsync(Strings.Repair_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(output)));
            return;
        }

        RepairReport? report = null;
        var fileName = Path.GetFileName(output);
        var done = await RunOptimizingAsync(
            Format(Strings.Repair_Progress, Path.GetFileName(input)),
            async token => report = await optimizing.Repair.RepairAsync(input, output, token),
            Format(Strings.Repair_Done, fileName),
            Strings.Repair_Error_Title);
        if (!done || report is null)
        {
            return;
        }

        _logger.LogInformation("A damaged file was repaired: {Method}, {PageCount} pages", report.Method, report.PageCount);
        await _messages.ShowInfoAsync(Strings.Repair_Done_Title, Format(Strings.Repair_Done_Message, MethodName(report.Method), report.PageCount));
        await OpenFilesAsync([output]);
    }

    private bool CanRepair() => _optimizing is not null && _activeSave is null && !StatusBar.IsTaskRunning;

    /// <summary>
    /// Runs <paramref name="work"/> in the background with progress and cancel in the status bar; failures are
    /// explained. True when it finished.
    /// </summary>
    private async Task<bool> RunOptimizingAsync(string progressText, Func<CancellationToken, Task> work, string doneText, string errorTitle)
    {
        await WaitForBackgroundWorkAsync(cancelPrint: false);
        var task = StatusBar.StartTask(progressText);
        var running = Task.Run(() => work(task.Token), task.Token);
        _activeSave = running;
        NotifySaveCommands();
        string error;
        try
        {
            await running;
            task.Complete(doneText);
            return true;
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Optimize_Cancelled);
            return false;
        }
        catch (ExternalToolNotFoundException)
        {
            _logger.LogWarning("qpdf is not installed");
            task.Complete(Strings.Optimize_FailedStatus);
            error = Strings.Optimize_Error_NoQpdf;
        }
        catch (RepairFailedException ex)
        {
            _logger.LogWarning("A file could not be repaired: {Attempts}", string.Join(", ", ex.Attempts.Select(a => $"{a.Method}:{a.Failure}")));
            task.Complete(Strings.Optimize_FailedStatus);
            error = RepairFailure(ex);
        }
        catch (Exception ex) when (ex is QpdfException or ExternalToolException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning("Optimizing a file failed: {ErrorType}", ex.GetType().Name);
            task.Complete(Strings.Optimize_FailedStatus);
            error = Strings.Optimize_Error_Unreadable;
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
        }

        await _messages.ShowErrorAsync(errorTitle, error);
        return false;
    }

    /// <summary>
    /// The document as a file: its own when nothing is unsaved, otherwise a copy of what it shows written into its
    /// session folder (removed afterwards: the folder is returned). Null when it cannot be written.
    /// </summary>
    private async Task<(string? Path, string? WorkingFolder)> DocumentFileAsync(DocumentTabViewModel document, string purpose)
    {
        if (!document.IsDirty && File.Exists(document.FilePath) && !document.Workspace.HasChangedOnDisk())
        {
            return (document.FilePath, null);
        }

        var folder = Path.Combine(document.Workspace.WorkDirectory, $"{purpose}-{Guid.NewGuid():N}");
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

            return (path, folder);
        }
        catch (Exception ex) when (ex is DocumentSaveException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The open document could not be written for {Purpose}: {ErrorType}", purpose, ex.GetType().Name);
            return (null, folder);
        }
    }

    private static string RepairFailure(RepairFailedException failure)
    {
        var text = new StringBuilder(Strings.Repair_Error_Message);
        foreach (var (method, reason) in failure.Attempts)
        {
            text.AppendLine().Append(Format(
                Strings.Repair_Error_Line,
                MethodName(method),
                reason switch
                {
                    RepairAttemptFailure.Missing => Strings.Repair_Failure_Missing,
                    RepairAttemptFailure.BadResult => Strings.Repair_Failure_BadResult,
                    _ => Strings.Repair_Failure_Unreadable,
                }));
        }

        return text.ToString();
    }

    private static string MethodName(RepairMethod method) => method switch
    {
        RepairMethod.Pdfium => Strings.Repair_Method_Pdfium,
        RepairMethod.Qpdf => Strings.Repair_Method_Qpdf,
        RepairMethod.CrossReference => Strings.Repair_Method_CrossReference,
        _ => Strings.Repair_Method_PdfPig,
    };
}
