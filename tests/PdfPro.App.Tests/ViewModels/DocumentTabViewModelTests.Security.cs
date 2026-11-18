using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.App.Services.Security;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Security;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Mật khẩu &amp; quyền" on the tab: a new protection is one undoable step; missing passwords are only stored.</summary>
public sealed partial class DocumentTabViewModelTests
{
    [Fact]
    public async Task ANewProtection_IsOneUndoableStep_WithItsPasswordsInMemory()
    {
        await using var tab = await CreateAsync(2);
        var chosen = new ProtectionSetting(Guid.Empty, EncryptionAlgorithm.Aes256, DocumentPermissions.Print, HasOpenPassword: true);

        tab.SetProtection(new ProtectionChoice(chosen, new DocumentPasswords("mo", "chu"), KeepsSetting: false)).ShouldBeTrue();

        var protection = tab.Workspace.Protection.ShouldNotBeNull();
        protection.PasswordsId.ShouldNotBe(Guid.Empty);
        tab.Workspace.Passwords.SecurityOf(protection).ShouldBe(new DocumentSecurity("mo", "chu", EncryptionAlgorithm.Aes256, DocumentPermissions.Print));
        tab.IsDirty.ShouldBeTrue();
        tab.UndoToolTip.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Cmd_UndoWith, Strings.History_SetProtection));

        tab.UndoCommand.Execute(null);

        tab.Workspace.Protection.ShouldBeNull();
        tab.IsDirty.ShouldBeFalse();
        tab.RedoCommand.Execute(null);
        tab.Workspace.Protection.ShouldBe(protection, "redo brings the same protection and its passwords back");
    }

    [Fact]
    public async Task TakingTheProtectionOff_IsAStepToo()
    {
        await using var tab = await CreateAsync(2);
        tab.SetProtection(new ProtectionChoice(new ProtectionSetting(Guid.Empty, EncryptionAlgorithm.Aes128, DocumentPermissions.All, false), new DocumentPasswords(null, "chu"), false));

        tab.SetProtection(new ProtectionChoice(null, null, KeepsSetting: false)).ShouldBeTrue();

        tab.Workspace.Protection.ShouldBeNull();
        tab.UndoToolTip.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Cmd_UndoWith, Strings.History_RemoveProtection));
    }

    [Fact]
    public async Task MissingPasswords_OfTheSameProtection_AreOnlyStored()
    {
        await using var tab = await CreateAsync(2);
        tab.SetProtection(new ProtectionChoice(new ProtectionSetting(Guid.Empty, EncryptionAlgorithm.Aes256, DocumentPermissions.All, true), new DocumentPasswords("mo", "chu"), false));
        var protection = tab.Workspace.Protection!;
        tab.Workspace.Passwords.Set(protection.PasswordsId, new DocumentPasswords(null, "chu"));
        tab.Workspace.NeedsPasswordsToSave.ShouldBeTrue();
        var steps = tab.Workspace.Session.History.UndoCommands.Count;

        tab.SetProtection(new ProtectionChoice(protection, new DocumentPasswords("mo-moi", null), KeepsSetting: true)).ShouldBeTrue();

        tab.Workspace.Session.History.UndoCommands.Count.ShouldBe(steps, "giving a password is not an edit");
        tab.Workspace.NeedsPasswordsToSave.ShouldBeFalse();
        tab.Workspace.Passwords.Get(protection.PasswordsId).ShouldBe(new DocumentPasswords("mo-moi", "chu"));
    }

    [Fact]
    public async Task Copying_FollowsTheAuthorsPermission()
    {
        var document = FakePdfDocument.WithPages(1);
        document.Features = new Pdfium.PdfDocumentFeatures("1.7", 6, 0xFFFFF0C4, 0, Pdfium.PdfFormType.None, false, 0xFFFFF0C4);
        await using var restricted = _factory.Create(await _fixture.CreateAsync(document));
        await using var plain = await CreateAsync(1);

        restricted.CanCopyText.ShouldBeFalse();
        plain.CanCopyText.ShouldBeTrue();
    }
}
