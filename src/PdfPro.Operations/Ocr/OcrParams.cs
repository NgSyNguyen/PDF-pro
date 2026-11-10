using System.ComponentModel.DataAnnotations;

namespace PdfPro.Operations.Ocr;

/// <summary>What OCR does with pages that already have text.</summary>
public enum ExistingTextMode
{
    /// <summary>Pages with any text (painted or hidden) are left as they are (the default).</summary>
    Skip,

    /// <summary>
    /// Hidden text left by an earlier OCR is removed and those pages are recognised again; pages with painted text are
    /// left as they are.
    /// </summary>
    Replace,

    /// <summary>
    /// Every page is recognised, hidden text removed first — for pages whose painted text cannot be copied (fonts
    /// without a Unicode map); the painted text stays.
    /// </summary>
    All,
}

/// <summary>Parameters of ocr.run.</summary>
/// <param name="Languages">Trained data to read with, main language first ("vie"; "vie", "eng").</param>
/// <param name="ExistingText">What to do with pages that already have text.</param>
/// <param name="Pages">The pages to recognise (0-based); null for all.</param>
/// <param name="Parallelism">How many pages are recognised at the same time; 0 for the number of processors less one.</param>
/// <param name="Dpi">The resolution pages are rendered at for OCR.</param>
/// <param name="AutoRotate">Recognise pages that are turned (90°, 180°, 270°) the right way up (the page itself is not turned).</param>
/// <param name="Deskew">Straighten the picture OCR reads when its lines are tilted (1.5° or more).</param>
/// <param name="StraightenPages">Also straighten the pages themselves (their content is turned; images are not changed).</param>
/// <param name="ConfidenceThreshold">Pages whose mean confidence (0–100) is below this are reported for checking.</param>
/// <param name="Hocr">Also keep the hOCR of every page in the report.</param>
public sealed record OcrParams(
    [property: Required, MinLength(1)] IReadOnlyList<string> Languages,
    ExistingTextMode ExistingText = ExistingTextMode.Skip,
    IReadOnlyList<int>? Pages = null,
    [property: Range(0, 64)] int Parallelism = 0,
    [property: Range(150, 600)] int Dpi = 300,
    bool AutoRotate = true,
    bool Deskew = true,
    bool StraightenPages = false,
    [property: Range(0.0, 100.0)] double ConfidenceThreshold = 60,
    bool Hocr = false)
{
    /// <summary>The number of pages recognised at once.</summary>
    public int Workers => Parallelism > 0 ? Parallelism : Math.Max(1, Environment.ProcessorCount - 1);
}

/// <summary>What happened to a page.</summary>
public enum OcrPageOutcome
{
    /// <summary>Its text was recognised and laid over it.</summary>
    Recognized,

    /// <summary>It already had text and was left as it was.</summary>
    HadText,

    /// <summary>It is blank: there was nothing to recognise.</summary>
    Blank,

    /// <summary>Recognition found no words.</summary>
    NoText,

    /// <summary>The engine failed on it (or took too long); it was left as it was.</summary>
    Failed,
}

/// <summary>One page of an OCR run.</summary>
/// <param name="PageIndex">The page (0-based).</param>
/// <param name="Outcome">What happened to it.</param>
/// <param name="MeanConfidence">The mean confidence of its words (0–100), when recognised.</param>
/// <param name="Words">How many words were recognised.</param>
/// <param name="Rotation">Degrees the page's picture was turned clockwise to read it upright (0, 90, 180, 270).</param>
/// <param name="Skew">The tilt the picture was straightened by (degrees, rising to the right), or 0.</param>
/// <param name="Text">The recognised text (NFC), empty when none.</param>
/// <param name="Hocr">The page's hOCR when asked for.</param>
public sealed record OcrPageReport(int PageIndex, OcrPageOutcome Outcome, double? MeanConfidence, int Words, int Rotation, double Skew, string Text, string? Hocr = null);

/// <summary>What an OCR run did.</summary>
/// <param name="Pages">Every page asked for, in order.</param>
/// <param name="ConfidenceThreshold">The threshold of the run.</param>
/// <param name="RemovedHiddenText">Pages whose hidden text (of an earlier OCR) was removed.</param>
/// <param name="StraightenedPages">Pages whose content was turned straight.</param>
/// <param name="Duration">How long the run took.</param>
public sealed record OcrReport(IReadOnlyList<OcrPageReport> Pages, double ConfidenceThreshold, IReadOnlyList<int> RemovedHiddenText, IReadOnlyList<int> StraightenedPages, TimeSpan Duration)
{
    public IEnumerable<OcrPageReport> Recognized => Pages.Where(p => p.Outcome == OcrPageOutcome.Recognized);

    /// <summary>Recognised pages whose mean confidence is below the threshold: worth checking.</summary>
    public IEnumerable<OcrPageReport> LowConfidence => Recognized.Where(p => p.MeanConfidence < ConfidenceThreshold);

    /// <summary>Pages read turned (their picture was upside down or sideways).</summary>
    public IEnumerable<OcrPageReport> Turned => Recognized.Where(p => p.Rotation != 0);

    /// <summary>The text of the recognised pages, a form feed between pages (as TXT export writes it).</summary>
    public string Text => string.Join("\f", Pages.Where(p => p.Outcome == OcrPageOutcome.Recognized).Select(p => p.Text));
}
