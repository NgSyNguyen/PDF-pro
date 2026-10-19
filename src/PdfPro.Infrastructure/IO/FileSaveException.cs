namespace PdfPro.Infrastructure.IO;

public enum FileSaveFailure
{
    /// <summary>Another program has the file open (e.g. a PDF reader) and does not let it be replaced.</summary>
    Locked,

    /// <summary>The file is read-only or the user may not write to its folder.</summary>
    AccessDenied,

    /// <summary>The disk is full.</summary>
    DiskFull,

    /// <summary>Any other I/O failure.</summary>
    Failed,
}

/// <summary>
/// Writing or replacing a file failed; the original file is unchanged. The message never contains the path (logs stay
/// private); the inner exception, which may, must not be logged as text.
/// </summary>
public sealed class FileSaveException : IOException
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorUnableToRemoveReplaced = unchecked((int)0x80070497);

    public FileSaveException(FileSaveFailure failure, Exception innerException)
        : base($"The file could not be saved: {failure}.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        Failure = failure;
        HResult = innerException.HResult;
    }

    public FileSaveFailure Failure { get; }

    /// <summary>Classifies an I/O failure of a save.</summary>
    public static FileSaveException From(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is FileSaveException saveError)
        {
            return saveError;
        }

        var failure = error switch
        {
            UnauthorizedAccessException => FileSaveFailure.AccessDenied,
            IOException { HResult: ErrorSharingViolation or ErrorLockViolation or ErrorUnableToRemoveReplaced } => FileSaveFailure.Locked,
            IOException { HResult: ErrorDiskFull or ErrorHandleDiskFull } => FileSaveFailure.DiskFull,
            _ => FileSaveFailure.Failed,
        };
        return new FileSaveException(failure, error);
    }
}
