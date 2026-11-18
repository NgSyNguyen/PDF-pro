using PdfPro.Core.Imaging;

namespace PdfPro.Core.Ocr;

/// <summary>Which trained data recognises: the most accurate (slower) or the fast one.</summary>
public enum OcrDataKind
{
    /// <summary>tessdata_best: the most accurate models (the default).</summary>
    Best,

    /// <summary>tessdata_fast: several times faster, a little less accurate.</summary>
    Fast,
}

/// <summary>Why recognition could not be done.</summary>
public enum OcrFailure
{
    /// <summary>The OCR program or its trained data is not installed.</summary>
    NotInstalled,

    /// <summary>A language asked for has no trained data (<see cref="OcrException.Detail"/> names it).</summary>
    LanguageMissing,

    /// <summary>The program ran but gave no result.</summary>
    Failed,

    /// <summary>A page took longer than allowed.</summary>
    TimedOut,
}

/// <summary>Recognition could not be done; the app explains <see cref="Failure"/>.</summary>
public sealed class OcrException(OcrFailure failure, string? detail = null, Exception? innerException = null)
    : Exception($"OCR failed: {failure}{(detail is null ? string.Empty : " (" + detail + ")")}.", innerException)
{
    public OcrFailure Failure { get; } = failure;

    /// <summary>What is missing or what the program said (no document content).</summary>
    public string? Detail { get; } = detail;
}

/// <summary>A picture of a page and its resolution.</summary>
/// <param name="Image">The grey pixels.</param>
/// <param name="Dpi">Pixels per inch, the same both ways.</param>
public sealed record OcrImage(GrayImage Image, double Dpi)
{
    /// <summary>The picture's width in points (1/72 inch).</summary>
    public double WidthInPoints => Image.Width * 72.0 / Dpi;

    /// <summary>The picture's height in points.</summary>
    public double HeightInPoints => Image.Height * 72.0 / Dpi;
}

/// <summary>How to recognise a page.</summary>
/// <param name="Languages">Trained data to use, the main language first ("vie", "eng").</param>
public sealed record OcrOptions(IReadOnlyList<string> Languages)
{
    /// <summary>Also write the page as hOCR (HTML with the box of every word).</summary>
    public bool Hocr { get; init; }

    /// <summary>How long one page may take.</summary>
    public TimeSpan PageTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Where temporary files go (a folder of its own is made there and removed); null for the system's.</summary>
    public string? WorkingFolder { get; init; }
}

/// <summary>A box in a picture, in pixels from its top-left corner.</summary>
public readonly record struct OcrBox(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>A recognised word.</summary>
/// <param name="Text">The word (NFC).</param>
/// <param name="Box">Where it is in the picture.</param>
/// <param name="Confidence">How sure the engine is, 0–100.</param>
/// <param name="Block">Its block, paragraph and line, as numbered by the engine.</param>
public sealed record OcrWord(string Text, OcrBox Box, double Confidence, int Block, int Paragraph, int Line);

/// <summary>What was recognised on a page.</summary>
/// <param name="TextLayer">
/// A one-page PDF of the picture's size (<see cref="OcrImage.WidthInPoints"/> × <see cref="OcrImage.HeightInPoints"/>)
/// holding only the recognised text, invisible (text render mode 3), each word over its place on the picture.
/// </param>
/// <param name="Words">The words, in reading order.</param>
/// <param name="Text">The page as plain text (NFC), lines and paragraphs as recognised.</param>
/// <param name="Hocr">The page as hOCR when asked for.</param>
public sealed record OcrPageResult(byte[] TextLayer, IReadOnlyList<OcrWord> Words, string Text, string? Hocr)
{
    /// <summary>The mean confidence of the words (0–100), or null when nothing was recognised.</summary>
    public double? MeanConfidence => Words.Count == 0 ? null : Words.Average(w => w.Confidence);
}

/// <summary>How a page picture is turned, as told by orientation and script detection.</summary>
/// <param name="Rotate">Degrees (0, 90, 180, 270) to turn the picture clockwise to make the text upright.</param>
/// <param name="Confidence">How sure the detection is (Tesseract's measure; above 2 it is usually right).</param>
/// <param name="Script">The script found ("Latin"), or null.</param>
public sealed record OcrOrientation(int Rotate, double Confidence, string? Script);

/// <summary>An installed OCR engine.</summary>
/// <param name="Name">"Tesseract".</param>
/// <param name="Program">Where its program is.</param>
/// <param name="Data">Which trained data it uses (null: the engine's own, of no known kind).</param>
/// <param name="Languages">The languages it has data for (orientation detection not included), sorted.</param>
/// <param name="DetectsOrientation">Whether it can tell how pages are turned.</param>
public sealed record OcrEngineInfo(string Name, string Program, OcrDataKind? Data, IReadOnlyList<string> Languages, bool DetectsOrientation);

/// <summary>Recognises the text of page pictures (Tesseract, run as a separate program).</summary>
public interface IOcrEngine
{
    /// <summary>The engine as installed now, or null when it is not.</summary>
    OcrEngineInfo? Describe();

    /// <summary>Recognises one page. Several pages may be recognised at the same time.</summary>
    /// <exception cref="OcrException">Not installed, a language missing, or the engine failed or took too long.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    Task<OcrPageResult> RecognizeAsync(OcrImage image, OcrOptions options, CancellationToken cancellationToken);

    /// <summary>How the page is turned, or null when it cannot tell (too little text).</summary>
    /// <exception cref="OcrException">Not installed (or without orientation data), or the engine failed.</exception>
    Task<OcrOrientation?> DetectOrientationAsync(OcrImage image, CancellationToken cancellationToken);
}
