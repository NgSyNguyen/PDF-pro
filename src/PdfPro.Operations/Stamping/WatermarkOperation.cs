using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Stamping;
using PdfPro.Operations.Organizing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Stamping;

/// <summary>
/// stamp.watermark: text or a picture over (or under) the content of the chosen pages, at one of nine places or tiled,
/// turned and see-through. It lives in the "Watermark" layer, so viewers can hide it and stamp.remove can take it off.
/// </summary>
public sealed class WatermarkOperation : DocumentContentOperation<WatermarkParams>
{
    public const string OperationId = "stamp.watermark";

    public override string Id => OperationId;

    public override string Stage => "Stage_Watermark";

    protected override OperationValidationResult ValidateCore(WatermarkParams parameters, DocumentInfo document)
        => ParameterChecks.Combine(
            StampContent.CheckOneOf(!string.IsNullOrWhiteSpace(parameters.Text), parameters.ImagePath is not null, nameof(parameters.Text)),
            StampContent.CheckStyle(parameters.Style, nameof(parameters.Style)),
            StampContent.CheckImage(parameters.ImagePath, nameof(parameters.ImagePath)),
            StampContent.CheckPages(parameters.Pages, document, nameof(parameters.Pages)));

    protected override void Apply(PdfDocument document, string inputPath, WatermarkParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        // One drawing for the whole document, placed on every page.
        var drawing = parameters.ImagePath is { } image
            ? StampContent.Picture(document, image)
            : StampContent.Text(document, parameters.Text!, parameters.Style ?? WatermarkParams.DefaultStyle);
        var pages = StampContent.Pages(parameters.Pages, document.PageCount);
        var angle = -parameters.Rotation; // counter-clockwise for people, clockwise on screen
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(i, pages.Count, Stage));
            if (drawing is null)
            {
                continue;
            }

            var page = document.Pages[pages[i]];
            var geometry = PageStamper.Geometry(page);
            var shown = geometry.DisplaySize;
            var scale = parameters.ImagePath is null ? 1 : shown.Width * parameters.ImageScale / drawing.Size.Width;
            var size = new PdfSize(drawing.Size.Width * scale, drawing.Size.Height * scale);
            var centers = parameters.Tile
                ? StampPlacement.Tile(shown, size, angle, parameters.TileGap)
                : [StampPlacement.Place(shown, size, angle, parameters.Anchor, parameters.MarginX, parameters.MarginY)];
            var placements = centers
                .Select(c => new FormPlacement(drawing.Form.Form, StampContent.Placement(drawing, geometry, c, angle, scale, scale), parameters.Opacity))
                .ToArray();
            PageStamper.Place(page, placements, parameters.BehindContent ? StampDepth.Below : StampDepth.Above, StampContent.WatermarkLayer);
        }

        progress.Report(new OperationProgress(pages.Count, pages.Count, Stage));
    }
}
