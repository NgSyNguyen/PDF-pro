using PdfPro.Core.Security;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Security;

/// <summary>A protected file could not be opened: no password was given, it is wrong, or it is not the owner password.</summary>
public sealed class DocumentPasswordException(DocumentPasswordProblem problem, Exception? innerException = null)
    : Exception($"The document cannot be opened for this: {problem}.", innerException)
{
    public DocumentPasswordProblem Problem { get; } = problem;
}

public enum DocumentPasswordProblem
{
    /// <summary>The file is protected and no password was given, or it is wrong.</summary>
    WrongPassword,

    /// <summary>The open password was given, but changing the protection takes the owner password.</summary>
    OwnerPasswordRequired,
}

/// <summary>
/// Protection of PDF files with PDFsharp's standard security handler (12.6.3): AES-256 (revision 6) or AES-128
/// (revision 4) with an open password, an owner password and permissions, or none at all. Passwords are never logged.
/// </summary>
public static class EncryptionWriter
{
    /// <summary>Encrypts the document when it is saved.</summary>
    public static void Protect(PdfDocument document, DocumentSecurity security)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(security);
        if (security.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(security));
        }

        var permissions = security.Permissions;
        var settings = document.SecuritySettings;
        settings.UserPassword = security.UserPassword ?? string.Empty;
        settings.OwnerPassword = security.OwnerPassword;
        settings.PermitPrint = permissions.HasFlag(DocumentPermissions.Print) || permissions.HasFlag(DocumentPermissions.PrintHighQuality);
        settings.PermitFullQualityPrint = permissions.HasFlag(DocumentPermissions.PrintHighQuality);
        settings.PermitModifyDocument = permissions.HasFlag(DocumentPermissions.Modify);
        settings.PermitExtractContent = permissions.HasFlag(DocumentPermissions.Copy);
        settings.PermitAnnotations = permissions.HasFlag(DocumentPermissions.Annotate);
        settings.PermitFormsFill = permissions.HasFlag(DocumentPermissions.FillForms) || permissions.HasFlag(DocumentPermissions.Annotate);
        settings.PermitAssembleDocument = permissions.HasFlag(DocumentPermissions.Assemble);
        if (security.Algorithm == EncryptionAlgorithm.Aes128)
        {
            document.SecurityHandler.SetEncryptionToV4UsingAES(true);
        }
        else
        {
            document.SecurityHandler.SetEncryptionToV5(true);
        }
    }

    /// <summary>Writes the document without protection when it is saved.</summary>
    public static void Unprotect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
    }

    /// <summary>The permissions of a /P value (12.6.4.2): bits 3 print, 4 modify, 5 copy, 6 annotate, 9 fill, 11 assemble, 12 high-quality print.</summary>
    public static DocumentPermissions PermissionsOf(uint value)
    {
        var permissions = DocumentPermissions.None;
        bool Bit(int n) => (value & (1u << (n - 1))) != 0;
        permissions |= Bit(3) ? DocumentPermissions.Print : 0;
        permissions |= Bit(3) && Bit(12) ? DocumentPermissions.PrintHighQuality : 0;
        permissions |= Bit(4) ? DocumentPermissions.Modify : 0;
        permissions |= Bit(5) ? DocumentPermissions.Copy : 0;
        permissions |= Bit(6) ? DocumentPermissions.Annotate : 0;
        permissions |= Bit(9) ? DocumentPermissions.FillForms : 0;
        permissions |= Bit(11) ? DocumentPermissions.Assemble : 0;
        return permissions;
    }

    /// <summary>
    /// Opens a file to change it: with its password when it is protected — which must then be the owner password, as
    /// changing a protected document takes it.
    /// </summary>
    /// <exception cref="DocumentPasswordException">The password is missing, wrong, or only the open password.</exception>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    public static PdfDocument OpenForChange(string path, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document;
        try
        {
            document = password is null ? PdfReader.Open(path, PdfDocumentOpenMode.Modify) : PdfReader.Open(path, password, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
        {
            // PDFsharp: "To modify the document the owner password is required".
            throw new DocumentPasswordException(DocumentPasswordProblem.OwnerPasswordRequired, ex);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            // "A password is required to open the PDF document." / "The specified password is invalid."
            throw new DocumentPasswordException(DocumentPasswordProblem.WrongPassword, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }

        return document;
    }
}
