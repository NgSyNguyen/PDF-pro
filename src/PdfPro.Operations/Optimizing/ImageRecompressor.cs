using System.IO.Compression;
using PdfPro.Core.Imaging;

namespace PdfPro.Operations.Optimizing;

/// <summary>What an image's data is, as read from the file.</summary>
internal enum ImageSource
{
    /// <summary>JPEG data (DCTDecode) of 1 or 3 components.</summary>
    Jpeg,

    /// <summary>8-bit samples (the general filters undone) of 1, 3 or 4 channels.</summary>
    Samples,

    /// <summary>1-bit samples of one channel, rows packed: masks, scanned text, two-colour pictures.</summary>
    Bilevel,
}

/// <summary>How an image's new data is coded.</summary>
internal enum ImageEncoding
{
    Jpeg,

    /// <summary>Flate of the samples filtered by PNG predictors (<c>/Predictor 15</c>).</summary>
    FlatePredicted,

    /// <summary>Flate of the samples as they are (1-bit images).</summary>
    Flate,

    /// <summary>CCITT Group 4 (1-bit images).</summary>
    G4,
}

/// <summary>An image to recompress, read out of the document (so that it can be worked on in parallel).</summary>
/// <param name="Data">JPEG data, samples or packed bits, according to <paramref name="Source"/>.</param>
/// <param name="EncodedLength">The bytes the image takes in the file now.</param>
/// <param name="Size">The largest it is drawn at.</param>
/// <param name="Lossless">Must stay exact apart from resampling (soft masks, CMYK): never JPEG.</param>
internal sealed record ImageJob(ImageSource Source, byte[] Data, int Width, int Height, int Channels, long EncodedLength, DisplaySize Size, bool Lossless);

/// <summary>The new data of an image, smaller than before.</summary>
internal sealed record ImageOutcome(byte[] Data, ImageEncoding Encoding, int Width, int Height, bool Resampled);

/// <summary>The image settings of a compression: null resolution = never resample nor recompress lossily.</summary>
/// <param name="Dpi">The resolution images are brought down to.</param>
/// <param name="Quality">JPEG quality (IJG scale 1–100).</param>
internal sealed record ImageSettings(int? Dpi, int Quality);

/// <summary>
/// Decides and makes the new data of one image (SPEC Phase 9 "Nén"). Its effective resolution is its pixels over the
/// largest size it is drawn at; above 1.5 × the target it is brought down to the target by area averaging. Photographs
/// are written as JPEG at the chosen quality; pictures of few colours, soft masks and CMYK images stay lossless (Flate
/// with PNG predictors); 1-bit images stay 1-bit (CCITT G4 or Flate, whichever is smaller) at their resolution. A JPEG
/// already at or below the quality (within 5) and resolution is never coded again, so compressing twice changes
/// nothing more. The result is kept only when smaller — 10 % smaller when the image was not resampled, since a lossy
/// coding must buy something.
/// </summary>
internal static class ImageRecompressor
{
    /// <summary>Images above this many times the target resolution are resampled.</summary>
    public const double ResampleAbove = 1.5;

    /// <summary>Images larger than this are left alone (memory).</summary>
    public const long MaxPixels = 100_000_000;

    /// <summary>A JPEG of a quality at most this much above the target is kept as it is.</summary>
    private const int QualitySlack = 5;

    /// <summary>A lossy coding of an image not resampled must be at most this fraction of what it replaces.</summary>
    private const double LossyGain = 0.9;

    public static ImageOutcome? Process(ImageJob job, ImageSettings settings)
    {
        if ((long)job.Width * job.Height > MaxPixels || job.Width <= 0 || job.Height <= 0)
        {
            return null;
        }

        return job.Source switch
        {
            ImageSource.Bilevel => Bilevel(job),
            ImageSource.Jpeg => FromJpeg(job, settings),
            _ => FromSamples(job, settings),
        };
    }

    /// <summary>The size the image is resampled to, or null when it is not above the threshold.</summary>
    public static (int Width, int Height)? Target(ImageJob job, ImageSettings settings)
    {
        if (settings.Dpi is not { } dpi || job.Size.Width <= 0 || job.Size.Height <= 0)
        {
            return null;
        }

        var effective = Math.Min(job.Width * 72 / job.Size.Width, job.Height * 72 / job.Size.Height);
        if (effective <= dpi * ResampleAbove)
        {
            return null;
        }

        var factor = dpi / effective;
        return (Math.Max(1, (int)Math.Round(job.Width * factor)), Math.Max(1, (int)Math.Round(job.Height * factor)));
    }

    private static ImageOutcome? Bilevel(ImageJob job)
    {
        var g4 = CcittG4.Encode(job.Data, job.Width, job.Height);
        var flate = Deflate(job.Data);
        var (data, encoding) = g4.Length <= flate.Length ? (g4, ImageEncoding.G4) : (flate, ImageEncoding.Flate);
        return data.Length < job.EncodedLength ? new ImageOutcome(data, encoding, job.Width, job.Height, Resampled: false) : null;
    }

    private static ImageOutcome? FromJpeg(ImageJob job, ImageSettings settings)
    {
        if (settings.Dpi is null || JpegHeader.Read(job.Data) is not { BitsPerSample: 8 } header || header.Components != job.Channels || header.AdobeTransform == 2)
        {
            return null;
        }

        var target = Target(job, settings);
        if (target is null && header.Quality is { } current && current <= settings.Quality + QualitySlack)
        {
            return null; // as good as asked already: coding it again would only lose more
        }

        if (JpegCodec.Decode(job.Data, job.Channels) is not { } pixels || pixels.Width != job.Width || pixels.Height != job.Height)
        {
            return null;
        }

        if (target is { } size)
        {
            pixels = Shrink(pixels, size);
        }

        // Never above the quality the picture already has: that would only spend bytes on its artefacts.
        var quality = Math.Min(settings.Quality, header.Quality ?? settings.Quality);
        var jpeg = JpegCodec.Encode(pixels, quality);
        return Smaller(jpeg.Length, job.EncodedLength, target is not null)
            ? new ImageOutcome(jpeg, ImageEncoding.Jpeg, pixels.Width, pixels.Height, target is not null)
            : null;
    }

    private static ImageOutcome? FromSamples(ImageJob job, ImageSettings settings)
    {
        if (job.Data.Length < (long)job.Width * job.Height * job.Channels)
        {
            return null;
        }

        var pixels = new PixelImage(job.Data, job.Width, job.Height, job.Channels);
        var target = Target(job, settings);
        if (target is { } size)
        {
            pixels = Shrink(pixels, size);
        }

        var resampled = target is not null;
        var lossy = settings.Dpi is not null && !job.Lossless && job.Channels is 1 or 3 && !FewColours(job);
        if (lossy)
        {
            var jpeg = JpegCodec.Encode(pixels, settings.Quality);
            if (Smaller(jpeg.Length, job.EncodedLength, resampled))
            {
                return new ImageOutcome(jpeg, ImageEncoding.Jpeg, pixels.Width, pixels.Height, resampled);
            }
        }

        var flate = Deflate(PngPredictor.Encode(pixels.Samples, pixels.Width, pixels.Height, pixels.Channels));
        return flate.Length < job.EncodedLength ? new ImageOutcome(flate, ImageEncoding.FlatePredicted, pixels.Width, pixels.Height, resampled) : null;
    }

    private static bool Smaller(long length, long before, bool resampled) => resampled ? length < before : length <= before * LossyGain;

    private static PixelImage Shrink(PixelImage pixels, (int Width, int Height) size)
        => new(AreaResampler.Shrink(pixels.Samples, pixels.Width, pixels.Height, pixels.Channels, size.Width, size.Height), size.Width, size.Height, pixels.Channels);

    /// <summary>
    /// A drawing, chart or screenshot rather than a photograph: at most 256 colours (RGB) or 32 grey levels. JPEG would
    /// blur its edges and often be larger than Flate.
    /// </summary>
    private static bool FewColours(ImageJob job)
    {
        var limit = job.Channels == 1 ? 32 : 256;
        var seen = new HashSet<int>();
        var count = (long)job.Width * job.Height;
        for (long i = 0; i < count; i++)
        {
            var at = i * job.Channels;
            var value = job.Channels == 1 ? job.Data[at] : (job.Data[at] << 16) | (job.Data[at + 1] << 8) | job.Data[at + 2];
            if (seen.Add(value) && seen.Count > limit)
            {
                return false;
            }
        }

        return true;
    }

    public static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(Math.Max(256, data.Length / 4));
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}
