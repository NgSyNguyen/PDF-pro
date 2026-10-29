using System.IO;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Forms;
using PdfPro.Operations.Signing;

namespace PdfPro.App.ViewModels;

/// <summary>"Ký số" (security.sign): signing the saved file of the active document into a signed copy.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Signs the active document's file: what is signed is the file as saved, so unsaved changes are saved first; the
    /// dialog chooses the certificate and the look, a visible signature is then placed on a page, and the signed copy
    /// (the signature appended as an incremental update, earlier signatures staying valid) is written in the background
    /// to the file the user names — which opens, or replaces the tab when it is the document's own file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSign))]
    private async Task SignAsync()
    {
        if (_signing is not { } signing || ActiveDocument is not { } document)
        {
            return;
        }

        // PdfSigner signs only unencrypted files; a protected document would be saved encrypted.
        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.Sign_Error_Title, Strings.Sign_Error_Encrypted);
            return;
        }

        if (!await SavedForSigningAsync(document))
        {
            return;
        }

        var input = document.FilePath;
        var form = await signing.Forms.ReadAsync(input, CancellationToken.None);
        var library = signing.Signatures;
        var dialog = new SignDialogViewModel(
            signing.Certificates,
            _fileDialogs,
            [.. form.Fields.Where(f => f.Kind == FormFieldKind.Signature && !f.IsSigned).Select(f => f.Name)],
            library?.List() ?? [],
            id => library?.Load(id),
            _settings.Current.TimestampServer,
            _settings.Current.UseTimestamp);
        if (!_pageDialogs.ShowSign(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        using var certificate = choice.Certificate;
        await RememberTimestampSettingsAsync(dialog);
        SignatureBox? box = null;
        if (choice.Placement == SignaturePlacementKind.Draw)
        {
            box = await document.PlaceSignatureAsync();
            if (box is null || !Tabs.Contains(document))
            {
                return;
            }

            // The box was placed on the pages as saved: they must not have changed meanwhile.
            if (document.IsDirty)
            {
                await _messages.ShowErrorAsync(Strings.Sign_Error_Title, Strings.Sign_Error_Changed);
                return;
            }
        }

        var output = _fileDialogs.PickSavePath(Format(Strings.Sign_SignedName, Path.GetFileNameWithoutExtension(document.FileName)) + ".pdf", Path.GetDirectoryName(input));
        if (output is null)
        {
            return;
        }

        output = Path.GetFullPath(output);
        var replacesDocument = document.IsSameFile(output);
        if (!replacesDocument && Documents.Any(d => d.IsSameFile(output)))
        {
            await _messages.ShowErrorAsync(Strings.Sign_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(output)));
            return;
        }

        var time = DateTimeOffset.Now;
        var appearance = choice.Placement switch
        {
            SignaturePlacementKind.Draw => new SignatureAppearance(box!.PageIndex + 1, box.Rect, choice.Lines(time), choice.Image),
            SignaturePlacementKind.Field => new SignatureAppearance(1, default, choice.Lines(time), choice.Image),
            _ => null,
        };
        var fieldName = choice.Placement == SignaturePlacementKind.Field && choice.FieldName is { } name ? name : FreeSignatureFieldName(form.Fields);
        var request = new SignRequest(fieldName, appearance, choice.Reason, choice.Location, choice.Contact, choice.TimestampServer);
        if (!await WriteSignedAsync(signing, input, output, request, certificate, time))
        {
            return;
        }

        _logger.LogInformation("A document was signed ({Placement}, timestamped: {Timestamped})", choice.Placement, request.TimestampServer is not null);
        if (replacesDocument)
        {
            await ReopenAsync(document, output);
        }
        else
        {
            await OpenFilesAsync([output]);
        }
    }

    private bool CanSign() => _signing is not null && HasActiveDocument && _activeSave is null && !StatusBar.IsTaskRunning;

    /// <summary>
    /// The document's file holds what is shown: objects waiting in the page editor are applied and unsaved changes saved
    /// (when the user agrees). False when they were not.
    /// </summary>
    private async Task<bool> SavedForSigningAsync(DocumentTabViewModel document)
    {
        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return false;
        }

        if (!document.IsDirty && File.Exists(document.FilePath) && !document.Workspace.HasChangedOnDisk())
        {
            return true;
        }

        var choice = await _messages.AskAsync(Strings.Sign_SaveFirst_Title, Format(Strings.Sign_SaveFirst_Message, document.FileName), Strings.Sign_SaveFirst_Save, null, Strings.Common_Cancel);
        return choice == DialogChoice.Primary
            && await SaveDocumentAsync(document, saveAs: false)
            && Tabs.Contains(document)
            && !document.IsDirty
            && File.Exists(document.FilePath);
    }

    /// <summary>
    /// Writes the signed copy in the background with progress and cancel in the status bar. A timestamp that cannot be
    /// had is explained, and the user may sign without one. False when nothing was written (the user has been told why).
    /// </summary>
    private async Task<bool> WriteSignedAsync(Services.Security.SigningServices signing, string input, string output, SignRequest request, X509Certificate2 certificate, DateTimeOffset time)
    {
        await WaitForBackgroundWorkAsync(cancelPrint: false);
        var fileName = Path.GetFileName(output);
        var task = StatusBar.StartTask(Format(Strings.Sign_Progress, fileName));
        var writing = Task.Run(() => SignOperation.SignFileAsync(input, output, request, certificate, signing.Timestamps, time, task.Token), task.Token);
        _activeSave = writing;
        NotifySaveCommands();
        string error;
        try
        {
            await writing;
            task.Complete(Format(Strings.Sign_Done, fileName));
            return true;
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Sign_Cancelled);
            return false;
        }
        catch (SigningException ex) when (ex.Failure == SigningFailure.Timestamp && request.TimestampServer is not null)
        {
            _logger.LogWarning("No timestamp for a signature: {ErrorType}", ex.InnerException?.GetType().Name ?? nameof(SigningException));
            task.Complete(Format(Strings.Sign_Failed, fileName));
            error = string.Empty;
        }
        catch (SigningException ex)
        {
            _logger.LogWarning("A document could not be signed: {Failure} ({ErrorType})", ex.Failure, ex.InnerException?.GetType().Name);
            task.Complete(Format(Strings.Sign_Failed, fileName));
            error = SigningMessage(ex.Failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A signed copy could not be written: {ErrorType} (HResult {HResult:X8})", ex.GetType().Name, ex.HResult);
            task.Complete(Format(Strings.Sign_Failed, fileName));
            error = Strings.Sign_Error_Unreadable;
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
        }

        if (error.Length == 0)
        {
            var choice = await _messages.AskAsync(Strings.Sign_NoTimestamp_Title, Strings.Sign_NoTimestamp_Message, Strings.Sign_NoTimestamp_Sign, null, Strings.Common_Cancel);
            return choice == DialogChoice.Primary && await WriteSignedAsync(signing, input, output, request with { TimestampServer = null }, certificate, time);
        }

        await _messages.ShowErrorAsync(Strings.Sign_Error_Title, error);
        return false;
    }

    /// <summary>The time-stamping choices are kept for the next signature.</summary>
    private async Task RememberTimestampSettingsAsync(SignDialogViewModel dialog)
    {
        var server = dialog.TimestampServer.Trim();
        if (server == _settings.Current.TimestampServer && dialog.UseTimestamp == _settings.Current.UseTimestamp)
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(s => s with { TimestampServer = server, UseTimestamp = dialog.UseTimestamp });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The timestamp settings could not be saved");
        }
    }

    /// <summary>The document's own file was replaced by its signed copy: the tab shows it, at the same page.</summary>
    private async Task ReopenAsync(DocumentTabViewModel document, string path)
    {
        if (await _workspaces.OpenAsync(path) is { } workspace && Tabs.Contains(document))
        {
            await ReplaceTabAsync(document, workspace);
        }
    }

    /// <summary>A tab shows another workspace of its document, in the same position and at the same page.</summary>
    private async Task ReplaceTabAsync(DocumentTabViewModel document, DocumentWorkspace workspace)
    {
        var page = document.Viewer.CurrentPageIndex;
        var index = Tabs.IndexOf(document);
        var tab = _tabFactory.Create(workspace);
        if (index < 0)
        {
            Tabs.Add(tab);
        }
        else
        {
            Tabs.Insert(index, tab);
            Tabs.Remove(document);
        }

        SelectedTab = tab;
        tab.Viewer.NavigateTo(page);
        await document.DisposeAsync();
    }

    /// <summary>"Chữ ký 1", "Chữ ký 2"…: the first such name no field of the document has.</summary>
    private static string FreeSignatureFieldName(IReadOnlyList<FormField> fields)
    {
        var taken = fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var name = Format(Strings.Sign_FieldName, n);
            if (!taken.Contains(name) && !taken.Any(t => t.StartsWith(name + ".", StringComparison.Ordinal)))
            {
                return name;
            }
        }
    }
}
