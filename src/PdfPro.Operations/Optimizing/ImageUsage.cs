using PdfPro.Core.Geometry;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>The largest an image is drawn at, in points along its own width and height (default user space).</summary>
internal readonly record struct DisplaySize(double Width, double Height)
{
    public DisplaySize Max(DisplaySize other) => new(Math.Max(Width, other.Width), Math.Max(Height, other.Height));
}

/// <summary>
/// How large each image XObject of a document is drawn: the content of every page, of the forms they paint (nested),
/// of annotation appearances, tiling patterns and soft masks is followed with its transformation matrix, and each
/// "Do" of an image records the size the unit square has under it. An image whose size is not known — drawn by a
/// Type 3 glyph, or reachable from content that cannot be read, or never drawn where it is looked for — is not in
/// <see cref="Sizes"/> and is left as it is.
/// </summary>
internal sealed class ImageUsage
{
    /// <summary>Statements read in all, beyond which the measure gives up (every image is then left as it is).</summary>
    private const long MaxStatements = 20_000_000;

    /// <summary>Forms inside forms deeper than this are not followed (their images stay unmeasured).</summary>
    private const int MaxDepth = 16;

    private readonly Dictionary<PdfDictionary, DisplaySize> _sizes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _unknown = new(ReferenceEqualityComparer.Instance);
    private long _statements;

    private ImageUsage()
    {
    }

    /// <summary>The images whose display size is known, by their dictionary.</summary>
    public IReadOnlyDictionary<PdfDictionary, DisplaySize> Sizes => _sizes;

    /// <summary>Whether the measure ran out of its budget (nothing is then known).</summary>
    public bool GaveUp { get; private set; }

    public static ImageUsage Measure(PdfDocument document, CancellationToken cancellationToken)
    {
        var usage = new ImageUsage();
        try
        {
            foreach (var page in document.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                usage.MeasurePage(page);
            }
        }
        catch (BudgetExceededException)
        {
            usage.GaveUp = true;
            usage._sizes.Clear();
        }

        foreach (var image in usage._unknown)
        {
            usage._sizes.Remove(image);
        }

        return usage;
    }

    private void MeasurePage(PdfPage page)
    {
        var userUnit = AppearancePlacement.Number(page.Elements["/UserUnit"] ?? new PdfInteger(1));
        var space = userUnit > 0 ? PdfMatrix.Scaling(userUnit, userUnit) : PdfMatrix.Identity;
        var resources = PageResources(page);
        if (PageContent(page) is { } content)
        {
            Walk(content, resources, space, space, 0, []);
        }
        else
        {
            MarkUnknown(resources, 0);
        }

        if (Annotations(page) is not { } annotations)
        {
            return;
        }

        foreach (var item in annotations.Elements)
        {
            if (AsDictionary(item) is not { } annotation || AsArray(annotation.Elements["/Rect"]) is not { Elements.Count: 4 } rect
                || AsDictionary(annotation.Elements["/AP"]) is not { } appearances)
            {
                continue;
            }

            foreach (var kind in appearances.Elements.Values)
            {
                if (AsDictionary(kind) is not { } entry)
                {
                    continue;
                }

                // One stream, or one per state.
                var forms = entry.Stream is not null ? [entry] : entry.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>().Where(f => f.Stream is not null);
                foreach (var form in forms)
                {
                    if (AppearancePlacement.Fit(form, AppearancePlacement.Rectangle(rect)) is { } fit)
                    {
                        DrawForm(form, resources, fit.Then(space), 0, []);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Follows a content stream drawn with <paramref name="ctm"/>, whose patterns map to <paramref name="patternSpace"/>
    /// (the default space of the page or form the stream belongs to).
    /// </summary>
    private void Walk(byte[] content, PdfDictionary? resources, PdfMatrix ctm, PdfMatrix patternSpace, int depth, HashSet<PdfDictionary> open)
    {
        MeasurePatterns(resources, patternSpace, depth, open);
        MarkType3Fonts(resources, depth);
        var saved = new Stack<PdfMatrix>();
        var reader = new ContentReader(content);
        while (reader.MoveNext())
        {
            if (++_statements > MaxStatements)
            {
                throw new BudgetExceededException();
            }

            var operands = reader.Operands;
            switch (reader.Operator)
            {
                case "q":
                    if (saved.Count < 4096)
                    {
                        saved.Push(ctm);
                    }

                    break;
                case "Q":
                    if (saved.TryPop(out var restored))
                    {
                        ctm = restored;
                    }

                    break;
                case "cm" when operands.Count == 6 && operands.All(o => o.Number is not null):
                    ctm = new PdfMatrix(operands[0].Number!.Value, operands[1].Number!.Value, operands[2].Number!.Value, operands[3].Number!.Value, operands[4].Number!.Value, operands[5].Number!.Value).Then(ctm);
                    break;
                case "Do" when operands is [{ IsName: true, Text: var name }]:
                    if (XObject(resources, name) is { } xObject)
                    {
                        switch (Name(xObject, "/Subtype"))
                        {
                            case "/Image":
                                Record(xObject, ctm);
                                break;
                            case "/Form":
                                DrawForm(xObject, resources, ctm, depth, open);
                                break;
                        }
                    }

                    break;
                case "gs" when operands is [{ IsName: true, Text: var state }]:
                    // A soft mask's group is drawn in the space current when the graphics state is set (11.6.5.2).
                    if (Entry(resources, "/ExtGState", state) is { } graphicsState && AsDictionary(graphicsState.Elements["/SMask"]) is { } mask
                        && AsDictionary(mask.Elements["/G"]) is { Stream: not null } group)
                    {
                        DrawForm(group, resources, ctm, depth, open);
                    }

                    break;
            }
        }
    }

    /// <summary>A form XObject painted with <paramref name="ctm"/>: its content goes through its /Matrix.</summary>
    private void DrawForm(PdfDictionary form, PdfDictionary? outerResources, PdfMatrix ctm, int depth, HashSet<PdfDictionary> open)
    {
        var resources = AsDictionary(form.Elements["/Resources"]) ?? outerResources;
        if (depth >= MaxDepth || !open.Add(form))
        {
            MarkUnknown(resources, depth);
            return;
        }

        try
        {
            var space = AppearancePlacement.FormMatrix(form).Then(ctm);
            if (StreamData.Decode(form) is { } content)
            {
                Walk(content, resources, space, space, depth + 1, open);
            }
            else
            {
                MarkUnknown(resources, depth);
            }
        }
        finally
        {
            open.Remove(form);
        }
    }

    /// <summary>Tiling patterns map their cells to the default space of the stream using them (8.7.3.1).</summary>
    private void MeasurePatterns(PdfDictionary? resources, PdfMatrix patternSpace, int depth, HashSet<PdfDictionary> open)
    {
        if (resources is null || AsDictionary(resources.Elements["/Pattern"]) is not { } patterns)
        {
            return;
        }

        foreach (var value in patterns.Elements.Values)
        {
            if (AsDictionary(value) is { Stream: not null } pattern && StreamData.Integer(pattern, "/PatternType") == 1)
            {
                DrawForm(pattern, resources, patternSpace, depth, open);
            }
        }
    }

    /// <summary>Images a Type 3 glyph draws are sized by the text that shows it: not measured, so left alone.</summary>
    private void MarkType3Fonts(PdfDictionary? resources, int depth)
    {
        if (resources is null || AsDictionary(resources.Elements["/Font"]) is not { } fonts)
        {
            return;
        }

        foreach (var value in fonts.Elements.Values)
        {
            if (AsDictionary(value) is { } font && Name(font, "/Subtype") == "/Type3")
            {
                MarkUnknown(AsDictionary(font.Elements["/Resources"]), depth);
            }
        }
    }

    /// <summary>Every image reachable from <paramref name="resources"/> (through forms too) has an unknown size.</summary>
    private void MarkUnknown(PdfDictionary? resources, int depth)
    {
        if (resources is null || depth > MaxDepth || AsDictionary(resources.Elements["/XObject"]) is not { } xObjects)
        {
            return;
        }

        foreach (var value in xObjects.Elements.Values)
        {
            if (AsDictionary(value) is not { } xObject)
            {
                continue;
            }

            if (Name(xObject, "/Subtype") == "/Image")
            {
                _unknown.Add(xObject);
            }
            else if (AsDictionary(xObject.Elements["/Resources"]) is { } inner && !ReferenceEquals(inner, resources))
            {
                MarkUnknown(inner, depth + 1);
            }
        }
    }

    private void Record(PdfDictionary image, PdfMatrix ctm)
    {
        // The unit square of the image goes to a parallelogram: its sides are the image's width and height.
        var size = new DisplaySize(Math.Sqrt((ctm.A * ctm.A) + (ctm.B * ctm.B)), Math.Sqrt((ctm.C * ctm.C) + (ctm.D * ctm.D)));
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
        {
            _unknown.Add(image);
            return;
        }

        _sizes[image] = _sizes.TryGetValue(image, out var known) ? known.Max(size) : size;
    }

    private static PdfDictionary? XObject(PdfDictionary? resources, string name) => Entry(resources, "/XObject", name);

    private static PdfDictionary? Entry(PdfDictionary? resources, string category, string name)
        => resources is not null && AsDictionary(resources.Elements[category]) is { } entries ? AsDictionary(entries.Elements["/" + name]) : null;

    /// <summary>The page's resources, inherited from the page tree when it has none of its own.</summary>
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

    /// <summary>The page's content streams decoded and joined, or null when one cannot be decoded.</summary>
    private static byte[]? PageContent(PdfDictionary page)
    {
        var contents = Resolve(page.Elements["/Contents"]);
        var streams = contents switch
        {
            PdfDictionary stream => [stream],
            PdfArray array => array.Elements.Select(AsDictionary).ToList(),
            _ => [],
        };

        using var joined = new MemoryStream();
        foreach (var stream in streams)
        {
            if (stream is null || StreamData.Decode(stream) is not { } data)
            {
                return null;
            }

            joined.Write(data);
            joined.WriteByte((byte)'\n');
        }

        return joined.ToArray();
    }

    private sealed class BudgetExceededException : Exception;
}
