using PDFiumCore;
using PDFiumCore.Delegates;

namespace PdfPro.Pdfium.Native;

/// <summary>
/// Feeds PDFium from a .NET <see cref="FileStream"/> through FPDF_LoadCustomDocument: any path works (long paths,
/// Unicode), other programs may read or rename the file while it is open, but nobody can overwrite it underneath us.
/// A document held in memory is fed the same way.
/// </summary>
/// <remarks>
/// PDFiumCore declares the C <c>unsigned long</c> fields/arguments as 64-bit, but on Windows they are 32-bit,
/// so the upper halves are undefined and must be masked. Files are therefore limited to 4 GB (a PDFium limit too).
/// </remarks>
internal sealed unsafe class PdfFileSource : IDisposable
{
    private readonly Stream _stream;
    private readonly Func_int___IntPtr_ulong_bytePtr_ulong _getBlock;
    private int _disposed;

    private PdfFileSource(Stream stream)
    {
        _stream = stream;
        _getBlock = GetBlock;
        Access = new FPDF_FILEACCESS
        {
            MFileLen = (ulong)stream.Length,
            MGetBlock = _getBlock,
            MParam = IntPtr.Zero,
        };
    }

    public FPDF_FILEACCESS Access { get; }

    /// <summary>The last I/O error raised while PDFium was reading, if any.</summary>
    public Exception? ReadError { get; private set; }

    /// <exception cref="PdfDocumentException">The file cannot be opened.</exception>
    public static PdfFileSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PdfFileNotFoundException(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PdfFileAccessException(PdfOpenFailure.AccessDenied, ex);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new PdfFileAccessException(PdfOpenFailure.FileInUse, ex);
        }
        catch (IOException ex)
        {
            throw new PdfFileAccessException(PdfOpenFailure.ReadFailed, ex);
        }

        if (stream.Length > uint.MaxValue)
        {
            stream.Dispose();
            throw new PdfFileAccessException(PdfOpenFailure.FileTooLarge);
        }

        return new PdfFileSource(stream);
    }

    /// <summary>A document held in memory (read only; the bytes must not change while it is open).</summary>
    /// <exception cref="PdfDocumentException">The document is larger than PDFium reads.</exception>
    public static PdfFileSource FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.LongLength > uint.MaxValue
            ? throw new PdfFileAccessException(PdfOpenFailure.FileTooLarge)
            : new PdfFileSource(new MemoryStream(bytes, writable: false));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stream.Dispose();
        Access.Dispose();
    }

    private static bool IsSharingViolation(IOException exception)
    {
        const int errorSharingViolation = unchecked((int)0x80070020);
        const int errorLockViolation = unchecked((int)0x80070021);
        return exception.HResult is errorSharingViolation or errorLockViolation;
    }

    // Called by PDFium on its thread. Must never throw: an exception crossing the native frame kills the process.
    private int GetBlock(IntPtr param, ulong position, byte* buffer, ulong size)
    {
        try
        {
            var offset = (long)(position & 0xFFFFFFFF);
            var count = (int)(size & 0xFFFFFFFF);
            if (count < 0 || offset + count > _stream.Length)
            {
                return 0;
            }

            _stream.Position = offset;
            _stream.ReadExactly(new Span<byte>(buffer, count));
            return 1;
        }
        catch (Exception ex)
        {
            ReadError = ex;
            return 0;
        }
    }
}
