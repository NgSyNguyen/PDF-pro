using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Ocr;

namespace PdfPro.Operations.Ocr;

/// <summary>A page read by OCR: what was recognised, and how the picture it read maps back to the rendered page.</summary>
/// <param name="Result">The engine's result.</param>
/// <param name="ToRender">From the picture OCR read (pixels) to the rendered page (pixels).</param>
/// <param name="Width">The width of the picture OCR read.</param>
/// <param name="Height">Its height.</param>
/// <param name="Rotation">Degrees the rendered page was turned clockwise.</param>
/// <param name="Skew">Degrees it was then straightened by (its lines rose to the right by that much), or 0.</param>
/// <param name="Tilt">How its lines rise to the right, as measured (straightened or not; 0 when unclear).</param>
internal sealed record PageReading(OcrPageResult Result, PdfMatrix ToRender, int Width, int Height, int Rotation, double Skew, double Tilt)
{
    /// <summary>Mean confidence, -1 when nothing was recognised (any reading beats none).</summary>
    public double Score => Result.MeanConfidence ?? -1;
}

/// <summary>
/// Reads one rendered page. Only the picture OCR reads is changed: turned the right way up and straightened. Turning
/// is told by the engine's orientation detection, which is unsure on many scans (Vietnamese scans of 90 DPI give
/// confidences below 2, wrong ones included), so unless it is sure the page is read both ways and the reading with the
/// higher mean confidence is kept; a page read poorly upright is also tried upside down. Straightening is done from
/// 1.5° on: below that Tesseract reads as well or better without the blur of resampling.
/// </summary>
internal sealed class OcrPageReader(IOcrEngine engine, OcrOptions options, bool autoRotate, bool deskew, bool measureTilt)
{
    /// <summary>An orientation detected at least this sure is taken without reading the page both ways.</summary>
    public const double SureOrientation = 10;

    /// <summary>Tilts below this are left for OCR to cope with.</summary>
    public const double MinSkew = 1.5;

    /// <summary>A page read upright with a mean confidence below this is tried upside down as well.</summary>
    public const double PoorConfidence = 50;

    /// <summary>The best reading of the page.</summary>
    /// <exception cref="OcrException">The engine failed.</exception>
    public async Task<PageReading> ReadAsync(GrayImage render, double dpi, CancellationToken cancellationToken)
    {
        var turns = new List<int> { 0 };
        if (autoRotate && await engine.DetectOrientationAsync(new OcrImage(render, dpi), cancellationToken).ConfigureAwait(false) is { Rotate: not 0 } orientation)
        {
            turns = orientation.Confidence >= SureOrientation ? [orientation.Rotate] : [0, orientation.Rotate];
        }

        PageReading? best = null;
        foreach (var turn in turns)
        {
            var reading = await ReadTurnedAsync(render, dpi, turn, cancellationToken).ConfigureAwait(false);
            if (best is null || reading.Score > best.Score)
            {
                best = reading;
            }
        }

        if (autoRotate && best!.Score < PoorConfidence && !turns.Contains(180))
        {
            var upsideDown = await ReadTurnedAsync(render, dpi, 180, cancellationToken).ConfigureAwait(false);
            if (upsideDown.Score > best.Score)
            {
                best = upsideDown;
            }
        }

        return best!;
    }

    /// <summary>One reading of the page, its picture turned clockwise by <paramref name="rotation"/> and straightened when tilted.</summary>
    internal async Task<PageReading> ReadTurnedAsync(GrayImage render, double dpi, int rotation, CancellationToken cancellationToken)
    {
        var turned = GrayTransforms.RotateQuarter(render, rotation);
        var (image, toRender, skew) = (turned.Image, turned.ToSource, 0.0);
        var tilt = measureTilt && SkewDetector.Estimate(image) is { IsReliable: true } estimate ? estimate.Degrees : 0;
        if (deskew && Math.Abs(tilt) >= MinSkew)
        {
            var straight = GrayTransforms.Rotate(image, tilt);
            (image, toRender, skew) = (straight.Image, straight.ToSource.Then(toRender), tilt);
        }

        var result = await engine.RecognizeAsync(new OcrImage(image, dpi), options, cancellationToken).ConfigureAwait(false);
        return new PageReading(result, toRender, image.Width, image.Height, rotation, skew, tilt);
    }
}
