using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Materializing;

/// <summary>
/// After pages were resized, destinations on them (bookmarks, links, named destinations, the open action) still give
/// coordinates in the old page space: move them with the page content so a jump shows the same spot.
/// </summary>
internal static class MovedDestinations
{
    private const int MaxOutlineDepth = 64;
    private const int MaxOutlineItems = 100_000;

    /// <param name="document">The document.</param>
    /// <param name="transforms">How each resized page's coordinates moved, by page object id.</param>
    public static void Apply(PdfDocument document, IReadOnlyDictionary<PdfObjectID, PageTransform> transforms)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(transforms);
        if (transforms.Count == 0)
        {
            return;
        }

        var done = new HashSet<PdfArray>(ReferenceEqualityComparer.Instance);
        void Move(PdfArray? destination)
        {
            if (destination is not null && done.Add(destination))
            {
                MoveDestination(destination, transforms);
            }
        }

        var names = new NamedDestinations(document);
        foreach (var destination in names.All)
        {
            Move(destination);
        }

        foreach (var page in document.Pages)
        {
            if (Annotations(page) is not { } annotations)
            {
                continue;
            }

            foreach (var item in annotations.Elements)
            {
                if (AsDictionary(item) is { } annotation && Name(annotation, "/Subtype") == "/Link" && Destinations.IsInternal(annotation))
                {
                    Move(Destinations.Of(annotation, names));
                }
            }
        }

        var catalog = document.Internals.Catalog;
        if (AsDictionary(catalog.Elements["/Outlines"]) is { } outlines)
        {
            var budget = MaxOutlineItems;
            MoveOutline(outlines, names, depth: 0, ref budget, Move);
        }

        var openAction = catalog.Elements["/OpenAction"];
        Move(AsDictionary(openAction) is { } action ? Destinations.Of(action, names) : Destinations.Explicit(openAction, names));
    }

    private static void MoveOutline(PdfDictionary parent, NamedDestinations names, int depth, ref int budget, Action<PdfArray?> move)
    {
        if (depth >= MaxOutlineDepth)
        {
            return;
        }

        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var item = AsDictionary(parent.Elements["/First"]); item is not null && seen.Add(item) && --budget >= 0; item = AsDictionary(item.Elements["/Next"]))
        {
            if (Destinations.IsInternal(item))
            {
                move(Destinations.Of(item, names));
            }

            MoveOutline(item, names, depth + 1, ref budget, move);
        }
    }

    /// <summary>Moves the coordinates of an explicit destination [page /XYZ left top zoom], [page /FitH top]…</summary>
    private static void MoveDestination(PdfArray destination, IReadOnlyDictionary<PdfObjectID, PageTransform> transforms)
    {
        var elements = destination.Elements;
        if (elements.Count < 2 || elements[0] is not PdfReference page || !transforms.TryGetValue(page.ObjectID, out var transform)
            || Resolve(elements[1]) is not PdfName { Value: var kind })
        {
            return;
        }

        switch (kind)
        {
            case "/XYZ" when elements.Count >= 4:
                MovePoint(elements, 2, 3, transform);
                break;
            case "/FitH" or "/FitBH" when elements.Count >= 3:
                MoveCoordinate(elements, 2, transform, vertical: true);
                break;
            case "/FitV" or "/FitBV" when elements.Count >= 3:
                MoveCoordinate(elements, 2, transform, vertical: false);
                break;
            case "/FitR" when elements.Count >= 6:
                MovePoint(elements, 2, 3, transform);
                MovePoint(elements, 4, 5, transform);
                break;
        }
    }

    /// <summary>A point whose coordinates may each be null ("keep the current one").</summary>
    private static void MovePoint(PdfArray.ArrayElements elements, int x, int y, PageTransform transform)
    {
        MoveCoordinate(elements, x, transform, vertical: false);
        MoveCoordinate(elements, y, transform, vertical: true);
    }

    private static void MoveCoordinate(PdfArray.ArrayElements elements, int index, PageTransform transform, bool vertical)
    {
        double? value = Resolve(elements[index]) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => null,
        };

        if (value is { } v)
        {
            var moved = vertical ? transform.Apply(0, v).Y : transform.Apply(v, 0).X;
            elements[index] = new PdfReal(Math.Round(moved, 4));
        }
    }
}
