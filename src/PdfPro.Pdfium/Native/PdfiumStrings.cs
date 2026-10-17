using System.Runtime.InteropServices;
using System.Text;

namespace PdfPro.Pdfium.Native;

/// <summary>Helpers for PDFium APIs that return UTF-16LE strings through a "query size, then fill" protocol.</summary>
internal static class PdfiumStrings
{
    /// <summary>Upper bound for any single string we accept from a document (defends against hostile files).</summary>
    private const int MaxBytes = 1 << 20;

    /// <summary>
    /// Calls <paramref name="read"/> with (buffer, length) twice: first to get the byte length including the
    /// UTF-16 terminator, then to fill the buffer. Returns null when the value is absent or empty.
    /// </summary>
    public static string? ReadUtf16(Func<IntPtr, ulong, ulong> read)
    {
        var length = (long)(read(IntPtr.Zero, 0) & 0xFFFFFFFF);
        if (length <= 2 || length > MaxBytes)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((nint)length);
        try
        {
            var written = (long)(read(buffer, (ulong)length) & 0xFFFFFFFF);
            var bytes = (int)Math.Min(written, length) - 2; // drop the terminator
            if (bytes <= 0)
            {
                return null;
            }

            unsafe
            {
                var text = Encoding.Unicode.GetString((byte*)buffer, bytes);
                return text.Length == 0 ? null : text;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
