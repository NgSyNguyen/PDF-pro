using PDFiumCore;
using PDFiumCore.Delegates;

namespace PdfPro.Pdfium.Native;

/// <summary>
/// Receives the bytes of FPDF_SaveAsCopy and writes them to a .NET stream. PDFiumCore declares the C
/// <c>unsigned long</c> size as 64-bit while it is 32-bit on Windows, so the upper half is masked.
/// </summary>
internal sealed unsafe class PdfFileWriter : IDisposable
{
    private readonly Stream _output;
    private readonly Func_int___IntPtr___IntPtr_ulong _writeBlock; // kept alive while PDFium may call it

    public PdfFileWriter(Stream output)
    {
        _output = output;
        _writeBlock = WriteBlock;
        Native = new FPDF_FILEWRITE_ { Version = 1, WriteBlock = _writeBlock };
    }

    public FPDF_FILEWRITE_ Native { get; }

    /// <summary>The I/O error that stopped the write, if any.</summary>
    public Exception? Error { get; private set; }

    public void Dispose() => Native.Dispose();

    // Called by PDFium on its thread. Must never throw: an exception crossing the native frame kills the process.
    private int WriteBlock(IntPtr self, IntPtr data, ulong size)
    {
        try
        {
            var count = (int)(size & 0xFFFFFFFF);
            if (count > 0)
            {
                _output.Write(new ReadOnlySpan<byte>((void*)data, count));
            }

            return 1;
        }
        catch (Exception ex)
        {
            Error = ex;
            return 0;
        }
    }
}
