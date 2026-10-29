using PdfPro.App.Resources;
using PdfPro.App.Services.Security;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Security;

namespace PdfPro.App.Tests.ViewModels;

public sealed class SecurityViewModelTests
{
    private static readonly ProtectionSetting Current = new(Guid.NewGuid(), EncryptionAlgorithm.Aes256, DocumentPermissions.Print | DocumentPermissions.FillForms, HasOpenPassword: true);

    [Fact]
    public void AnUnprotectedDocument_StartsWithoutProtection_AndSensiblePermissions()
    {
        var dialog = new SecurityViewModel(current: null, known: null);

        dialog.Protect.ShouldBeFalse();
        dialog.IsUnprotected.ShouldBeTrue();
        dialog.RequireOpenPassword.ShouldBeTrue();
        dialog.Algorithm.ShouldBe(EncryptionAlgorithm.Aes256);
        dialog.Print.ShouldBe(PrintAllowance.HighQuality);
        dialog.AllowFillForms.ShouldBeTrue();
        dialog.AllowCopy.ShouldBeFalse();
        dialog.IsEditable.ShouldBeTrue();
    }

    [Fact]
    public void NewPasswords_GiveANewProtection_WithThePermissionsChosen()
    {
        var dialog = new SecurityViewModel(current: null, known: null) { Protect = true, Print = PrintAllowance.LowQuality, AllowCopy = true, AllowFillForms = false };
        Type(dialog, open: "Mở-cửa#2026!", owner: "Chủ-sở-hữu!789x");
        var closed = Closed(dialog);

        dialog.ApplyCommand.Execute(null);

        closed().ShouldBe(true);
        var result = dialog.Result.ShouldNotBeNull();
        result.KeepsSetting.ShouldBeFalse();
        result.Setting.ShouldNotBeNull().Permissions.ShouldBe(DocumentPermissions.Print | DocumentPermissions.Copy);
        result.Setting.HasOpenPassword.ShouldBeTrue();
        result.Passwords.ShouldBe(new DocumentPasswords("Mở-cửa#2026!", "Chủ-sở-hữu!789x"));
        result.ToString().ShouldNotContain("Chủ-sở-hữu");
    }

    [Theory]
    [InlineData("", "", "Chu-so-huu!789x", "Chu-so-huu!789x", nameof(Strings.Security_Error_OpenMissing))]
    [InlineData("Mo-cua#2026!", "Mo-cua#2026?", "Chu-so-huu!789x", "Chu-so-huu!789x", nameof(Strings.Security_Error_OpenMismatch))]
    [InlineData("Mo-cua#2026!", "Mo-cua#2026!", "", "", nameof(Strings.Security_Error_OwnerMissing))]
    [InlineData("Mo-cua#2026!", "Mo-cua#2026!", "Chu-so-huu!789x", "khac", nameof(Strings.Security_Error_OwnerMismatch))]
    [InlineData("Giong-nhau#2026", "Giong-nhau#2026", "Giong-nhau#2026", "Giong-nhau#2026", nameof(Strings.Security_Error_Same))]
    public void Mistakes_AreExplained_AndNothingIsTaken(string open, string openAgain, string owner, string ownerAgain, string expected)
    {
        var dialog = new SecurityViewModel(current: null, known: null) { Protect = true };
        dialog.SetOpenPassword(open);
        dialog.SetOpenConfirmation(openAgain);
        dialog.SetOwnerPassword(owner);
        dialog.SetOwnerConfirmation(ownerAgain);

        dialog.ApplyCommand.Execute(null);

        dialog.Error.ShouldBe(Strings.ResourceManager.GetString(expected, Strings.Culture));
        dialog.Result.ShouldBeNull();
    }

    [Fact]
    public void Aes128_RefusesVietnamesePasswords()
    {
        var dialog = new SecurityViewModel(current: null, known: null) { Protect = true, Algorithm = EncryptionAlgorithm.Aes128 };
        Type(dialog, open: "Mật-khẩu#2026", owner: "Chu-so-huu!789x");

        dialog.ApplyCommand.Execute(null);

        dialog.Error.ShouldBe(Strings.Security_Error_Aes128);
        dialog.Result.ShouldBeNull();
    }

    [Fact]
    public void AWeakPassword_IsWarnedAboutOnce_ThenTaken()
    {
        var dialog = new SecurityViewModel(current: null, known: null) { Protect = true, RequireOpenPassword = true };
        Type(dialog, open: "123456", owner: "Chu-so-huu!789x");

        dialog.OpenStrength.IsWeak.ShouldBeTrue();
        dialog.OpenStrength.Text.ShouldBe(Strings.Security_Strength_VeryWeak);
        dialog.OpenStrength.Advice.ShouldBe(Strings.Security_Advice_Common);
        dialog.ApplyCommand.Execute(null);
        dialog.Error.ShouldBe(Strings.Security_Warning_Weak);
        dialog.Result.ShouldBeNull();

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldNotBeNull().Passwords!.Open.ShouldBe("123456");
    }

    [Fact]
    public void EmptyBoxes_KeepTheKnownPasswords_AndAnUnchangedChoiceChangesNothing()
    {
        var dialog = new SecurityViewModel(Current, new DocumentPasswords("Mo-cua#2026!", "Chu-so-huu!789x"));

        dialog.Protect.ShouldBeTrue();
        dialog.KeepsKnownOwnerPassword.ShouldBeTrue();
        dialog.ApplyCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.KeepsSetting.ShouldBeTrue();
        result.Setting.ShouldBe(Current);
        result.Passwords.ShouldBeNull();
    }

    [Fact]
    public void ChangedPermissions_WithTheKnownPasswords_GiveANewProtection()
    {
        var dialog = new SecurityViewModel(Current, new DocumentPasswords("Mo-cua#2026!", "Chu-so-huu!789x")) { AllowCopy = true };

        dialog.ApplyCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.KeepsSetting.ShouldBeFalse();
        result.Setting!.Permissions.ShouldBe(Current.Permissions | DocumentPermissions.Copy);
        result.Passwords.ShouldBe(new DocumentPasswords("Mo-cua#2026!", "Chu-so-huu!789x"));
    }

    [Fact]
    public void MissingOpenPassword_OfTheSameProtection_IsOnlyStored()
    {
        // Opened with its owner password: the open password is unknown and saving asks for it.
        var dialog = new SecurityViewModel(Current, new DocumentPasswords(null, "Chu-so-huu!789x"), needsPasswords: true);
        dialog.KeepsKnownOpenPassword.ShouldBeFalse();
        dialog.ApplyCommand.Execute(null);
        dialog.Error.ShouldBe(Strings.Security_Error_OpenMissing);

        dialog.SetOpenPassword("Mo-cua#2026!");
        dialog.SetOpenConfirmation("Mo-cua#2026!");
        dialog.ApplyCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.KeepsSetting.ShouldBeTrue();
        result.Setting.ShouldBe(Current);
        result.Passwords.ShouldBe(new DocumentPasswords("Mo-cua#2026!", "Chu-so-huu!789x"));
    }

    [Fact]
    public void NoProtection_TakesItOff()
    {
        var dialog = new SecurityViewModel(Current, new DocumentPasswords("Mo-cua#2026!", "Chu-so-huu!789x")) { IsUnprotected = true };

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBe(new ProtectionChoice(null, null, KeepsSetting: false));
    }

    [Fact]
    public async Task AViewOnlyDocument_IsUnlockedWithTheOwnerPassword()
    {
        var tried = new List<string>();
        var dialog = new SecurityViewModel(DocumentPermissions.Assemble, password =>
        {
            tried.Add(password);
            return Task.FromResult(password == "chu" ? null : Strings.Security_Unlock_Wrong);
        });
        var closed = Closed(dialog);

        dialog.IsViewOnly.ShouldBeTrue();
        dialog.FilePermissionsText.ShouldNotBeNull().ShouldContain("✗ " + Strings.Security_Print_None);
        dialog.FilePermissionsText.ShouldContain("✓ " + Strings.Security_AllowAssemble);
        dialog.UnlockCommand.CanExecute(null).ShouldBeFalse("nothing typed yet");

        dialog.SetUnlockPassword("sai");
        await dialog.UnlockCommand.ExecuteAsync(null);
        dialog.Error.ShouldBe(Strings.Security_Unlock_Wrong);
        closed().ShouldBeNull();

        dialog.SetUnlockPassword("chu");
        await dialog.UnlockCommand.ExecuteAsync(null);

        tried.ShouldBe(["sai", "chu"]);
        closed().ShouldBe(true);
        dialog.Error.ShouldBeNull();
    }

    [Fact]
    public void Strength_IsShownAsTyped()
    {
        var dialog = new SecurityViewModel(current: null, known: null) { Protect = true };

        dialog.SetOwnerPassword("ngựa-xanh-đá-cuội-97");

        dialog.OwnerStrength.Level.ShouldBe(5);
        dialog.OwnerStrength.Text.ShouldBe(Strings.Security_Strength_VeryStrong);
        dialog.OwnerStrength.IsWeak.ShouldBeFalse();
        dialog.SetOwnerPassword(string.Empty);
        dialog.OwnerStrength.Level.ShouldBe(0);
    }

    private static void Type(SecurityViewModel dialog, string open, string owner)
    {
        dialog.SetOpenPassword(open);
        dialog.SetOpenConfirmation(open);
        dialog.SetOwnerPassword(owner);
        dialog.SetOwnerConfirmation(owner);
    }

    private static Func<bool?> Closed(SecurityViewModel dialog)
    {
        bool? accepted = null;
        dialog.CloseRequested += (_, value) => accepted = value;
        return () => accepted;
    }
}
