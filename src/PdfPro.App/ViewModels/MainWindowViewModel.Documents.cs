using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.ViewModels;

/// <summary>Saving, closing with unsaved changes, application exit and crash recovery.</summary>
public sealed partial class MainWindowViewModel : IExitGuard
{
    private Task? _activeSave;

    /// <summary>The answer to "Lưu thay đổi?" for one document.</summary>
    private enum UnsavedChoice
    {
        Saved,
        Discard,
        Cancel,
    }

    /// <summary>
    /// Set once every unsaved document has been saved or discarded (or Windows is shutting down): the window may
    /// close without asking again.
    /// </summary>
    public bool ExitConfirmed { get; set; }

    public bool HasUnsavedDocuments => Documents.Any(d => d.IsDirty);

    /// <summary>
    /// Asks about every document with unsaved changes, one after the other (Lưu / Không lưu / Hủy), then stops the
    /// running print job. Returns false when the user cancelled or a save failed: the application keeps running and
    /// nothing has been discarded.
    /// </summary>
    public async Task<bool> ConfirmExitAsync()
    {
        var discarded = new List<DocumentTabViewModel>();
        foreach (var document in Documents.Where(d => d.IsDirty && !d.DiscardChanges).ToList())
        {
            switch (await AskToSaveAsync(document, exiting: true))
            {
                case UnsavedChoice.Cancel:
                    return false;
                case UnsavedChoice.Discard:
                    discarded.Add(document);
                    break;
            }
        }

        await WaitForBackgroundWorkAsync(cancelPrint: true);
        discarded.ForEach(d => d.DiscardChanges = true);
        ExitConfirmed = true;
        return true;
    }

    public void CancelExit()
    {
        ExitConfirmed = false;
        foreach (var document in Documents)
        {
            document.DiscardChanges = false;
        }
    }

    /// <summary>Offers to restore documents left unsaved by a crash, then opens the chosen ones.</summary>
    public async Task RecoverSessionsAsync()
    {
        IReadOnlyList<DocumentWorkspace> recovered;
        try
        {
            recovered = await _recovery.RecoverAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Looking for documents to recover failed: {ErrorType}", ex.GetType().Name);
            return;
        }

        foreach (var workspace in recovered)
        {
            var tab = _tabFactory.Create(workspace);
            Tabs.Add(tab);
            SelectedTab = tab;
        }

        if (recovered.Count > 0 && !StatusBar.IsTaskRunning)
        {
            StatusBar.TaskText = Format(Strings.Recovery_Done, recovered.Count);
        }
    }

    /// <summary>Ctrl+S: writes the active document over its file (after warnings for signed or changed files).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (ActiveDocument is { } document)
        {
            await SaveDocumentAsync(document, saveAs: false);
        }
    }

    private bool CanSave() => ActiveDocument is { IsReadOnly: false, IsDirty: true } && !StatusBar.IsTaskRunning;

    /// <summary>Ctrl+Shift+S: writes the active document to a new file, which becomes the document's file.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private async Task SaveAsAsync()
    {
        if (ActiveDocument is { } document)
        {
            await SaveDocumentAsync(document, saveAs: true);
        }
    }

    private bool CanSaveAs() => ActiveDocument is { IsReadOnly: false } && !StatusBar.IsTaskRunning;

    /// <summary>
    /// Saves a document with progress and cancel in the status bar. Returns true once the document is saved; false
    /// when the user cancelled or the save failed (the user has been told why; the original file is unchanged).
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="saveAs">Ask for a new file instead of writing over the document's file.</param>
    /// <param name="cancelPrint">Stop a running print job instead of waiting for it (the application is closing).</param>
    internal async Task<bool> SaveDocumentAsync(DocumentTabViewModel document, bool saveAs, bool cancelPrint = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsReadOnly)
        {
            return false;
        }

        // Its folder may be gone (renamed or removed since the document was opened): ask where to save instead.
        var target = saveAs || string.IsNullOrEmpty(document.FilePath) || !Directory.Exists(Path.GetDirectoryName(document.FilePath))
            ? null
            : document.FilePath;
        if (target is not null)
        {
            switch (await ConfirmOverwriteAsync(document))
            {
                case DialogChoice.Primary:
                    target = null; // "Lưu thành…" instead
                    break;
                case DialogChoice.Cancel:
                    return false;
            }
        }

        var isNewFile = target is null;
        if (target is null)
        {
            target = _fileDialogs.PickSavePath(document.FileName, Path.GetDirectoryName(document.FilePath));
            if (target is null)
            {
                return false;
            }

            if (Documents.Any(d => !ReferenceEquals(d, document) && d.IsSameFile(target)))
            {
                await _messages.ShowErrorAsync(Strings.Save_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(target)));
                return false;
            }
        }

        await WaitForBackgroundWorkAsync(cancelPrint);
        var fileName = Path.GetFileName(target);
        var task = StatusBar.StartTask(Format(Strings.Save_Progress, fileName));
        var progress = new Progress<OperationProgress>(p => task.Report(p.Fraction, Format(Strings.Save_Progress, fileName)));
        var saving = _saver.SaveAsync(document.Workspace, target, progress, task.Token);
        _activeSave = saving;
        NotifySaveCommands();
        SaveFailure? failure = null;
        try
        {
            await saving;
            task.Complete(Format(Strings.Save_Done, fileName));
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Save_Cancelled);
            return false;
        }
        catch (DocumentSaveException ex)
        {
            task.Complete(Strings.Save_FailedStatus);
            failure = ex.Failure;
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
        }

        // Only now that this save is over: the explanation may start another one ("Lưu thành…").
        if (failure is { } reason)
        {
            return await ExplainSaveFailureAsync(document, reason, fileName);
        }

        if (isNewFile)
        {
            await UpdateRecentFilesAsync(list => RecentFileList.Add(list, target));
        }

        return true;
    }

    /// <summary>Signed or externally changed files get a warning first. Primary = save as, Secondary = overwrite.</summary>
    private async Task<DialogChoice> ConfirmOverwriteAsync(DocumentTabViewModel document)
    {
        if (document.Document.Features.HasDigitalSignatures || document.Workspace.OriginalDocument.Features.HasDigitalSignatures)
        {
            var choice = await _messages.AskAsync(
                Strings.Save_Signed_Title,
                Format(Strings.Save_Signed_Message, document.FileName),
                Strings.Save_SaveAsButton,
                Strings.Save_OverwriteAnyway,
                Strings.Common_Cancel);
            if (choice != DialogChoice.Secondary)
            {
                return choice;
            }
        }

        if (document.Workspace.HasChangedOnDisk())
        {
            return await _messages.AskAsync(
                Strings.Save_Changed_Title,
                Format(Strings.Save_Changed_Message, document.FileName),
                Strings.Save_SaveAsButton,
                Strings.Save_OverwriteAnyway,
                Strings.Common_Cancel);
        }

        return DialogChoice.Secondary;
    }

    /// <summary>
    /// Explains a failed save. A locked or protected file offers "Lưu thành…" right away; returns the outcome of that.
    /// </summary>
    private async Task<bool> ExplainSaveFailureAsync(DocumentTabViewModel document, SaveFailure failure, string fileName)
    {
        var message = Format(SaveFailureMessage(failure), fileName);

        if (failure is SaveFailure.Locked or SaveFailure.AccessDenied)
        {
            var choice = await _messages.AskAsync(Strings.Save_Error_Title, message, Strings.Save_SaveAsButton, null, Strings.Common_Close);
            return choice == DialogChoice.Primary && await SaveDocumentAsync(document, saveAs: true);
        }

        await _messages.ShowErrorAsync(Strings.Save_Error_Title, message);
        return false;
    }

    /// <summary>Lưu / Không lưu / Hủy for one document; a failed or cancelled save counts as Hủy.</summary>
    private async Task<UnsavedChoice> AskToSaveAsync(DocumentTabViewModel document, bool exiting)
    {
        SelectedTab = document;
        var choice = await _messages.AskAsync(
            Strings.Close_SaveChanges_Title,
            Format(Strings.Close_SaveChanges_Message, document.FileName),
            Strings.Close_Save,
            Strings.Close_DontSave,
            Strings.Common_Cancel);
        return choice switch
        {
            DialogChoice.Primary => await SaveDocumentAsync(document, saveAs: false, cancelPrint: exiting) ? UnsavedChoice.Saved : UnsavedChoice.Cancel,
            DialogChoice.Secondary => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel,
        };
    }

    /// <summary>
    /// Waits for the running print job (cancelled first when the application exits) and save: the status bar runs
    /// one task at a time, and documents must not be closed or saved under them.
    /// </summary>
    private async Task WaitForBackgroundWorkAsync(bool cancelPrint)
    {
        while (_activePrint is not null || _activeSave is not null)
        {
            if (_activePrint is { } print)
            {
                if (cancelPrint)
                {
                    print.Status.Cancel();
                }

                await Task.WhenAny(print.Completion);
            }
            else if (_activeSave is { } save)
            {
                await Task.WhenAny(save);
            }

            // The task's own continuation completes its status bar entry; it may run after this one.
            await Task.Yield();
        }
    }

    private void NotifySaveCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
