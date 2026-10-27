using PDFiumCore;
using PdfPro.Core.Annotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium.Native;

namespace PdfPro.Pdfium;

/// <summary>A loaded page. Gate-affine: use only on the PDFium thread, dispose when done.</summary>
public sealed class PdfiumPage : IDisposable
{
    private readonly PdfiumGate _gate;
    private readonly PageSafeHandle _handle;

    internal PdfiumPage(PdfiumGate gate, PageSafeHandle handle, int index)
    {
        _gate = gate;
        _handle = handle;
        Index = index;
    }

    public int Index { get; }

    internal FpdfPageT Native
    {
        get
        {
            _gate.VerifyAccess();
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.Native;
        }
    }

    internal PageSafeHandle Handle => _handle;

    /// <summary>Size in points as displayed (after /Rotate, limited to the visible box).</summary>
    public PdfSize DisplaySize
    {
        get
        {
            var native = Native;
            return new PdfSize(fpdfview.FPDF_GetPageWidthF(native), fpdfview.FPDF_GetPageHeightF(native));
        }
    }

    /// <summary>The visible box exactly as PDFium displays it (CropBox ∩ MediaBox) and the page's /Rotate.</summary>
    public PageGeometry Geometry
    {
        get
        {
            var native = Native;
            using var box = new FS_RECTF_();
            var rotation = fpdf_edit.FPDFPageGetRotation(native) * 90;
            if (fpdfview.FPDF_GetPageBoundingBox(native, box) == 0)
            {
                // Should not happen for a loaded page; fall back to the display size at the origin.
                var size = DisplaySize;
                var unrotated = rotation is 90 or 270 ? new PdfSize(size.Height, size.Width) : size;
                return new PageGeometry(new PdfRect(0, 0, unrotated.Width, unrotated.Height), rotation);
            }

            return new PageGeometry(new PdfRect(box.Left, box.Bottom, box.Right, box.Top), rotation);
        }
    }

    /// <summary>The raw /MediaBox, or null when the page dictionary has none.</summary>
    public PdfRect? MediaBox
    {
        get
        {
            float left = 0, bottom = 0, right = 0, top = 0;
            return fpdf_transformpage.FPDFPageGetMediaBox(Native, ref left, ref bottom, ref right, ref top) != 0
                ? new PdfRect(left, bottom, right, top)
                : null;
        }
    }

    /// <summary>The raw /CropBox, or null when the page dictionary has none.</summary>
    public PdfRect? CropBox
    {
        get
        {
            float left = 0, bottom = 0, right = 0, top = 0;
            return fpdf_transformpage.FPDFPageGetCropBox(Native, ref left, ref bottom, ref right, ref top) != 0
                ? new PdfRect(left, bottom, right, top)
                : null;
        }
    }

    /// <summary>PDFium's own page→device mapping; used by tests to validate PageCoordinateMapper.</summary>
    internal (int X, int Y) PageToDevice(int sizeX, int sizeY, int quarterTurns, double x, double y)
    {
        int deviceX = 0, deviceY = 0;
        fpdfview.FPDF_PageToDevice(Native, 0, 0, sizeX, sizeY, quarterTurns, x, y, ref deviceX, ref deviceY);
        return (deviceX, deviceY);
    }

    /// <summary>Loads the text layer of the page. Dispose the result.</summary>
    public PdfiumTextPage LoadText()
    {
        var native = fpdf_text.FPDFTextLoadPage(Native);
        if (native is null || native.__Instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("PDFium could not load the text layer of the page.");
        }

        return new PdfiumTextPage(_gate, new TextPageSafeHandle(_gate, native, _handle));
    }

    /// <summary>
    /// Every annotation of the page in /Annots order: kind, place, name, author, comment, date, the annotation it
    /// answers and the review state it sets, flags, and the marked lines of text markup. Text is not normalized.
    /// </summary>
    public IReadOnlyList<FileAnnotation> GetAnnotations()
    {
        var native = Native;
        var count = fpdf_annot.FPDFPageGetAnnotCount(native);
        var result = new List<FileAnnotation>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            using var annotation = OpenAnnotation(fpdf_annot.FPDFPageGetAnnot(native, i));
            if (annotation is null)
            {
                continue;
            }

            var handle = annotation.Native;
            var kind = fpdf_annot.FPDFAnnotGetSubtype(handle) is var subtype && Enum.IsDefined((FileAnnotationKind)subtype) ? (FileAnnotationKind)subtype : FileAnnotationKind.Unknown;
            using var rect = new FS_RECTF_();
            var bounds = fpdf_annot.FPDFAnnotGetRect(handle, rect) != 0 ? new PdfRect(rect.Left, rect.Bottom, rect.Right, rect.Top) : default;
            var modified = PdfDate.TryParse(ReadString(handle, "M"), out var date) ? date : (DateTimeOffset?)null;
            int? inReplyTo = null;
            using (var parent = OpenAnnotation(fpdf_annot.FPDFAnnotGetLinkedAnnot(handle, "IRT")))
            {
                if (parent is not null && fpdf_annot.FPDFPageGetAnnotIndex(native, parent.Native) is var index and >= 0)
                {
                    inReplyTo = index;
                }
            }

            result.Add(new FileAnnotation(
                i,
                kind,
                bounds,
                ReadString(handle, "NM"),
                ReadString(handle, "T"),
                ReadString(handle, "Contents"),
                modified,
                inReplyTo,
                ReadString(handle, "State"),
                ReadString(handle, "StateModel"),
                fpdf_annot.FPDFAnnotGetFlags(handle),
                ReadQuads(handle)));
        }

        return result;
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>Wraps an annotation handle (null for none) so it is always closed.</summary>
    private AnnotationSafeHandle? OpenAnnotation(FpdfAnnotationT? annotation)
        => annotation is null || annotation.__Instance == IntPtr.Zero ? null : new AnnotationSafeHandle(_gate, annotation, _handle);

    /// <summary>A string entry of an annotation's dictionary (UTF-16), or null when absent or empty.</summary>
    private static string? ReadString(FpdfAnnotationT annotation, string key)
    {
        ushort probe = 0;
        var length = (long)(fpdf_annot.FPDFAnnotGetStringValue(annotation, key, ref probe, 0) & 0xFFFFFFFF);
        if (length <= 2 || length > MaxStringBytes)
        {
            return null;
        }

        var buffer = new ushort[(length + 1) / 2];
        var written = (long)(fpdf_annot.FPDFAnnotGetStringValue(annotation, key, ref buffer[0], (ulong)length) & 0xFFFFFFFF);
        var chars = (int)(Math.Min(written, length) / 2) - 1; // drop the terminator
        if (chars <= 0)
        {
            return null;
        }

        var text = new char[chars];
        for (var i = 0; i < chars; i++)
        {
            text[i] = (char)buffer[i];
        }

        return new string(text);
    }

    private static IReadOnlyList<TextQuad> ReadQuads(FpdfAnnotationT annotation)
    {
        if (fpdf_annot.FPDFAnnotHasAttachmentPoints(annotation) == 0)
        {
            return [];
        }

        var count = (int)Math.Min(fpdf_annot.FPDFAnnotCountAttachmentPoints(annotation), MaxQuads);
        var quads = new List<TextQuad>(count);
        for (var i = 0; i < count; i++)
        {
            using var quad = new FS_QUADPOINTSF();
            if (fpdf_annot.FPDFAnnotGetAttachmentPoints(annotation, (ulong)i, quad) != 0)
            {
                // /QuadPoints order: upper left, upper right, lower left, lower right.
                quads.Add(new TextQuad(new PdfPoint(quad.X1, quad.Y1), new PdfPoint(quad.X2, quad.Y2), new PdfPoint(quad.X3, quad.Y3), new PdfPoint(quad.X4, quad.Y4)));
            }
        }

        return quads;
    }

    /// <summary>Upper bound for a string read from a document (defends against hostile files).</summary>
    private const int MaxStringBytes = 1 << 20;

    /// <summary>Upper bound for the marked lines of one annotation.</summary>
    private const int MaxQuads = 10_000;
}
