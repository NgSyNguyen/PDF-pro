using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>An image that may be recompressed: drawn at <paramref name="Size"/> at most; a mask of another image or not.</summary>
internal sealed record ImageCandidate(PdfDictionary Image, DisplaySize Size, bool Mask);

/// <summary>Reading images out of a document for <see cref="ImageRecompressor"/>, and writing their new data back.</summary>
internal static class ImageStreams
{
    /// <summary>
    /// The images that may be recompressed: every image whose display size is known, and the soft masks and stencil
    /// masks of those (drawn at their size). Left out: colour-key masked images, soft masks with /Matte (premultiplied:
    /// they must keep the size of their image) with their image, and JPEG 2000 with its own mask.
    /// </summary>
    public static List<ImageCandidate> Candidates(IReadOnlyDictionary<PdfDictionary, DisplaySize> sizes)
    {
        // Masks are drawn with their image: their size is the image's.
        var all = new Dictionary<PdfDictionary, (DisplaySize Size, bool Mask)>(ReferenceEqualityComparer.Instance);
        var excluded = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var (image, size) in sizes)
        {
            Add(image, size, mask: false);
            var softMask = AsDictionary(image.Elements["/SMask"]);
            if (softMask is { Stream: not null } && softMask.Elements.ContainsKey("/Matte"))
            {
                excluded.Add(image);
                excluded.Add(softMask);
            }
            else if (softMask is { Stream: not null })
            {
                Add(softMask, size, mask: true);
            }

            if (Resolve(image.Elements["/Mask"]) is PdfDictionary { Stream: not null } stencil)
            {
                Add(stencil, size, mask: true);
            }
            else if (Resolve(image.Elements["/Mask"]) is PdfArray)
            {
                excluded.Add(image); // colour-key masking: samples must stay exact
            }

            if (image.Elements.ContainsKey("/SMaskInData"))
            {
                excluded.Add(image);
            }
        }

        return [.. all.Where(e => !excluded.Contains(e.Key)).Select(e => new ImageCandidate(e.Key, e.Value.Size, e.Value.Mask))];

        void Add(PdfDictionary image, DisplaySize size, bool mask)
            => all[image] = all.TryGetValue(image, out var known) ? (known.Size.Max(size), known.Mask || mask) : (size, mask);
    }

    /// <summary>Writes an image's new data and the entries that describe it.</summary>
    public static void Write(PdfDocument document, PdfDictionary image, ImageOutcome outcome, int channels)
    {
        image.Stream!.Value = outcome.Data;
        image.Elements.SetInteger("/Length", outcome.Data.Length);
        image.Elements.SetInteger("/Width", outcome.Width);
        image.Elements.SetInteger("/Height", outcome.Height);
        image.Elements.Remove("/DecodeParms");
        switch (outcome.Encoding)
        {
            case ImageEncoding.Jpeg:
                image.Elements.SetName("/Filter", "/DCTDecode");
                image.Elements.SetInteger("/BitsPerComponent", 8);
                break;
            case ImageEncoding.FlatePredicted:
                image.Elements.SetName("/Filter", "/FlateDecode");
                var predictor = new PdfDictionary(document);
                predictor.Elements.SetInteger("/Predictor", 15);
                predictor.Elements.SetInteger("/Colors", channels);
                predictor.Elements.SetInteger("/BitsPerComponent", 8);
                predictor.Elements.SetInteger("/Columns", outcome.Width);
                image.Elements["/DecodeParms"] = predictor;
                break;
            case ImageEncoding.Flate:
                image.Elements.SetName("/Filter", "/FlateDecode");
                break;
            case ImageEncoding.G4:
                image.Elements.SetName("/Filter", "/CCITTFaxDecode");
                var fax = new PdfDictionary(document);
                fax.Elements.SetInteger("/K", -1);
                fax.Elements.SetInteger("/Columns", outcome.Width);
                fax.Elements.SetInteger("/Rows", outcome.Height);
                image.Elements["/DecodeParms"] = fax;
                break;
        }
    }

    /// <summary>
    /// The image's data decoded for <see cref="ImageRecompressor"/>, or null when it is of a kind not handled: colour
    /// spaces or depths other than grey, RGB and CMYK in 8 bits or one component in 1 bit (indexed, separations, Lab,
    /// 2/4/16 bits…), codecs other than JPEG (JPEG 2000, JBIG2, CCITT), external or undecodable data. Reads the
    /// document: not to be called from several threads at once.
    /// </summary>
    public static ImageJob? Job(ImageCandidate candidate)
    {
        var (image, size, mask) = (candidate.Image, candidate.Size, candidate.Mask);
        var width = StreamData.Integer(image, "/Width") ?? 0;
        var height = StreamData.Integer(image, "/Height") ?? 0;
        var stencil = Resolve(image.Elements["/ImageMask"]) is PdfBoolean { Value: true };
        var bits = stencil ? 1 : StreamData.Integer(image, "/BitsPerComponent") ?? 0;
        if (width <= 0 || height <= 0 || image.Stream?.Value is not { } encoded || image.Elements.ContainsKey("/F"))
        {
            return null;
        }

        // A soft mask has no colour space of its own: it is grey.
        var (channels, continuous) = stencil || (mask && !image.Elements.ContainsKey("/ColorSpace")) ? (1, true) : Components(image.Elements["/ColorSpace"]);
        if (StreamData.Decode(image, out var remaining) is not { } data)
        {
            return null;
        }

        if (bits == 1 && channels == 1 && remaining.Count == 0)
        {
            return data.Length >= (long)((width + 7) / 8) * height
                ? new ImageJob(ImageSource.Bilevel, data, width, height, 1, encoded.Length, size, Lossless: true)
                : null;
        }

        if (bits != 8 || !continuous || channels is not (1 or 3 or 4))
        {
            return null;
        }

        var lossless = mask || channels == 4;
        if (remaining.Count == 0)
        {
            return new ImageJob(ImageSource.Samples, data, width, height, channels, encoded.Length, size, lossless);
        }

        // JPEG: its own colour transform settings are not followed by every decoder; CMYK JPEG is left alone.
        return remaining is [("/DCTDecode", var parameters)] && parameters is null && channels is 1 or 3 && !lossless
            ? new ImageJob(ImageSource.Jpeg, data, width, height, channels, encoded.Length, size, Lossless: false)
            : null;
    }

    /// <summary>The number of colour components of a colour space, and whether its samples can be averaged (and JPEG coded).</summary>
    private static (int Channels, bool Continuous) Components(PdfItem? colorSpace)
    {
        switch (Resolve(colorSpace))
        {
            case PdfName name:
                return name.Value switch
                {
                    "/DeviceGray" or "/G" or "/CalGray" => (1, true),
                    "/DeviceRGB" or "/RGB" or "/CalRGB" => (3, true),
                    "/DeviceCMYK" or "/CMYK" => (4, true),
                    _ => (0, false),
                };
            case PdfArray { Elements.Count: > 0 } array when Resolve(array.Elements[0]) is PdfName family:
                switch (family.Value)
                {
                    case "/CalGray":
                        return (1, true);
                    case "/CalRGB":
                        return (3, true);
                    case "/ICCBased" when array.Elements.Count > 1 && AsDictionary(array.Elements[1]) is { } profile:
                        return StreamData.Integer(profile, "/N") is { } n and (1 or 3 or 4) ? (n, true) : (0, false);
                    case "/Indexed" or "/I" or "/Separation":
                        return (1, false); // one component, but not a level that can be averaged
                    default:
                        return (0, false);
                }

            default:
                return (0, false);
        }
    }
}
