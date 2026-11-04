using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Operations;
using PdfPro.Operations.Forms;

namespace PdfPro.App.ViewModels;

/// <summary>"Trộn dữ liệu": one filled copy of the form per row of a table, written in the background like a save.</summary>
public sealed partial class MainWindowViewModel
{
    private async void OnFormMergeRequested(object? sender, FormMergeRequest request)
    {
        if (sender is DocumentTabViewModel document && Tabs.Contains(document))
        {
            await MergeFormAsync(document, request);
        }
    }

    /// <summary>
    /// Writes the filled copies in the background (all or nothing), with progress and cancel in the status bar, from the
    /// document as it is shown (its own file when nothing is unsaved, else a working copy); then lists what did not fit
    /// and opens the folder when asked. Returns true once every file is written.
    /// </summary>
    internal async Task<bool> MergeFormAsync(DocumentTabViewModel document, FormMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        await WaitForBackgroundWorkAsync(cancelPrint: false);
        if (!Tabs.Contains(document))
        {
            return false;
        }

        var choice = request.Choice;
        var text = Format(Strings.FormMerge_Progress, choice.Table.Rows.Count);
        var task = StatusBar.StartTask(text);
        var progress = new Progress<OperationProgress>(p => task.Report(p.Fraction, text));
        string? workingFolder = null;
        var write = WriteMergeAsync();
        _activeSave = write;
        NotifySaveCommands();
        string error;
        try
        {
            var result = await write;
            var folderName = Path.GetFileName(choice.OutputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            task.Complete(Format(Strings.FormMerge_Done, result.OutputPaths.Count, folderName));
            if (choice.OpenFolderWhenDone)
            {
                _shell.OpenFolder(choice.OutputFolder);
            }

            if (result.UnknownColumns.Count > 0 || result.Problems.Count > 0)
            {
                await _messages.ShowInfoAsync(Strings.FormMerge_Title, Report(result));
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.FormMerge_Cancelled);
            return false;
        }
        catch (Exception ex) when (ex is DocumentSaveException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationValidationException)
        {
            _logger.LogWarning("A form merge failed: {ErrorType} (HResult {HResult:X8})", ex.GetType().Name, ex.HResult);
            task.Complete(Strings.FormMerge_FailedStatus);
            error = ex switch
            {
                InvalidDataException => Strings.FormMerge_Error_NoForm,
                OperationValidationException => Strings.Edit_Error_Invalid,
                InvalidOperationException => Strings.FormMerge_Error_OutputIsInput,
                DocumentSaveException => Strings.FormMerge_Error_Prepare,
                _ => SplitFailureMessage(ex, choice.OutputFolder),
            };
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }

        await _messages.ShowErrorAsync(Strings.FormMerge_Error_Title, error);
        return false;

        async Task<MergeFormResult> WriteMergeAsync()
        {
            var template = document.FilePath;
            if (document.IsDirty || !File.Exists(document.FilePath) || document.Workspace.HasChangedOnDisk())
            {
                // What is shown, with the values typed in the session: a copy in the session folder under its own name.
                workingFolder = Path.Combine(document.Workspace.WorkDirectory, $"merge-{Guid.NewGuid():N}");
                Directory.CreateDirectory(workingFolder);
                template = Path.Combine(workingFolder, document.FileName);
                await _saver.ExportAsync(document.Workspace, [.. Enumerable.Range(0, document.Viewer.PageCount)], template, new Progress<OperationProgress>(), task.Token);
            }

            return await request.Operation.WriteAsync(template, choice.Table, choice.Parameters, choice.OutputFolder, progress, task.Token);
        }
    }

    /// <summary>The columns that are not fields and the values that did not fit (at most ten of each).</summary>
    private static string Report(MergeFormResult result)
    {
        var message = new StringBuilder(Format(Strings.FormMerge_Written, result.OutputPaths.Count));
        if (result.UnknownColumns.Count > 0)
        {
            message.AppendLine().AppendLine().Append(Format(Strings.FormMerge_UnknownColumns, string.Join(", ", result.UnknownColumns.Take(10)) + (result.UnknownColumns.Count > 10 ? ", …" : string.Empty)));
        }

        if (result.Problems.Count > 0)
        {
            var lines = result.Problems.Take(10).Select(p => Format(Strings.FormMerge_Problem, p.Row, p.Column));
            message.AppendLine().AppendLine().Append(Format(Strings.FormMerge_Problems, result.Problems.Count))
                .AppendLine().Append(string.Join(Environment.NewLine, lines.Select(l => "• " + l)))
                .Append(result.Problems.Count > 10 ? Environment.NewLine + "…" : string.Empty);
        }

        return message.ToString();
    }
}
