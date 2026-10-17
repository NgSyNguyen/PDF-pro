namespace PdfPro.Pdfium;

/// <summary>Why a document could not be opened; the UI maps each value to a localized message.</summary>
public enum PdfOpenFailure
{
    FileNotFound,
    AccessDenied,
    FileInUse,
    FileTooLarge,
    ReadFailed,
    PasswordRequired,
    WrongPassword,
    Corrupted,
    UnsupportedSecurity,
}

/// <summary>
/// A document could not be opened. Messages never contain the file path or the password (they end up in logs).
/// </summary>
public class PdfDocumentException : Exception
{
    public PdfDocumentException(PdfOpenFailure failure, string message, Exception? innerException = null, uint? pdfiumErrorCode = null)
        : base(message, innerException)
    {
        Failure = failure;
        PdfiumErrorCode = pdfiumErrorCode;
    }

    public PdfOpenFailure Failure { get; }

    /// <summary>FPDF_GetLastError value, when PDFium reported the failure.</summary>
    public uint? PdfiumErrorCode { get; }
}

public sealed class PdfFileNotFoundException(Exception? innerException = null)
    : PdfDocumentException(PdfOpenFailure.FileNotFound, "The PDF file does not exist.", innerException);

/// <summary>The file exists but cannot be read: access denied, locked by another program, too large or unreadable.</summary>
public sealed class PdfFileAccessException(PdfOpenFailure failure, Exception? innerException = null)
    : PdfDocumentException(failure, $"The PDF file cannot be read ({failure}).", innerException);

public sealed class PdfPasswordRequiredException()
    : PdfDocumentException(PdfOpenFailure.PasswordRequired, "The PDF document is password protected.", pdfiumErrorCode: PdfiumErrors.Password);

public sealed class PdfWrongPasswordException()
    : PdfDocumentException(PdfOpenFailure.WrongPassword, "The password for the PDF document is incorrect.", pdfiumErrorCode: PdfiumErrors.Password);

public sealed class PdfCorruptedFileException(uint? pdfiumErrorCode = null, Exception? innerException = null)
    : PdfDocumentException(PdfOpenFailure.Corrupted, "The file is damaged or is not a PDF document.", innerException, pdfiumErrorCode);

public sealed class PdfUnsupportedSecurityException()
    : PdfDocumentException(PdfOpenFailure.UnsupportedSecurity, "The PDF document uses an unsupported security handler.", pdfiumErrorCode: PdfiumErrors.Security);

/// <summary>FPDF_GetLastError codes (fpdfview.h).</summary>
public static class PdfiumErrors
{
    public const uint Success = 0;
    public const uint Unknown = 1;
    public const uint File = 2;
    public const uint Format = 3;
    public const uint Password = 4;
    public const uint Security = 5;
    public const uint Page = 6;

    /// <summary>Maps a failed load to a typed exception.</summary>
    internal static PdfDocumentException ForLoad(uint errorCode, bool passwordSupplied, Exception? readError) => errorCode switch
    {
        Password when passwordSupplied => new PdfWrongPasswordException(),
        Password => new PdfPasswordRequiredException(),
        Security => new PdfUnsupportedSecurityException(),
        File when readError is not null => new PdfFileAccessException(PdfOpenFailure.ReadFailed, readError),
        _ => new PdfCorruptedFileException(errorCode, readError),
    };
}
