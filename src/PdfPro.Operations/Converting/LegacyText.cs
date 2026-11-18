using System.Collections.Concurrent;
using LegacyFont = PdfPro.Core.Text.LegacyFont;
using LegacyVietnamese = PdfPro.Core.Text.LegacyVietnamese;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;

namespace PdfPro.Operations.Converting;

/// <summary>
/// The text of PdfPig's words and lines with TCVN3 and VNI fonts turned into Vietnamese (<see cref="LegacyVietnamese"/>);
/// text of other fonts is left as it is.
/// </summary>
internal static class LegacyText
{
    private static readonly ConcurrentDictionary<string, LegacyFont> Fonts = new(StringComparer.Ordinal);

    /// <summary>The font of a letter, as far as its encoding goes.</summary>
    public static LegacyFont FontOf(Letter letter) => letter.FontName is { } name ? Fonts.GetOrAdd(name, LegacyVietnamese.Detect) : LegacyFont.None;

    /// <summary>Whether a letter is drawn with a TCVN3 or VNI font.</summary>
    public static bool IsLegacy(Letter letter) => FontOf(letter).IsLegacy;

    /// <summary>A word's text.</summary>
    public static string Of(Word word)
        => word.Letters.Any(IsLegacy) ? LegacyVietnamese.Convert(word.Letters.Select(l => (l.Value, (string?)l.FontName))) : word.Text;

    /// <summary>A line's text: its words with a space between them.</summary>
    public static string Of(TextLine line)
        => line.Words.Any(w => w.Letters.Any(IsLegacy)) ? string.Join(' ', line.Words.Select(Of)) : line.Text;
}
