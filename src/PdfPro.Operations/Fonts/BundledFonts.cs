using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace PdfPro.Operations.Fonts;

/// <summary>
/// The fonts PDF_Pro writes text with (SPEC "Tiếng Việt"): Noto Sans Regular and Bold (SIL OFL 1.1), shipped inside
/// this assembly. The standard 14 PDF fonts have no Vietnamese letters, so text added to documents (tables of contents,
/// labels, later stamps) always uses these, embedded as subsets.
/// </summary>
public sealed class BundledFonts : IFontResolver
{
    /// <summary>Family name to give <c>XFont</c>.</summary>
    public const string Family = "Noto Sans";

    private const string RegularFace = "NotoSans-Regular";
    private const string BoldFace = "NotoSans-Bold";

    private static readonly Lock RegistrationLock = new();
    private readonly ConcurrentDictionary<string, byte[]> _faces = new(StringComparer.Ordinal);

    private BundledFonts()
    {
    }

    public static BundledFonts Instance { get; } = new();

    /// <summary>
    /// Makes PDFsharp use these fonts. PDFsharp allows one resolver per process, set before any text is drawn;
    /// calling this again is harmless.
    /// </summary>
    public static void Register()
    {
        lock (RegistrationLock)
        {
            if (!ReferenceEquals(GlobalFontSettings.FontResolver, Instance))
            {
                GlobalFontSettings.FontResolver = Instance;
            }
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        => string.Equals(familyName, Family, StringComparison.OrdinalIgnoreCase)
            ? new FontResolverInfo(bold ? BoldFace : RegularFace, mustSimulateBold: false, mustSimulateItalic: italic)
            : null;

    public byte[]? GetFont(string faceName)
        => faceName is RegularFace or BoldFace ? _faces.GetOrAdd(faceName, Load) : null;

    private static byte[] Load(string faceName)
    {
        using var stream = typeof(BundledFonts).Assembly.GetManifestResourceStream($"PdfPro.Fonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"The bundled font {faceName} is missing from the assembly.");
        using var copy = new MemoryStream((int)stream.Length);
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
