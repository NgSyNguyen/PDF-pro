using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>Inline images (ISO 32000 8.9.7): read, made grey, written again as ASCII hex of Flate data.</summary>
internal sealed partial class GrayscaleConverter
{
    private static readonly Dictionary<string, string> InlineKeys = new(StringComparer.Ordinal)
    {
        ["/BPC"] = "/BitsPerComponent",
        ["/CS"] = "/ColorSpace",
        ["/D"] = "/Decode",
        ["/DP"] = "/DecodeParms",
        ["/F"] = "/Filter",
        ["/H"] = "/Height",
        ["/IM"] = "/ImageMask",
        ["/I"] = "/Interpolate",
        ["/W"] = "/Width",
        ["/L"] = "/Length",
    };

    private static readonly Dictionary<string, string> InlineFilters = new(StringComparer.Ordinal)
    {
        ["/AHx"] = "/ASCIIHexDecode",
        ["/A85"] = "/ASCII85Decode",
        ["/LZW"] = "/LZWDecode",
        ["/Fl"] = "/FlateDecode",
        ["/RL"] = "/RunLengthDecode",
        ["/CCF"] = "/CCITTFaxDecode",
        ["/DCT"] = "/DCTDecode",
    };

    /// <summary>
    /// <paramref name="statement"/> is "BI … ID data EI". A grey image or a stencil mask is left as it is
    /// (<paramref name="replacement"/> null); a colour one is written anew in DeviceGray. False when it cannot be read.
    /// </summary>
    private bool ConvertInlineImage(ReadOnlySpan<byte> statement, PdfDictionary? resources, out string? replacement)
    {
        replacement = null;
        var image = new PdfDictionary(_document);
        var at = 2;
        while (true)
        {
            SkipWhite(statement, ref at);
            if (at + 1 < statement.Length && statement[at] == 'I' && statement[at + 1] == 'D' && (at + 2 == statement.Length || IsWhite(statement[at + 2])))
            {
                at += 2;
                break;
            }

            if (ReadItem(statement, ref at, 0) is not PdfName key || ReadItem(statement, ref at, 0) is not { } value)
            {
                return false;
            }

            var full = InlineKeys.GetValueOrDefault(key.Value, key.Value);
            image.Elements[full] = full == "/Filter" ? ExpandFilters(value) : value;
        }

        if (Resolve(image.Elements["/ImageMask"]) is PdfBoolean { Value: true })
        {
            return true;
        }

        if (ColorSpaces.Read(image.Elements["/ColorSpace"], resources) is not { } space)
        {
            return false;
        }

        if (space.IsGray || space.PaintsNothing)
        {
            return true;
        }

        // The data: after the one white-space byte following "ID", up to the white space before the final "EI".
        var dataStart = at + 1;
        var end = statement.Length - 2;
        var dataEnd = end > dataStart && IsWhite(statement[end - 1]) ? end - 1 : end;
        if (StreamData.Integer(image, "/Length") is { } length && length >= 0 && dataStart + length <= end)
        {
            dataEnd = dataStart + length;
        }

        if (dataEnd < dataStart)
        {
            return false;
        }

        image.CreateStream(statement[dataStart..dataEnd].ToArray());
        var (width, height) = (StreamData.Integer(image, "/Width") ?? 0, StreamData.Integer(image, "/Height") ?? 0);
        var bits = StreamData.Integer(image, "/BitsPerComponent") ?? 0;
        if (width <= 0 || height <= 0 || (long)width * height > ImageRecompressor.MaxPixels || StreamData.Decode(image, out var remaining) is not { } data)
        {
            return false;
        }

        var decode = PdfFunction.Numbers(image.Elements["/Decode"]);
        string colorSpace;
        byte[] samples;
        var outputBits = 8;
        var decodeText = string.Empty;
        if (space is ColorSpace.Indexed indexed && remaining.Count == 0)
        {
            var table = new byte[indexed.Highest + 1];
            for (var i = 0; i < table.Length; i++)
            {
                table[i] = Byte(indexed.GrayOfEntry(i));
            }

            colorSpace = $"[/I /G {indexed.Highest} <{System.Convert.ToHexString(table)}>]";
            samples = data;
            outputBits = bits;
            if (decode is not null)
            {
                decodeText = " /D [" + string.Join(' ', decode.Select(N)) + "]";
            }
        }
        else if (remaining.Count == 0 && GraySamples(data, width, height, bits, space, decode) is { } gray)
        {
            colorSpace = "/G";
            samples = gray;
        }
        else if (remaining is [("/DCTDecode", null)] && space is ColorSpace.Rgb && bits == 8 && decode is null
            && JpegCodec.Decode(data, 3) is { Width: var w, Height: var h } pixels && w == width && h == height)
        {
            colorSpace = "/G";
            samples = new byte[w * h];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = Byte(ColorSpace.FromRgb(pixels.Samples[i * 3] / 255.0, pixels.Samples[(i * 3) + 1] / 255.0, pixels.Samples[(i * 3) + 2] / 255.0));
            }
        }
        else
        {
            return false;
        }

        var interpolate = Resolve(image.Elements["/Interpolate"]) is PdfBoolean { Value: true } ? " /I true" : string.Empty;
        var payload = System.Convert.ToHexString(ImageRecompressor.Deflate(samples));
        replacement = string.Create(
            CultureInfo.InvariantCulture,
            $"BI /W {width} /H {height} /CS {colorSpace} /BPC {outputBits}{decodeText}{interpolate} /F [/AHx /Fl] ID\n{payload}>\nEI");
        ImagesConverted++;
        return true;
    }

    private static bool IsWhite(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static void SkipWhite(ReadOnlySpan<byte> text, ref int at)
    {
        while (at < text.Length && IsWhite(text[at]))
        {
            at++;
        }
    }

    private PdfItem ExpandFilters(PdfItem value)
    {
        switch (value)
        {
            case PdfName name:
                return new PdfName(InlineFilters.GetValueOrDefault(name.Value, name.Value));
            case PdfArray array:
                var expanded = new PdfArray(_document);
                foreach (var item in array.Elements)
                {
                    expanded.Elements.Add(ExpandFilters(item));
                }

                return expanded;
            default:
                return value;
        }
    }

    /// <summary>One object of an inline image dictionary: name, number, boolean, string, array or dictionary; null otherwise.</summary>
    private PdfItem? ReadItem(ReadOnlySpan<byte> text, ref int at, int depth)
    {
        SkipWhite(text, ref at);
        if (at >= text.Length || depth > 8)
        {
            return null;
        }

        switch (text[at])
        {
            case (byte)'/':
                var nameEnd = at + 1;
                while (nameEnd < text.Length && !IsWhite(text[nameEnd]) && !IsDelimiter(text[nameEnd]))
                {
                    nameEnd++;
                }

                var name = new PdfName("/" + Encoding.Latin1.GetString(text[(at + 1)..nameEnd]));
                at = nameEnd;
                return name;
            case (byte)'[':
                at++;
                var array = new PdfArray(_document);
                while (true)
                {
                    SkipWhite(text, ref at);
                    if (at >= text.Length)
                    {
                        return null;
                    }

                    if (text[at] == ']')
                    {
                        at++;
                        return array;
                    }

                    if (ReadItem(text, ref at, depth + 1) is not { } element)
                    {
                        return null;
                    }

                    array.Elements.Add(element);
                }

            case (byte)'<' when at + 1 < text.Length && text[at + 1] == '<':
                at += 2;
                var dictionary = new PdfDictionary(_document);
                while (true)
                {
                    SkipWhite(text, ref at);
                    if (at + 1 < text.Length && text[at] == '>' && text[at + 1] == '>')
                    {
                        at += 2;
                        return dictionary;
                    }

                    if (ReadItem(text, ref at, depth + 1) is not PdfName key || ReadItem(text, ref at, depth + 1) is not { } value)
                    {
                        return null;
                    }

                    dictionary.Elements[key.Value] = value;
                }

            case (byte)'<':
                var close = text[at..].IndexOf((byte)'>');
                if (close < 0)
                {
                    return null;
                }

                var hex = new StringBuilder();
                foreach (var c in text.Slice(at + 1, close - 1))
                {
                    if (Uri.IsHexDigit((char)c))
                    {
                        hex.Append((char)c);
                    }
                }

                if (hex.Length % 2 == 1)
                {
                    hex.Append('0');
                }

                at += close + 1;
                return new PdfString(Encoding.Latin1.GetString(System.Convert.FromHexString(hex.ToString())));
            case (byte)'(':
                return ReadLiteral(text, ref at);
        }

        var end = at;
        while (end < text.Length && !IsWhite(text[end]) && !IsDelimiter(text[end]))
        {
            end++;
        }

        var token = Encoding.Latin1.GetString(text[at..end]);
        at = end;
        return token switch
        {
            "true" => new PdfBoolean(true),
            "false" => new PdfBoolean(false),
            _ when int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => new PdfInteger(integer),
            _ when double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) => new PdfReal(real),
            _ => null,
        };
    }

    private static PdfString? ReadLiteral(ReadOnlySpan<byte> text, ref int at)
    {
        var value = new StringBuilder();
        var depth = 0;
        for (var i = at; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case (byte)'\\' when i + 1 < text.Length && text[i + 1] is >= (byte)'0' and <= (byte)'7':
                    var octal = 0;
                    for (var digits = 0; digits < 3 && i + 1 < text.Length && text[i + 1] is >= (byte)'0' and <= (byte)'7'; digits++)
                    {
                        octal = (octal * 8) + (text[++i] - '0');
                    }

                    value.Append((char)(octal & 0xFF));
                    continue;
                case (byte)'\\' when i + 1 < text.Length:
                    var next = text[++i];
                    value.Append(next switch
                    {
                        (byte)'n' => '\n',
                        (byte)'r' => '\r',
                        (byte)'t' => '\t',
                        (byte)'b' => '\b',
                        (byte)'f' => '\f',
                        _ => (char)next,
                    });
                    continue;
                case (byte)'(':
                    if (depth++ > 0)
                    {
                        value.Append('(');
                    }

                    continue;
                case (byte)')':
                    if (--depth == 0)
                    {
                        at = i + 1;
                        return new PdfString(value.ToString());
                    }

                    value.Append(')');
                    continue;
                default:
                    value.Append((char)c);
                    continue;
            }
        }

        return null;
    }
}
