using PdfPro.Core.Security;

namespace PdfPro.Core.Tests.Security;

public sealed class DocumentSecurityTests
{
    [Fact]
    public void ToString_NeverShowsThePasswords()
    {
        var security = new DocumentSecurity("mở-cửa-7", "chủ-sở-hữu-9", Permissions: DocumentPermissions.Print);

        var shown = security.ToString();

        shown.ShouldNotContain("mở-cửa-7");
        shown.ShouldNotContain("chủ-sở-hữu-9");
        shown.ShouldContain("Aes256");
        shown.ShouldContain("Print");
        (security with { Algorithm = EncryptionAlgorithm.Aes128 }).ToString().ShouldNotContain("chủ-sở-hữu-9");
    }

    [Fact]
    public void ADocumentWithoutAnOpenPassword_IsFineWithAnOwnerPassword()
    {
        var security = new DocumentSecurity(null, "chủ-sở-hữu-9");

        security.Problem().ShouldBeNull();
        security.HasUserPassword.ShouldBeFalse();
        security.Algorithm.ShouldBe(EncryptionAlgorithm.Aes256, "AES-256 is the default");
        security.Permissions.ShouldBe(DocumentPermissions.All);
    }

    [Theory]
    [InlineData(null, "", EncryptionAlgorithm.Aes256, "owner password")]
    [InlineData("cung-mot", "cung-mot", EncryptionAlgorithm.Aes256, "differ")]
    [InlineData("mở", "chu", EncryptionAlgorithm.Aes128, "ASCII")]
    [InlineData("abc", "0123456789012345678901234567890123", EncryptionAlgorithm.Aes128, "32")]
    [InlineData("abc", "def", (EncryptionAlgorithm)7, "not valid")]
    public void Problems_AreFound(string? user, string owner, EncryptionAlgorithm algorithm, string expected)
        => new DocumentSecurity(user, owner, algorithm).Problem().ShouldNotBeNull().ShouldContain(expected);

    [Fact]
    public void Passwords_HaveAtMost127Bytes()
    {
        // "ệ" is three bytes in UTF-8: 43 of them are 129 bytes.
        new DocumentSecurity(null, new string('ệ', 42)).Problem().ShouldBeNull();
        new DocumentSecurity(null, new string('ệ', 43)).Problem().ShouldNotBeNull().ShouldContain("127");
        new DocumentSecurity(new string('a', 128), "chu").Problem().ShouldNotBeNull();
    }

    [Fact]
    public void UnknownPermissionBits_AreRejected()
        => new DocumentSecurity(null, "chu", Permissions: (DocumentPermissions)(1 << 12)).Problem().ShouldNotBeNull();
}
