using System.Text;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Optimizing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Converting.PdfA;

/// <summary>
/// What still keeps a document from PDF/A after <see cref="PdfAStructure"/>, page by page: fonts that are not embedded,
/// CMYK colours without a default colour space for them, and, for PDF/A-1, transparency, JPEG 2000 and 16-bit images;
/// and LZW streams left anywhere. Everything a page's resources reach (forms, patterns, Type 3 glyphs, soft masks, the
/// appearances of its comments) is looked at, used or not: it is a check, not a validation.
/// </summary>
internal static class PdfAInspection
{
    public static IReadOnlyList<PdfAProblem> Problems(PdfDocument document, PdfALevel level)
    {
        var fonts = new SortedDictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var found = new SortedDictionary<PdfAProblemKind, SortedSet<int>>();
        for (var i = 0; i < document.PageCount; i++)
        {
            new PageInspector(level, i + 1, fonts, found).Page(document.Pages[i]);
        }

        var problems = new List<PdfAProblem>();
        problems.AddRange(fonts.Select(f => new PdfAProblem(PdfAProblemKind.FontNotEmbedded, [.. f.Value], f.Key)));
        problems.AddRange(found.Select(p => new PdfAProblem(p.Key, [.. p.Value])));
        if (document.Internals.GetAllObjects().OfType<PdfDictionary>().Any(d => d.Stream is not null && StreamData.Filters(d) is { } filters && filters.Any(f => f.Name == "/LZWDecode")))
        {
            problems.Add(new PdfAProblem(PdfAProblemKind.LzwStreams, []));
        }

        return problems;
    }

    /// <summary>The font's name without the subset tag ("ABCDEF+Arial-BoldMT" → "Arial-BoldMT").</summary>
    public static string FontName(PdfDictionary font)
    {
        var name = (Name(font, "/BaseFont") ?? "/?").TrimStart('/');
        return name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper) ? name[7..] : name;
    }

    /// <summary>Whether a font's program is in the file (Type 3 fonts are their own glyphs).</summary>
    public static bool IsEmbedded(PdfDictionary font)
    {
        var subtype = Name(font, "/Subtype");
        if (subtype == "/Type3")
        {
            return true;
        }

        var described = subtype == "/Type0" && AsArray(font.Elements["/DescendantFonts"]) is { Elements.Count: > 0 } descendants
            ? AsDictionary(descendants.Elements[0])
            : font;
        return described is not null && AsDictionary(described.Elements["/FontDescriptor"]) is { } descriptor
            && (descriptor.Elements.ContainsKey("/FontFile") || descriptor.Elements.ContainsKey("/FontFile2") || descriptor.Elements.ContainsKey("/FontFile3"));
    }

    /// <summary>Whether a colour space is, or is built on, DeviceCMYK.</summary>
    public static bool UsesCmyk(PdfItem? space, int depth = 0)
    {
        if (depth > 8)
        {
            return false;
        }

        return Resolve(space) switch
        {
            PdfName name => name.Value is "/DeviceCMYK" or "/CMYK",
            PdfArray { Elements.Count: > 1 } array when Resolve(array.Elements[0]) is PdfName family => family.Value switch
            {
                "/Indexed" or "/I" or "/Pattern" => UsesCmyk(array.Elements[1], depth + 1),
                "/Separation" or "/DeviceN" => array.Elements.Count > 2 && UsesCmyk(array.Elements[2], depth + 1),
                _ => false,
            },
            _ => false,
        };
    }

    /// <summary>Whether the resources set a default colour space for DeviceCMYK.</summary>
    public static bool HasDefaultCmyk(PdfDictionary? resources)
        => resources is not null && AsDictionary(resources.Elements["/ColorSpace"]) is { } spaces && spaces.Elements.ContainsKey("/DefaultCMYK");

    /// <summary>The page's resources, inherited from the page tree when it has none of its own.</summary>
    public static PdfDictionary? PageResources(PdfDictionary page)
    {
        var node = page;
        for (var i = 0; node is not null && i < 64; i++)
        {
            if (AsDictionary(node.Elements["/Resources"]) is { } resources)
            {
                return resources;
            }

            node = AsDictionary(node.Elements["/Parent"]);
        }

        return null;
    }

    /// <summary>The page's content streams decoded and joined (those that cannot be decoded are left out).</summary>
    public static byte[] PageContent(PdfDictionary page)
    {
        var streams = Resolve(page.Elements["/Contents"]) switch
        {
            PdfArray array => array.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList(),
            PdfDictionary single => [single],
            _ => [],
        };
        using var joined = new MemoryStream();
        foreach (var stream in streams)
        {
            if (StreamData.Decode(stream) is { } data)
            {
                joined.Write(data);
                joined.WriteByte((byte)'\n');
            }
        }

        return joined.ToArray();
    }

    private sealed class PageInspector(PdfALevel level, int page, SortedDictionary<string, SortedSet<int>> fonts, SortedDictionary<PdfAProblemKind, SortedSet<int>> found)
    {
        private readonly HashSet<PdfDictionary> _visited = new(ReferenceEqualityComparer.Instance);

        public void Page(PdfPage pageDictionary)
        {
            var resources = PageResources(pageDictionary);
            if (level == PdfALevel.A1B && IsTransparencyGroup(pageDictionary))
            {
                Found(PdfAProblemKind.Transparency);
            }

            Content(PageContent(pageDictionary), resources);
            Resources(resources);
            foreach (var annotation in Annotations(pageDictionary)?.Elements.Select(AsDictionary).OfType<PdfDictionary>() ?? [])
            {
                if (AsDictionary(annotation.Elements["/AP"]) is not { } appearances)
                {
                    continue;
                }

                foreach (var entry in appearances.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>())
                {
                    var forms = entry.Stream is not null ? [entry] : entry.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>().Where(f => f.Stream is not null);
                    foreach (var form in forms)
                    {
                        Form(form, resources);
                    }
                }
            }
        }

        private void Found(PdfAProblemKind kind)
        {
            if (!found.TryGetValue(kind, out var pages))
            {
                found[kind] = pages = [];
            }

            pages.Add(page);
        }

        private void Resources(PdfDictionary? resources)
        {
            if (resources is null || !_visited.Add(resources))
            {
                return;
            }

            foreach (var font in Values(resources, "/Font"))
            {
                Font(font);
            }

            foreach (var xobject in Values(resources, "/XObject"))
            {
                XObject(xobject, resources);
            }

            foreach (var pattern in Values(resources, "/Pattern"))
            {
                Pattern(pattern, resources);
            }

            foreach (var shading in Values(resources, "/Shading"))
            {
                Shading(shading, resources);
            }

            foreach (var state in Values(resources, "/ExtGState"))
            {
                GraphicsState(state, resources);
            }

            if (AsDictionary(resources.Elements["/ColorSpace"]) is { } spaces && !HasDefaultCmyk(resources)
                && spaces.Elements.Values.Any(s => UsesCmyk(s)))
            {
                Found(PdfAProblemKind.DeviceCmyk);
            }
        }

        private void Font(PdfDictionary font)
        {
            if (!_visited.Add(font))
            {
                return;
            }

            if (Name(font, "/Subtype") == "/Type3")
            {
                var resources = AsDictionary(font.Elements["/Resources"]);
                Resources(resources);
                foreach (var glyph in AsDictionary(font.Elements["/CharProcs"])?.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>() ?? [])
                {
                    if (StreamData.Decode(glyph) is { } data)
                    {
                        Content(data, resources);
                    }
                }

                return;
            }

            if (!IsEmbedded(font))
            {
                var name = FontName(font);
                if (!fonts.TryGetValue(name, out var pages))
                {
                    fonts[name] = pages = [];
                }

                pages.Add(page);
            }
        }

        private void XObject(PdfDictionary xobject, PdfDictionary? resources)
        {
            if (!_visited.Add(xobject))
            {
                return;
            }

            switch (Name(xobject, "/Subtype"))
            {
                case "/Image":
                    if (UsesCmyk(xobject.Elements["/ColorSpace"]) && !HasDefaultCmyk(resources))
                    {
                        Found(PdfAProblemKind.DeviceCmyk);
                    }

                    if (level == PdfALevel.A1B)
                    {
                        if (xobject.Elements.ContainsKey("/SMask") || StreamData.Integer(xobject, "/SMaskInData") is > 0)
                        {
                            Found(PdfAProblemKind.Transparency);
                        }

                        if (StreamData.Filters(xobject)?.Any(f => f.Name == "/JPXDecode") == true)
                        {
                            Found(PdfAProblemKind.Jpeg2000);
                        }

                        if (StreamData.Integer(xobject, "/BitsPerComponent") == 16)
                        {
                            Found(PdfAProblemKind.SixteenBitImages);
                        }
                    }

                    if (AsDictionary(xobject.Elements["/SMask"]) is { } mask)
                    {
                        XObject(mask, resources);
                    }

                    break;
                case "/Form":
                    Form(xobject, resources);
                    break;
            }
        }

        private void Form(PdfDictionary form, PdfDictionary? outer)
        {
            if (!_visited.Add(form))
            {
                return;
            }

            if (level == PdfALevel.A1B && IsTransparencyGroup(form))
            {
                Found(PdfAProblemKind.Transparency);
            }

            var resources = AsDictionary(form.Elements["/Resources"]) ?? outer;
            if (StreamData.Decode(form) is { } data)
            {
                Content(data, resources);
            }

            Resources(resources);
        }

        private void Pattern(PdfDictionary pattern, PdfDictionary? outer)
        {
            if (!_visited.Add(pattern))
            {
                return;
            }

            if (StreamData.Integer(pattern, "/PatternType") == 2)
            {
                if (AsDictionary(pattern.Elements["/Shading"]) is { } shading)
                {
                    Shading(shading, outer);
                }

                return;
            }

            var resources = AsDictionary(pattern.Elements["/Resources"]) ?? outer;
            if (StreamData.Decode(pattern) is { } data)
            {
                Content(data, resources);
            }

            Resources(resources);
        }

        private void Shading(PdfDictionary shading, PdfDictionary? resources)
        {
            if (UsesCmyk(shading.Elements["/ColorSpace"]) && !HasDefaultCmyk(resources))
            {
                Found(PdfAProblemKind.DeviceCmyk);
            }
        }

        private void GraphicsState(PdfDictionary state, PdfDictionary? resources)
        {
            if (!_visited.Add(state))
            {
                return;
            }

            var mask = Resolve(state.Elements["/SMask"]);
            if (level == PdfALevel.A1B)
            {
                var opacity = new[] { "/CA", "/ca" }.Select(k => Resolve(state.Elements[k])).Any(v => v is PdfReal { Value: < 1 } or PdfInteger { Value: < 1 });
                var blend = Resolve(state.Elements["/BM"]) switch
                {
                    PdfName name => name.Value is not ("/Normal" or "/Compatible"),
                    PdfArray modes => modes.Elements.Select(Resolve).OfType<PdfName>().FirstOrDefault()?.Value is not (null or "/Normal" or "/Compatible"),
                    _ => false,
                };
                if (mask is PdfDictionary || opacity || blend)
                {
                    Found(PdfAProblemKind.Transparency);
                }
            }

            if (mask is PdfDictionary softMask && AsDictionary(softMask.Elements["/G"]) is { } group)
            {
                Form(group, resources);
            }
        }

        /// <summary>
        /// Colour operators of a content stream: k / K (DeviceCMYK), cs / CS naming it, inline images in CMYK —
        /// unless the resources it draws with set a default colour space for it.
        /// </summary>
        private void Content(byte[] content, PdfDictionary? resources)
        {
            if (content.Length == 0 || HasDefaultCmyk(resources))
            {
                return;
            }

            var reader = new ContentReader(content);
            while (reader.MoveNext())
            {
                var cmyk = reader.Operator switch
                {
                    "k" or "K" => true,
                    "cs" or "CS" => reader.Operands.Count == 1 && reader.Operands[0] is { IsName: true, Text: "DeviceCMYK" },
                    "BI" => InlineImageIsCmyk(content.AsSpan(reader.Start, reader.End - reader.Start)),
                    _ => false,
                };
                if (cmyk)
                {
                    Found(PdfAProblemKind.DeviceCmyk);
                    return;
                }
            }
        }

        private static bool InlineImageIsCmyk(ReadOnlySpan<byte> image)
        {
            var header = Encoding.Latin1.GetString(image[..Math.Min(image.Length, 512)]);
            var data = header.IndexOf(" ID", StringComparison.Ordinal);
            var dictionary = data < 0 ? header : header[..data];
            return dictionary.Contains("/DeviceCMYK", StringComparison.Ordinal) || dictionary.Contains("/CMYK", StringComparison.Ordinal);
        }

        private static bool IsTransparencyGroup(PdfDictionary owner)
            => AsDictionary(owner.Elements["/Group"]) is { } group && Name(group, "/S") == "/Transparency";

        private static IEnumerable<PdfDictionary> Values(PdfDictionary resources, string category)
            => AsDictionary(resources.Elements[category])?.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>().ToList() ?? [];
    }
}
