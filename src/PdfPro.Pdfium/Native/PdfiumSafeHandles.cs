using System.Runtime.InteropServices;
using PDFiumCore;

namespace PdfPro.Pdfium.Native;

internal enum PdfiumHandleKind
{
    Document,
    Page,
    TextPage,
    Bitmap,
    Annotation,
}

/// <summary>Counts live native handles per kind so tests can prove nothing leaks.</summary>
internal static class PdfiumHandleTracker
{
    private static readonly int[] Open = new int[Enum.GetValues<PdfiumHandleKind>().Length];

    public static int OpenCount(PdfiumHandleKind kind) => Volatile.Read(ref Open[(int)kind]);

    public static int TotalOpen
    {
        get
        {
            var total = 0;
            for (var i = 0; i < Open.Length; i++)
            {
                total += Volatile.Read(ref Open[i]);
            }

            return total;
        }
    }

    public static void Opened(PdfiumHandleKind kind) => Interlocked.Increment(ref Open[(int)kind]);

    public static void Closed(PdfiumHandleKind kind) => Interlocked.Decrement(ref Open[(int)kind]);
}

/// <summary>
/// Base for PDFium handles. The native close always runs on the PDFium thread: inline when disposed there,
/// otherwise posted to the gate (this also covers the finalizer thread).
/// </summary>
internal abstract class PdfiumSafeHandle : SafeHandle
{
    private readonly PdfiumHandleKind _kind;

    protected PdfiumSafeHandle(PdfiumGate gate, IntPtr native, PdfiumHandleKind kind)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (native == IntPtr.Zero)
        {
            throw new ArgumentException("A PDFium handle cannot be null.", nameof(native));
        }

        Gate = gate;
        _kind = kind;
        SetHandle(native);
        PdfiumHandleTracker.Opened(kind);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected PdfiumGate Gate { get; }

    protected sealed override bool ReleaseHandle()
    {
        Gate.Post(() =>
        {
            try
            {
                CloseNative();
            }
            finally
            {
                PdfiumHandleTracker.Closed(_kind);
                OnClosed();
            }
        });
        return true;
    }

    /// <summary>Calls the PDFium close/destroy function. Runs on the PDFium thread.</summary>
    protected abstract void CloseNative();

    /// <summary>Releases what the handle kept alive (parent handles, file streams). Runs on the PDFium thread.</summary>
    protected virtual void OnClosed()
    {
    }
}

internal sealed class DocumentSafeHandle : PdfiumSafeHandle
{
    private readonly PdfFileSource _source;

    public DocumentSafeHandle(PdfiumGate gate, FpdfDocumentT native, PdfFileSource source)
        : base(gate, native.__Instance, PdfiumHandleKind.Document)
    {
        Native = native;
        _source = source;
    }

    public FpdfDocumentT Native { get; }

    protected override void CloseNative() => fpdfview.FPDF_CloseDocument(Native);

    // PDFium may read from the file until the document is closed, so the stream lives exactly as long.
    protected override void OnClosed() => _source.Dispose();
}

/// <summary>A page keeps its document alive (SafeHandle ref count), so pages always close before their document.</summary>
internal sealed class PageSafeHandle : PdfiumSafeHandle
{
    private readonly DocumentSafeHandle _document;
    private readonly bool _documentReferenced;

    public PageSafeHandle(PdfiumGate gate, FpdfPageT native, DocumentSafeHandle document)
        : base(gate, native.__Instance, PdfiumHandleKind.Page)
    {
        Native = native;
        _document = document;
        document.DangerousAddRef(ref _documentReferenced);
    }

    public FpdfPageT Native { get; }

    protected override void CloseNative() => fpdfview.FPDF_ClosePage(Native);

    protected override void OnClosed()
    {
        if (_documentReferenced)
        {
            _document.DangerousRelease();
        }
    }
}

internal sealed class TextPageSafeHandle : PdfiumSafeHandle
{
    private readonly PageSafeHandle _page;
    private readonly bool _pageReferenced;

    public TextPageSafeHandle(PdfiumGate gate, FpdfTextpageT native, PageSafeHandle page)
        : base(gate, native.__Instance, PdfiumHandleKind.TextPage)
    {
        Native = native;
        _page = page;
        page.DangerousAddRef(ref _pageReferenced);
    }

    public FpdfTextpageT Native { get; }

    protected override void CloseNative() => fpdf_text.FPDFTextClosePage(Native);

    protected override void OnClosed()
    {
        if (_pageReferenced)
        {
            _page.DangerousRelease();
        }
    }
}

/// <summary>An annotation of a page; keeps its page alive (SafeHandle ref count) like a text page does.</summary>
internal sealed class AnnotationSafeHandle : PdfiumSafeHandle
{
    private readonly PageSafeHandle _page;
    private readonly bool _pageReferenced;

    public AnnotationSafeHandle(PdfiumGate gate, FpdfAnnotationT native, PageSafeHandle page)
        : base(gate, native.__Instance, PdfiumHandleKind.Annotation)
    {
        Native = native;
        _page = page;
        page.DangerousAddRef(ref _pageReferenced);
    }

    public FpdfAnnotationT Native { get; }

    protected override void CloseNative() => fpdf_annot.FPDFPageCloseAnnot(Native);

    protected override void OnClosed()
    {
        if (_pageReferenced)
        {
            _page.DangerousRelease();
        }
    }
}

internal sealed class BitmapSafeHandle : PdfiumSafeHandle
{
    public BitmapSafeHandle(PdfiumGate gate, FpdfBitmapT native)
        : base(gate, native.__Instance, PdfiumHandleKind.Bitmap)
    {
        Native = native;
    }

    public FpdfBitmapT Native { get; }

    protected override void CloseNative() => fpdfview.FPDFBitmapDestroy(Native);
}
