using System.Text;

namespace PdfPro.Core.Text;

/// <summary>The 8-bit Vietnamese encodings of fonts from before Unicode.</summary>
public enum LegacyVietnameseEncoding
{
    /// <summary>A Unicode (or any other) font: the text is what it says.</summary>
    None,

    /// <summary>TCVN3 (ABC, TCVN 5712:1993): ".VnTime", ".VnArial"…; one code per letter.</summary>
    Tcvn3,

    /// <summary>VNI Windows: "VNI-Times", "VNI-Helve"…; a base letter followed by codes for its marks.</summary>
    Vni,
}

/// <summary>A font's legacy encoding, and whether it draws every letter as a capital (".VnTimeH").</summary>
public readonly record struct LegacyFont(LegacyVietnameseEncoding Encoding, bool Uppercase)
{
    public static LegacyFont None { get; } = new(LegacyVietnameseEncoding.None, false);

    public bool IsLegacy => Encoding != LegacyVietnameseEncoding.None;
}

/// <summary>
/// Text drawn with Vietnamese fonts from before Unicode reads as garbage ("Céng hoµ x· héi chñ nghÜa ViÖt Nam",
/// "Coäng hoøa xaõ hoäi chuû nghóa Vieät Nam"): the PDF maps the font's codes through WinAnsi, so the letters come out
/// as the Latin-1 characters of the same codes. Here the font's name tells the encoding (TCVN3 fonts begin with
/// ".Vn", VNI fonts with "VNI-"; TCVN3 fonts ending in "H" draw capitals) and the characters are turned back into
/// Vietnamese. The conversion keeps one output per input character — a VNI mark becomes the combining mark of the
/// letter before it — so the boxes of a page's characters still line up with their text; NFC puts the letters
/// together.
/// </summary>
public static class LegacyVietnamese
{
    private const char Acute = '́';
    private const char Grave = '̀';
    private const char Hook = '̉';
    private const char Tilde = '̃';
    private const char DotBelow = '̣';
    private const char Circumflex = '̂';
    private const char Breve = '̆';

    /// <summary>TCVN3: Latin-1 code → Vietnamese letter (codes 0xA1–0xFE).</summary>
    private static readonly Dictionary<char, char> Tcvn3 = BuildTcvn3();

    /// <summary>VNI letters of one code (đ, ơ, ư, the five i, ỵ), lower and upper case.</summary>
    private static readonly Dictionary<char, string> VniSingles = new()
    {
        ['ñ'] = "đ", ['Ñ'] = "Đ",
        ['ô'] = "ơ", ['Ô'] = "Ơ",
        ['ö'] = "ư", ['Ö'] = "Ư",
        ['í'] = "í", ['Í'] = "Í",
        ['ì'] = "ì", ['Ì'] = "Ì",
        ['æ'] = "ỉ", ['Æ'] = "Ỉ",
        ['ó'] = "ĩ", ['Ó'] = "Ĩ",
        ['ò'] = "ị", ['Ò'] = "Ị",
        ['î'] = "ỵ", ['Î'] = "Ỵ",
    };

    /// <summary>VNI tone codes (after a, e, o, u, y, ơ, ư), lower and upper case.</summary>
    private static readonly Dictionary<char, string> VniTones = new()
    {
        ['ù'] = Marks(Acute), ['Ù'] = Marks(Acute),
        ['ø'] = Marks(Grave), ['Ø'] = Marks(Grave),
        ['û'] = Marks(Hook), ['Û'] = Marks(Hook),
        ['õ'] = Marks(Tilde), ['Õ'] = Marks(Tilde),
        ['ï'] = Marks(DotBelow), ['Ï'] = Marks(DotBelow),
    };

    /// <summary>VNI circumflex codes, with a tone or not (after a, e, o).</summary>
    private static readonly Dictionary<char, string> VniCircumflex = new()
    {
        ['â'] = Marks(Circumflex), ['Â'] = Marks(Circumflex),
        ['á'] = Marks(Circumflex, Acute), ['Á'] = Marks(Circumflex, Acute),
        ['à'] = Marks(Circumflex, Grave), ['À'] = Marks(Circumflex, Grave),
        ['å'] = Marks(Circumflex, Hook), ['Å'] = Marks(Circumflex, Hook),
        ['ã'] = Marks(Circumflex, Tilde), ['Ã'] = Marks(Circumflex, Tilde),
        ['ä'] = Marks(Circumflex, DotBelow), ['Ä'] = Marks(Circumflex, DotBelow),
    };

    /// <summary>VNI breve codes, with a tone or not (after a).</summary>
    private static readonly Dictionary<char, string> VniBreve = new()
    {
        ['ê'] = Marks(Breve), ['Ê'] = Marks(Breve),
        ['é'] = Marks(Breve, Acute), ['É'] = Marks(Breve, Acute),
        ['è'] = Marks(Breve, Grave), ['È'] = Marks(Breve, Grave),
        ['ú'] = Marks(Breve, Hook), ['Ú'] = Marks(Breve, Hook),
        ['ü'] = Marks(Breve, Tilde), ['Ü'] = Marks(Breve, Tilde),
        ['ë'] = Marks(Breve, DotBelow), ['Ë'] = Marks(Breve, DotBelow),
    };

    /// <summary>The legacy encoding of a font by its name (a subset tag "ABCDEF+" and a style ",Bold" do not count).</summary>
    public static LegacyFont Detect(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return LegacyFont.None;
        }

        var name = fontName.Trim();
        if (name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper))
        {
            name = name[7..];
        }

        var style = name.IndexOf(',', StringComparison.Ordinal);
        if (style > 0)
        {
            name = name[..style];
        }

        if (name.StartsWith("VNI-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("VNI ", StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyFont(LegacyVietnameseEncoding.Vni, false);
        }

        var tcvn = name.StartsWith(".Vn", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("Vn", StringComparison.Ordinal) && name.Length > 2 && char.IsAsciiLetterUpper(name[2]));
        if (!tcvn)
        {
            return LegacyFont.None;
        }

        // ".VnTimeH", ".VnArialH", ".VnArial NarrowH": the capital fonts ("-Bold" may follow the name).
        var family = name.Split('-')[0].TrimEnd();
        return new LegacyFont(LegacyVietnameseEncoding.Tcvn3, family.EndsWith('H'));
    }

    /// <summary>The text of <paramref name="text"/> drawn with a font of <paramref name="font"/>'s encoding, in NFC.</summary>
    public static string Convert(string text, LegacyFont font)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!font.IsLegacy || text.Length == 0)
        {
            return text;
        }

        return string.Concat(Convert([.. text.Select(c => c.ToString())], font)).Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// The characters <paramref name="chars"/> (in drawing order, one string each) of a font of <paramref name="font"/>'s
    /// encoding, each turned into what it means: a letter, or the combining marks a VNI code adds to the letter before
    /// it. As many outputs as inputs; joined and put in NFC they are the Vietnamese text.
    /// </summary>
    public static IReadOnlyList<string> Convert(IReadOnlyList<string> chars, LegacyFont font)
    {
        ArgumentNullException.ThrowIfNull(chars);
        var result = new string[chars.Count];
        switch (font.Encoding)
        {
            case LegacyVietnameseEncoding.Tcvn3:
                for (var i = 0; i < chars.Count; i++)
                {
                    var text = chars[i] is { Length: 1 } single && Tcvn3.TryGetValue(single[0], out var letter) ? letter.ToString() : chars[i];
                    result[i] = font.Uppercase ? text.ToUpperInvariant() : text;
                }

                break;

            case LegacyVietnameseEncoding.Vni:
                var previous = '\0';
                for (var i = 0; i < chars.Count; i++)
                {
                    var c = chars[i] is { Length: 1 } one ? one[0] : '\0';
                    var basis = char.ToLowerInvariant(previous);
                    if (c != '\0' && basis is 'a' && VniBreve.TryGetValue(c, out var breve))
                    {
                        result[i] = breve;
                        previous = '\0';
                    }
                    else if (c != '\0' && basis is 'a' or 'e' or 'o' && VniCircumflex.TryGetValue(c, out var circumflex))
                    {
                        result[i] = circumflex;
                        previous = '\0';
                    }
                    else if (c != '\0' && basis is 'a' or 'e' or 'o' or 'u' or 'y' or 'ơ' or 'ư' or 'i' && VniTones.TryGetValue(c, out var tone))
                    {
                        result[i] = tone;
                        previous = '\0';
                    }
                    else if (c != '\0' && VniSingles.TryGetValue(c, out var letter))
                    {
                        result[i] = letter;
                        previous = letter[0];
                    }
                    else
                    {
                        result[i] = chars[i];
                        previous = c;
                    }
                }

                break;

            default:
                for (var i = 0; i < chars.Count; i++)
                {
                    result[i] = chars[i];
                }

                break;
        }

        return result;
    }

    /// <summary>
    /// The text of letters each drawn with its own font (a word, a line), the legacy ones turned into Vietnamese;
    /// runs of one font are converted together (a VNI mark follows its letter).
    /// </summary>
    public static string Convert(IEnumerable<(string Text, string? Font)> letters)
    {
        ArgumentNullException.ThrowIfNull(letters);
        var text = new StringBuilder();
        var run = new List<string>();
        var current = LegacyFont.None;
        foreach (var (letter, fontName) in letters)
        {
            var font = Detect(fontName);
            if (font != current)
            {
                text.AppendJoin(string.Empty, Convert(run, current));
                run.Clear();
                current = font;
            }

            run.Add(letter);
        }

        text.AppendJoin(string.Empty, Convert(run, current));
        return text.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>A Unicode font that looks like a legacy one (".VnArial" → "Arial"); null for a font that is not legacy.</summary>
    public static string? UnicodeFont(string? fontName)
    {
        if (!Detect(fontName).IsLegacy)
        {
            return null;
        }

        var name = fontName!.ToUpperInvariant();
        return name.Contains("ARIAL", StringComparison.Ordinal) || name.Contains("HELVE", StringComparison.Ordinal) || name.Contains("UNIVER", StringComparison.Ordinal) || name.Contains("AVO", StringComparison.Ordinal) || name.Contains("SWISS", StringComparison.Ordinal)
            ? "Arial"
            : name.Contains("COURI", StringComparison.Ordinal) ? "Courier New" : "Times New Roman";
    }

    private static string Marks(params char[] marks) => new(marks);

    private static Dictionary<char, char> BuildTcvn3()
    {
        // TCVN 5712:1993 (VN3): the capitals with marks of one code, the lowercase letters with every mark.
        var table = new Dictionary<char, char>
        {
            ['¡'] = 'Ă', ['¢'] = 'Â', ['£'] = 'Ê', ['¤'] = 'Ô', ['¥'] = 'Ơ', ['¦'] = 'Ư', ['§'] = 'Đ',
            ['¨'] = 'ă', ['©'] = 'â', ['ª'] = 'ê', ['«'] = 'ô', ['¬'] = 'ơ', ['­'] = 'ư', ['®'] = 'đ',
        };
        void Row(string codes, string letters)
        {
            for (var i = 0; i < codes.Length; i++)
            {
                table[codes[i]] = letters[i];
            }
        }

        Row("µ¶·¸¹", "àảãáạ");
        Row("»¼½¾Æ", "ằẳẵắặ");
        Row("ÇÈÉÊË", "ầẩẫấậ");
        Row("ÌÎÏÐÑ", "èẻẽéẹ");
        Row("ÒÓÔÕÖ", "ềểễếệ");
        Row("×ØÜÝÞ", "ìỉĩíị");
        Row("ßáâãä", "òỏõóọ");
        Row("åæçèé", "ồổỗốộ");
        Row("êëìíî", "ờởỡớợ");
        Row("ïñòóô", "ùủũúụ");
        Row("õö÷øù", "ừửữứự");
        Row("úûüýþ", "ỳỷỹýỵ");
        return table;
    }
}
