namespace PdfPro.Core.Security;

/// <summary>
/// The protection a document is saved with, without its passwords: they stay in memory, under
/// <see cref="PasswordsId"/>, so this record can be kept in the undo history and in the session manifest. After a
/// crash the passwords are gone and the document asks for them again before it is saved (never saved unprotected).
/// </summary>
/// <param name="PasswordsId">Where the passwords are kept in memory.</param>
/// <param name="Algorithm">The encryption.</param>
/// <param name="Permissions">What may be done without the owner password.</param>
/// <param name="HasOpenPassword">Opening the saved file takes a password.</param>
public sealed record ProtectionSetting(Guid PasswordsId, EncryptionAlgorithm Algorithm, DocumentPermissions Permissions, bool HasOpenPassword)
{
    /// <summary>
    /// The protection a file has, kept when it is saved again: AES-128 for revision 4 (AES-128, or RC4 in old files),
    /// AES-256 otherwise (revision 5 and 6; RC4 revisions 2 and 3 become AES-256).
    /// </summary>
    /// <param name="passwordsId">Where its passwords are kept.</param>
    /// <param name="securityRevision">The revision of its standard security handler (/R).</param>
    /// <param name="permissions">What it permits without the owner password.</param>
    /// <param name="hasOpenPassword">Opening it takes a password.</param>
    public static ProtectionSetting OfFile(Guid passwordsId, int securityRevision, DocumentPermissions permissions, bool hasOpenPassword)
        => new(passwordsId, securityRevision == 4 ? EncryptionAlgorithm.Aes128 : EncryptionAlgorithm.Aes256, permissions, hasOpenPassword);

    /// <summary>The full protection with its passwords.</summary>
    /// <exception cref="ArgumentException">The passwords do not fit (no owner password, or no open password although one is needed).</exception>
    public DocumentSecurity WithPasswords(string? openPassword, string ownerPassword)
    {
        if (HasOpenPassword == string.IsNullOrEmpty(openPassword))
        {
            throw new ArgumentException(HasOpenPassword ? "An open password is needed." : "This protection has no open password.", nameof(openPassword));
        }

        var security = new DocumentSecurity(HasOpenPassword ? openPassword : null, ownerPassword, Algorithm, Permissions);
        return security.Problem() is { } problem ? throw new ArgumentException(problem, nameof(ownerPassword)) : security;
    }
}
