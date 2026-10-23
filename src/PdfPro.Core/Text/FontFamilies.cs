namespace PdfPro.Core.Text;

/// <summary>
/// Font families PDF_Pro ships for writing text into documents (SPEC "Tiếng Việt"): full Vietnamese coverage, SIL OFL
/// 1.1, embedded as subsets. Other names in parameters refer to fonts installed on the computer.
/// </summary>
public static class FontFamilies
{
    public const string NotoSans = "Noto Sans";

    public const string NotoSerif = "Noto Serif";

    public const string BeVietnamPro = "Be Vietnam Pro";

    /// <summary>The family used when none is chosen.</summary>
    public const string Default = NotoSans;

    public static IReadOnlyList<string> Bundled { get; } = [NotoSans, NotoSerif, BeVietnamPro];

    public static bool IsBundled(string family) => Bundled.Contains(family, StringComparer.OrdinalIgnoreCase);
}
