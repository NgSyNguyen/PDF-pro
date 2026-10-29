using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Security;

namespace PdfPro.App.ViewModels;

/// <summary>"Mật khẩu &amp; quyền" (security.encrypt): protecting the open document, and unlocking a view-only one.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The security dialog of the active document: for a view-only protected file, its permissions and the owner
    /// password that reopens it for editing; otherwise its protection, applied when it is saved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditProtection))]
    private void Protect()
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly)
        {
            var permissions = PermissionBits.ToPermissions(document.Document.Features.UserPermissions);
            _pageDialogs.ShowSecurity(new SecurityViewModel(permissions, password => UnlockAsync(document, password)));
            return;
        }

        EditProtection(document, needsPasswords: false);
    }

    private bool CanEditProtection() => HasActiveDocument && _activeSave is null;

    /// <summary>The protection of an editable document in the dialog; true when the user confirmed.</summary>
    private bool EditProtection(DocumentTabViewModel document, bool needsPasswords)
    {
        var workspace = document.Workspace;
        var current = workspace.Protection;
        var dialog = new SecurityViewModel(current, current is null ? null : workspace.Passwords.Get(current.PasswordsId), needsPasswords);
        return _pageDialogs.ShowSecurity(dialog) && dialog.Result is { } choice && document.SetProtection(choice);
    }

    /// <summary>
    /// Saving keeps the protection, which needs its passwords: when they are not all known (a recovered session, a file
    /// opened with its owner password), the dialog asks for them. False when the user gave up (nothing is saved).
    /// </summary>
    private bool EnsureProtectionPasswords(DocumentTabViewModel document)
        => !document.Workspace.NeedsPasswordsToSave || (EditProtection(document, needsPasswords: true) && !document.Workspace.NeedsPasswordsToSave);

    /// <summary>
    /// Reopens a view-only protected document with its owner password, editable, in the same tab position and at the
    /// same page (it has no unsaved changes: view-only documents cannot be changed). Null when done, else why not.
    /// </summary>
    private async Task<string?> UnlockAsync(DocumentTabViewModel document, string ownerPassword)
    {
        var workspace = document.Workspace;
        if (string.IsNullOrEmpty(document.FilePath) || !Tabs.Contains(document))
        {
            return Strings.Security_Unlock_Unreadable;
        }

        var result = await _workspaces.OpenUnlockedAsync(document.FilePath, new ProtectionUnlock(ownerPassword, workspace.OpenPassword, workspace.OpenPassword is not null));
        if (result.Workspace is not { } unlocked)
        {
            return result.Failure switch
            {
                UnlockFailure.WrongPassword => Strings.Security_Unlock_Wrong,
                UnlockFailure.NotOwnerPassword => Strings.Security_Unlock_NotOwner,
                _ => Strings.Security_Unlock_Unreadable,
            };
        }

        await ReplaceTabAsync(document, unlocked);
        _logger.LogInformation("A protected document was unlocked for editing");
        return null;
    }
}
