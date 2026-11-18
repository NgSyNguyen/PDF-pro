using System.Buffers;
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
        var size = checked((int)info.BytesSize64);

        // Grey is decoded straight into the image; RGB into a pooled RGBA buffer (Skia has no 3-byte format) — a scan
        // page is about 35 MB of it, so renting it saves the large-object heap a page's worth of garbage each time.
        var pixels = components == 1 ? new byte[size] : ArrayPool<byte>.Shared.Rent(size);
        try
        {
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

            return components == 1 ? new PixelImage(pixels, width, height, 1) : new PixelImage(Channels.Drop(pixels.AsSpan(0, size), 4, 3), width, height, 3);
        }
        finally
        {
            if (components != 1)
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
    }

    /// <summary>A baseline JPEG of a grey (1 sample) or RGB (3 samples) image at <paramref name="quality"/> (IJG scale).</summary>
    public static byte[] Encode(PixelImage image, int quality)
    {
        if (image.Channels is not (1 or 3))
        {
            throw new ArgumentException("Only grey and RGB images are written as JPEG.", nameof(image));
        }

        var gray = image.Channels == 1;
        var info = new SKImageInfo(image.Width, image.Height, gray ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque);

        // RGB goes through a pooled RGBA buffer, as in Decode.
        var samples = gray ? image.Samples : ArrayPool<byte>.Shared.Rent(checked((int)info.BytesSize64));
        try
        {
            if (!gray)
            {
                Channels.AddOpaque(image.Samples, samples);
            }

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
        finally
        {
            if (!gray)
            {
                ArrayPool<byte>.Shared.Return(samples);
            }
        }
    }
}

/// <summary>Converting interleaved samples between 3 and 4 per pixel.</summary>
internal static class Channels
{
    /// <summary>The first <paramref name="kept"/> samples of each pixel of <paramref name="from"/> samples.</summary>
    public static byte[] Drop(ReadOnlySpan<byte> samples, int from, int kept)
    {
        var pixels = samples.Length / from;
        var result = new byte[pixels * kept];
        if (from == 4 && kept == 3)
        {
            for (int i = 0, o = 0, end = pixels * 4; i < end; i += 4, o += 3)
            {
                result[o] = samples[i];
                result[o + 1] = samples[i + 1];
                result[o + 2] = samples[i + 2];
            }

            return result;
        }

        for (var i = 0; i < pixels; i++)
        {
            samples.Slice(i * from, kept).CopyTo(result.AsSpan(i * kept, kept));
        }

        return result;
    }

    /// <summary>RGB samples into <paramref name="rgba"/> (at least 4/3 as long) with an opaque alpha sample after each pixel.</summary>
    public static void AddOpaque(ReadOnlySpan<byte> rgb, Span<byte> rgba)
    {
        for (int i = 0, o = 0, end = rgb.Length / 3 * 3; i < end; i += 3, o += 4)
        {
            rgba[o] = rgb[i];
            rgba[o + 1] = rgb[i + 1];
            rgba[o + 2] = rgb[i + 2];
            rgba[o + 3] = 255;
        }
    }
}
