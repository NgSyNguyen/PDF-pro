using PdfSharp.Pdf;

namespace PdfPro.Operations.Converting.PdfA;

/// <summary>Colour profiles embedded into PDF/A documents.</summary>
internal static class IccProfiles
{
    /// <summary>The name PDFsharp 6.2.4 ships the ICC's sRGB profile under (sRGB2014.icc, free to copy and embed).</summary>
    private const string SrgbResource = "PdfSharp.Resources.sRGB2014.icc";

    private static readonly Lazy<byte[]> SrgbData = new(LoadSrgb, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The sRGB IEC61966-2.1 display profile of the International Color Consortium (sRGB2014.icc, ICC version 2, which
    /// PDF/A-1 needs), as PDFsharp carries it for its own PDF/A output.
    /// </summary>
    public static byte[] Srgb => SrgbData.Value;

    /// <summary>Whether <paramref name="profile"/> looks like an ICC profile of <paramref name="space"/> ("RGB ", "CMYK") of major version ≤ <paramref name="maxVersion"/>.</summary>
    public static bool IsProfile(ReadOnlySpan<byte> profile, string space, int maxVersion)
        => profile.Length >= 132
           && profile[36..40].SequenceEqual("acsp"u8)
           && profile[16..20].SequenceEqual(System.Text.Encoding.ASCII.GetBytes(space))
           && profile[8] is > 0 && profile[8] <= maxVersion;

    private static byte[] LoadSrgb()
    {
        using var stream = typeof(PdfDocument).Assembly.GetManifestResourceStream(SrgbResource)
            ?? throw new InvalidOperationException("PDFsharp no longer carries the sRGB profile.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var data = copy.ToArray();
        return IsProfile(data, "RGB ", 2) ? data : throw new InvalidOperationException("The sRGB profile PDFsharp carries is not an ICC version 2 RGB profile.");
    }
}
