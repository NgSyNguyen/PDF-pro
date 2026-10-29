using PdfPro.App.Services.Security;
using PdfPro.App.ViewModels.Security;
using PdfPro.Operations.Security;

namespace PdfPro.App.ViewModels;

/// <summary>"Mật khẩu &amp; quyền" of the open document.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>
    /// Applies the choice of the security dialog: a new protection (or none) is one undoable step ("security.protection"),
    /// its passwords kept in the workspace's memory; the same protection given its missing passwords only stores them.
    /// False when the document cannot be edited.
    /// </summary>
    public bool SetProtection(ProtectionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!Workspace.CanEdit)
        {
            return false;
        }

        if (choice.KeepsSetting)
        {
            if (choice.Setting is { } kept && choice.Passwords is { } given)
            {
                var known = Workspace.Passwords.Get(kept.PasswordsId);
                Workspace.Passwords.Set(kept.PasswordsId, new DocumentPasswords(given.Open ?? known?.Open, given.Owner ?? known?.Owner));
            }

            return true;
        }

        var setting = choice.Setting is { } chosen && choice.Passwords is { } passwords
            ? chosen with { PasswordsId = Workspace.Passwords.Add(passwords) }
            : choice.Setting;
        Apply(_operations.Get<SetProtectionOperation>(), new SetProtectionParams(setting));
        return true;
    }
}
