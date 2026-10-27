using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Core.Stamping;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Stamping;

/// <summary>A place on the page and the text it gets on each page (empty: nothing on that page).</summary>
internal sealed record TextZone(StampAnchor Anchor, Func<int, string> TextOf);

/// <summary>
/// Writes short texts (headers, footers, page numbers, Bates numbers) at fixed places of pages. Lines align towards
/// their side of the page; the same text at the same place is drawn once and reused on every page.
/// </summary>
internal static class TextZones
{
    public static void Stamp(
        PdfDocument document,
        IReadOnlyList<int> pages,
        IReadOnlyList<TextZone> zones,
        TextStyle style,
        double marginX,
        double marginY,
        StampLayer layer,
        IProgress<OperationProgress> progress,
        string stage,
        CancellationToken cancellationToken)
    {
        var drawings = new Dictionary<(int Column, string Text), StampDrawing?>();
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(i, pages.Count, stage));
            var page = document.Pages[pages[i]];
            var geometry = PageStamper.Geometry(page);
            var placements = new List<FormPlacement>();
            foreach (var zone in zones)
            {
                var text = zone.TextOf(pages[i]);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var column = (int)zone.Anchor % 3;
                if (!drawings.TryGetValue((column, text), out var drawing))
                {
                    var alignment = column switch { 0 => TextAlignment.Left, 1 => TextAlignment.Center, _ => TextAlignment.Right };
                    drawings[(column, text)] = drawing = StampContent.Text(document, text, style with { Alignment = alignment });
                }

                if (drawing is not null)
                {
                    var center = StampPlacement.Place(geometry.DisplaySize, drawing.Size, 0, zone.Anchor, marginX, marginY);
                    placements.Add(new FormPlacement(drawing.Form.Form, StampContent.Placement(drawing, geometry, center)));
                }
            }

            PageStamper.Place(page, placements, StampDepth.Above, layer);
        }

        progress.Report(new OperationProgress(pages.Count, pages.Count, stage));
    }
}
