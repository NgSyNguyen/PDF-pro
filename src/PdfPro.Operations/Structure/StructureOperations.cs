using System.Globalization;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Structure;
using PdfPro.Core.Text;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Structure;

/// <param name="Bookmarks">The whole bookmark tree (empty removes every bookmark).</param>
/// <param name="ShowPanel">The reader shows the bookmarks when the document opens.</param>
public sealed record SetBookmarksParams(IReadOnlyList<BookmarkNode> Bookmarks, bool ShowPanel = true);

/// <summary>
/// structure.bookmarks (<see cref="OperationKind.Content"/>, "Bookmark"): the document's bookmark tree replaced by the
/// one given — titles (NFC, Unicode), the page and height each opens, bold, italics and colour, which are open — in one
/// revision. The old items are left unreferenced (PDFsharp drops them when saving).
/// </summary>
public sealed class SetBookmarksOperation : DocumentContentOperation<SetBookmarksParams>
{
    public const string OperationId = "structure.bookmarks";

    public override string Id => OperationId;

    public override string Stage => "Stage_Structure";

    protected override OperationValidationResult ValidateCore(SetBookmarksParams parameters, DocumentInfo document)
    {
        var count = 0;
        var problem = Check(parameters.Bookmarks, 0);
        return problem is null
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Bookmarks), problem));

        string? Check(IReadOnlyList<BookmarkNode> nodes, int depth)
        {
            if (depth >= OutlineItems.MaxDepth)
            {
                return "The bookmarks are nested too deep.";
            }

            foreach (var node in nodes)
            {
                if (++count > OutlineItems.MaxItems)
                {
                    return "There are too many bookmarks.";
                }

                if (string.IsNullOrWhiteSpace(node.Title))
                {
                    return "A bookmark has no title.";
                }

                if (node.Page < 0 || node.Page >= document.PageCount)
                {
                    return "A bookmark opens a page outside the document.";
                }

                if (node.Color is { } color && Color(color) is null)
                {
                    return "A bookmark colour is not #RRGGBB.";
                }

                if (Check(node.Children ?? [], depth + 1) is { } inner)
                {
                    return inner;
                }
            }

            return null;
        }
    }

    protected override void Apply(PdfDocument document, string inputPath, SetBookmarksParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new OperationProgress(0, 1, Stage));
        var catalog = document.Internals.Catalog;
        catalog.Elements.Remove("/Outlines");
        if (parameters.Bookmarks.Count == 0)
        {
            if (Name(catalog, "/PageMode") == "/UseOutlines")
            {
                catalog.Elements.Remove("/PageMode");
            }

            return;
        }

        var root = new PdfDictionary(document);
        root.Elements.SetName("/Type", "/Outlines");
        document.Internals.AddObject(root);
        Add(document, root, parameters.Bookmarks);
        root.Elements.SetInteger("/Count", Visible(parameters.Bookmarks));
        catalog.Elements["/Outlines"] = root.Reference!;
        if (parameters.ShowPanel)
        {
            catalog.Elements.SetName("/PageMode", "/UseOutlines");
        }

        progress.Report(new OperationProgress(1, 1, Stage));
    }

    /// <summary>"#RRGGBB" as three numbers 0–1, or null.</summary>
    internal static (double R, double G, double B)? Color(string text)
        => text.Length == 7 && text[0] == '#' && int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? (((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0)
            : null;

    /// <summary>Items visible under a node when it is open: its children, and theirs that are open.</summary>
    private static int Visible(IReadOnlyList<BookmarkNode> children)
        => children.Count + children.Where(c => c.Open).Sum(c => Visible(c.Children ?? []));

    private static void Add(PdfDocument document, PdfDictionary parent, IReadOnlyList<BookmarkNode> nodes)
    {
        var items = new List<PdfDictionary>(nodes.Count);
        foreach (var node in nodes)
        {
            var item = new PdfDictionary(document);
            document.Internals.AddObject(item);
            var title = TextNormalizer.ToNfc(node.Title.Trim());
            item.Elements["/Title"] = title.All(c => c < 128) ? new PdfString(title) : new PdfString(title, PdfStringEncoding.Unicode);
            item.Elements["/Parent"] = parent.Reference!;
            item.Elements["/Dest"] = Destination(document, node.Page, node.Top);
            var flags = (node.Italic ? 1 : 0) | (node.Bold ? 2 : 0);
            if (flags != 0)
            {
                item.Elements.SetInteger("/F", flags);
            }

            if (node.Color is { } color && Color(color) is var (r, g, b))
            {
                item.Elements["/C"] = new PdfArray(document, new PdfReal(r), new PdfReal(g), new PdfReal(b));
            }

            var children = node.Children ?? [];
            if (children.Count > 0)
            {
                Add(document, item, children);
                item.Elements.SetInteger("/Count", (node.Open ? 1 : -1) * Visible(children));
            }

            items.Add(item);
        }

        OutlineItems.Relink(parent, items);
    }

    /// <summary>[page /XYZ null top null] for a height, [page /Fit] otherwise.</summary>
    internal static PdfArray Destination(PdfDocument document, int page, double? top)
    {
        var target = document.Pages[page].Reference!;
        return top is { } y
            ? new PdfArray(document, target, new PdfName("/XYZ"), PdfNull.Value, new PdfReal(y), PdfNull.Value)
            : new PdfArray(document, target, new PdfName("/Fit"));
    }
}

/// <param name="Description">The new description (null: leave it).</param>
/// <param name="Open">How the document opens (null: leave it).</param>
public sealed record SetMetadataParams(DocumentDescription? Description = null, OpenSettings? Open = null);

/// <summary>
/// structure.metadata (<see cref="OperationKind.Content"/>, "Thuộc tính"): title, author, subject, keywords and
/// creator written to the information dictionary and to the XMP packet alike (the rest of the packet kept), the
/// modification date set; and how the document opens: page and zoom (/OpenAction), the panel shown (/PageMode), the
/// title in the window (/ViewerPreferences /DisplayDocTitle).
/// </summary>
public sealed class SetMetadataOperation : DocumentContentOperation<SetMetadataParams>
{
    public const string OperationId = "structure.metadata";

    private static readonly (string Key, string Xmp)[] Fields = [("/Title", "title"), ("/Author", "creator"), ("/Subject", "description"), ("/Keywords", "keywords"), ("/Creator", "creatortool")];

    public override string Id => OperationId;

    public override string Stage => "Stage_Structure";

    /// <summary>The clock the modification date is read from (tests set it).</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    protected override OperationValidationResult ValidateCore(SetMetadataParams parameters, DocumentInfo document)
    {
        if (parameters.Description is null && parameters.Open is null)
        {
            return OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Description), "Nothing to change."));
        }

        if (parameters.Open is { } open && ((open.Page is { } page && (page < 0 || page >= document.PageCount))
            || (open.Zoom == OpenZoom.Percent && open.Percent is not (>= 10 and <= 6400))))
        {
            return OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Open), "The page or the zoom is out of range."));
        }

        return OperationValidationResult.Success;
    }

    protected override void Apply(PdfDocument document, string inputPath, SetMetadataParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new OperationProgress(0, 1, Stage));
        var info = document.Info;
        var now = Now();
        if (parameters.Description is { } description)
        {
            var values = new[] { description.Title, description.Author, description.Subject, description.Keywords, description.Creator };
            for (var i = 0; i < Fields.Length; i++)
            {
                if (values[i] is not { } value)
                {
                    continue;
                }

                var text = TextNormalizer.ToNfc(value.Trim());
                if (text.Length == 0)
                {
                    info.Elements.Remove(Fields[i].Key);
                }
                else
                {
                    info.Elements[Fields[i].Key] = text.All(c => c < 128) ? new PdfString(text) : new PdfString(text, PdfStringEncoding.Unicode);
                }
            }

            info.Elements["/ModDate"] = new PdfString(Converting.PdfA.PdfAMetadata.InfoDate(now));
            var packet = Fields.ToDictionary(f => f.Xmp, f => Attachments.Text(info.Elements[f.Key]) ?? string.Empty, StringComparer.Ordinal);
            packet["producer"] = Attachments.Text(info.Elements["/Producer"]) ?? XmpPacket.Read(document).GetValueOrDefault("producer") ?? string.Empty;
            XmpPacket.Write(document, packet, now);
        }

        if (parameters.Open is { } open)
        {
            Settings(document, open);
        }

        progress.Report(new OperationProgress(1, 1, Stage));
    }

    private static void Settings(PdfDocument document, OpenSettings open)
    {
        var catalog = document.Internals.Catalog;
        if (open.Page is not null || open.Zoom is not null)
        {
            var current = DocumentStructureReaderOpen(document);
            var page = document.Pages[open.Page ?? current.Page ?? 0].Reference!;
            var zoom = open.Zoom ?? current.Zoom ?? OpenZoom.Default;
            catalog.Elements["/OpenAction"] = zoom switch
            {
                OpenZoom.FitPage => new PdfArray(document, page, new PdfName("/Fit")),
                OpenZoom.FitWidth => new PdfArray(document, page, new PdfName("/FitH"), PdfNull.Value),
                OpenZoom.ActualSize => new PdfArray(document, page, new PdfName("/XYZ"), PdfNull.Value, PdfNull.Value, new PdfReal(1)),
                OpenZoom.Percent => new PdfArray(document, page, new PdfName("/XYZ"), PdfNull.Value, PdfNull.Value, new PdfReal((open.Percent ?? 100) / 100.0)),
                _ => new PdfArray(document, page, new PdfName("/XYZ"), PdfNull.Value, PdfNull.Value, PdfNull.Value),
            };
        }

        switch (open.Panel)
        {
            case OpenPanel.Default:
                catalog.Elements.Remove("/PageMode");
                break;
            case { } panel:
                catalog.Elements.SetName("/PageMode", panel switch
                {
                    OpenPanel.Bookmarks => "/UseOutlines",
                    OpenPanel.Thumbnails => "/UseThumbs",
                    OpenPanel.FullScreen => "/FullScreen",
                    _ => "/UseNone",
                });
                break;
        }

        if (open.ShowTitle is { } showTitle)
        {
            var preferences = AsDictionary(catalog.Elements["/ViewerPreferences"]);
            if (preferences is null)
            {
                preferences = new PdfDictionary(document);
                catalog.Elements["/ViewerPreferences"] = preferences;
            }

            if (showTitle)
            {
                preferences.Elements.SetBoolean("/DisplayDocTitle", true);
            }
            else
            {
                preferences.Elements.Remove("/DisplayDocTitle");
            }
        }
    }

    /// <summary>The page and zoom the document opens at now (to keep the one not changed).</summary>
    private static (int? Page, OpenZoom? Zoom) DocumentStructureReaderOpen(PdfDocument document)
    {
        var destination = Destinations.Explicit(document.Internals.Catalog.Elements["/OpenAction"], new NamedDestinations(document));
        if (destination is not { Elements.Count: >= 2 })
        {
            return (null, null);
        }

        int? page = null;
        for (var i = 0; i < document.PageCount && page is null; i++)
        {
            if (ReferenceEquals(document.Pages[i], AsDictionary(destination.Elements[0])))
            {
                page = i;
            }
        }

        var zoom = Resolve(destination.Elements[1]) switch
        {
            PdfName { Value: "/Fit" or "/FitB" } => OpenZoom.FitPage,
            PdfName { Value: "/FitH" or "/FitBH" } => OpenZoom.FitWidth,
            _ => (OpenZoom?)null,
        };
        return (page, zoom);
    }
}

/// <param name="Ranges">The label ranges (empty removes the labels: pages are numbered 1, 2, 3…).</param>
public sealed record SetPageLabelsParams(IReadOnlyList<PageLabelRange> Ranges);

/// <summary>
/// structure.pageLabels (<see cref="OperationKind.Content"/>, "Nhãn trang"): the document's page labels (12.4.2) — "i, ii,
/// iii" for the front matter, "1, 2…" for the body, a prefix ("PL-1") — written as one /PageLabels number tree. A first
/// range is added at page 1 when none starts there (the reader needs one).
/// </summary>
public sealed class SetPageLabelsOperation : DocumentContentOperation<SetPageLabelsParams>
{
    public const string OperationId = "structure.pageLabels";

    public override string Id => OperationId;

    public override string Stage => "Stage_Structure";

    protected override OperationValidationResult ValidateCore(SetPageLabelsParams parameters, DocumentInfo document)
        => parameters.Ranges.Any(r => r.FirstPage < 0 || r.FirstPage >= document.PageCount || r.Start < 1)
            ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Ranges), "A label range starts outside the document or below 1."))
            : OperationValidationResult.Success;

    protected override void Apply(PdfDocument document, string inputPath, SetPageLabelsParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var catalog = document.Internals.Catalog;
        var ranges = PageLabels.Normalize(parameters.Ranges, document.PageCount);
        if (ranges.Count == 0)
        {
            catalog.Elements.Remove("/PageLabels");
            return;
        }

        if (ranges[0].FirstPage > 0)
        {
            ranges = [new PageLabelRange(0), .. ranges];
        }

        var numbers = new PdfArray(document);
        foreach (var range in ranges)
        {
            var label = new PdfDictionary(document);
            label.Elements.SetName("/Type", "/PageLabel");
            var style = range.Style switch
            {
                PageLabelStyle.Decimal => "/D",
                PageLabelStyle.UpperRoman => "/R",
                PageLabelStyle.LowerRoman => "/r",
                PageLabelStyle.UpperLetters => "/A",
                PageLabelStyle.LowerLetters => "/a",
                _ => null,
            };
            if (style is not null)
            {
                label.Elements.SetName("/S", style);
            }

            if (range.Prefix is { Length: > 0 } prefix)
            {
                var text = TextNormalizer.ToNfc(prefix);
                label.Elements["/P"] = text.All(c => c < 128) ? new PdfString(text) : new PdfString(text, PdfStringEncoding.Unicode);
            }

            if (range.Start != 1)
            {
                label.Elements.SetInteger("/St", range.Start);
            }

            numbers.Elements.Add(new PdfInteger(range.FirstPage));
            numbers.Elements.Add(label);
        }

        var tree = new PdfDictionary(document);
        tree.Elements["/Nums"] = numbers;
        catalog.Elements["/PageLabels"] = tree;
    }
}

/// <param name="Add">Links to make.</param>
/// <param name="Remove">Links to take off, as listed.</param>
public sealed record EditLinksParams(IReadOnlyList<LinkSpec>? Add = null, IReadOnlyList<LinkRef>? Remove = null);

/// <summary>
/// structure.links (<see cref="OperationKind.Content"/>, "Liên kết"): links taken off pages (found again by their index
/// and area, never another one), then links made — to a web address or e-mail (http, https and mailto only: never a
/// program or a local file) or to a page — invisible, printable, in one revision.
/// </summary>
public sealed class EditLinksOperation : DocumentContentOperation<EditLinksParams>
{
    public const string OperationId = "structure.links";

    public override string Id => OperationId;

    public override string Stage => "Stage_Structure";

    /// <summary>Whether a web address may be a link: http, https or mailto, with something after the scheme.</summary>
    public static bool IsAllowedUrl(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto)
            && (uri.Scheme == Uri.UriSchemeMailto ? uri.OriginalString.Length > "mailto:".Length : uri.Host.Length > 0);

    protected override OperationValidationResult ValidateCore(EditLinksParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        if ((parameters.Add?.Count ?? 0) + (parameters.Remove?.Count ?? 0) == 0)
        {
            errors.Add(new OperationValidationError(nameof(parameters.Add), "Nothing to add or remove."));
        }

        foreach (var link in parameters.Add ?? [])
        {
            if (link.Page < 0 || link.Page >= document.PageCount || link.Rect.Width <= 0 || link.Rect.Height <= 0)
            {
                errors.Add(new OperationValidationError(nameof(parameters.Add), "A link is outside the document or has no area."));
            }
            else if (link.Target.Url is { } url ? !IsAllowedUrl(url) : link.Target.Page is not { } page || page < 0 || page >= document.PageCount)
            {
                errors.Add(new OperationValidationError(nameof(parameters.Add), "A link goes nowhere the document allows."));
            }
        }

        if ((parameters.Remove ?? []).Any(r => r.Page < 0 || r.Page >= document.PageCount))
        {
            errors.Add(new OperationValidationError(nameof(parameters.Remove), "A link to remove is outside the document."));
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed([.. errors]);
    }

    protected override void Apply(PdfDocument document, string inputPath, EditLinksParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new OperationProgress(0, 1, Stage));
        foreach (var page in (parameters.Remove ?? []).GroupBy(r => r.Page))
        {
            var annotations = Annotations(document.Pages[page.Key]) ?? throw new InvalidDataException("A link to remove is not on its page.");
            var doomed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var link in page)
            {
                var found = link.Index < annotations.Elements.Count && AsDictionary(annotations.Elements[link.Index]) is { } at && Matches(at, link) ? at : null;
                doomed.Add(found ?? throw new InvalidDataException("A link to remove is not on its page any more."));
            }

            var kept = new PdfArray(document);
            foreach (var item in annotations.Elements.Where(i => AsDictionary(i) is not { } d || !doomed.Contains(d)))
            {
                kept.Elements.Add(item);
            }

            document.Pages[page.Key].Elements["/Annots"] = kept;
        }

        foreach (var link in parameters.Add ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Pages[link.Page];
            var annotation = new PdfDictionary(document);
            annotation.Elements.SetName("/Type", "/Annot");
            annotation.Elements.SetName("/Subtype", "/Link");
            annotation.Elements["/Rect"] = new PdfArray(document, new PdfReal(link.Rect.Left), new PdfReal(link.Rect.Bottom), new PdfReal(link.Rect.Right), new PdfReal(link.Rect.Top));
            annotation.Elements["/Border"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
            annotation.Elements.SetInteger("/F", 4);
            annotation.Elements["/P"] = page.Reference!;
            if (link.Target.Url is { } url)
            {
                var action = new PdfDictionary(document);
                action.Elements.SetName("/S", "/URI");
                action.Elements["/URI"] = new PdfString(new Uri(url.Trim()).AbsoluteUri);
                annotation.Elements["/A"] = action;
            }
            else
            {
                annotation.Elements["/Dest"] = SetBookmarksOperation.Destination(document, link.Target.Page!.Value, link.Target.Top);
            }

            document.Internals.AddObject(annotation);
            var annotations = Annotations(page);
            var copy = new PdfArray(document);
            foreach (var item in annotations?.Elements ?? Enumerable.Empty<PdfItem>())
            {
                copy.Elements.Add(item);
            }

            copy.Elements.Add(annotation.Reference!);
            page.Elements["/Annots"] = copy;
        }

        progress.Report(new OperationProgress(1, 1, Stage));
    }

    private static bool Matches(PdfDictionary annotation, LinkRef link)
        => Name(annotation, "/Subtype") == "/Link" && DocumentStructureReader.Rect(annotation) is { } rect
            && Math.Abs(rect.Left - link.Rect.Left) <= 1 && Math.Abs(rect.Bottom - link.Rect.Bottom) <= 1
            && Math.Abs(rect.Right - link.Rect.Right) <= 1 && Math.Abs(rect.Top - link.Rect.Top) <= 1;
}
