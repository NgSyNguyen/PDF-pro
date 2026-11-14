namespace PdfPro.Operations.Converting;

/// <summary>The PDF/A level a document is converted to (ISO 19005, conformance level B: the look is preserved).</summary>
public enum PdfALevel
{
    /// <summary>PDF/A-2b (ISO 19005-2, on PDF 1.7): transparency, layers and JPEG 2000 allowed. The default.</summary>
    A2B,

    /// <summary>PDF/A-1b (ISO 19005-1, on PDF 1.4): for archives that ask for it; no transparency, no layers.</summary>
    A1B,
}

/// <param name="Level">The level converted to.</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2).pdf").</param>
public sealed record ToPdfAParams(PdfALevel Level = PdfALevel.A2B, bool Overwrite = false);

/// <summary>What the conversion changed in the document to make it PDF/A.</summary>
public enum PdfAFix
{
    /// <summary>The XMP metadata was written anew (PDF/A identification, title, author… agreeing with the document information).</summary>
    Metadata,

    /// <summary>An sRGB output intent was added (it says how the document's colours are meant).</summary>
    OutputIntent,

    /// <summary>The password protection was removed (PDF/A forbids encryption).</summary>
    Decrypted,

    /// <summary>Scripts and the actions that run them (JavaScript, additional actions) were removed.</summary>
    Scripts,

    /// <summary>Forbidden actions were removed: launching programs, resetting or importing forms, hiding, named actions other than page turns…</summary>
    Actions,

    /// <summary>Embedded files and attachment comments were removed.</summary>
    Attachments,

    /// <summary>Sound, video, 3D and rich media (annotations, actions, alternate presentations) were removed.</summary>
    Multimedia,

    /// <summary>Hidden comments were removed (hidden form fields stay, drawn as nothing).</summary>
    HiddenAnnotations,

    /// <summary>Comments were set to print, as PDF/A requires.</summary>
    AnnotationFlags,

    /// <summary>Comments without an appearance got one (a note icon, or an empty one); rollover and down appearances were dropped.</summary>
    AnnotationAppearances,

    /// <summary>Form fields were drawn (their appearance had been left to the viewer).</summary>
    FormAppearances,

    /// <summary>The XFA form was removed (the AcroForm fields stay).</summary>
    Xfa,

    /// <summary>Comments of kinds PDF/A-1 does not know (polygons, carets…) were drawn into the page.</summary>
    FlattenedAnnotations,

    /// <summary>Semi-transparent comments were made opaque (PDF/A-1).</summary>
    AnnotationOpacity,

    /// <summary>Layers: hidden ones removed and the rest made part of the page (PDF/A-1), or their settings completed (PDF/A-2).</summary>
    Layers,

    /// <summary>Streams compressed with LZW were compressed again with Flate.</summary>
    LzwStreams,

    /// <summary>Image smoothing requests were removed.</summary>
    ImageInterpolation,

    /// <summary>Alternate images, OPI references and reference XObjects were removed.</summary>
    AlternateImages,

    /// <summary>PostScript fragments were removed.</summary>
    PostScript,

    /// <summary>Transfer functions were removed from graphics states.</summary>
    TransferFunctions,

    /// <summary>XMP metadata of pages, images and other objects was removed.</summary>
    ObjectMetadata,

    /// <summary>Digital signatures were in the document: the converted copy no longer carries valid ones.</summary>
    SignaturesInvalidated,

    /// <summary>Fonts that were not embedded were embedded from the fonts of this computer of the same name.</summary>
    FontsEmbedded,

    /// <summary>Fonts that were not embedded, and not on this computer, were replaced by a bundled font (Noto Sans or Serif): their text may look different.</summary>
    FontsSubstituted,

    /// <summary>A CMYK colour profile was given to the CMYK colours (PDF/A wants every colour defined).</summary>
    CmykProfile,

    /// <summary>Pages with transparency were made pictures of themselves (PDF/A-1): their text can no longer be selected.</summary>
    PagesAsPictures,
}

/// <param name="Fix">What was changed.</param>
/// <param name="Count">How many things (scripts, comments, streams…) it concerned.</param>
public sealed record PdfAChange(PdfAFix Fix, int Count);

/// <summary>What still keeps the converted document from being PDF/A, as far as PDF_Pro can tell.</summary>
public enum PdfAProblemKind
{
    /// <summary>A font is not embedded and no font program was found for it (<see cref="PdfAProblem.Detail"/>: its name).</summary>
    FontNotEmbedded,

    /// <summary>CMYK colours are used without a colour profile for them.</summary>
    DeviceCmyk,

    /// <summary>Transparency is used (PDF/A-1 forbids it).</summary>
    Transparency,

    /// <summary>JPEG 2000 images are used (PDF/A-1 forbids them).</summary>
    Jpeg2000,

    /// <summary>Images with 16 bits per colour component are used (PDF/A-1 forbids them).</summary>
    SixteenBitImages,

    /// <summary>Streams compressed with LZW could not be decompressed to compress them again.</summary>
    LzwStreams,
}

/// <param name="Kind">The problem.</param>
/// <param name="Pages">The pages it is on (1-based, ascending; empty when it is not on a page).</param>
/// <param name="Detail">A font name, for <see cref="PdfAProblemKind.FontNotEmbedded"/>.</param>
public sealed record PdfAProblem(PdfAProblemKind Kind, IReadOnlyList<int> Pages, string? Detail = null);

/// <summary>A document converted to PDF/A.</summary>
/// <param name="Output">The PDF written.</param>
/// <param name="Level">The level converted to.</param>
/// <param name="PageCount">Its pages.</param>
/// <param name="Changes">What was changed.</param>
/// <param name="Problems">What still keeps it from being PDF/A (PDF_Pro's own checks, not a full validation).</param>
/// <param name="PicturePages">The pages made pictures of themselves (1-based), when <see cref="PdfAFix.PagesAsPictures"/>.</param>
public sealed record PdfAReport(string Output, PdfALevel Level, int PageCount, IReadOnlyList<PdfAChange> Changes, IReadOnlyList<PdfAProblem> Problems, IReadOnlyList<int>? PicturePages = null)
{
    /// <summary>The pages made pictures of themselves (1-based), ascending.</summary>
    public IReadOnlyList<int> PicturePages { get; init; } = PicturePages ?? [];

    /// <summary>Whether PDF_Pro's own checks found something left.</summary>
    public bool HasProblems => Problems.Count > 0;
}
