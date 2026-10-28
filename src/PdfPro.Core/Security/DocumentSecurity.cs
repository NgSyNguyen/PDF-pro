using System.Text;

namespace PdfPro.Core.Security;

/// <summary>How a protected document is encrypted.</summary>
public enum EncryptionAlgorithm
{
    /// <summary>AES with a 256-bit key (PDF 2.0, revision 6): the default.</summary>
    Aes256 = 0,

    /// <summary>AES with a 128-bit key (PDF 1.6): for readers older than Acrobat 9.</summary>
    Aes128 = 1,
}

/// <summary>What people who open a protected document with its open password may do (12.6.4.2, table 22).</summary>
[Flags]
public enum DocumentPermissions
{
    None = 0,

    /// <summary>Print (at low quality unless <see cref="PrintHighQuality"/>).</summary>
    Print = 1 << 0,

    /// <summary>Print at full quality.</summary>
    PrintHighQuality = 1 << 1,

    /// <summary>Change the document's content.</summary>
    Modify = 1 << 2,

    /// <summary>Copy or take out text and pictures.</summary>
    Copy = 1 << 3,

    /// <summary>Add and change comments, fill forms.</summary>
    Annotate = 1 << 4,

    /// <summary>Fill forms (also without <see cref="Annotate"/>).</summary>
    FillForms = 1 << 5,

    /// <summary>Insert, rotate, delete pages, add bookmarks.</summary>
    Assemble = 1 << 6,

    // Reading the text out for accessibility is always allowed: PDF 2.0 deprecates the bit that restricted it.
    All = Print | PrintHighQuality | Modify | Copy | Annotate | FillForms | Assemble,
}

/// <summary>
/// The protection of a document: the password to open it (none: anyone opens it, the permissions still apply), the
/// owner password that lifts every restriction, the algorithm and the permissions. Passwords stay in memory only:
/// they are never logged, written into the session or shown by <see cref="ToString"/>.
/// </summary>
/// <param name="UserPassword">The open password, or null/empty for none.</param>
/// <param name="OwnerPassword">The owner (permissions) password; must differ from the open password.</param>
/// <param name="Algorithm">The encryption.</param>
/// <param name="Permissions">What may be done without the owner password.</param>
public sealed record DocumentSecurity(string? UserPassword, string OwnerPassword, EncryptionAlgorithm Algorithm = EncryptionAlgorithm.Aes256, DocumentPermissions Permissions = DocumentPermissions.All)
{
    /// <summary>The longest password (PDF 2.0 takes up to 127 UTF-8 bytes).</summary>
    public const int MaxPasswordBytes = 127;

    /// <summary>The longest AES-128 password (revision 4 uses 32 bytes).</summary>
    public const int MaxAes128PasswordLength = 32;

    public bool HasUserPassword => !string.IsNullOrEmpty(UserPassword);

    /// <summary>What is wrong (English, for validation), or null.</summary>
    public string? Problem()
    {
        if (string.IsNullOrEmpty(OwnerPassword))
        {
            return "An owner password is needed.";
        }

        if (HasUserPassword && string.Equals(UserPassword, OwnerPassword, StringComparison.Ordinal))
        {
            return "The open password and the owner password must differ.";
        }

        if (Encoding.UTF8.GetByteCount(OwnerPassword) > MaxPasswordBytes || Encoding.UTF8.GetByteCount(UserPassword ?? string.Empty) > MaxPasswordBytes)
        {
            return $"A password has at most {MaxPasswordBytes} bytes.";
        }

        // Revision 4 pads or cuts passwords to 32 bytes of PDFDocEncoding: longer ones would silently lose their end.
        if (Algorithm == EncryptionAlgorithm.Aes128 && ((UserPassword ?? string.Empty) + OwnerPassword).Any(c => c > 0x7E || c < 0x20))
        {
            return "AES-128 passwords can only hold plain ASCII characters.";
        }

        if (Algorithm == EncryptionAlgorithm.Aes128 && Math.Max(OwnerPassword.Length, UserPassword?.Length ?? 0) > MaxAes128PasswordLength)
        {
            return $"An AES-128 password has at most {MaxAes128PasswordLength} characters.";
        }

        return !Enum.IsDefined(Algorithm) || (Permissions & ~DocumentPermissions.All) != 0 ? "The algorithm or permissions are not valid." : null;
    }

    /// <summary>Never shows the passwords.</summary>
    public override string ToString() => $"DocumentSecurity {{ {Algorithm}, open password: {(HasUserPassword ? "yes" : "no")}, permissions: {Permissions} }}";

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(ToString());
        return true;
    }
}
