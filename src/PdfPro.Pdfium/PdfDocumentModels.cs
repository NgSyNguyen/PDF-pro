using PdfPro.Core.Geometry;

namespace PdfPro.Pdfium;

/// <summary>Document information dictionary (strings decoded from UTF-16, dates parsed when valid).</summary>
public sealed record PdfDocumentMetadata(
    string? Title,
    string? Author,
    string? Subject,
    string? Keywords,
    string? Creator,
    string? Producer,
    DateTimeOffset? CreationDate,
    DateTimeOffset? ModificationDate);

public enum PdfFormType
{
    None,
    AcroForm,
    XfaFull,
    XfaForeground,
}

/// <summary>Document-level facts used by the properties dialog and by operations (encryption, signatures…).</summary>
public sealed record PdfDocumentFeatures(
    string FileVersion,
    int SecurityHandlerRevision,
    uint Permissions,
    int SignatureCount,
    PdfFormType FormType,
    bool HasJavaScript)
{
    private const uint PrintPermission = 1 << 2;
    private const uint CopyPermission = 1 << 4;
    private const uint AssemblePermission = 1 << 10;

    public bool IsEncrypted => SecurityHandlerRevision >= 0;

    public bool HasDigitalSignatures => SignatureCount > 0;

    public bool CanPrint => (Permissions & PrintPermission) != 0;

    public bool CanCopyContent => (Permissions & CopyPermission) != 0;

    /// <summary>Pages may be inserted, rotated, deleted or taken into another document (bit 11, "assemble").</summary>
    public bool CanAssemble => (Permissions & AssemblePermission) != 0;
}

/// <summary>Target of a bookmark or link. Coordinates are in PDF user space; null means "keep current".</summary>
public sealed record PdfDestination(int PageIndex, double? Left, double? Top, double? Zoom);

/// <summary>One entry of the outline (bookmark) tree.</summary>
public sealed record PdfBookmark(string Title, PdfDestination? Destination, IReadOnlyList<PdfBookmark> Children);

[Flags]
public enum PdfRenderFlags
{
    None = 0,

    /// <summary>Draw annotation appearances (form fields, comments, stamps).</summary>
    Annotations = 1,

    /// <summary>Sub-pixel (ClearType-like) text; only for opaque, unscaled screen output.</summary>
    LcdText = 2,

    Grayscale = 4,

    /// <summary>Render for printing (print-only annotations, no screen-only content).</summary>
    Printing = 8,
}

/// <summary>What to render. <see cref="Scale"/> is device pixels per PDF point (1.0 = 72 DPI).</summary>
public sealed record PageRenderOptions
{
    public required double Scale { get; init; }

    public PdfRenderFlags Flags { get; init; } = PdfRenderFlags.Annotations;

    /// <summary>Only this part of the full page bitmap (in device pixels); null renders the whole page.</summary>
    public PixelRect? Region { get; init; }

    /// <summary>Extra clockwise quarter turns on top of the page's own /Rotate (used by the page plan).</summary>
    public int ExtraQuarterTurns { get; init; }
}
