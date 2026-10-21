using System.IO;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;

namespace PdfPro.App.ViewModels;

/// <summary>Page organizer state of the active document and "Trích trang" (written in the background like a save).</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The active document shows its organizer grid: the sidebar and the viewer's status controls are hidden.</summary>
    public bool IsOrganizing => ActiveDocument?.IsOrganizing == true;

    /// <summary>Page navigation and zoom in the status bar apply to the viewer: shown only while it is visible.</summary>
    public bool ShowsViewerStatus => HasActiveDocument && !IsOrganizing;

    /// <summary>
    /// Writes pages of a document to a new file chosen by the user, with progress and cancel in the status bar. The
    /// document's own file cannot be the target. Returns true once the file is written.
    /// </summary>
    internal async Task<bool> ExtractPagesAsync(DocumentTabViewModel document, IReadOnlyList<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (document.IsReadOnly || pageIndices.Count == 0)
        {
            return false;
        }

        var suggested = Format(Strings.Extract_FileName, Path.GetFileNameWithoutExtension(document.FileName), PageRanges.Format(pageIndices)) + ".pdf";
        var target = _fileDialogs.PickSavePath(suggested, Path.GetDirectoryName(document.FilePath));
        if (target is null)
        {
            return false;
        }

        if (document.IsSameFile(target))
        {
            await _messages.ShowErrorAsync(Strings.Extract_Error_Title, Strings.Extract_Error_OwnFile);
            return false;
        }

        if (Documents.Any(d => !ReferenceEquals(d, document) && d.IsSameFile(target)))
        {
            await _messages.ShowErrorAsync(Strings.Extract_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(target)));
            return false;
        }

        await WaitForBackgroundWorkAsync(cancelPrint: false);
        var fileName = Path.GetFileName(target);
        var task = StatusBar.StartTask(Format(Strings.Extract_Progress, fileName));
        var progress = new Progress<OperationProgress>(p => task.Report(p.Fraction, Format(Strings.Extract_Progress, fileName)));
        var export = _saver.ExportAsync(document.Workspace, pageIndices, target, progress, task.Token);
        _activeSave = export;
        NotifySaveCommands();
        SaveFailure? failure = null;
        try
        {
            await export;
            task.Complete(Format(Strings.Extract_Done, pageIndices.Count, fileName));
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Extract_Cancelled);
            return false;
        }
        catch (DocumentSaveException ex)
        {
            task.Complete(Strings.Extract_FailedStatus);
            failure = ex.Failure;
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
        }

        if (failure is { } reason)
        {
            await _messages.ShowErrorAsync(Strings.Extract_Error_Title, Format(SaveFailureMessage(reason), fileName));
            return false;
        }

        return true;
    }

    private async void OnExtractRequested(object? sender, IReadOnlyList<int> pageIndices)
    {
        if (sender is DocumentTabViewModel document && Tabs.Contains(document))
        {
            await ExtractPagesAsync(document, pageIndices);
        }
    }

    /// <summary>The explanation of a failed save or extraction ("{0}" is the file name).</summary>
    private static string SaveFailureMessage(SaveFailure failure) => failure switch
    {
        SaveFailure.Locked => Strings.Save_Error_Locked,
        SaveFailure.AccessDenied => Strings.Save_Error_AccessDenied,
        SaveFailure.DiskFull => Strings.Save_Error_DiskFull,
        SaveFailure.SourceUnreadable => Strings.Save_Error_SourceUnreadable,
        SaveFailure.VerificationFailed => Strings.Save_Error_Verification,
        _ => Strings.Save_Error_Failed,
    };
}
