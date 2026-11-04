using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;

namespace PdfPro.Pdfium;

/// <summary>A signature dictionary of the document as PDFium reads it.</summary>
/// <param name="ByteRange">The ranges of the file the signature covers (pairs of offset and length).</param>
/// <param name="Contents">The signature itself (a CMS object for adbe.pkcs7.detached), padding included.</param>
/// <param name="SubFilter">The encoding of the signature ("adbe.pkcs7.detached", "ETSI.CAdES.detached"…).</param>
/// <param name="Reason">Why it was signed, as the signer wrote it.</param>
/// <param name="Time">When, as written in /M (a PDF date), or null.</param>
public sealed record PdfSignatureData(IReadOnlyList<long> ByteRange, byte[] Contents, string SubFilter, string? Reason, string? Time);

/// <summary>Reading digital signatures through PDFium.</summary>
public sealed partial class PdfiumDocument
{
    /// <summary>Upper bound for the bytes of one signature (defends against hostile files).</summary>
    private const int MaxSignatureBytes = 16 << 20;

    /// <summary>Every signature of the document, in the order of its fields.</summary>
    public unsafe IReadOnlyList<PdfSignatureData> GetSignatures()
    {
        var native = Native;
        var count = Math.Max(0, fpdf_signature.FPDF_GetSignatureCount(native));
        var signatures = new List<PdfSignatureData>(count);
        for (var i = 0; i < count; i++)
        {
            var signature = fpdf_signature.FPDF_GetSignatureObject(native, i);
            if (signature is null || signature.__Instance == IntPtr.Zero)
            {
                continue;
            }

            signatures.Add(new PdfSignatureData(ByteRange(signature), Contents(signature), Ascii(signature, fpdf_signature.FPDFSignatureObjGetSubFilter), Reason(signature), Ascii(signature, fpdf_signature.FPDFSignatureObjGetTime)));
        }

        return signatures;
    }

    private static long[] ByteRange(FpdfSignatureT signature)
    {
        var dummy = 0;
        var count = (int)Math.Min(fpdf_signature.FPDFSignatureObjGetByteRange(signature, ref dummy, 0), 64);
        if (count <= 0)
        {
            return [];
        }

        var values = new int[count];
        _ = fpdf_signature.FPDFSignatureObjGetByteRange(signature, ref values[0], (ulong)count);
        return [.. values.Select(v => (long)(uint)v)];
    }

    private static byte[] Contents(FpdfSignatureT signature)
    {
        var length = (long)fpdf_signature.FPDFSignatureObjGetContents(signature, IntPtr.Zero, 0);
        if (length <= 0 || length > MaxSignatureBytes)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            var written = (long)fpdf_signature.FPDFSignatureObjGetContents(signature, buffer, (ulong)length);
            var bytes = new byte[Math.Min(written, length)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? Reason(FpdfSignatureT signature)
    {
        var length = (long)fpdf_signature.FPDFSignatureObjGetReason(signature, IntPtr.Zero, 0);
        if (length <= 2 || length > 1 << 16)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            _ = fpdf_signature.FPDFSignatureObjGetReason(signature, buffer, (ulong)length);
            return Marshal.PtrToStringUni(buffer, (int)(length / 2) - 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private unsafe delegate ulong AsciiReader(FpdfSignatureT signature, sbyte* buffer, ulong length);

    private static unsafe string Ascii(FpdfSignatureT signature, AsciiReader read)
    {
        var length = (long)read(signature, null, 0);
        if (length <= 1 || length > 4096)
        {
            return string.Empty;
        }

        var buffer = new sbyte[length];
        fixed (sbyte* pointer = buffer)
        {
            _ = read(signature, pointer, (ulong)length);
            return Encoding.ASCII.GetString((byte*)pointer, (int)length - 1);
        }
    }
}
