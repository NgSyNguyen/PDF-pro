using System.Globalization;
using PdfPro.Core.Geometry;
using PdfPro.Core.Structure;
using PdfPro.Core.Text;
using PdfPro.Operations.Converting.PdfA;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Structure;

/// <summary>
/// Reads what a document says about itself (DocumentStructure): its description (the information dictionary, the
/// XMP packet where the dictionary has nothing), producer and dates, how it opens (/OpenAction, /PageMode,
/// /ViewerPreferences), its page labels (/PageLabels number tree) and its links (/Link annotations with the text under
/// them, read by PdfPig).
/// </summary>
public static class DocumentStructureReader
{
    private const int MaxDepth = 32;

    /// <exception cref="InvalidDataException">The document cannot be read.</exception>
    public static DocumentStructure Read(string path, string? password = null)
    {
        using var document = Attachments.Open(path, password);
        var info = document.Info;
        var xmp = XmpPacket.Read(document);
        string? Value(string key, string xmpKey) => Attachments.Text(info.Elements[key]) is { Length: > 0 } text ? text : xmp.GetValueOrDefault(xmpKey);
        var description = new DocumentDescription(
            Value("/Title", "title"),
            Value("/Author", "creator"),
            Value("/Subject", "description"),
            Value("/Keywords", "keywords"),
            Value("/Creator", "creatortool"));
        var links = Links(document);
        var texts = LinkTexts(path, password, links);
        return new DocumentStructure(
            description,
            Value("/Producer", "producer"),
            PdfAMetadata.ParseDate(Attachments.Text(info.Elements["/CreationDate"])),
            PdfAMetadata.ParseDate(Attachments.Text(info.Elements["/ModDate"])),
            Open(document),
            Labels(document),
            [.. links.Select((l, i) => l with { Text = texts[i] })],
            document.PageCount);
    }

    /// <summary>The page labels of an open document, in page order.</summary>
    internal static IReadOnlyList<PageLabelRange> Labels(PdfDocument document)
    {
        var ranges = new List<PageLabelRange>();
        Numbers(AsDictionary(document.Internals.Catalog.Elements["/PageLabels"]), 0);
        return PageLabels.Normalize(ranges, document.PageCount);

        void Numbers(PdfDictionary? node, int depth)
        {
            if (node is null || depth > MaxDepth)
            {
                return;
            }

            if (AsArray(node.Elements["/Nums"]) is { } numbers)
            {
                for (var i = 0; i + 1 < numbers.Elements.Count; i += 2)
                {
                    if (Resolve(numbers.Elements[i]) is PdfInteger { Value: >= 0 } first && AsDictionary(numbers.Elements[i + 1]) is { } label)
                    {
                        var style = Name(label, "/S") switch
                        {
                            "/D" => PageLabelStyle.Decimal,
                            "/R" => PageLabelStyle.UpperRoman,
                            "/r" => PageLabelStyle.LowerRoman,
                            "/A" => PageLabelStyle.UpperLetters,
                            "/a" => PageLabelStyle.LowerLetters,
                            _ => PageLabelStyle.None,
                        };
                        var start = Resolve(label.Elements["/St"]) is PdfInteger { Value: > 0 } st ? st.Value : 1;
                        ranges.Add(new PageLabelRange(first.Value, style, Attachments.Text(label.Elements["/P"]), start));
                    }
                }
            }

            foreach (var kid in AsArray(node.Elements["/Kids"])?.Elements ?? Enumerable.Empty<PdfItem>())
            {
                Numbers(AsDictionary(kid), depth + 1);
            }
        }
    }

    private static OpenSettings Open(PdfDocument document)
    {
        var catalog = document.Internals.Catalog;
        int? page = null;
        OpenZoom? zoom = null;
        int? percent = null;
        if (Destinations.Explicit(catalog.Elements["/OpenAction"], new NamedDestinations(document)) is { Elements.Count: >= 2 } destination)
        {
            page = PageIndex(document, destination.Elements[0]);
            switch (Resolve(destination.Elements[1]))
            {
                case PdfName { Value: "/Fit" or "/FitB" }:
                    zoom = OpenZoom.FitPage;
                    break;
                case PdfName { Value: "/FitH" or "/FitBH" }:
                    zoom = OpenZoom.FitWidth;
                    break;
                case PdfName { Value: "/XYZ" } when destination.Elements.Count >= 5 && Number(destination.Elements[4]) is { } scale and > 0:
                    (zoom, percent) = Math.Abs(scale - 1) < 0.001 ? (OpenZoom.ActualSize, (int?)null) : (OpenZoom.Percent, (int)Math.Round(scale * 100));
                    break;
            }
        }

        var panel = Name(catalog, "/PageMode") switch
        {
            "/UseNone" => OpenPanel.None,
            "/UseOutlines" => OpenPanel.Bookmarks,
            "/UseThumbs" => OpenPanel.Thumbnails,
            "/FullScreen" => OpenPanel.FullScreen,
            _ => OpenPanel.Default,
        };
        var showTitle = Resolve(AsDictionary(catalog.Elements["/ViewerPreferences"])?.Elements["/DisplayDocTitle"]) is PdfBoolean { Value: true };
        return new OpenSettings(page, zoom ?? OpenZoom.Default, percent, panel, showTitle);
    }

    /// <summary>The /Link annotations of every page, in order.</summary>
    internal static List<LinkInfo> Links(PdfDocument document)
    {
        var names = new NamedDestinations(document);
        var links = new List<LinkInfo>();
        for (var p = 0; p < document.PageCount; p++)
        {
            var annotations = Annotations(document.Pages[p]);
            for (var i = 0; i < (annotations?.Elements.Count ?? 0); i++)
            {
                if (AsDictionary(annotations!.Elements[i]) is not { } link || Name(link, "/Subtype") != "/Link" || Rect(link) is not { } rect)
                {
                    continue;
                }

                LinkTarget? target = null;
                if (AsDictionary(link.Elements["/A"]) is { } action && Name(action, "/S") == "/URI" && Attachments.Text(action.Elements["/URI"]) is { Length: > 0 } uri)
                {
                    target = new LinkTarget(uri);
                }
                else if (Destinations.Of(link, names) is { Elements.Count: >= 1 } destination && PageIndex(document, destination.Elements[0]) is { } page)
                {
                    var top = Resolve(destination.Elements.ElementAtOrDefault(1)) is PdfName { Value: "/XYZ" } && destination.Elements.Count >= 4 ? Number(destination.Elements[3]) : null;
                    target = new LinkTarget(Page: page, Top: top);
                }

                links.Add(new LinkInfo(p, i, rect, target, null));
            }
        }

        return links;
    }

    internal static PdfRect? Rect(PdfDictionary annotation)
        => AsArray(annotation.Elements["/Rect"]) is { Elements.Count: 4 } r && r.Elements.Select(Number).ToList() is [{ } a, { } b, { } c, { } d]
            ? new PdfRect(Math.Min(a, c), Math.Min(b, d), Math.Max(a, c), Math.Max(b, d))
            : null;

    internal static double? Number(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        _ => null,
    };

    private static int? PageIndex(PdfDocument document, PdfItem item)
    {
        if (Resolve(item) is PdfInteger { Value: >= 0 } number && number.Value < document.PageCount)
        {
            return number.Value;
        }

        if (AsDictionary(item) is not { } target)
        {
            return null;
        }

        for (var i = 0; i < document.PageCount; i++)
        {
            if (ReferenceEquals(document.Pages[i], target) || (document.Pages[i].Reference is { } reference && item is PdfReference other && reference.ObjectID == other.ObjectID))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The words under each link, read by PdfPig (empty when the document's text cannot be read).</summary>
    private static string?[] LinkTexts(string path, string? password, List<LinkInfo> links)
    {
        var texts = new string?[links.Count];
        if (links.Count == 0)
        {
            return texts;
        }

        try
        {
            var options = new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
            if (!string.IsNullOrEmpty(password))
            {
                options.Password = password;
            }

            using var pig = UglyToad.PdfPig.PdfDocument.Open(path, options);
            foreach (var group in links.Select((l, i) => (Link: l, Index: i)).GroupBy(x => x.Link.Page))
            {
                var words = pig.GetPage(group.Key + 1).GetWords().ToList();
                foreach (var (link, index) in group)
                {
                    var inside = words
                        .Where(w => Overlap(link.Rect, new PdfRect(w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top)) > 0.5)
                        .Select(w => w.Text);
                    var text = TextNormalizer.ToNfc(string.Join(' ', inside)).Trim();
                    texts[index] = text.Length == 0 ? null : text.Length > 200 ? text[..200] : text;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // The links are listed without their text.
        }

        return texts;
    }

    /// <summary>The share of <paramref name="word"/> inside <paramref name="area"/>.</summary>
    private static double Overlap(PdfRect area, PdfRect word)
    {
        var width = Math.Min(area.Right, word.Right) - Math.Max(area.Left, word.Left);
        var height = Math.Min(area.Top, word.Top) - Math.Max(area.Bottom, word.Bottom);
        var size = Math.Max(0.01, word.Width * word.Height);
        return width > 0 && height > 0 ? width * height / size : 0;
    }
}

/// <summary>Reading and updating the document description in an XMP packet, keeping everything else it says.</summary>
internal static class XmpPacket
{
    private static readonly System.Xml.Linq.XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly System.Xml.Linq.XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly System.Xml.Linq.XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    private static readonly System.Xml.Linq.XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";

    private static readonly (System.Xml.Linq.XName Name, string Key)[] Properties =
    [
        (Dc + "title", "title"),
        (Dc + "creator", "creator"),
        (Dc + "description", "description"),
        (Pdf + "Keywords", "keywords"),
        (Xmp + "CreatorTool", "creatortool"),
        (Pdf + "Producer", "producer"),
    ];

    /// <summary>The description values of the document's packet ("title", "creator", …), empty when it has none.</summary>
    public static Dictionary<string, string> Read(PdfDocument document)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Load(document) is not { } xml)
        {
            return values;
        }

        foreach (var description in xml.Descendants(Rdf + "Description"))
        {
            foreach (var (name, key) in Properties)
            {
                var element = description.Element(name);
                var text = description.Attribute(name)?.Value
                    ?? (element is null ? null : element.Descendants(Rdf + "li").FirstOrDefault()?.Value ?? element.Value);
                if (!string.IsNullOrWhiteSpace(text) && !values.ContainsKey(key))
                {
                    values[key] = TextNormalizer.ToNfc(text.Trim());
                }
            }
        }

        return values;
    }

    /// <summary>
    /// Writes the description into the packet: the old properties of the description (as elements or attributes) are
    /// taken out and written again in a description of their own; anything else (PDF/A identification…) stays. A
    /// document without a packet gets one. The packet is stored unfiltered.
    /// </summary>
    public static void Write(PdfDocument document, IReadOnlyDictionary<string, string> values, DateTimeOffset modified)
    {
        var xml = Load(document) ?? new System.Xml.Linq.XDocument(
            new System.Xml.Linq.XElement(
                (System.Xml.Linq.XNamespace)"adobe:ns:meta/" + "xmpmeta",
                new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "adobe:ns:meta/"),
                new System.Xml.Linq.XElement(Rdf + "RDF", new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "rdf", Rdf.NamespaceName))));
        var rdf = xml.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf is null)
        {
            return;
        }

        var replaced = Properties.Select(p => p.Name).Append(Xmp + "ModifyDate").Append(Xmp + "MetadataDate").ToHashSet();
        foreach (var description in rdf.Elements(Rdf + "Description").ToList())
        {
            System.Xml.Linq.Extensions.Remove(description.Attributes().Where(a => replaced.Contains(a.Name)).ToList());
            System.Xml.Linq.Extensions.Remove(description.Elements().Where(e => replaced.Contains(e.Name)).ToList());
            if (!description.HasElements && !description.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != Rdf + "about"))
            {
                description.Remove();
            }
        }

        var date = PdfAMetadata.XmpDate(modified);
        var added = new System.Xml.Linq.XElement(
            Rdf + "Description",
            new System.Xml.Linq.XAttribute(Rdf + "about", string.Empty),
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "dc", Dc.NamespaceName),
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "pdf", Pdf.NamespaceName),
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "xmp", Xmp.NamespaceName));
        if (values.GetValueOrDefault("title") is { Length: > 0 } title)
        {
            added.Add(new System.Xml.Linq.XElement(Dc + "title", Alt(title)));
        }

        if (values.GetValueOrDefault("creator") is { Length: > 0 } creator)
        {
            added.Add(new System.Xml.Linq.XElement(Dc + "creator", new System.Xml.Linq.XElement(Rdf + "Seq", new System.Xml.Linq.XElement(Rdf + "li", creator))));
        }

        if (values.GetValueOrDefault("description") is { Length: > 0 } subject)
        {
            added.Add(new System.Xml.Linq.XElement(Dc + "description", Alt(subject)));
        }

        foreach (var (name, key) in new[] { (Pdf + "Keywords", "keywords"), (Xmp + "CreatorTool", "creatortool"), (Pdf + "Producer", "producer") })
        {
            if (values.GetValueOrDefault(key) is { Length: > 0 } value)
            {
                added.Add(new System.Xml.Linq.XElement(name, value));
            }
        }

        added.Add(new System.Xml.Linq.XElement(Xmp + "ModifyDate", date), new System.Xml.Linq.XElement(Xmp + "MetadataDate", date));
        rdf.Add(added);

        var packet = "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n"
            + xml.Root!.ToString(System.Xml.Linq.SaveOptions.None)
            + "\n<?xpacket end=\"w\"?>";
        var stream = new PdfDictionary(document);
        stream.Elements.SetName("/Type", "/Metadata");
        stream.Elements.SetName("/Subtype", "/XML");
        stream.CreateStream(System.Text.Encoding.UTF8.GetBytes(packet));
        document.Internals.AddObject(stream);
        document.Internals.Catalog.Elements["/Metadata"] = stream.Reference!;

        static System.Xml.Linq.XElement Alt(string text)
            => new(Rdf + "Alt", new System.Xml.Linq.XElement(Rdf + "li", new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xml + "lang", "x-default"), text));
    }

    private static System.Xml.Linq.XDocument? Load(PdfDocument document)
    {
        if (AsDictionary(document.Internals.Catalog.Elements["/Metadata"]) is not { Stream: not null } stream)
        {
            return null;
        }

        try
        {
            var bytes = Optimizing.StreamData.Bytes(stream);
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, IgnoreProcessingInstructions = true };
            using var reader = System.Xml.XmlReader.Create(new MemoryStream(bytes), settings);
            return System.Xml.Linq.XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return null;
        }
    }
}
