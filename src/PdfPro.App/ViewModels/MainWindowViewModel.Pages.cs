using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Merging;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;

namespace PdfPro.App.ViewModels;

/// <summary>Page organizer state of the active document and "Trích trang" (written in the background like a save).</summary>
public sealed partial class MainWindowViewModel
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDiskFull = unchecked((int)0x80070070);

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

    /// <summary>
    /// Runs a content change ("Đổi khổ giấy") in the background with progress and cancel in the status bar; the
    /// document cannot be edited or saved meanwhile. Returns true once the change is in the document's history.
    /// </summary>
    internal async Task<bool> ChangeContentAsync(DocumentTabViewModel document, ContentChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        await WaitForBackgroundWorkAsync(cancelPrint: false);
        if (!Tabs.Contains(document) || !document.CanEditPages)
        {
            return false;
        }

        var task = StatusBar.StartTask(Format(Strings.Content_Progress, request.Description));
        var progress = new Progress<OperationProgress>(p => task.Report(p.Fraction, Format(Strings.Content_Progress, request.Description)));
        var change = _contentChanger.ApplyAsync(document.Workspace, request.Operation, request.Parameters, progress, task.Token);
        _activeSave = change;
        NotifySaveCommands();
        string? error = null;
        try
        {
            await change;
            task.Complete(Format(Strings.Content_Done, request.Description));
        }
        catch (OperationCanceledException)
        {
            task.Complete(Format(Strings.Content_Cancelled, request.Description));
            return false;
        }
        catch (OperationValidationException ex)
        {
            _logger.LogWarning("A content change was refused: {OperationId}, {ErrorCount} error(s)", ex.OperationId, ex.Errors.Count);
            task.Complete(Format(Strings.Content_FailedStatus, request.Description));
            error = Strings.Edit_Error_Invalid;
        }
        catch (ContentChangeException ex)
        {
            task.Complete(Format(Strings.Content_FailedStatus, request.Description));
            error = ex.Failure switch
            {
                ContentChangeFailure.SourceUnreadable => Strings.Content_Error_SourceUnreadable,
                ContentChangeFailure.DiskFull => Strings.Content_Error_DiskFull,
                _ => Strings.Content_Error_Failed,
            };
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
        }

        if (error is not null)
        {
            await _messages.ShowErrorAsync(Strings.Content_Error_Title, error);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes the files of a confirmed split in the background (all or nothing), with progress and cancel in the status
    /// bar, then opens their folder when asked. Returns true once every file is written.
    /// </summary>
    internal async Task<bool> SplitAsync(DocumentTabViewModel document, SplitRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        await WaitForBackgroundWorkAsync(cancelPrint: false);
        if (!Tabs.Contains(document))
        {
            return false;
        }

        var choice = request.Choice;
        var folderName = Path.GetFileName(choice.OutputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var task = StatusBar.StartTask(Format(Strings.Split_Progress, document.FileName));
        var progress = new Progress<OperationProgress>(p => task.Report(p.Fraction, Format(Strings.Split_Progress, document.FileName)));
        IReadOnlyCollection<string> protectedPaths = document.FilePath.Length > 0 ? [document.FilePath] : [];
        var write = request.Operation.WriteAsync(request.Document, choice.Segments, choice.Parameters, choice.OutputFolder, protectedPaths, progress, task.Token);
        _activeSave = write;
        NotifySaveCommands();
        string error;
        try
        {
            var files = await write;
            task.Complete(Format(Strings.Split_Done, files.Count, folderName));
            if (choice.OpenFolder)
            {
                _shell.OpenFolder(choice.OutputFolder);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Split_Cancelled);
            return false;
        }
        catch (Exception ex) when (ex is MaterializationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Splitting a document failed: {ErrorType} (HResult {HResult:X8})", ex.GetType().Name, ex.HResult);
            task.Complete(Strings.Split_FailedStatus);
            error = SplitFailureMessage(ex, choice.OutputFolder);
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
        }

        await _messages.ShowErrorAsync(Strings.Split_Error_Title, error);
        return false;
    }

    private static string SplitFailureMessage(Exception error, string folder) => error switch
    {
        MaterializationException => Strings.Split_Error_SourceUnreadable,
        UnauthorizedAccessException => Format(Strings.Split_Error_AccessDenied, folder),
        IOException { HResult: ErrorDiskFull or ErrorHandleDiskFull } => Strings.Split_Error_DiskFull,
        IOException { HResult: ErrorSharingViolation or ErrorLockViolation } => Strings.Split_Error_FileInUse,
        _ => Strings.Split_Error_Failed,
    };

    /// <summary>
    /// "Gộp": the merge dialog (with the active document first, as displayed), then the merge in the background with
    /// progress and cancel; the result opens in a new tab when asked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        var initial = new List<MergeItemViewModel>();
        string? suggested = null;
        if (ActiveDocument is { IsReadOnly: false } document)
        {
            if (await OpenDocumentForMergeAsync(document) is { } item)
            {
                initial.Add(item);
            }

            if (Path.GetDirectoryName(document.FilePath) is { Length: > 0 } folder)
            {
                suggested = Path.Combine(folder, Format(Strings.Merge_DefaultName, Path.GetFileNameWithoutExtension(document.FileName)) + ".pdf");
            }
        }

        var dialog = new MergeViewModel(_merge, _fileDialogs, initial, suggested);
        if (!_pageDialogs.ShowMerge(dialog) || dialog.Result is not { } result)
        {
            return;
        }

        var fileName = Path.GetFileName(result.OutputPath);
        if (Documents.Any(d => d.IsSameFile(result.OutputPath)))
        {
            await _messages.ShowErrorAsync(Strings.Merge_Error_Title, Format(Strings.Save_AlreadyOpen, fileName));
            return;
        }

        await WaitForBackgroundWorkAsync(cancelPrint: false);
        var text = Format(Strings.Merge_Progress, result.Sources.Count);
        var task = StatusBar.StartTask(text);
        var progress = new Progress<OperationProgress>(p => task.Report(p.Fraction, text));
        var write = _merge.WriteAsync(result.Sources, result.Parameters, result.OutputPath, progress, task.Token);
        _activeSave = write;
        NotifySaveCommands();
        string? error = null;
        try
        {
            await write;
            task.Complete(Format(Strings.Merge_Done, result.Sources.Count, fileName));
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Merge_Cancelled);
            return;
        }
        catch (Exception ex) when (ex is MaterializationException or IOException or UnauthorizedAccessException or OperationValidationException)
        {
            _logger.LogWarning("Merging failed: {ErrorType} (HResult {HResult:X8})", ex.GetType().Name, ex.HResult);
            task.Complete(Strings.Merge_FailedStatus);
            error = ex switch
            {
                MaterializationException { Failure: MaterializationFailure.SourceMissing } => Strings.Merge_Error_SourceMissing,
                MaterializationException => Strings.Merge_Error_SourceUnreadable,
                OperationValidationException => Strings.Edit_Error_Invalid,
                _ => Format(Strings.Merge_Error_Failed, fileName),
            };
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
        }

        if (error is null)
        {
            if (result.OpenWhenDone)
            {
                await OpenFilesAsync([result.OutputPath]);
            }

            return;
        }

        await _messages.ShowErrorAsync(Strings.Merge_Error_Title, error);
    }

    private bool CanMerge() => _activeSave is null;

    /// <summary>
    /// The open document as a file to merge: its own file when nothing is unsaved, otherwise a copy of what it shows
    /// written into its session folder. Null when it cannot be read (the merge then starts without it).
    /// </summary>
    private async Task<MergeItemViewModel?> OpenDocumentForMergeAsync(DocumentTabViewModel document)
    {
        var name = Path.GetFileNameWithoutExtension(document.FileName);
        try
        {
            string path;
            string displayName;
            if (!document.IsDirty && File.Exists(document.FilePath) && !document.Workspace.HasChangedOnDisk())
            {
                path = document.FilePath;
                displayName = document.FileName;
            }
            else
            {
                Directory.CreateDirectory(document.Workspace.WorkDirectory);
                path = Path.Combine(document.Workspace.WorkDirectory, $"merge-{Guid.NewGuid():N}.pdf");
                var task = StatusBar.StartTask(Strings.Merge_Preparing);
                try
                {
                    await _saver.ExportAsync(document.Workspace, [.. Enumerable.Range(0, document.Viewer.PageCount)], path, new Progress<OperationProgress>(), task.Token);
                }
                finally
                {
                    task.Complete(string.Empty);
                }

                displayName = Format(Strings.Merge_CurrentDocument, document.FileName);
            }

            return await _merge.AddAsync(path, document.FileName) is { } source ? new MergeItemViewModel(source, displayName, name) : null;
        }
        catch (Exception ex) when (ex is DocumentSaveException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The open document could not be prepared for merging: {ErrorType}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Writes a sheet layout (N-up, booklet, poster) of the document as displayed into a new file in the background:
    /// the document is first written to its session folder, then laid out. The new file opens when asked.
    /// </summary>
    internal async Task<bool> LayoutAsync(DocumentTabViewModel document, LayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        var fileName = Path.GetFileName(request.OutputPath);
        if (Documents.Any(d => d.IsSameFile(request.OutputPath)))
        {
            await _messages.ShowErrorAsync(Strings.Layout_Error_Title, Format(Strings.Save_AlreadyOpen, fileName));
            return false;
        }

        await WaitForBackgroundWorkAsync(cancelPrint: false);
        if (!Tabs.Contains(document))
        {
            return false;
        }

        var text = Format(Strings.Layout_Progress, fileName);
        var task = StatusBar.StartTask(text);
        var input = Path.Combine(document.Workspace.WorkDirectory, $"layout-{Guid.NewGuid():N}.pdf");
        var pages = Enumerable.Range(0, document.Viewer.PageCount).ToArray();

        async Task RunAsync(CancellationToken token)
        {
            Directory.CreateDirectory(document.Workspace.WorkDirectory);
            await _saver.ExportAsync(document.Workspace, pages, input, new Progress<OperationProgress>(p => task.Report(p.Fraction / 2, text)), token);
            await request.Write(input, new Progress<OperationProgress>(p => task.Report(0.5 + (p.Fraction / 2), text)), token);
        }

        var job = RunAsync(task.Token);
        _activeSave = job;
        NotifySaveCommands();
        string? error = null;
        try
        {
            await job;
            task.Complete(Format(Strings.Layout_Done, fileName));
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Layout_Cancelled);
            return false;
        }
        catch (DocumentSaveException ex)
        {
            task.Complete(Strings.Layout_FailedStatus);
            error = Format(SaveFailureMessage(ex.Failure), document.FileName);
        }
        catch (Exception ex) when (ex is MaterializationException or IOException or UnauthorizedAccessException or OperationValidationException)
        {
            _logger.LogWarning("A sheet layout failed: {ErrorType} (HResult {HResult:X8})", ex.GetType().Name, ex.HResult);
            task.Complete(Strings.Layout_FailedStatus);
            error = ex is MaterializationException ? Strings.Merge_Error_SourceUnreadable : Format(Strings.Merge_Error_Failed, fileName);
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
            try
            {
                File.Delete(input);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("A working copy could not be removed: {ErrorType}", ex.GetType().Name);
            }
        }

        if (error is not null)
        {
            await _messages.ShowErrorAsync(Strings.Layout_Error_Title, error);
            return false;
        }

        if (request.OpenWhenDone)
        {
            await OpenFilesAsync([request.OutputPath]);
        }

        return true;
    }

    private async void OnLayoutRequested(object? sender, LayoutRequest request)
    {
        if (sender is DocumentTabViewModel document && Tabs.Contains(document))
        {
            await LayoutAsync(document, request);
        }
    }

    private async void OnSplitRequested(object? sender, SplitRequest request)
    {
        if (sender is DocumentTabViewModel document && Tabs.Contains(document))
        {
            await SplitAsync(document, request);
        }
    }

    private async void OnContentChangeRequested(object? sender, ContentChangeRequest request)
    {
        if (sender is DocumentTabViewModel document && Tabs.Contains(document))
        {
            await ChangeContentAsync(document, request);
        }
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
