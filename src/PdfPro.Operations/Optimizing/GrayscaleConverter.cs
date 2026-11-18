using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfPro.Core.Imaging;
using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>
/// Turns the colours of a document grey where they are written, keeping text and drawings as they are: colour operators
/// of page content, forms, tiling patterns, Type 3 glyphs and annotation appearances become DeviceGray (ISO 32000
/// 10.4.2 formulas; separations and DeviceN through their tint transforms), shadings get a sampled grey function,
/// images grey samples (JPEG stays JPEG, colour tables become grey tables), inline images grey data, annotation and
/// field colours grey. A page drawing something this does not handle (JPEG 2000 or CMYK JPEG images, colour-key masks,
/// mesh shadings without a function, undecodable streams…) is listed in <see cref="Failed"/>: it is made a picture of
/// itself afterwards. Objects shared by pages are converted once.
/// </summary>
internal sealed partial class GrayscaleConverter
{
    private const int MaxDepth = 24;
    private const int DefaultJpegQuality = 85;

    private readonly PdfDocument _document;
    private readonly Dictionary<PdfDictionary, bool> _done = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _open = new(ReferenceEqualityComparer.Instance);

    private GrayscaleConverter(PdfDocument document) => _document = document;

    /// <summary>The pages (0-based) not converted completely.</summary>
    public List<int> Failed { get; } = [];

    public int ImagesConverted { get; private set; }

    /// <summary>Converts <paramref name="document"/> in place.</summary>
    public static GrayscaleConverter Convert(PdfDocument document, IProgress<OperationProgress>? progress, string stage, CancellationToken cancellationToken)
    {
        var converter = new GrayscaleConverter(document);
        if (AsDictionary(document.Internals.Catalog.Elements["/AcroForm"]) is { } form)
        {
            converter.ConvertAppearanceString(form, "/DA");
        }

        for (var i = 0; i < document.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(i, document.PageCount, stage));
            bool ok;
            try
            {
                ok = converter.ConvertPage(document.Pages[i]);
            }
            catch (FormatException)
            {
                ok = false; // a tint transform or calculator function that cannot be computed
            }

            if (!ok)
            {
                converter.Failed.Add(i);
            }
        }

        return converter;
    }

    private static string N(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static byte Byte(double gray) => (byte)Math.Clamp(Math.Round(gray * 255), 0, 255);

    private bool ConvertPage(PdfPage page)
    {
        page.Elements.Remove("/Thumb"); // a colour picture of the page; viewers make their own
        var resources = PageResources(page);
        var ok = ConvertGroup(page);
        var contents = Resolve(page.Elements["/Contents"]) switch
        {
            PdfDictionary stream => [stream],
            PdfArray array => array.Elements.Select(AsDictionary).ToList(),
            _ => [],
        };
        if (contents.Count > 0)
        {
            using var joined = new MemoryStream();
            foreach (var stream in contents)
            {
                if (stream is null || StreamData.Decode(stream) is not { } data)
                {
                    return false;
                }

                joined.Write(data);
                joined.WriteByte((byte)'\n');
            }

            if (Rewrite(joined.ToArray(), resources, 0, out var changed) is not { } rewritten)
            {
                return false;
            }

            if (changed)
            {
                var content = new PdfDictionary(_document);
                content.CreateStream([]);
                SetStream(content, rewritten);
                _document.Internals.AddObject(content);
                page.Elements["/Contents"] = content.Reference!;
            }
        }

        if (Annotations(page) is { } annotations)
        {
            foreach (var item in annotations.Elements)
            {
                if (AsDictionary(item) is { } annotation)
                {
                    ok &= ConvertAnnotation(annotation);
                }
            }
        }

        return ok;
    }

    private static PdfDictionary? PageResources(PdfDictionary page)
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

    private static void SetStream(PdfDictionary dictionary, byte[] data)
    {
        var deflated = ImageRecompressor.Deflate(data);
        dictionary.Stream!.Value = deflated;
        dictionary.Elements.SetInteger("/Length", deflated.Length);
        dictionary.Elements.SetName("/Filter", "/FlateDecode");
        dictionary.Elements.Remove("/DecodeParms");
    }

    /// <summary>A transparency group's blending space becomes DeviceGray (8.4.5).</summary>
    private static bool ConvertGroup(PdfDictionary owner)
    {
        if (AsDictionary(owner.Elements["/Group"]) is { } group && group.Elements.ContainsKey("/CS"))
        {
            if (ColorSpaces.Read(group.Elements["/CS"], null) is not { } space)
            {
                return false;
            }

            if (!space.IsGray)
            {
                group.Elements.SetName("/CS", "/DeviceGray");
            }
        }

        return true;
    }

    /// <summary>Runs <paramref name="convert"/> once per object; an object being converted (a cycle) counts as done.</summary>
    private bool Once(PdfDictionary dictionary, Func<bool> convert)
    {
        if (_done.TryGetValue(dictionary, out var known))
        {
            return known;
        }

        if (!_open.Add(dictionary))
        {
            return true;
        }

        try
        {
            var ok = convert();
            _done[dictionary] = ok;
            return ok;
        }
        finally
        {
            _open.Remove(dictionary);
        }
    }

    /// <summary>
    /// The content with its colour operators grey, or null when it uses something not handled. Statements that do not
    /// change are copied byte for byte.
    /// </summary>
    private byte[]? Rewrite(byte[] content, PdfDictionary? resources, int depth, out bool changed)
    {
        changed = false;
        if (depth > MaxDepth)
        {
            return null;
        }

        using var output = new MemoryStream(content.Length + 256);
        var copied = 0;
        var (fill, stroke) = ((ColorSpace)ColorSpace.DeviceGray.Instance, (ColorSpace)ColorSpace.DeviceGray.Instance);
        var saved = new Stack<(ColorSpace Fill, ColorSpace Stroke)>();
        var reader = new ContentReader(content);
        while (reader.MoveNext())
        {
            var operands = reader.Operands;
            var numbers = operands.Select(o => o.Number).ToArray();
            string? replacement = null;
            switch (reader.Operator)
            {
                case "q":
                    saved.Push((fill, stroke));
                    break;
                case "Q":
                    if (saved.Count > 0)
                    {
                        (fill, stroke) = saved.Pop();
                    }

                    break;
                case "g":
                    fill = ColorSpace.DeviceGray.Instance;
                    break;
                case "G":
                    stroke = ColorSpace.DeviceGray.Instance;
                    break;
                case "rg" or "RG" when numbers.Length >= 3 && numbers[^3..].All(n => n is not null):
                    replacement = $"{N(ColorSpace.FromRgb(numbers[^3]!.Value, numbers[^2]!.Value, numbers[^1]!.Value))} {(reader.Operator == "rg" ? "g" : "G")}";
                    SetSpace(reader.Operator == "rg", ColorSpace.DeviceGray.Instance);
                    break;
                case "k" or "K" when numbers.Length >= 4 && numbers[^4..].All(n => n is not null):
                    replacement = $"{N(ColorSpace.FromCmyk(numbers[^4]!.Value, numbers[^3]!.Value, numbers[^2]!.Value, numbers[^1]!.Value))} {(reader.Operator == "k" ? "g" : "G")}";
                    SetSpace(reader.Operator == "k", ColorSpace.DeviceGray.Instance);
                    break;
                case "cs" or "CS":
                    if (operands.Count == 0 || !operands[^1].IsName || !SetColorSpace(reader.Operator == "cs", operands[^1].Text, resources, out replacement))
                    {
                        return null;
                    }

                    break;
                case "sc" or "scn" or "SC" or "SCN":
                    if (!SetColor(reader.Operator, operands, numbers, reader.Operator is "sc" or "scn" ? fill : stroke, resources, depth, out replacement))
                    {
                        return null;
                    }

                    break;
                case "sh":
                    if (operands.Count == 0 || !operands[^1].IsName
                        || AsDictionary(AsDictionary(resources?.Elements["/Shading"])?.Elements["/" + operands[^1].Text]) is not { } shading
                        || !ConvertShading(shading))
                    {
                        return null;
                    }

                    break;
                case "Do":
                    if (operands.Count > 0 && operands[^1].IsName && !ConvertXObject(operands[^1].Text, resources, depth))
                    {
                        return null;
                    }

                    break;
                case "gs":
                    if (operands.Count > 0 && operands[^1].IsName
                        && AsDictionary(AsDictionary(resources?.Elements["/ExtGState"])?.Elements["/" + operands[^1].Text]) is { } state
                        && !ConvertExtGState(state, depth))
                    {
                        return null;
                    }

                    break;
                case "Tf":
                    if (operands.Count >= 2 && operands[^2].IsName
                        && AsDictionary(AsDictionary(resources?.Elements["/Font"])?.Elements["/" + operands[^2].Text]) is { } font
                        && Name(font, "/Subtype") == "/Type3"
                        && !ConvertType3(font, resources, depth))
                    {
                        return null;
                    }

                    break;
                case "BI":
                    if (!ConvertInlineImage(content.AsSpan(reader.Start, reader.End - reader.Start), resources, out replacement))
                    {
                        return null;
                    }

                    break;
            }

            if (replacement is not null)
            {
                output.Write(content, copied, reader.Start - copied);
                output.Write(Encoding.ASCII.GetBytes(replacement));
                copied = reader.End;
                changed = true;
            }
        }

        output.Write(content, copied, content.Length - copied);
        return output.ToArray();

        void SetSpace(bool isFill, ColorSpace space)
        {
            if (isFill)
            {
                fill = space;
            }
            else
            {
                stroke = space;
            }
        }

        bool SetColorSpace(bool isFill, string name, PdfDictionary? resources, out string? replacement)
        {
            replacement = null;
            if (ColorSpaces.Read(new PdfName("/" + name), resources) is not { } space)
            {
                return false;
            }

            SetSpace(isFill, space);
            if (space.IsGray || space.PaintsNothing)
            {
                return true;
            }

            if (space is ColorSpace.Pattern pattern)
            {
                // An uncoloured pattern is painted in the underlying space: it is made grey where it is named.
                if (pattern.Under is { IsGray: false } && AsDictionary(resources?.Elements["/ColorSpace"])?.Elements["/" + name] is { } entry
                    && AsArray(entry) is { Elements.Count: >= 2 } array)
                {
                    array.Elements[1] = new PdfName("/DeviceGray");
                }

                return true;
            }

            var initial = space.Gray(space.Initial);
            replacement = isFill ? $"/DeviceGray cs {N(initial)} sc" : $"/DeviceGray CS {N(initial)} SC";
            return true;
        }
    }

    /// <summary>A colour in the current space: grey numbers instead, the pattern converted.</summary>
    private bool SetColor(string op, List<ContentOperand> operands, double?[] numbers, ColorSpace space, PdfDictionary? resources, int depth, out string? replacement)
    {
        replacement = null;
        if (space.PaintsNothing)
        {
            return true;
        }

        var setter = op is "sc" or "scn" ? "sc" : "SC";
        if (space is ColorSpace.Pattern pattern)
        {
            if (operands.Count == 0 || !operands[^1].IsName
                || AsDictionary(AsDictionary(resources?.Elements["/Pattern"])?.Elements["/" + operands[^1].Text]) is not { } found
                || !ConvertPattern(found, depth))
            {
                return false;
            }

            if (pattern.Under is null || pattern.Under.IsGray)
            {
                return true;
            }

            var values = numbers.Take(numbers.Length - 1).Where(n => n is not null).Select(n => n!.Value).ToArray();
            replacement = $"{N(pattern.Under.Gray(values))} /{operands[^1].Text} {op}";
            return true;
        }

        if (space.IsGray)
        {
            return true;
        }

        var components = numbers.Where(n => n is not null).Select(n => n!.Value).ToArray();
        if (components.Length < space.Components)
        {
            return false;
        }

        replacement = $"{N(space.Gray(components.AsSpan(components.Length - space.Components)))} {setter}";
        return true;
    }

    private bool ConvertXObject(string name, PdfDictionary? resources, int depth)
    {
        if (AsDictionary(AsDictionary(resources?.Elements["/XObject"])?.Elements["/" + name]) is not { Stream: not null } xObject)
        {
            return true; // nothing is drawn
        }

        return Name(xObject, "/Subtype") switch
        {
            "/Image" => Once(xObject, () => ConvertImage(xObject)),
            "/Form" => ConvertForm(xObject, resources, depth + 1),
            _ => true,
        };
    }

    /// <summary>A form's content (with its own resources, or those of what draws it) and its group.</summary>
    private bool ConvertForm(PdfDictionary form, PdfDictionary? inherited, int depth)
        => Once(form, () =>
        {
            if (form.Stream is null || StreamData.Decode(form) is not { } data)
            {
                return false;
            }

            var resources = AsDictionary(form.Elements["/Resources"]) ?? inherited;
            if (!ConvertGroup(form) || Rewrite(data, resources, depth, out var changed) is not { } rewritten)
            {
                return false;
            }

            if (changed)
            {
                SetStream(form, rewritten);
            }

            return true;
        });

    private bool ConvertPattern(PdfDictionary pattern, int depth)
        => Once(pattern, () => StreamData.Integer(pattern, "/PatternType") switch
        {
            1 => ConvertTilingPattern(pattern, depth),
            2 => (AsDictionary(pattern.Elements["/Shading"]) is not { } shading || ConvertShading(shading))
                && (AsDictionary(pattern.Elements["/ExtGState"]) is not { } state || ConvertExtGState(state, depth)),
            _ => false,
        });

    private bool ConvertTilingPattern(PdfDictionary pattern, int depth)
    {
        if (pattern.Stream is null || StreamData.Decode(pattern) is not { } data
            || Rewrite(data, AsDictionary(pattern.Elements["/Resources"]), depth + 1, out var changed) is not { } rewritten)
        {
            return false;
        }

        if (changed)
        {
            SetStream(pattern, rewritten);
        }

        return true;
    }

    /// <summary>A soft mask's group form and backdrop colour.</summary>
    private bool ConvertExtGState(PdfDictionary state, int depth)
        => Once(state, () =>
        {
            if (AsDictionary(state.Elements["/SMask"]) is not { } mask)
            {
                return true;
            }

            if (AsDictionary(mask.Elements["/G"]) is { } group)
            {
                var space = AsDictionary(group.Elements["/Group"])?.Elements["/CS"] is { } cs ? ColorSpaces.Read(cs, null) : null;
                if (PdfFunction.Numbers(mask.Elements["/BC"]) is { } backdrop && space is { IsGray: false } && backdrop.Length >= space.Components)
                {
                    mask.Elements["/BC"] = GrayArray(space.Gray(backdrop));
                }

                if (!ConvertForm(group, null, depth + 1))
                {
                    return false;
                }
            }

            return true;
        });

    private bool ConvertType3(PdfDictionary font, PdfDictionary? resources, int depth)
        => Once(font, () =>
        {
            if (AsDictionary(font.Elements["/CharProcs"]) is not { } glyphs)
            {
                return true;
            }

            var own = AsDictionary(font.Elements["/Resources"]) ?? resources;
            foreach (var item in glyphs.Elements.Values)
            {
                if (AsDictionary(item) is not { Stream: not null } glyph || StreamData.Decode(glyph) is not { } data
                    || Rewrite(data, own, depth + 1, out var changed) is not { } rewritten)
                {
                    return false;
                }

                if (changed)
                {
                    SetStream(glyph, rewritten);
                }
            }

            return true;
        });

    /// <summary>
    /// Shadings with a function (types 1–7) get a sampled grey function in DeviceGray; mesh shadings with colours in
    /// their data (types 4–7 without a function) are not handled.
    /// </summary>
    private bool ConvertShading(PdfDictionary shading)
        => Once(shading, () =>
        {
            if (ColorSpaces.Read(shading.Elements["/ColorSpace"], null) is not { } space)
            {
                return false;
            }

            if (space.IsGray)
            {
                return true;
            }

            if (space.PaintsNothing || PdfFunction.Read(shading.Elements["/Function"]) is not { } function || function.Outputs < space.Components)
            {
                return false;
            }

            var type = StreamData.Integer(shading, "/ShadingType");
            var domain = PdfFunction.Numbers(shading.Elements["/Domain"]);
            byte[] samples;
            double[] sampledDomain;
            int[] size;
            if (type == 1)
            {
                sampledDomain = domain is { Length: 4 } ? domain : [0, 1, 0, 1];
                size = [64, 64];
                samples = new byte[64 * 64];
                for (var y = 0; y < 64; y++)
                {
                    for (var x = 0; x < 64; x++)
                    {
                        var u = sampledDomain[0] + ((sampledDomain[1] - sampledDomain[0]) * x / 63);
                        var v = sampledDomain[2] + ((sampledDomain[3] - sampledDomain[2]) * y / 63);
                        samples[(y * 64) + x] = Byte(space.Gray(function.Evaluate([u, v])));
                    }
                }
            }
            else if (function.Inputs == 1)
            {
                sampledDomain = type is 2 or 3 && domain is { Length: 2 } ? domain : function.Domain;
                size = [256];
                samples = new byte[256];
                for (var i = 0; i < 256; i++)
                {
                    var t = sampledDomain[0] + ((sampledDomain[1] - sampledDomain[0]) * i / 255);
                    samples[i] = Byte(space.Gray(function.Evaluate([t])));
                }
            }
            else
            {
                return false;
            }

            var gray = new PdfDictionary(_document);
            gray.Elements.SetInteger("/FunctionType", 0);
            gray.Elements["/Domain"] = NumberArray(sampledDomain);
            gray.Elements["/Range"] = NumberArray([0, 1]);
            gray.Elements["/Size"] = NumberArray([.. size.Select(s => (double)s)]);
            gray.Elements.SetInteger("/BitsPerSample", 8);
            gray.CreateStream([]);
            SetStream(gray, samples);
            _document.Internals.AddObject(gray);
            shading.Elements["/Function"] = gray.Reference!;
            shading.Elements.SetName("/ColorSpace", "/DeviceGray");
            if (PdfFunction.Numbers(shading.Elements["/Background"]) is { } background && background.Length >= space.Components)
            {
                shading.Elements["/Background"] = GrayArray(space.Gray(background));
            }

            return true;
        });

    /// <summary>An image's samples, colour table or JPEG grey (its soft mask and stencil mask are grey already).</summary>
    private bool ConvertImage(PdfDictionary image)
    {
        if (Resolve(image.Elements["/ImageMask"]) is PdfBoolean { Value: true })
        {
            return true;
        }

        if (ColorSpaces.Read(image.Elements["/ColorSpace"], null) is not { } space || Resolve(image.Elements["/Mask"]) is PdfArray)
        {
            return false; // JPEG 2000 with its own colour space, colour-key masking (samples must stay exact)
        }

        if (space.IsGray || space.PaintsNothing)
        {
            return true;
        }

        if (space is ColorSpace.Indexed indexed)
        {
            image.Elements["/ColorSpace"] = GrayTable(indexed);
            ImagesConverted++;
            return true;
        }

        var (width, height) = (StreamData.Integer(image, "/Width") ?? 0, StreamData.Integer(image, "/Height") ?? 0);
        var bits = StreamData.Integer(image, "/BitsPerComponent") ?? 0;
        if (width <= 0 || height <= 0 || (long)width * height > ImageRecompressor.MaxPixels || StreamData.Decode(image, out var remaining) is not { } data)
        {
            return false;
        }

        var decode = PdfFunction.Numbers(image.Elements["/Decode"]);
        if (remaining.Count == 0 && GraySamples(data, width, height, bits, space, decode) is { } samples)
        {
            var flate = ImageRecompressor.Deflate(PngPredictor.Encode(samples, width, height, 1));
            ImageStreams.Write(_document, image, new ImageOutcome(flate, ImageEncoding.FlatePredicted, width, height, false), 1);
        }
        else if (remaining is [("/DCTDecode", null)] && space is ColorSpace.Rgb && bits == 8 && decode is null
            && JpegCodec.Decode(data, 3) is { Width: var w, Height: var h } pixels && w == width && h == height)
        {
            var gray = new byte[w * h];
            for (var i = 0; i < gray.Length; i++)
            {
                gray[i] = Byte(ColorSpace.FromRgb(pixels.Samples[i * 3] / 255.0, pixels.Samples[(i * 3) + 1] / 255.0, pixels.Samples[(i * 3) + 2] / 255.0));
            }

            var quality = Math.Clamp(JpegHeader.Read(data)?.Quality ?? DefaultJpegQuality, 50, 95);
            ImageStreams.Write(_document, image, new ImageOutcome(JpegCodec.Encode(new PixelImage(gray, w, h, 1), quality), ImageEncoding.Jpeg, w, h, false), 1);
        }
        else
        {
            return false;
        }

        image.Elements.SetName("/ColorSpace", "/DeviceGray");
        image.Elements.Remove("/Decode");
        image.Elements.Remove("/Alternates");
        ImagesConverted++;
        return true;
    }

    /// <summary>
    /// Grey 8-bit samples of an image of 8 or 16 bits per component in <paramref name="space"/>, its /Decode applied;
    /// null for other depths.
    /// </summary>
    private static byte[]? GraySamples(byte[] data, int width, int height, int bits, ColorSpace space, double[]? decode)
    {
        var n = space.Components;
        var bytesPerSample = bits switch { 8 => 1, 16 => 2, _ => 0 };
        if (bytesPerSample == 0 || data.LongLength < (long)width * height * n * bytesPerSample)
        {
            return null;
        }

        double[] ranges = decode is not null && decode.Length == 2 * n
            ? decode
            : space is ColorSpace.Lab ? [0, 100, -100, 100, -100, 100] : [.. Enumerable.Range(0, n).SelectMany(_ => new double[] { 0, 1 })];
        var max = bytesPerSample == 1 ? 255.0 : 65535.0;
        var gray = new byte[width * height];
        var components = new double[n];
        var plain8 = bytesPerSample == 1 && decode is null && space is ColorSpace.Rgb or ColorSpace.Cmyk;
        for (var i = 0; i < gray.Length; i++)
        {
            var at = i * n * bytesPerSample;
            if (plain8 && n == 3)
            {
                gray[i] = Byte(ColorSpace.FromRgb(data[at] / 255.0, data[at + 1] / 255.0, data[at + 2] / 255.0));
                continue;
            }

            if (plain8 && n == 4)
            {
                gray[i] = Byte(ColorSpace.FromCmyk(data[at] / 255.0, data[at + 1] / 255.0, data[at + 2] / 255.0, data[at + 3] / 255.0));
                continue;
            }

            for (var c = 0; c < n; c++)
            {
                var sample = bytesPerSample == 1 ? data[at + c] : (data[at + (2 * c)] << 8) | data[at + (2 * c) + 1];
                components[c] = ranges[2 * c] + (sample * (ranges[(2 * c) + 1] - ranges[2 * c]) / max);
            }

            gray[i] = Byte(space.Gray(components));
        }

        return gray;
    }

    /// <summary>[/Indexed /DeviceGray hival &lt;grey table&gt;]: the same indexes, each entry its grey.</summary>
    private PdfArray GrayTable(ColorSpace.Indexed indexed)
    {
        var table = new byte[indexed.Highest + 1];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = Byte(indexed.GrayOfEntry(i));
        }

        var lookup = new PdfDictionary(_document);
        lookup.CreateStream([]);
        SetStream(lookup, table);
        _document.Internals.AddObject(lookup);
        var array = new PdfArray(_document);
        array.Elements.Add(new PdfName("/Indexed"));
        array.Elements.Add(new PdfName("/DeviceGray"));
        array.Elements.Add(new PdfInteger(indexed.Highest));
        array.Elements.Add(lookup.Reference!);
        return array;
    }

    private PdfArray GrayArray(double gray) => NumberArray([gray]);

    /// <summary>An annotation colour: grey as three equal RGB components (viewers drawing their own appearances read RGB best).</summary>
    private PdfArray AnnotationGray(double gray) => NumberArray([gray, gray, gray]);

    private PdfArray NumberArray(double[] numbers)
    {
        var array = new PdfArray(_document);
        foreach (var number in numbers)
        {
            array.Elements.Add(number == Math.Floor(number) && Math.Abs(number) < int.MaxValue ? new PdfInteger((int)number) : new PdfReal(Math.Round(number, 6)));
        }

        return array;
    }

    /// <summary>Colours of an annotation (border, interior, field border and background, default appearance) and its appearances.</summary>
    private bool ConvertAnnotation(PdfDictionary annotation)
    {
        foreach (var key in new[] { "/C", "/IC" })
        {
            ConvertColorArray(annotation, key);
        }

        // A note without an appearance is drawn by the viewer in its own yellow (PDFium ignores /C): it gets a grey one.
        if (Name(annotation, "/Subtype") is "/Text" && !annotation.Elements.ContainsKey("/AP"))
        {
            AddNoteAppearance(annotation);
        }

        if (AsDictionary(annotation.Elements["/MK"]) is { } characteristics)
        {
            ConvertColorArray(characteristics, "/BC");
            ConvertColorArray(characteristics, "/BG");
        }

        ConvertAppearanceString(annotation, "/DA");
        ConvertStyleString(annotation, "/DS");
        ConvertStyleString(annotation, "/RC");
        if (AsDictionary(annotation.Elements["/AP"]) is not { } appearances)
        {
            return true;
        }

        var ok = true;
        foreach (var kind in appearances.Elements.Values)
        {
            if (AsDictionary(kind) is not { } entry)
            {
                continue;
            }

            var forms = entry.Stream is not null ? [entry] : entry.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>().Where(f => f.Stream is not null);
            foreach (var form in forms)
            {
                ok &= ConvertForm(form, null, 1);
            }
        }

        return ok;
    }

    /// <summary>A note icon (a sheet with three lines) in the note's grey, or the grey of the usual yellow.</summary>
    private void AddNoteAppearance(PdfDictionary annotation)
    {
        if (PdfFunction.Numbers(annotation.Elements["/Rect"]) is not { Length: 4 } rect)
        {
            return;
        }

        var (width, height) = (Math.Abs(rect[2] - rect[0]), Math.Abs(rect[3] - rect[1]));
        if (width < 2 || height < 2)
        {
            return;
        }

        var fill = ColorSpaces.GrayOfArray(annotation.Elements["/C"]) ?? ColorSpace.FromRgb(1, 1, 0);
        annotation.Elements["/C"] = AnnotationGray(fill);
        var content = new StringBuilder();
        content.Append(CultureInfo.InvariantCulture, $"q 1 w {N(fill)} g 0 G 0.5 0.5 {N(width - 1)} {N(height - 1)} re B\n");
        foreach (var level in new[] { 0.7, 0.5, 0.3 })
        {
            content.Append(CultureInfo.InvariantCulture, $"{N(width * 0.2)} {N(height * level)} m {N(width * (level == 0.3 ? 0.6 : 0.8))} {N(height * level)} l\n");
        }

        content.Append("S Q\n");
        var form = new PdfDictionary(_document);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = NumberArray([0, 0, width, height]);
        form.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
        _document.Internals.AddObject(form);
        var appearances = new PdfDictionary(_document);
        appearances.Elements["/N"] = form.Reference!;
        annotation.Elements["/AP"] = appearances;
    }

    private void ConvertColorArray(PdfDictionary owner, string key)
    {
        if (AsArray(owner.Elements[key]) is { Elements.Count: 3 or 4 } && ColorSpaces.GrayOfArray(owner.Elements[key]) is { } gray)
        {
            owner.Elements[key] = AnnotationGray(gray);
        }
    }

    /// <summary>"r g b rg" and "c m y k k" in a default appearance string (12.7.4.3) become "x g".</summary>
    private void ConvertAppearanceString(PdfDictionary owner, string key)
    {
        if (Resolve(owner.Elements[key]) is not PdfString text)
        {
            return;
        }

        var converted = RgbOperator().Replace(text.Value, m => $"{N(ColorSpace.FromRgb(Parse(m, 1), Parse(m, 2), Parse(m, 3)))} {(m.Groups[4].Value == "rg" ? "g" : "G")}");
        converted = CmykOperator().Replace(converted, m => $"{N(ColorSpace.FromCmyk(Parse(m, 1), Parse(m, 2), Parse(m, 3), Parse(m, 4)))} {(m.Groups[5].Value == "k" ? "g" : "G")}");
        if (converted != text.Value)
        {
            owner.Elements[key] = new PdfString(converted);
        }

        static double Parse(Match match, int group) => double.Parse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>CSS colours "#rrggbb" in a free text annotation's style or rich text (12.7.3.4) become grey.</summary>
    private void ConvertStyleString(PdfDictionary owner, string key)
    {
        if (Resolve(owner.Elements[key]) is not PdfString text)
        {
            return;
        }

        var converted = HexColor().Replace(text.Value, m =>
        {
            var rgb = System.Convert.FromHexString(m.Groups[1].Value);
            var level = Byte(ColorSpace.FromRgb(rgb[0] / 255.0, rgb[1] / 255.0, rgb[2] / 255.0));
            return "#" + System.Convert.ToHexString([level, level, level]);
        });
        if (converted != text.Value)
        {
            owner.Elements[key] = new PdfString(converted, text.Encoding == PdfStringEncoding.Unicode ? PdfStringEncoding.Unicode : PdfStringEncoding.PDFDocEncoding);
        }
    }

    [GeneratedRegex(@"(?<![\w.])([-+]?\d*\.?\d+)\s+([-+]?\d*\.?\d+)\s+([-+]?\d*\.?\d+)\s+(rg|RG)\b")]
    private static partial Regex RgbOperator();

    [GeneratedRegex(@"(?<![\w.])([-+]?\d*\.?\d+)\s+([-+]?\d*\.?\d+)\s+([-+]?\d*\.?\d+)\s+([-+]?\d*\.?\d+)\s+(k|K)\b")]
    private static partial Regex CmykOperator();

    [GeneratedRegex("#([0-9A-Fa-f]{6})")]
    private static partial Regex HexColor();
}
