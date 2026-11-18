using PdfPro.Operations.Converting.PdfA;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Extracting;

/// <summary>Whether a font's program is in the file.</summary>
public enum FontEmbedding
{
    /// <summary>The whole program is in the file.</summary>
    Embedded,

    /// <summary>Only the glyphs used ("ABCDEF+Name"): fine for printing, not for editing with other letters.</summary>
    Subset,

    /// <summary>Not in the file: another computer or a printer uses a font of its own, and the text may look or print wrong.</summary>
    NotEmbedded,
}

/// <summary>A font a document uses.</summary>
/// <param name="Name">Its name without a subset tag ("Arial-BoldMT").</param>
/// <param name="Type">"TrueType", "Type 1", "Type 1 (CFF)", "OpenType", "Type 0 (CID TrueType)", "Type 0 (CID CFF)", "Type 3".</param>
/// <param name="Embedding">Embedded whole, as a subset, or not at all.</param>
/// <param name="Encoding">Its encoding ("WinAnsiEncoding", "Identity-H", "custom"), or null.</param>
/// <param name="IsStandard">One of the 14 standard fonts every PDF reader has (Helvetica, Times…).</param>
/// <param name="HasUnicode">It maps its codes to Unicode (/ToUnicode): its text can be copied and searched.</param>
/// <param name="Pages">The pages using it (1-based).</param>
public sealed record FontEntry(string Name, string Type, FontEmbedding Embedding, string? Encoding, bool IsStandard, bool HasUnicode, IReadOnlyList<int> Pages);

/// <summary>
/// The fonts of a document (extract.fonts, "Phông chữ"): those of the pages' resources, of the forms they draw (nested),
/// of their annotations' appearances and of Type 3 glyphs, each with whether it is embedded — useful before printing
/// or sending a file. Each font dictionary is listed once, with every page that uses it.
/// </summary>
public static class FontInventory
{
    private const int MaxDepth = 16;

    private static readonly HashSet<string> Standard14 = new(StringComparer.Ordinal)
    {
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Symbol", "ZapfDingbats",
    };

    /// <exception cref="InvalidDataException">The document cannot be read.</exception>
    public static IReadOnlyList<FontEntry> List(string path, string? password = null, CancellationToken cancellationToken = default)
    {
        using var document = Attachments.Open(path, password);
        var pages = new Dictionary<PdfDictionary, SortedSet<int>>(ReferenceEqualityComparer.Instance);
        var order = new List<PdfDictionary>();
        for (var p = 0; p < document.PageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Pages[p];
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            Resources(PdfAInspection.PageResources(page), p + 1, 0);
            foreach (var annotation in Annotations(page)?.Elements.Select(AsDictionary).OfType<PdfDictionary>() ?? [])
            {
                foreach (var entry in AsDictionary(annotation.Elements["/AP"])?.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>() ?? [])
                {
                    var forms = entry.Stream is not null ? [entry] : entry.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>().Where(f => f.Stream is not null);
                    foreach (var form in forms)
                    {
                        Resources(AsDictionary(form.Elements["/Resources"]), p + 1, 1);
                    }
                }
            }

            void Resources(PdfDictionary? resources, int number, int depth)
            {
                if (resources is null || depth > MaxDepth || !visited.Add(resources))
                {
                    return;
                }

                foreach (var font in Values(resources, "/Font"))
                {
                    if (!pages.TryGetValue(font, out var used))
                    {
                        pages[font] = used = [];
                        order.Add(font);
                    }

                    used.Add(number);
                    if (Name(font, "/Subtype") == "/Type3")
                    {
                        Resources(AsDictionary(font.Elements["/Resources"]), number, depth + 1);
                    }
                }

                foreach (var xobject in Values(resources, "/XObject").Where(x => Name(x, "/Subtype") == "/Form"))
                {
                    Resources(AsDictionary(xobject.Elements["/Resources"]), number, depth + 1);
                }

                foreach (var pattern in Values(resources, "/Pattern"))
                {
                    Resources(AsDictionary(pattern.Elements["/Resources"]), number, depth + 1);
                }
            }
        }

        return [.. order.Select(f => Describe(f, [.. pages[f]]))
            .OrderBy(f => f.Embedding == FontEmbedding.NotEmbedded ? 0 : 1)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static FontEntry Describe(PdfDictionary font, IReadOnlyList<int> pages)
    {
        var raw = (Name(font, "/BaseFont") ?? "/?").TrimStart('/');
        var name = PdfAInspection.FontName(font);
        var subtype = Name(font, "/Subtype");
        var descendant = subtype == "/Type0" ? AsDictionary(AsArray(font.Elements["/DescendantFonts"])?.Elements.FirstOrDefault()) : null;
        var descriptor = AsDictionary((descendant ?? font).Elements["/FontDescriptor"]);
        var type = (subtype, Name(descendant ?? font, "/Subtype")) switch
        {
            ("/Type3", _) => "Type 3",
            ("/Type0", "/CIDFontType2") => "Type 0 (CID TrueType)",
            ("/Type0", _) => "Type 0 (CID CFF)",
            ("/TrueType", _) => "TrueType",
            ("/MMType1", _) => "Multiple Master",
            _ when descriptor is not null && AsDictionary(descriptor.Elements["/FontFile3"]) is { } program
                => Name(program, "/Subtype") == "/OpenType" ? "OpenType" : "Type 1 (CFF)",
            _ => "Type 1",
        };
        var embedded = PdfAInspection.IsEmbedded(font);
        var subset = raw.Length > 7 && raw[6] == '+' && raw[..6].All(char.IsAsciiLetterUpper);
        var encoding = Resolve(font.Elements["/Encoding"]) switch
        {
            PdfName encodingName => encodingName.Value.TrimStart('/'),
            PdfDictionary differences => Name(differences, "/BaseEncoding")?.TrimStart('/') is { } baseEncoding ? baseEncoding + " + Differences" : "custom",
            _ => null,
        };
        return new FontEntry(
            name,
            type,
            !embedded ? FontEmbedding.NotEmbedded : subset ? FontEmbedding.Subset : FontEmbedding.Embedded,
            encoding,
            !embedded && Standard14.Contains(name.Split(',')[0]),
            font.Elements.ContainsKey("/ToUnicode"),
            pages);
    }

    private static IEnumerable<PdfDictionary> Values(PdfDictionary resources, string key)
        => AsDictionary(resources.Elements[key])?.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>() ?? [];
}
