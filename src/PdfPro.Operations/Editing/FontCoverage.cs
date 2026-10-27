using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Editing;

/// <summary>
/// Which characters a font of a page can write again: the characters its /ToUnicode map gives (a subset font embedded by
/// Word lists exactly the codes it has glyphs for), or, for a simple font that is not embedded, those of its WinAnsi
/// encoding. A font with neither is not trusted with any character.
/// </summary>
public static class FontCoverage
{
    /// <summary>Upper bound for a /ToUnicode stream read (defends against hostile files).</summary>
    private const int MaxCMapBytes = 1 << 20;

    /// <summary>Upper bound for the codes one bfrange may map.</summary>
    private const int MaxRange = 65_536;

    /// <summary>The characters of Windows-1252 at 0x80–0x9F.</summary>
    private const string WinAnsiExtras = "€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ";

    /// <summary>
    /// The characters of <paramref name="text"/> (distinct, in order of appearance) that the page's font named
    /// <paramref name="baseFontName"/> cannot write; empty when it can write them all. Line breaks are not counted.
    /// </summary>
    public static string Missing(PdfPage page, string baseFontName, string text)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(text);
        var covered = Find(page, baseFontName) is { } font ? Covered(font) : null;
        var missing = new StringBuilder();
        var seen = new HashSet<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\r' || !seen.Add(rune.Value) || covered?.Contains(rune.Value) == true)
            {
                continue;
            }

            missing.Append(rune.ToString());
        }

        return missing.ToString();
    }

    /// <summary>The page's font resource whose /BaseFont is <paramref name="baseFontName"/>, or null.</summary>
    internal static PdfDictionary? Find(PdfPage page, string baseFontName)
    {
        var name = baseFontName.StartsWith('/') ? baseFontName : "/" + baseFontName;
        return AsDictionary(page.Elements["/Resources"]) is { } resources && AsDictionary(resources.Elements["/Font"]) is { } fonts
            ? fonts.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>().FirstOrDefault(f => Name(f, "/BaseFont") == name)
            : null;
    }

    /// <summary>The code points the font can write, or null when that cannot be known.</summary>
    internal static IReadOnlySet<int>? Covered(PdfDictionary font)
    {
        if (AsDictionary(font.Elements["/ToUnicode"]) is { Stream: { } stream })
        {
            var bytes = stream.UnfilteredValue;
            return bytes.Length is > 0 and <= MaxCMapBytes ? ParseToUnicode(bytes) : null;
        }

        var subtype = Name(font, "/Subtype");
        if (subtype is "/Type1" or "/TrueType" or "/MMType1" && !IsEmbedded(font) && IsWinAnsi(font))
        {
            return WinAnsi;
        }

        return null;
    }

    /// <summary>The single characters a ToUnicode CMap maps codes to (bfchar and bfrange, ligatures left out).</summary>
    internal static HashSet<int> ParseToUnicode(byte[] cmap)
    {
        var result = new HashSet<int>();
        var tokens = Tokens(Encoding.Latin1.GetString(cmap)).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "beginbfchar")
            {
                for (i++; i + 1 < tokens.Count && tokens[i] != "endbfchar"; i += 2)
                {
                    AddSingle(result, tokens[i + 1]);
                }
            }
            else if (tokens[i] == "beginbfrange")
            {
                for (i++; i + 2 < tokens.Count && tokens[i] != "endbfrange";)
                {
                    var low = Code(tokens[i]);
                    var high = Code(tokens[i + 1]);
                    if (tokens[i + 2] == "[")
                    {
                        // <lo> <hi> [<d1> <d2> …]: one destination per code.
                        i += 3;
                        for (; i < tokens.Count && tokens[i] != "]"; i++)
                        {
                            AddSingle(result, tokens[i]);
                        }

                        i++;
                    }
                    else
                    {
                        // <lo> <hi> <d>: consecutive characters from d.
                        if (low is { } lo && high is { } hi && hi >= lo && hi - lo < MaxRange && Utf16(tokens[i + 2]) is [var first] && !char.IsSurrogate(first))
                        {
                            for (var code = 0; code <= hi - lo && first + code <= char.MaxValue; code++)
                            {
                                result.Add(first + code);
                            }
                        }

                        i += 3;
                    }
                }
            }
        }

        return result;
    }

    private static readonly HashSet<int> WinAnsi =
    [
        .. Enumerable.Range(0x20, 0x7F - 0x20),
        .. Enumerable.Range(0xA0, 0x100 - 0xA0),
        .. WinAnsiExtras.Select(c => (int)c),
    ];

    private static void AddSingle(HashSet<int> result, string destination)
    {
        if (Utf16(destination) is { } chars && new string(chars).EnumerateRunes().ToList() is [var rune])
        {
            result.Add(rune.Value);
        }
    }

    /// <summary>A hex string token ("&lt;0041&gt;") as UTF-16BE characters, or null.</summary>
    private static char[]? Utf16(string token)
    {
        if (token.Length < 2 || token[0] != '<' || token[^1] != '>')
        {
            return null;
        }

        var hex = token[1..^1];
        if (hex.Length == 0 || hex.Length % 4 != 0)
        {
            return null;
        }

        var chars = new char[hex.Length / 4];
        for (var i = 0; i < chars.Length; i++)
        {
            if (!ushort.TryParse(hex.AsSpan(i * 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            chars[i] = (char)value;
        }

        return chars;
    }

    private static long? Code(string token)
        => token.Length > 2 && token[0] == '<' && token[^1] == '>'
            && long.TryParse(token.AsSpan(1, token.Length - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>The tokens of a CMap: hex strings, brackets and bare words (comments and names dropped).</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '%')
            {
                while (i < text.Length && text[i] is not ('\n' or '\r'))
                {
                    i++;
                }
            }
            else if (c == '<' && i + 1 < text.Length && text[i + 1] != '<')
            {
                var end = text.IndexOf('>', i);
                if (end < 0)
                {
                    yield break;
                }

                yield return string.Concat("<", new string([.. text[(i + 1)..end].Where(char.IsAsciiHexDigit)]), ">");
                i = end + 1;
            }
            else if (c is '[' or ']')
            {
                yield return c.ToString();
                i++;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('[' or ']' or '<' or '%'))
                {
                    i++;
                }

                if (i == start)
                {
                    i++; // a lone delimiter such as "<<"
                    continue;
                }

                yield return text[start..i];
            }
        }
    }

    private static bool IsEmbedded(PdfDictionary font)
        => AsDictionary(font.Elements["/FontDescriptor"]) is { } descriptor
            && (descriptor.Elements.ContainsKey("/FontFile") || descriptor.Elements.ContainsKey("/FontFile2") || descriptor.Elements.ContainsKey("/FontFile3"));

    private static bool IsWinAnsi(PdfDictionary font)
        => Resolve(font.Elements["/Encoding"]) switch
        {
            PdfName name => name.Value == "/WinAnsiEncoding",
            PdfDictionary encoding => !encoding.Elements.ContainsKey("/Differences") && Name(encoding, "/BaseEncoding") is null or "/WinAnsiEncoding",
            _ => false,
        };
}
