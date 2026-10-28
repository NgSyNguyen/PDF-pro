using PdfPro.Core.Security;

namespace PdfPro.Core.Tests.Security;

public sealed class ProtectionSettingTests
{
    [Theory]
    [InlineData(6, EncryptionAlgorithm.Aes256)]
    [InlineData(5, EncryptionAlgorithm.Aes256)]
    [InlineData(4, EncryptionAlgorithm.Aes128)]
    [InlineData(3, EncryptionAlgorithm.Aes256)]
    [InlineData(2, EncryptionAlgorithm.Aes256)]
    public void AFilesProtection_IsKept_RC4BecomingAes256(int revision, EncryptionAlgorithm expected)
        => ProtectionSetting.OfFile(Guid.NewGuid(), revision, DocumentPermissions.Print, hasOpenPassword: false).Algorithm.ShouldBe(expected);

    [Fact]
    public void WithPasswords_GivesTheFullProtection()
    {
        var setting = new ProtectionSetting(Guid.NewGuid(), EncryptionAlgorithm.Aes128, DocumentPermissions.Copy, HasOpenPassword: true);

        var security = setting.WithPasswords("mo", "chu");

        security.ShouldBe(new DocumentSecurity("mo", "chu", EncryptionAlgorithm.Aes128, DocumentPermissions.Copy));
    }

    [Fact]
    public void WithPasswords_RefusesPasswordsThatDoNotFit()
    {
        var withOpen = new ProtectionSetting(Guid.NewGuid(), EncryptionAlgorithm.Aes256, DocumentPermissions.All, HasOpenPassword: true);
        var withoutOpen = withOpen with { HasOpenPassword = false };

        Should.Throw<ArgumentException>(() => withOpen.WithPasswords(null, "chu"));
        Should.Throw<ArgumentException>(() => withoutOpen.WithPasswords("mo", "chu"));
        Should.Throw<ArgumentException>(() => withOpen.WithPasswords("giong", "giong"));
        withoutOpen.WithPasswords(null, "chu").UserPassword.ShouldBeNull();
    }

    [Fact]
    public void PermissionBits_ReadTheFlagsOfP()
    {
        PermissionBits.ToPermissions(uint.MaxValue).ShouldBe(DocumentPermissions.All);
        PermissionBits.ToPermissions(0xFFFFF0C0).ShouldBe(DocumentPermissions.None);
        PermissionBits.ToPermissions(0xFFFFF0C4).ShouldBe(DocumentPermissions.Print);
        PermissionBits.ToPermissions(0xFFFFF8C4).ShouldBe(DocumentPermissions.Print | DocumentPermissions.PrintHighQuality);
        PermissionBits.ToPermissions(0xFFFFF0D0).ShouldBe(DocumentPermissions.Copy);
        PermissionBits.ToPermissions(0xFFFFF4C0).ShouldBe(DocumentPermissions.Assemble);
        PermissionBits.ToPermissions(0xFFFFF1C0).ShouldBe(DocumentPermissions.FillForms);
    }
}
