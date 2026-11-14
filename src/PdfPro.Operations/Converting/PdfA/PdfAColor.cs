using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Converting.PdfA;

/// <summary>
/// CMYK colours under an sRGB output intent (ISO 19005-1 6.2.3.3, 19005-2 6.2.4.3): DeviceCMYK is allowed only where a
/// default colour space defines it. Every resource dictionary gets /DefaultCMYK = [/ICCBased <see cref="IccProfiles.Cmyk"/>]
/// (which covers k / K, cs / CS, inline images), and DeviceCMYK named in images, shadings, transparency groups and
/// colour space arrays (indexed bases, separation and DeviceN alternates) becomes that space; overprint mode 1, which
/// PDF/A-2 forbids with ICC-based CMYK, becomes 0. The profile draws CMYK as viewers draw DeviceCMYK.
/// </summary>
internal static class PdfAColor
{
    public static void DefineCmyk(PdfDocument document, PdfAChanges changes)
    {
        var profile = new PdfDictionary(document);
        profile.Elements.SetInteger("/N", 4);
        profile.CreateStream(PdfAStructure.Deflate(IccProfiles.Cmyk));
        profile.Elements.SetName("/Filter", "/FlateDecode");
        document.Internals.AddObject(profile);
        var space = new PdfArray(document, new PdfName("/ICCBased"), profile.Reference!);
        document.Internals.AddObject(space);
        var reference = space.Reference!;

        foreach (var dictionary in PdfAStructure.AllDictionaries(document))
        {
            if (AsDictionary(dictionary.Elements["/Resources"]) is { } resources)
            {
                if (AsDictionary(resources.Elements["/ColorSpace"]) is not { } spaces)
                {
                    spaces = new PdfDictionary(document);
                    resources.Elements["/ColorSpace"] = spaces;
                }

                if (!spaces.Elements.ContainsKey("/DefaultCMYK"))
                {
                    spaces.Elements["/DefaultCMYK"] = reference;
                }

                foreach (var key in spaces.Elements.Keys.Where(k => k != "/DefaultCMYK").ToList())
                {
                    spaces.Elements[key] = Replace(spaces.Elements[key], reference) ?? spaces.Elements[key];
                }
            }

            foreach (var key in new[] { "/ColorSpace", "/CS" })
            {
                if (Replace(dictionary.Elements[key], reference) is { } replaced
                    && (Name(dictionary, "/Subtype") == "/Image" || dictionary.Elements.ContainsKey("/ShadingType") || Name(dictionary, "/S") == "/Transparency"))
                {
                    dictionary.Elements[key] = replaced;
                }
            }

            if (Resolve(dictionary.Elements["/OPM"]) is PdfInteger { Value: 1 })
            {
                dictionary.Elements.SetInteger("/OPM", 0);
            }
        }

        changes.Add(PdfAFix.CmykProfile);
    }

    /// <summary>The colour space with DeviceCMYK replaced by <paramref name="cmyk"/>; null when it has none.</summary>
    private static PdfItem? Replace(PdfItem? space, PdfReference cmyk, int depth = 0)
    {
        if (depth > 8)
        {
            return null;
        }

        switch (Resolve(space))
        {
            case PdfName { Value: "/DeviceCMYK" or "/CMYK" }:
                return cmyk;
            case PdfArray { Elements.Count: > 1 } array when Resolve(array.Elements[0]) is PdfName family:
                var at = family.Value switch
                {
                    "/Indexed" or "/I" or "/Pattern" => 1,
                    "/Separation" or "/DeviceN" => 2,
                    _ => -1,
                };
                if (at < 0 || at >= array.Elements.Count || Replace(array.Elements[at], cmyk, depth + 1) is not { } inner)
                {
                    return null;
                }

                array.Elements[at] = inner;
                return space;
            default:
                return null;
        }
    }
}
