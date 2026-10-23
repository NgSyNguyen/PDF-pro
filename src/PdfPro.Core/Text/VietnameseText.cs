namespace PdfPro.Core.Text;

/// <summary>Vietnamese letters, for checking that a font can write Vietnamese.</summary>
public static class VietnameseText
{
    /// <summary>Every lower-case letter of the Vietnamese alphabet with every tone mark, precomposed (NFC).</summary>
    public const string LowerLetters = "aàảãáạăằẳẵắặâầẩẫấậbcdđeèẻẽéẹêềểễếệghiìỉĩíịklmnoòỏõóọôồổỗốộơờởỡớợpqrstuùủũúụưừửữứựvxyỳỷỹýỵ";

    /// <summary>The lower- and upper-case letters.</summary>
    public static string Letters { get; } = LowerLetters + LowerLetters.ToUpperInvariant();
}
