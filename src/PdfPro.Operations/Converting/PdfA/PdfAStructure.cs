using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfPro.Core.Forms;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Security;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Converting.PdfA;

/// <summary>What a conversion changed, counted by kind.</summary>
internal sealed class PdfAChanges
{
    private readonly Dictionary<PdfAFix, int> _counts = [];

    public int this[PdfAFix fix] => _counts.GetValueOrDefault(fix);

    public void Add(PdfAFix fix, int count = 1)
    {
        if (count > 0)
        {
            _counts[fix] = _counts.GetValueOrDefault(fix) + count;
        }
    }

    public IReadOnlyList<PdfAChange> ToList() => [.. _counts.OrderBy(c => c.Key).Select(c => new PdfAChange(c.Key, c.Value))];
}

/// <summary>
/// The changes of the document's objects that PDF/A asks for (ISO 19005-1 and -2, level B), made with PDFsharp at the
/// level of dictionaries: forbidden actions, scripts, attachments and multimedia removed; comments printable, with only
/// a normal appearance (PDF/A-2: every comment has one); form fields drawn; layers settled; LZW streams compressed
/// again; image alternates, OPI, PostScript and transfer functions removed; XMP of objects other than the catalog
/// removed; and an sRGB output intent. Fonts, CMYK colours and transparency are looked at by <see cref="PdfAInspection"/>.
/// </summary>
internal static class PdfAStructure
{
    public const string OutputCondition = "sRGB IEC61966-2.1";

    /// <summary>Upper bound for the dictionaries visited (defends against hostile files).</summary>
    private const int MaxDictionaries = 2_000_000;

    // Annotation flags (ISO 32000-1 12.5.3).
    private const int Invisible = 1;
    private const int Hidden = 2;
    private const int Print = 4;
    private const int NoView = 32;
    private const int ToggleNoView = 256;

    private static readonly HashSet<string> PageTurns = new(StringComparer.Ordinal) { "/NextPage", "/PrevPage", "/FirstPage", "/LastPage" };

    /// <summary>The annotation types of PDF 1.4 (PDF/A-1 allows these, but sound, movie and attachments).</summary>
    private static readonly HashSet<string> Pdf14Annotations = new(StringComparer.Ordinal)
    {
        "/Text", "/Link", "/FreeText", "/Line", "/Square", "/Circle", "/Highlight", "/Underline", "/Squiggly", "/StrikeOut",
        "/Stamp", "/Ink", "/Popup", "/Widget", "/PrinterMark", "/TrapNet",
    };

    /// <summary>The other annotation types of ISO 32000-1 that PDF/A-2 allows.</summary>
    private static readonly HashSet<string> Iso32000Annotations = new(StringComparer.Ordinal) { "/Polygon", "/PolyLine", "/Caret", "/Watermark", "/Redact" };

    private static readonly HashSet<string> MultimediaAnnotations = new(StringComparer.Ordinal) { "/Sound", "/Movie", "/Screen", "/3D", "/RichMedia" };

    private static readonly HashSet<string> RenderingIntents = new(StringComparer.Ordinal) { "/RelativeColorimetric", "/AbsoluteColorimetric", "/Perceptual", "/Saturation" };

    public static void Apply(PdfDocument document, PdfALevel level, PdfAChanges changes, CancellationToken cancellationToken)
    {
        Layers(document, level, changes, cancellationToken);
        Catalog(document, changes);
        var signatures = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var dictionary in AllDictionaries(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary(document, dictionary, level, changes, signatures);
        }

        changes.Add(PdfAFix.SignaturesInvalidated, signatures.Count);
        Forms(document, changes);
        Comments(document, level, changes, cancellationToken);
        OutputIntent(document, changes);
    }

    /// <summary>Data compressed with Flate, as zlib (RFC 1950) wants it.</summary>
    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Every dictionary of the document: the indirect objects and what they hold directly.</summary>
    public static List<PdfDictionary> AllDictionaries(PdfDocument document)
    {
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var result = new List<PdfDictionary>();
        var pending = new Stack<PdfItem>(document.Internals.GetAllObjects());
        while (pending.Count > 0 && seen.Count < MaxDictionaries)
        {
            switch (pending.Pop())
            {
                case PdfDictionary dictionary when seen.Add(dictionary):
                    result.Add(dictionary);
                    foreach (var value in dictionary.Elements.Values)
                    {
                        if (value is PdfDictionary or PdfArray)
                        {
                            pending.Push(value);
                        }
                    }

                    break;
                case PdfArray array when seen.Add(array):
                    foreach (var value in array.Elements)
                    {
                        if (value is PdfDictionary or PdfArray)
                        {
                            pending.Push(value);
                        }
                    }

                    break;
            }
        }

        return result;
    }

    public static List<PdfDictionary> Dictionaries(PdfItem? item)
        => AsArray(item)?.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList() ?? [];

    /// <summary>PDF/A-1 has no layers: hidden ones go with what they draw, the others become plain content. PDF/A-2: every configuration is named, has no /AS, and orders every group.</summary>
    private static void Layers(PdfDocument document, PdfALevel level, PdfAChanges changes, CancellationToken cancellationToken)
    {
        var catalog = document.Internals.Catalog;
        if (AsDictionary(catalog.Elements["/OCProperties"]) is not { } properties)
        {
            return;
        }

        var groups = Dictionaries(properties.Elements["/OCGs"]);
        if (level == PdfALevel.A1B)
        {
            DocumentSanitizer.Apply(
                document,
                new SanitizeParams(Metadata: false, Scripts: false, Attachments: false, RiskyActions: false, ExternalLinks: false, HiddenLayers: true, OutsideVisibleArea: false, Thumbnails: false, PrivateData: false),
                new HashSet<int>(),
                cancellationToken);
            catalog.Elements.Remove("/OCProperties");
            foreach (var dictionary in AllDictionaries(document).Where(d => d.Elements.ContainsKey("/OC")))
            {
                dictionary.Elements.Remove("/OC");
            }

            changes.Add(PdfAFix.Layers, Math.Max(1, groups.Count));
            return;
        }

        var fixes = 0;
        var configurations = new List<PdfDictionary>();
        if (AsDictionary(properties.Elements["/D"]) is { } main)
        {
            configurations.Add(main);
        }

        configurations.AddRange(Dictionaries(properties.Elements["/Configs"]));
        for (var i = 0; i < configurations.Count; i++)
        {
            var configuration = configurations[i];
            if (Resolve(configuration.Elements["/Name"]) is not PdfString { Value.Length: > 0 })
            {
                configuration.Elements["/Name"] = new PdfString(i == 0 ? "Default" : string.Create(CultureInfo.InvariantCulture, $"Configuration {i + 1}"));
                fixes++;
            }

            if (configuration.Elements.ContainsKey("/AS"))
            {
                configuration.Elements.Remove("/AS");
                fixes++;
            }

            if (AsArray(configuration.Elements["/Order"]) is { } order)
            {
                var listed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                Listed(order, listed, 0);
                foreach (var group in groups.Where(g => !listed.Contains(g)))
                {
                    order.Elements.Add(group.Reference is { } reference ? reference : group);
                    fixes++;
                }
            }
        }

        changes.Add(PdfAFix.Layers, fixes);
    }

    private static void Listed(PdfArray order, HashSet<PdfDictionary> listed, int depth)
    {
        foreach (var item in order.Elements)
        {
            switch (Resolve(item))
            {
                case PdfDictionary group:
                    listed.Add(group);
                    break;
                case PdfArray nested when depth < 16:
                    Listed(nested, listed, depth + 1);
                    break;
            }
        }
    }

    private static void Catalog(PdfDocument document, PdfAChanges changes)
    {
        var catalog = document.Internals.Catalog;
        if (AsDictionary(catalog.Elements["/Names"]) is { } names)
        {
            if (names.Elements.ContainsKey("/JavaScript"))
            {
                changes.Add(PdfAFix.Scripts, Math.Max(1, NameTreeLeaves(AsDictionary(names.Elements["/JavaScript"]), 0)));
                names.Elements.Remove("/JavaScript");
            }

            if (names.Elements.ContainsKey("/EmbeddedFiles"))
            {
                changes.Add(PdfAFix.Attachments, Math.Max(1, NameTreeLeaves(AsDictionary(names.Elements["/EmbeddedFiles"]), 0)));
                names.Elements.Remove("/EmbeddedFiles");
            }

            if (names.Elements.ContainsKey("/AlternatePresentations"))
            {
                changes.Add(PdfAFix.Multimedia);
                names.Elements.Remove("/AlternatePresentations");
            }

            if (names.Elements.Count == 0)
            {
                catalog.Elements.Remove("/Names");
            }
        }

        if (catalog.Elements.ContainsKey("/Collection"))
        {
            catalog.Elements.Remove("/Collection");
            changes.Add(PdfAFix.Attachments);
        }

        // Usage rights and modification permissions are signatures of a document this conversion changes.
        foreach (var key in new[] { "/Perms", "/Requirements", "/NeedsRendering" })
        {
            catalog.Elements.Remove(key);
        }
    }

    private static void Dictionary(PdfDocument document, PdfDictionary dictionary, PdfALevel level, PdfAChanges changes, HashSet<PdfDictionary> signatures)
    {
        var catalog = document.Internals.Catalog;
        foreach (var key in new[] { "/A", "/OpenAction" })
        {
            if (AsDictionary(dictionary.Elements[key]) is { } action && IsAction(action) && Forbidden(action) is { } fix)
            {
                dictionary.Elements.Remove(key);
                changes.Add(fix);
            }
        }

        // Additional actions (triggers of scripts mostly) are forbidden on the catalog, pages, fields and widgets.
        if (AsDictionary(dictionary.Elements["/AA"]) is { } additional)
        {
            foreach (var action in additional.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>())
            {
                changes.Add(Forbidden(action) ?? PdfAFix.Actions);
            }

            dictionary.Elements.Remove("/AA");
        }

        if (IsAction(dictionary))
        {
            Chain(dictionary, changes);
            if (Name(dictionary, "/S") == "/Rendition" && dictionary.Elements.ContainsKey("/JS"))
            {
                dictionary.Elements.Remove("/JS");
                changes.Add(PdfAFix.Scripts);
            }
        }

        if (dictionary.Stream is not null)
        {
            foreach (var key in new[] { "/F", "/FFilter", "/FDecodeParms" })
            {
                dictionary.Elements.Remove(key);
            }

            if (Recompress(document, dictionary))
            {
                changes.Add(PdfAFix.LzwStreams);
            }
        }

        if (dictionary.Elements.ContainsKey("/Metadata") && !ReferenceEquals(dictionary, catalog))
        {
            dictionary.Elements.Remove("/Metadata");
            changes.Add(PdfAFix.ObjectMetadata);
        }

        switch (Name(dictionary, "/Subtype"))
        {
            case "/Image":
                if (Remove(dictionary, "/Alternates", "/OPI"))
                {
                    changes.Add(PdfAFix.AlternateImages);
                }

                if (dictionary.Elements.ContainsKey("/Interpolate"))
                {
                    var smoothing = Resolve(dictionary.Elements["/Interpolate"]) is PdfBoolean { Value: true };
                    dictionary.Elements.Remove("/Interpolate");
                    changes.Add(PdfAFix.ImageInterpolation, smoothing ? 1 : 0);
                }

                break;
            case "/Form":
                if (Remove(dictionary, "/OPI", "/Ref"))
                {
                    changes.Add(PdfAFix.AlternateImages);
                }

                if (Name(dictionary, "/Subtype2") == "/PS")
                {
                    dictionary.Elements.Remove("/Subtype2");
                    dictionary.Elements.Remove("/PS");
                    changes.Add(PdfAFix.PostScript);
                }

                break;
            case "/PS" when dictionary.Stream is not null:
                // A PostScript XObject draws nothing in PDF: it becomes an empty form, so that what paints it still finds it.
                dictionary.Elements.Clear();
                dictionary.Elements.SetName("/Type", "/XObject");
                dictionary.Elements.SetName("/Subtype", "/Form");
                dictionary.Elements["/BBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
                dictionary.Stream.Value = [];
                changes.Add(PdfAFix.PostScript);
                break;
        }

        if (AsDictionary(dictionary.Elements["/ExtGState"]) is { } states)
        {
            foreach (var state in states.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>())
            {
                GraphicsState(state, changes);
            }
        }

        // PDF/A-1 has no transparency groups. Without transparency in it a group changes nothing that is drawn (PDFsharp
        // gives every page one); where there is transparency, PdfAInspection still finds it.
        if (level == PdfALevel.A1B && (Name(dictionary, "/Type") == "/Page" || Name(dictionary, "/Subtype") == "/Form")
            && AsDictionary(dictionary.Elements["/Group"]) is { } group && Name(group, "/S") == "/Transparency")
        {
            dictionary.Elements.Remove("/Group");
        }

        if (Name(dictionary, "/Type") == "/Page" && dictionary.Elements.ContainsKey("/PresSteps"))
        {
            dictionary.Elements.Remove("/PresSteps");
            changes.Add(PdfAFix.Multimedia);
        }

        if (Name(dictionary, "/Type") is "/Sig" or "/DocTimeStamp" || (dictionary.Elements.ContainsKey("/ByteRange") && dictionary.Elements.ContainsKey("/Contents")))
        {
            signatures.Add(dictionary);
        }
    }

    /// <summary>Transfer functions go (/TR2 may only be /Default); a rendering intent PDF does not define becomes the default one.</summary>
    private static void GraphicsState(PdfDictionary state, PdfAChanges changes)
    {
        if (state.Elements.ContainsKey("/TR"))
        {
            state.Elements.Remove("/TR");
            changes.Add(PdfAFix.TransferFunctions);
        }

        if (state.Elements.ContainsKey("/TR2") && Name(state, "/TR2") != "/Default")
        {
            state.Elements.SetName("/TR2", "/Default");
            changes.Add(PdfAFix.TransferFunctions);
        }

        if (Name(state, "/RI") is { } intent && !RenderingIntents.Contains(intent))
        {
            state.Elements.SetName("/RI", "/RelativeColorimetric");
        }
    }

    private static bool IsAction(PdfDictionary dictionary)
        => Name(dictionary, "/S") is not null && Name(dictionary, "/Type") is null or "/Action";

    /// <summary>Why PDF/A forbids an action (ISO 19005-1 6.6.1, 19005-2 6.6.1), or null when it allows it.</summary>
    private static PdfAFix? Forbidden(PdfDictionary action) => Name(action, "/S") switch
    {
        "/JavaScript" => PdfAFix.Scripts,
        "/GoToE" => PdfAFix.Attachments,
        "/Sound" or "/Movie" or "/Rendition" or "/GoTo3DView" or "/RichMediaExecute" => PdfAFix.Multimedia,
        "/Launch" or "/ResetForm" or "/ImportData" or "/Hide" or "/SetOCGState" or "/Trans" or "/SetState" or "/NOP" => PdfAFix.Actions,
        "/Named" when !PageTurns.Contains(Name(action, "/N") ?? string.Empty) => PdfAFix.Actions,
        _ => null,
    };

    /// <summary>Forbidden actions leave the chain an action continues with (/Next).</summary>
    private static void Chain(PdfDictionary action, PdfAChanges changes)
    {
        switch (Resolve(action.Elements["/Next"]))
        {
            case PdfDictionary next when Forbidden(next) is { } fix:
                action.Elements.Remove("/Next");
                changes.Add(fix);
                break;
            case PdfArray chain:
                for (var i = chain.Elements.Count - 1; i >= 0; i--)
                {
                    if (AsDictionary(chain.Elements[i]) is { } next && Forbidden(next) is { } fix)
                    {
                        chain.Elements.RemoveAt(i);
                        changes.Add(fix);
                    }
                }

                if (chain.Elements.Count == 0)
                {
                    action.Elements.Remove("/Next");
                }

                break;
        }
    }

    /// <summary>
    /// LZW (forbidden) becomes Flate: the leading general filters are undone and the data compressed with Flate in front
    /// of the filters left (an image codec). False when there is no LZW, or when it cannot be undone (then it stays).
    /// </summary>
    private static bool Recompress(PdfDocument document, PdfDictionary stream)
    {
        if (StreamData.Filters(stream) is not { } filters || !filters.Any(f => f.Name == "/LZWDecode"))
        {
            return false;
        }

        if (StreamData.Decode(stream, out var remaining) is not { } data || remaining.Any(f => f.Name == "/LZWDecode"))
        {
            return false;
        }

        var compressed = Deflate(data);
        stream.Stream!.Value = compressed;
        stream.Elements.SetInteger("/Length", compressed.Length);
        if (remaining.Count == 0)
        {
            stream.Elements.SetName("/Filter", "/FlateDecode");
            stream.Elements.Remove("/DecodeParms");
            return true;
        }

        var names = new PdfArray(document, new PdfName("/FlateDecode"));
        var parameters = new PdfArray(document, PdfNull.Value);
        foreach (var (name, parameter) in remaining)
        {
            names.Elements.Add(new PdfName(name));
            parameters.Elements.Add(parameter is null ? PdfNull.Value : parameter);
        }

        stream.Elements["/Filter"] = names;
        stream.Elements["/DecodeParms"] = parameters;
        return true;
    }

    /// <summary>
    /// The XFA form goes (the AcroForm fields stay), and fields are drawn: all of them when their drawing was left to
    /// the viewer (/NeedAppearances), else those without an appearance.
    /// </summary>
    private static void Forms(PdfDocument document, PdfAChanges changes)
    {
        if (AsDictionary(document.Internals.Catalog.Elements["/AcroForm"]) is not { } form)
        {
            return;
        }

        if (form.Elements.ContainsKey("/XFA"))
        {
            form.Elements.Remove("/XFA");
            changes.Add(PdfAFix.Xfa);
        }

        var all = Resolve(form.Elements["/NeedAppearances"]) is PdfBoolean { Value: true };
        form.Elements.Remove("/NeedAppearances");
        try
        {
            var fields = FormReader.Fields(document);
            var drawn = fields
                .Where(f => f.Field.Kind != FormFieldKind.Signature)
                .Sum(f => f.Widgets.Count(w => all || AppearancePlacement.Normal(w) is null));
            FormWriter.DrawMissingAppearances(document, fields, all);
            changes.Add(PdfAFix.FormAppearances, drawn);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            // A form PDF_Pro cannot read is left as it is; widgets without an appearance get an empty one below.
        }
    }

    /// <summary>
    /// The comments of every page: attachments and multimedia removed, hidden ones removed (hidden widgets drawn as
    /// nothing), the rest printable with only a normal appearance; PDF/A-1: kinds after PDF 1.4 drawn into the page and
    /// opacity made full; PDF/A-2: an appearance for every comment that has an area (a note icon, or an empty one).
    /// </summary>
    private static void Comments(PdfDocument document, PdfALevel level, PdfAChanges changes, CancellationToken cancellationToken)
    {
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Annotations(page) is not { } annotations)
            {
                continue;
            }

            var removed = new List<PdfDictionary>();
            var placements = new List<FormPlacement>();
            foreach (var annotation in annotations.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList())
            {
                var subtype = Name(annotation, "/Subtype") ?? string.Empty;
                if (subtype == "/FileAttachment")
                {
                    removed.Add(annotation);
                    changes.Add(PdfAFix.Attachments);
                    continue;
                }

                if (MultimediaAnnotations.Contains(subtype) || !(Pdf14Annotations.Contains(subtype) || Iso32000Annotations.Contains(subtype)))
                {
                    removed.Add(annotation);
                    changes.Add(PdfAFix.Multimedia);
                    continue;
                }

                var flags = StreamData.Integer(annotation, "/F") ?? 0;
                var popup = subtype == "/Popup";
                if ((flags & (Invisible | Hidden | NoView)) != 0 && !popup)
                {
                    if (subtype != "/Widget")
                    {
                        removed.Add(annotation);
                        changes.Add(PdfAFix.HiddenAnnotations);
                        continue;
                    }

                    EmptyAppearance(document, annotation);
                    changes.Add(PdfAFix.HiddenAnnotations);
                }

                if (level == PdfALevel.A1B && !Pdf14Annotations.Contains(subtype))
                {
                    if (AppearancePlacement.Normal(annotation) is { } appearance
                        && AsArray(annotation.Elements["/Rect"]) is { Elements.Count: 4 } rect
                        && AppearancePlacement.Place(document, appearance, AppearancePlacement.Rectangle(rect)) is { } placement)
                    {
                        placements.Add(placement);
                    }

                    removed.Add(annotation);
                    changes.Add(PdfAFix.FlattenedAnnotations);
                    continue;
                }

                if (!popup || level == PdfALevel.A1B)
                {
                    var wanted = (flags | Print) & ~(Invisible | Hidden | NoView | ToggleNoView);
                    if (wanted != flags)
                    {
                        annotation.Elements.SetInteger("/F", wanted);
                        changes.Add(PdfAFix.AnnotationFlags);
                    }
                }

                if (AsDictionary(annotation.Elements["/AP"]) is { } appearances)
                {
                    if (Remove(appearances, "/D", "/R"))
                    {
                        changes.Add(PdfAFix.AnnotationAppearances);
                    }
                }
                else if (level == PdfALevel.A2B && !popup && subtype != "/Link" && Area(annotation) is { } size)
                {
                    if (subtype == "/Text")
                    {
                        NoteAppearance(document, annotation, size);
                    }
                    else
                    {
                        EmptyAppearance(document, annotation);
                    }

                    changes.Add(PdfAFix.AnnotationAppearances);
                }

                if (level == PdfALevel.A1B && Resolve(annotation.Elements["/CA"]) is var opacity && Number(opacity) is < 1)
                {
                    annotation.Elements["/CA"] = new PdfReal(1);
                    changes.Add(PdfAFix.AnnotationOpacity);
                }
            }

            if (placements.Count > 0)
            {
                PageStamper.Place(page, placements, StampDepth.Above);
            }

            if (removed.Count > 0)
            {
                AnnotationThreads.Remove(document, page, removed);
            }
        }
    }

    /// <summary>The sRGB output intent (ICC's sRGB2014 profile), in place of any other.</summary>
    private static void OutputIntent(PdfDocument document, PdfAChanges changes)
    {
        var profile = new PdfDictionary(document);
        profile.Elements.SetInteger("/N", 3);
        profile.CreateStream(Deflate(IccProfiles.Srgb));
        profile.Elements.SetName("/Filter", "/FlateDecode");
        document.Internals.AddObject(profile);

        var intent = new PdfDictionary(document);
        intent.Elements.SetName("/Type", "/OutputIntent");
        intent.Elements.SetName("/S", "/GTS_PDFA1");
        intent.Elements["/OutputConditionIdentifier"] = new PdfString(OutputCondition);
        intent.Elements["/Info"] = new PdfString(OutputCondition);
        intent.Elements["/RegistryName"] = new PdfString("http://www.color.org");
        intent.Elements["/DestOutputProfile"] = profile.Reference!;
        document.Internals.AddObject(intent);
        document.Internals.Catalog.Elements["/OutputIntents"] = new PdfArray(document, intent.Reference!);
        changes.Add(PdfAFix.OutputIntent);
    }

    /// <summary>The width and height of the annotation's rectangle when it has an area.</summary>
    private static (double Width, double Height)? Area(PdfDictionary annotation)
    {
        if (AsArray(annotation.Elements["/Rect"]) is not { Elements.Count: 4 } rect)
        {
            return null;
        }

        var box = AppearancePlacement.Rectangle(rect);
        return box.Width > 0 && box.Height > 0 ? (box.Width, box.Height) : null;
    }

    /// <summary>An appearance that draws nothing; a widget whose appearance has states keeps them, each drawing nothing.</summary>
    private static void EmptyAppearance(PdfDocument document, PdfDictionary annotation)
    {
        var (width, height) = Area(annotation) ?? (0, 0);
        var form = Form(document, width, height, string.Empty);
        var states = AsDictionary(annotation.Elements["/AP"]) is { } old && AsDictionary(old.Elements["/N"]) is { Stream: null } normal ? normal.Elements.Keys.ToList() : null;
        var appearances = new PdfDictionary(document);
        if (states is { Count: > 0 })
        {
            var each = new PdfDictionary(document);
            foreach (var state in states)
            {
                each.Elements[state] = form.Reference!;
            }

            appearances.Elements["/N"] = each;
        }
        else
        {
            appearances.Elements["/N"] = form.Reference!;
        }

        annotation.Elements["/AP"] = appearances;
    }

    /// <summary>A note icon (a sheet with three lines) in the note's colour, yellow when it has none.</summary>
    private static void NoteAppearance(PdfDocument document, PdfDictionary annotation, (double Width, double Height) size)
    {
        var colour = AsArray(annotation.Elements["/C"]) is { Elements.Count: 3 } c && c.Elements.Select(Number).ToArray() is [double r, double g, double b]
            ? (r, g, b)
            : (1.0, 0.85, 0.2);
        static string N(double value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
        var (width, height) = size;
        var content = new StringBuilder();
        content.Append(CultureInfo.InvariantCulture, $"q 1 w {N(colour.Item1)} {N(colour.Item2)} {N(colour.Item3)} rg 0 G 0.5 0.5 {N(Math.Max(1, width - 1))} {N(Math.Max(1, height - 1))} re B\n");
        foreach (var level in new[] { 0.7, 0.5, 0.3 })
        {
            content.Append(CultureInfo.InvariantCulture, $"{N(width * 0.2)} {N(height * level)} m {N(width * (level == 0.3 ? 0.6 : 0.8))} {N(height * level)} l\n");
        }

        content.Append("S Q\n");
        var appearances = new PdfDictionary(document);
        appearances.Elements["/N"] = Form(document, width, height, content.ToString()).Reference!;
        annotation.Elements["/AP"] = appearances;
    }

    private static PdfDictionary Form(PdfDocument document, double width, double height, string content)
    {
        var form = new PdfDictionary(document);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfReal(width), new PdfReal(height));
        form.CreateStream(Encoding.ASCII.GetBytes(content));
        document.Internals.AddObject(form);
        return form;
    }

    private static double? Number(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => null,
    };

    /// <summary>Removes the keys; whether any was there.</summary>
    private static bool Remove(PdfDictionary dictionary, params string[] keys)
    {
        var any = false;
        foreach (var key in keys)
        {
            if (dictionary.Elements.ContainsKey(key))
            {
                dictionary.Elements.Remove(key);
                any = true;
            }
        }

        return any;
    }

    /// <summary>The leaves of a name tree (pairs of /Names arrays, through /Kids).</summary>
    private static int NameTreeLeaves(PdfDictionary? node, int depth)
    {
        if (node is null || depth > 32)
        {
            return 0;
        }

        var leaves = (AsArray(node.Elements["/Names"])?.Elements.Count ?? 0) / 2;
        foreach (var kid in Dictionaries(node.Elements["/Kids"]))
        {
            leaves += NameTreeLeaves(kid, depth + 1);
        }

        return leaves;
    }
}
