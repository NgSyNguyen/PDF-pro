using NSubstitute;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Security;
using PdfPro.Operations.Security;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Mật khẩu &amp; quyền": saving keeps the protection, a view-only protected document is unlocked in place.</summary>
public sealed partial class MainWindowViewModelTests
{
    private static readonly ProtectionSetting Unknown = new(Guid.NewGuid(), EncryptionAlgorithm.Aes256, DocumentPermissions.All, HasOpenPassword: true);

    [Fact]
    public async Task Save_OfAProtectionWithoutItsPasswords_AsksForThem_AndSavesNothingWithoutThem()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        viewModel.ActiveDocument!.Apply(TestOperations.Catalog().Get<SetProtectionOperation>(), new SetProtectionParams(Unknown)).ShouldBeTrue();
        SecurityViewModel? shown = null;
        _pageDialogs.ShowSecurity(Arg.Any<SecurityViewModel>()).Returns(call =>
        {
            shown = call.Arg<SecurityViewModel>();
            return false;
        });
        SaverMarksSaved();

        await viewModel.SaveCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull().NeedsPasswords.ShouldBeTrue();
        await DidNotSave();
    }

    [Fact]
    public async Task Save_WithThePasswordsGiven_KeepsTheProtection()
    {
        var (viewModel, path) = await CreateDirtyAsync();
        var document = viewModel.ActiveDocument!;
        document.Apply(TestOperations.Catalog().Get<SetProtectionOperation>(), new SetProtectionParams(Unknown)).ShouldBeTrue();
        _pageDialogs.ShowSecurity(Arg.Any<SecurityViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<SecurityViewModel>();
            foreach (var set in new Action<string>[] { dialog.SetOpenPassword, dialog.SetOpenConfirmation })
            {
                set("Mở-cửa#2026!");
            }

            dialog.SetOwnerPassword("Chủ-sở-hữu!789x");
            dialog.SetOwnerConfirmation("Chủ-sở-hữu!789x");
            dialog.ApplyCommand.Execute(null);
            return dialog.Result is not null;
        });
        SaverMarksSaved();

        await viewModel.SaveCommand.ExecuteAsync(null);

        await _saver.Received(1).SaveAsync(document.Workspace, path, Arg.Any<IProgress<Core.Operations.OperationProgress>>(), Arg.Any<CancellationToken>());
        document.Workspace.Protection.ShouldBe(Unknown, "the same protection, its passwords now known");
        document.Workspace.Passwords.SecurityOf(Unknown).ShouldBe(new DocumentSecurity("Mở-cửa#2026!", "Chủ-sở-hữu!789x", EncryptionAlgorithm.Aes256, DocumentPermissions.All));
    }

    [Fact]
    public async Task AViewOnlyProtectedDocument_IsUnlockedInItsTab_WithTheOwnerPassword()
    {
        var restricted = FakePdfDocument.WithPages(3);
        restricted.Features = new PdfDocumentFeatures("1.7", 6, 0xFFFFF4C0, 0, PdfFormType.None, false, 0xFFFFF4C0);
        var (viewModel, path) = await CreateWithFileAsync("mật.pdf", restricted);
        var viewOnly = viewModel.ActiveDocument!;
        viewOnly.IsReadOnly.ShouldBeTrue();
        _workspaces.OpenUnlockedAsync(path, Arg.Any<ProtectionUnlock>(), Arg.Any<CancellationToken>())
            .Returns(async _ => new UnlockResult(await _fixture.CreateAsync(FakePdfDocument.WithPages(3), path), null));
        Task? unlocking = null;
        _pageDialogs.ShowSecurity(Arg.Any<SecurityViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<SecurityViewModel>();
            dialog.IsViewOnly.ShouldBeTrue();
            dialog.SetUnlockPassword("chu-so-huu");
            unlocking = dialog.UnlockCommand.ExecuteAsync(null);
            return true;
        });

        viewModel.ProtectCommand.Execute(null);
        await unlocking.ShouldNotBeNull();

        await _workspaces.Received(1).OpenUnlockedAsync(path, new ProtectionUnlock("chu-so-huu", null, HasOpenPassword: false), Arg.Any<CancellationToken>());
        var unlocked = viewModel.Documents.ShouldHaveSingleItem();
        unlocked.ShouldNotBeSameAs(viewOnly);
        unlocked.IsReadOnly.ShouldBeFalse();
        viewModel.ActiveDocument.ShouldBeSameAs(unlocked);
    }

    [Fact]
    public async Task AWrongOwnerPassword_LeavesTheTabAsItWas_AndExplains()
    {
        var restricted = FakePdfDocument.WithPages(2);
        restricted.Features = new PdfDocumentFeatures("1.7", 6, 0xFFFFF4C0, 0, PdfFormType.None, false, 0xFFFFF4C0);
        var (viewModel, path) = await CreateWithFileAsync("mật.pdf", restricted);
        var viewOnly = viewModel.ActiveDocument!;
        _workspaces.OpenUnlockedAsync(path, Arg.Any<ProtectionUnlock>(), Arg.Any<CancellationToken>()).Returns(new UnlockResult(null, UnlockFailure.WrongPassword));
        SecurityViewModel? shown = null;
        Task? unlocking = null;
        _pageDialogs.ShowSecurity(Arg.Any<SecurityViewModel>()).Returns(call =>
        {
            shown = call.Arg<SecurityViewModel>();
            shown.SetUnlockPassword("sai");
            unlocking = shown.UnlockCommand.ExecuteAsync(null);
            return false;
        });

        viewModel.ProtectCommand.Execute(null);
        await unlocking.ShouldNotBeNull();

        shown!.Error.ShouldBe(Resources.Strings.Security_Unlock_Wrong);
        viewModel.Documents.ShouldHaveSingleItem().ShouldBeSameAs(viewOnly);
    }
}
