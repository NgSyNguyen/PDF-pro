using System.Buffers.Binary;
using System.Globalization;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Optimizing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using UglyToad.PdfPig.Fonts;
using UglyToad.PdfPig.Fonts.Encodings;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Converting.PdfA;

/// <summary>
/// Fonts PDF/A can keep (ISO 19005-1 6.3, 19005-2 6.2.11): every font a document draws with must be embedded. A font
/// that is not is embedded from the font of this computer of the same family and style (Helvetica, Times and Courier
/// being Arial, Times New Roman and Courier New), as a whole TrueType program; failing that, a simple font is replaced
/// by a bundled one (Noto Serif for serif faces, else Noto Sans) and said so. The widths in the font dictionary are
/// those of the program embedded (PDF/A checks them), the encoding one PDF/A allows for a non-symbolic TrueType font
/// (WinAnsi, with differences for PDF/A-2 only). Symbol fonts and the old Vietnamese encodings (TCVN3 ".Vn…", VNI)
/// are never replaced: their codes mean other letters. Composite fonts are embedded only from a font of the same name
/// (their codes are glyph numbers). For PDF/A-1, subset CID fonts get the CIDSet it asks for.
/// </summary>
internal static class PdfAFonts
{
    // Font descriptor flags (ISO 32000-1 9.8.2).
    private const int FixedPitch = 1;
    private const int Serif = 2;
    private const int Symbolic = 4;
    private const int Nonsymbolic = 32;
    private const int Italic = 64;
    private const int ForceBold = 1 << 18;

    private static readonly Dictionary<string, string> StandardFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = "Arial",
        ["Times"] = "Times New Roman",
        ["Courier"] = "Courier New",
    };

    private enum Outcome
    {
        Failed,
        Embedded,
        Substituted,
    }

    public static void Embed(PdfDocument document, PdfALevel level, FontLibrary library, PdfAChanges changes)
    {
        foreach (var font in PdfAStructure.AllDictionaries(document).Where(d => Name(d, "/Type") == "/Font" && Name(d, "/Subtype") is "/Type1" or "/MMType1" or "/TrueType" or "/Type0").ToList())
        {
            if (PdfAInspection.IsEmbedded(font))
            {
                continue;
            }

            Outcome outcome;
            try
            {
                outcome = Name(font, "/Subtype") == "/Type0" ? Composite(document, font, library) : Simple(document, font, level, library);
            }
            catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IOException or UnauthorizedAccessException)
            {
                outcome = Outcome.Failed; // a font file that cannot be read: the font stays as it is, and is reported
            }

            switch (outcome)
            {
                case Outcome.Embedded:
                    changes.Add(PdfAFix.FontsEmbedded);
                    break;
                case Outcome.Substituted:
                    changes.Add(PdfAFix.FontsSubstituted);
                    break;
            }
        }

        if (level == PdfALevel.A1B)
        {
            foreach (var descendant in PdfAStructure.AllDictionaries(document).Where(d => Name(d, "/Subtype") == "/CIDFontType2").ToList())
            {
                AddCidSet(document, descendant);
            }
        }
    }

    /// <summary>The glyphs of a TrueType program that have an outline (and glyph 0), by number; null when it cannot be read.</summary>
    public static bool[]? PresentGlyphs(byte[] program)
    {
        try
        {
            var tables = Tables(program);
            if (!tables.TryGetValue("head", out var head) || !tables.TryGetValue("maxp", out var maxp) || !tables.TryGetValue("loca", out var loca))
            {
                return null;
            }

            var count = BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(maxp.Offset + 4));
            var longOffsets = BinaryPrimitives.ReadInt16BigEndian(program.AsSpan(head.Offset + 50)) == 1;
            long Offset(int glyph) => longOffsets
                ? BinaryPrimitives.ReadUInt32BigEndian(program.AsSpan(loca.Offset + (glyph * 4)))
                : BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(loca.Offset + (glyph * 2))) * 2L;
            var present = new bool[count];
            for (var glyph = 0; glyph < count; glyph++)
            {
                present[glyph] = glyph == 0 || Offset(glyph + 1) > Offset(glyph);
            }

            return present;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The family, style and whether it replaces the font, for <paramref name="font"/>; null when nothing may stand for it.</summary>
    internal static (string Family, bool Bold, bool Italic, bool Substitute)? Face(PdfDictionary font, PdfDictionary? descriptor, FontLibrary library, bool allowSubstitute)
    {
        var name = PdfAInspection.FontName(font);
        var cut = name.IndexOfAny([',', '-']);
        var (family, style) = cut > 0 ? (name[..cut], name[(cut + 1)..]) : (name, string.Empty);
        var flags = descriptor is null ? 0 : StreamData.Integer(descriptor, "/Flags") ?? 0;
        var weight = descriptor is null ? 0 : StreamData.Integer(descriptor, "/FontWeight") ?? 0;
        var angle = descriptor is null ? 0 : Number(descriptor.Elements["/ItalicAngle"]) ?? 0;
        var bold = Contains(style, "Bold", "Black", "Heavy", "Semibold", "Demi") || (flags & ForceBold) != 0 || weight >= 600;
        var italic = Contains(style, "Italic", "Oblique") || (flags & Italic) != 0 || angle != 0;
        if (Contains(family, "Symbol", "Dingbats", "Wingdings", "Webdings", "Marlett") || family.StartsWith(".Vn", StringComparison.OrdinalIgnoreCase)
            || family.StartsWith("VNI", StringComparison.OrdinalIgnoreCase) || family.StartsWith("Vn", StringComparison.Ordinal))
        {
            return null;
        }

        family = StandardFamilies.GetValueOrDefault(family) ?? family;
        var key = Key(family);
        if (library.Families.FirstOrDefault(f => Key(f.Name) == key) is { } installed)
        {
            return (installed.Name, bold, italic, false);
        }

        if (!allowSubstitute)
        {
            return null;
        }

        var serif = (flags & Serif) != 0 || Contains(family, "Times", "Serif", "Roman", "Georgia", "Cambria", "Garamond", "Book", "Palatino");
        return (serif ? FontFamilies.NotoSerif : FontFamilies.NotoSans, bold, italic, true);
    }

    /// <summary>A family name compared without spaces, case or the PostScript suffixes "PSMT", "PS", "MT".</summary>
    internal static string Key(string family)
    {
        var key = new string([.. family.Where(char.IsLetterOrDigit)]).ToLowerInvariant();
        foreach (var suffix in new[] { "psmt", "ps", "mt" })
        {
            if (key.Length > suffix.Length + 2 && key.EndsWith(suffix, StringComparison.Ordinal))
            {
                return key[..^suffix.Length];
            }
        }

        return key;
    }

    /// <summary>
    /// A simple font gets a whole TrueType program, a TrueType descriptor, an encoding PDF/A allows and the widths of
    /// that program for its codes.
    /// </summary>
    private static Outcome Simple(PdfDocument document, PdfDictionary font, PdfALevel level, FontLibrary library)
    {
        var descriptor = AsDictionary(font.Elements["/FontDescriptor"]);
        if (Face(font, descriptor, library, allowSubstitute: true) is not var (family, bold, italic, substitute))
        {
            return Outcome.Failed;
        }

        var metrics = library.Metrics(family, bold, italic);
        var program = library.FontData(family, bold, italic);
        var names = CodeNames(font, out var encodingItem, out var keepEncoding);
        var (first, last) = (StreamData.Integer(font, "/FirstChar") ?? 32, StreamData.Integer(font, "/LastChar") ?? 255);
        if (first is < 0 or > 255 || last < first || last > 255)
        {
            (first, last) = (32, 255);
        }

        var winAnsi = WinAnsiEncoding.Instance;
        var lossy = false;
        if (level == PdfALevel.A1B || !keepEncoding)
        {
            var differences = new PdfArray(document);
            var previous = -2;
            for (var code = first; code <= last; code++)
            {
                if (names.TryGetValue(code, out var name) && name != winAnsi.GetName(code))
                {
                    if (level == PdfALevel.A1B)
                    {
                        lossy = true;
                        names[code] = winAnsi.GetName(code);
                        continue;
                    }

                    if (code != previous + 1)
                    {
                        differences.Elements.Add(new PdfInteger(code));
                    }

                    differences.Elements.Add(new PdfName("/" + name));
                    previous = code;
                }
            }

            if (differences.Elements.Count == 0)
            {
                font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
            }
            else
            {
                var encoding = new PdfDictionary(document);
                encoding.Elements.SetName("/Type", "/Encoding");
                encoding.Elements.SetName("/BaseEncoding", "/WinAnsiEncoding");
                encoding.Elements["/Differences"] = differences;
                font.Elements["/Encoding"] = encoding;
            }
        }
        else if (encodingItem is not null)
        {
            font.Elements["/Encoding"] = encodingItem;
        }

        var widths = new PdfArray(document);
        for (var code = first; code <= last; code++)
        {
            var codePoint = names.TryGetValue(code, out var name) ? Unicode(name) : -1;
            var advance = codePoint >= 0 ? metrics.Advance(codePoint) : metrics.AdvanceOfGlyph(0);
            widths.Elements.Add(new PdfInteger(Scale(advance, metrics)));
        }

        var flags = descriptor is null ? 0 : StreamData.Integer(descriptor, "/Flags") ?? 0;
        flags = (flags & (FixedPitch | Serif | ForceBold)) | Nonsymbolic | (italic ? Italic : 0);
        descriptor = Descriptor(document, font, descriptor, metrics, program, flags, italic);
        font.Elements.SetName("/Subtype", "/TrueType");
        font.Elements["/FontDescriptor"] = descriptor.Reference!;
        font.Elements.SetInteger("/FirstChar", first);
        font.Elements.SetInteger("/LastChar", last);
        font.Elements["/Widths"] = widths;
        return substitute || lossy ? Outcome.Substituted : Outcome.Embedded;
    }

    /// <summary>A composite font whose descendant is a TrueType CIDFont: the program of the font of the same name, its widths by glyph.</summary>
    private static Outcome Composite(PdfDocument document, PdfDictionary font, FontLibrary library)
    {
        if (AsArray(font.Elements["/DescendantFonts"]) is not { Elements.Count: > 0 } descendants
            || AsDictionary(descendants.Elements[0]) is not { } descendant || Name(descendant, "/Subtype") != "/CIDFontType2")
        {
            return Outcome.Failed;
        }

        var descriptor = AsDictionary(descendant.Elements["/FontDescriptor"]);
        if (Face(descendant, descriptor, library, allowSubstitute: false) is not var (family, bold, italic, _))
        {
            return Outcome.Failed;
        }

        var metrics = library.Metrics(family, bold, italic);
        var program = library.FontData(family, bold, italic);
        int[] glyphs;
        if (AsDictionary(descendant.Elements["/CIDToGIDMap"]) is { Stream: not null } map)
        {
            var bytes = StreamData.Bytes(map);
            glyphs = [.. Enumerable.Range(0, bytes.Length / 2).Select(cid => (bytes[cid * 2] << 8) | bytes[(cid * 2) + 1])];
        }
        else
        {
            descendant.Elements.SetName("/CIDToGIDMap", "/Identity");
            glyphs = [.. Enumerable.Range(0, metrics.GlyphCount)];
        }

        var widths = new PdfArray(document);
        foreach (var glyph in glyphs)
        {
            widths.Elements.Add(new PdfInteger(Scale(metrics.AdvanceOfGlyph(glyph), metrics)));
        }

        var flags = descriptor is null ? 0 : StreamData.Integer(descriptor, "/Flags") ?? 0;
        flags = (flags & (FixedPitch | Serif | ForceBold)) | Symbolic | (italic ? Italic : 0);
        descriptor = Descriptor(document, descendant, descriptor, metrics, program, flags, italic);
        descendant.Elements["/FontDescriptor"] = descriptor.Reference!;
        descendant.Elements["/W"] = new PdfArray(document, new PdfInteger(0), widths);
        return Outcome.Embedded;
    }

    /// <summary>The descriptor, made indirect, with the program (Flate) and the metrics it describes.</summary>
    private static PdfDictionary Descriptor(PdfDocument document, PdfDictionary font, PdfDictionary? descriptor, TrueTypeFont metrics, byte[] program, int flags, bool italic)
    {
        if (descriptor is null || descriptor.Reference is null)
        {
            var created = new PdfDictionary(document);
            if (descriptor is not null)
            {
                foreach (var key in descriptor.Elements.Keys)
                {
                    created.Elements[key] = descriptor.Elements[key];
                }
            }

            document.Internals.AddObject(created);
            descriptor = created;
        }

        var file = new PdfDictionary(document);
        file.CreateStream(PdfAStructure.Deflate(program));
        file.Elements.SetName("/Filter", "/FlateDecode");
        file.Elements.SetInteger("/Length1", program.Length);
        document.Internals.AddObject(file);

        var box = metrics.BoundingBox;
        descriptor.Elements.SetName("/Type", "/FontDescriptor");
        descriptor.Elements.SetName("/FontName", Name(font, "/BaseFont") ?? "/Font");
        descriptor.Elements.SetInteger("/Flags", flags);
        descriptor.Elements["/FontBBox"] = new PdfArray(document, new PdfInteger(Scale(box.XMin, metrics)), new PdfInteger(Scale(box.YMin, metrics)), new PdfInteger(Scale(box.XMax, metrics)), new PdfInteger(Scale(box.YMax, metrics)));
        if (!descriptor.Elements.ContainsKey("/ItalicAngle"))
        {
            descriptor.Elements.SetInteger("/ItalicAngle", italic ? -12 : 0);
        }

        descriptor.Elements.SetInteger("/Ascent", Scale(metrics.Ascender, metrics));
        descriptor.Elements.SetInteger("/Descent", Scale(metrics.Descender, metrics));
        descriptor.Elements.SetInteger("/CapHeight", Scale(metrics.CapHeight, metrics));
        if (!descriptor.Elements.ContainsKey("/StemV"))
        {
            descriptor.Elements.SetInteger("/StemV", (flags & ForceBold) != 0 ? 120 : 80);
        }

        foreach (var key in new[] { "/FontFile", "/FontFile3", "/CharSet", "/CIDSet" })
        {
            descriptor.Elements.Remove(key);
        }

        descriptor.Elements["/FontFile2"] = file.Reference!;
        return descriptor;
    }

    /// <summary>
    /// The glyph name of each code of a simple font: its base encoding (named, or the font's standard one) and its
    /// differences. <paramref name="keep"/>: the encoding is one PDF/A-2 allows as it is (WinAnsi or MacRoman, with
    /// differences or not).
    /// </summary>
    private static Dictionary<int, string> CodeNames(PdfDictionary font, out PdfItem? encodingItem, out bool keep)
    {
        encodingItem = font.Elements["/Encoding"];
        var dictionary = AsDictionary(encodingItem);
        var baseName = Resolve(encodingItem) is PdfName named ? named.Value : dictionary is null ? null : Name(dictionary, "/BaseEncoding");
        keep = baseName is "/WinAnsiEncoding" or "/MacRomanEncoding";
        UglyToad.PdfPig.Fonts.Encodings.Encoding baseEncoding = baseName switch
        {
            "/WinAnsiEncoding" => WinAnsiEncoding.Instance,
            "/MacRomanEncoding" => MacRomanEncoding.Instance,
            _ => Name(font, "/Subtype") == "/TrueType" && baseName is null ? WinAnsiEncoding.Instance : StandardEncoding.Instance,
        };
        var names = new Dictionary<int, string>();
        for (var code = 0; code < 256; code++)
        {
            if (baseEncoding.ContainsCode(code))
            {
                names[code] = baseEncoding.GetName(code);
            }
        }

        if (dictionary is not null && AsArray(dictionary.Elements["/Differences"]) is { } differences)
        {
            var code = 0;
            foreach (var item in differences.Elements.Select(Resolve))
            {
                switch (item)
                {
                    case PdfInteger number:
                        code = number.Value;
                        break;
                    case PdfName name when code is >= 0 and < 256:
                        names[code++] = name.Value.TrimStart('/');
                        break;
                }
            }
        }

        return names;
    }

    /// <summary>The character a glyph name stands for (Adobe Glyph List, "uniXXXX", "uXXXX[XX]"), or -1.</summary>
    private static int Unicode(string name)
    {
        var text = GlyphList.AdobeGlyphList.NameToUnicode(name);
        if (!string.IsNullOrEmpty(text))
        {
            return char.ConvertToUtf32(text, 0);
        }

        if (name.Length == 7 && name.StartsWith("uni", StringComparison.Ordinal) && int.TryParse(name.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var uni))
        {
            return uni;
        }

        return name.Length is 5 or 6 or 7 && name[0] == 'u' && int.TryParse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u) ? u : -1;
    }

    /// <summary>
    /// The CIDSet of a subset TrueType CIDFont whose glyphs are its CIDs (PDF/A-1 6.3.5): one bit for every glyph the
    /// program holds — those without an outline too (a space has none), as veraPDF reads "present in the font program".
    /// </summary>
    private static void AddCidSet(PdfDocument document, PdfDictionary descendant)
    {
        var name = Name(descendant, "/BaseFont") ?? string.Empty;
        if (AsDictionary(descendant.Elements["/FontDescriptor"]) is not { } descriptor || descriptor.Elements.ContainsKey("/CIDSet")
            || AsDictionary(descriptor.Elements["/FontFile2"]) is not { } file || !(name.Length > 8 && name[7] == '+')
            || (descendant.Elements.ContainsKey("/CIDToGIDMap") && Name(descendant, "/CIDToGIDMap") != "/Identity")
            || PresentGlyphs(StreamData.Bytes(file)) is not { } present)
        {
            return;
        }

        var bits = new byte[(present.Length + 7) / 8];
        for (var glyph = 0; glyph < present.Length; glyph++)
        {
            bits[glyph / 8] |= (byte)(0x80 >> (glyph % 8));
        }

        var set = new PdfDictionary(document);
        set.CreateStream(PdfAStructure.Deflate(bits));
        set.Elements.SetName("/Filter", "/FlateDecode");
        document.Internals.AddObject(set);
        descriptor.Elements["/CIDSet"] = set.Reference!;
    }

    private static Dictionary<string, (int Offset, int Length)> Tables(byte[] program)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(4));
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = program.AsSpan(12 + (i * 16), 16);
            tables[System.Text.Encoding.ASCII.GetString(record[..4])] = ((int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]), (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]));
        }

        return tables;
    }

    private static int Scale(int units, TrueTypeFont metrics) => (int)Math.Round(units * 1000.0 / metrics.UnitsPerEm, MidpointRounding.AwayFromZero);

    private static bool Contains(string text, params string[] parts) => parts.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static double? Number(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => null,
    };
}
