using System.Globalization;
using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Stamping;

/// <summary>A form to draw on a page: where (form space to the page's PDF space) and how opaque.</summary>
internal sealed record FormPlacement(PdfReference Form, PdfMatrix FormToPdf, double Opacity = 1);

/// <summary>Whether a stamp goes over the page content or under it.</summary>
internal enum StampDepth
{
    Above,
    Below,
}

/// <summary>
/// What PDF_Pro adds as a removable stamp: the optional content group (layer) it lives in — one per name per document,
/// so viewers can show, hide or skip printing it — and the kind written as a marker for stamp.remove.
/// </summary>
/// <param name="LayerName">Name of the layer shown by viewers ("Watermark", "Background", "Header/Footer").</param>
/// <param name="Kind">Marker value (a PDF name without the slash): Watermark, Background, HeaderFooter, PageNumbers, Bates.</param>
internal sealed record StampLayer(string LayerName, string Kind);

/// <summary>
/// Adds drawings (form XObjects) to pages. The added content is a stream of its own in the page's /Contents: above the
/// content the existing streams are first wrapped in q…Q so whatever graphics state they leave cannot bend the stamp;
/// below the content it simply comes first. Layered stamps are marked content of an optional content group and their
/// stream carries a <see cref="MarkerKey"/> entry, which is how PDF_Pro finds them again.
/// </summary>
internal static class PageStamper
{
    /// <summary>Entry of a content stream (and of its layer) that marks content added by PDF_Pro as a stamp.</summary>
    public const string MarkerKey = "/PdfProStamp";

    /// <summary>The page as PDFium displays it: visible box (CropBox ∩ MediaBox) and /Rotate.</summary>
    public static PageGeometry Geometry(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PageGeometry(PageBoxes.VisibleBox(page), PageBoxes.Rotation(page));
    }

    public static void Place(PdfPage page, IReadOnlyList<FormPlacement> placements, StampDepth depth, StampLayer? layer = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(placements);
        if (placements.Count == 0)
        {
            return;
        }

        var document = page.Owner;
        var resources = OwnResources(page);
        var content = new StringBuilder();
        if (layer is not null)
        {
            var properties = OwnEntry(resources, "/Properties");
            var name = UniqueName(properties, "/PdfProOC");
            properties.Elements[name] = Layer(document, layer);
            content.Append(CultureInfo.InvariantCulture, $"/OC {name} BDC\n");
        }

        var xObjects = OwnEntry(resources, "/XObject");
        PdfDictionary? states = null;
        foreach (var placement in placements)
        {
            var formName = UniqueName(xObjects, "/PdfProX");
            xObjects.Elements[formName] = placement.Form;
            content.Append("q ");
            if (placement.Opacity < 1)
            {
                // As a transparency group the form is composited as a whole with the outer opacity; otherwise the
                // opacity PDFsharp sets inside the form (1 for opaque colours) would replace it.
                MakeTransparencyGroup(document, placement.Form);
                states ??= OwnEntry(resources, "/ExtGState");
                var stateName = UniqueName(states, "/PdfProGS");
                states.Elements[stateName] = Opacity(document, placement.Opacity);
                content.Append(CultureInfo.InvariantCulture, $"{stateName} gs ");
            }

            var m = placement.FormToPdf;
            content.Append(CultureInfo.InvariantCulture, $"{Number(m.A)} {Number(m.B)} {Number(m.C)} {Number(m.D)} {Number(m.E)} {Number(m.F)} cm {formName} Do Q\n");
        }

        if (layer is not null)
        {
            content.Append("EMC\n");
        }

        var stamp = NewStream(document, content.ToString());
        if (layer is not null && AsDictionary(stamp) is { } stampStream)
        {
            stampStream.Elements.SetName(MarkerKey, "/" + layer.Kind);
        }

        var streams = ContentStreams(page);
        var contents = new PdfArray(document);
        if (depth == StampDepth.Below)
        {
            contents.Elements.Add(stamp);
            streams.ForEach(contents.Elements.Add);
        }
        else
        {
            contents.Elements.Add(NewStream(document, "q\n"));
            streams.ForEach(contents.Elements.Add);
            contents.Elements.Add(NewStream(document, "\nQ\n"));
            contents.Elements.Add(stamp);
        }

        page.Elements["/Contents"] = contents;
    }

    /// <summary>The page's content streams, in order.</summary>
    public static List<PdfItem> ContentStreams(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var streams = new List<PdfItem>();
        switch (page.Elements["/Contents"])
        {
            case PdfReference reference when reference.Value is PdfArray array:
                streams.AddRange(array.Elements);
                break;
            case PdfArray array:
                streams.AddRange(array.Elements);
                break;
            case PdfReference reference:
                streams.Add(reference);
                break;
        }

        return streams;
    }

    /// <summary>The document's layer of this name made by PDF_Pro (created, and listed as visible, when missing).</summary>
    public static PdfReference Layer(PdfDocument document, StampLayer layer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(layer);
        var catalog = document.Internals.Catalog;
        if (AsDictionary(catalog.Elements["/OCProperties"]) is not { } properties)
        {
            properties = new PdfDictionary(document);
            properties.Elements["/OCGs"] = new PdfArray(document);
            var defaults = new PdfDictionary(document);
            defaults.Elements["/ON"] = new PdfArray(document);
            defaults.Elements["/Order"] = new PdfArray(document);
            properties.Elements["/D"] = defaults;
            catalog.Elements["/OCProperties"] = properties;
        }

        var groups = AsArray(properties.Elements["/OCGs"]) ?? new PdfArray(document);
        properties.Elements["/OCGs"] = groups;
        foreach (var item in groups.Elements)
        {
            if (item is PdfReference reference && AsDictionary(reference) is { } group && group.Elements.ContainsKey(MarkerKey)
                && group.Elements.GetString("/Name") == layer.LayerName)
            {
                return reference;
            }
        }

        var created = new PdfDictionary(document);
        created.Elements.SetName("/Type", "/OCG");
        created.Elements["/Name"] = new PdfString(layer.LayerName);
        created.Elements.SetName(MarkerKey, "/Layer");
        var usage = new PdfDictionary(document);
        var print = new PdfDictionary(document);
        print.Elements.SetName("/PrintState", "/ON");
        var view = new PdfDictionary(document);
        view.Elements.SetName("/ViewState", "/ON");
        usage.Elements["/Print"] = print;
        usage.Elements["/View"] = view;
        created.Elements["/Usage"] = usage;
        document.Internals.AddObject(created);
        groups.Elements.Add(created.Reference!);

        var configuration = AsDictionary(properties.Elements["/D"]);
        if (configuration is not null)
        {
            foreach (var key in new[] { "/ON", "/Order" })
            {
                var list = AsArray(configuration.Elements[key]) ?? new PdfArray(document);
                list.Elements.Add(created.Reference!);
                configuration.Elements[key] = list;
            }
        }

        return created.Reference!;
    }

    private static void MakeTransparencyGroup(PdfDocument document, PdfReference form)
    {
        if (AsDictionary(form) is { } dictionary && !dictionary.Elements.ContainsKey("/Group"))
        {
            var group = new PdfDictionary(document);
            group.Elements.SetName("/Type", "/Group");
            group.Elements.SetName("/S", "/Transparency");
            dictionary.Elements["/Group"] = group;
        }
    }

    private static PdfReference Opacity(PdfDocument document, double opacity)
    {
        var state = new PdfDictionary(document);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements["/ca"] = new PdfReal(Math.Round(opacity, 4));
        state.Elements["/CA"] = new PdfReal(Math.Round(opacity, 4));
        document.Internals.AddObject(state);
        return state.Reference!;
    }

    /// <summary>
    /// A /Resources dictionary of the page's own: resource dictionaries are often shared by many pages (or inherited
    /// from the page tree), and names added for one page's stamp must not change what another page draws.
    /// </summary>
    private static PdfDictionary OwnResources(PdfPage page)
    {
        var source = AsDictionary(page.Elements["/Resources"]);
        for (var parent = AsDictionary(page.Elements["/Parent"]); source is null && parent is not null; parent = AsDictionary(parent.Elements["/Parent"]))
        {
            source = AsDictionary(parent.Elements["/Resources"]);
        }

        var own = Copy(page.Owner, source);
        page.Elements["/Resources"] = own;
        return own;
    }

    /// <summary>A copy of the resource sub-dictionary (/XObject, /ExtGState, /Properties) that only this page uses.</summary>
    private static PdfDictionary OwnEntry(PdfDictionary resources, string key)
    {
        var own = Copy(resources.Owner, AsDictionary(resources.Elements[key]));
        resources.Elements[key] = own;
        return own;
    }

    private static PdfDictionary Copy(PdfDocument document, PdfDictionary? source)
    {
        var copy = new PdfDictionary(document);
        if (source is not null)
        {
            foreach (var key in source.Elements.Keys)
            {
                copy.Elements[key] = source.Elements[key];
            }
        }

        return copy;
    }

    private static string UniqueName(PdfDictionary dictionary, string prefix)
    {
        for (var i = 1; ; i++)
        {
            var name = prefix + i.ToString(CultureInfo.InvariantCulture);
            if (!dictionary.Elements.ContainsKey(name))
            {
                return name;
            }
        }
    }

    private static PdfReference NewStream(PdfDocument document, string text)
    {
        var content = new PdfContent(document);
        content.CreateStream(Encoding.ASCII.GetBytes(text));
        document.Internals.AddObject(content);
        return content.Reference!;
    }

    private static string Number(double value) => Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);
}
