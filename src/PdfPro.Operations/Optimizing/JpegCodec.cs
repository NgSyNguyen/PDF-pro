using System.Runtime.InteropServices;
using SkiaSharp;

namespace PdfPro.Operations.Optimizing;

/// <summary>Pixels as 8-bit samples, interleaved: 1 (grey), 3 (RGB) or 4 (RGBA, CMYK) per pixel.</summary>
internal sealed record PixelImage(byte[] Samples, int Width, int Height, int Channels);

/// <summary>
/// JPEG decoding and encoding through Skia (libjpeg-turbo). No colour management on either side: the samples are
/// those of the file and the colour space stays the PDF's /ColorSpace, as a PDF reader would treat them; no ICC
/// profile is written into the JPEG data.
/// </summary>
internal static class JpegCodec
{
    /// <summary>
    /// The pixels of a JPEG file of 1 or 3 components: grey (1 sample) or RGB (3); null when Skia cannot decode it.
    /// </summary>
    public static PixelImage? Decode(byte[] jpeg, int components)
    {
        if (components is not (1 or 3))
        {
            return null;
        }

        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg)
        {
            return null;
        }

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        var info = new SKImageInfo(width, height, components == 1 ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = new byte[info.BytesSize64];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                return null;
            }
        }
        finally
        {
            handle.Free();
        }

        return components == 1 ? new PixelImage(pixels, width, height, 1) : new PixelImage(Channels.Drop(pixels, 4, 3), width, height, 3);
    }

    /// <summary>A baseline JPEG of a grey (1 sample) or RGB (3 samples) image at <paramref name="quality"/> (IJG scale).</summary>
    public static byte[] Encode(PixelImage image, int quality)
    {
        if (image.Channels is not (1 or 3))
        {
            throw new ArgumentException("Only grey and RGB images are written as JPEG.", nameof(image));
        }

        var gray = image.Channels == 1;
        var samples = gray ? image.Samples : Channels.AddOpaque(image.Samples);
        var info = new SKImageInfo(image.Width, image.Height, gray ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var encoded = pixmap.Encode(new SKJpegEncoderOptions(Math.Clamp(quality, 1, 100), SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
                ?? throw new InvalidOperationException("Skia could not encode the image as JPEG.");
            return encoded.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>Converting interleaved samples between 3 and 4 per pixel.</summary>
internal static class Channels
{
    /// <summary>The first <paramref name="kept"/> samples of each pixel of <paramref name="from"/> samples.</summary>
    public static byte[] Drop(byte[] samples, int from, int kept)
    {
        var pixels = samples.Length / from;
        var result = new byte[pixels * kept];
        for (var i = 0; i < pixels; i++)
        {
            Buffer.BlockCopy(samples, i * from, result, i * kept, kept);
        }

        return result;
    }

    /// <summary>RGB samples with an opaque alpha sample after each pixel.</summary>
    public static byte[] AddOpaque(byte[] rgb)
    {
        var pixels = rgb.Length / 3;
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            result[i * 4] = rgb[i * 3];
            result[(i * 4) + 1] = rgb[(i * 3) + 1];
            result[(i * 4) + 2] = rgb[(i * 3) + 2];
            result[(i * 4) + 3] = 255;
        }

        return result;
    }
}
