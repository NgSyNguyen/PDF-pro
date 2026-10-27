using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Operations.Organizing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Stamping;

/// <summary>
/// stamp.background: a colour or a picture under the content of the chosen pages, covering the page as shown, in the
/// "Background" layer (removable with stamp.remove).
/// </summary>
public sealed class BackgroundOperation : DocumentContentOperation<BackgroundParams>
{
    public const string OperationId = "stamp.background";

    public override string Id => OperationId;

    public override string Stage => "Stage_Background";

    protected override OperationValidationResult ValidateCore(BackgroundParams parameters, DocumentInfo document)
        => ParameterChecks.Combine(
            StampContent.CheckOneOf(parameters.Color is not null, parameters.ImagePath is not null, nameof(parameters.Color)),
            StampContent.CheckImage(parameters.ImagePath, nameof(parameters.ImagePath)),
            StampContent.CheckPages(parameters.Pages, document, nameof(parameters.Pages)));

    protected override void Apply(PdfDocument document, string inputPath, BackgroundParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var picture = parameters.ImagePath is { } path ? StampContent.Picture(document, path) : null;
        var fills = new Dictionary<PdfSize, StampDrawing>();
        var pages = StampContent.Pages(parameters.Pages, document.PageCount);
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(i, pages.Count, Stage));
            var page = document.Pages[pages[i]];
            var geometry = PageStamper.Geometry(page);
            var shown = geometry.DisplaySize;
            var center = new ViewPoint(shown.Width / 2, shown.Height / 2);
            FormPlacement placement;
            if (picture is not null)
            {
                var (sx, sy) = (shown.Width / picture.Size.Width, shown.Height / picture.Size.Height);
                if (parameters.Fit == BackgroundFit.Fit)
                {
                    sx = sy = Math.Min(sx, sy);
                }

                placement = new FormPlacement(picture.Form.Form, StampContent.Placement(picture, geometry, center, 0, sx, sy), parameters.Opacity);
            }
            else
            {
                // One rectangle per page size.
                if (!fills.TryGetValue(shown, out var fill))
                {
                    fills[shown] = fill = StampContent.Fill(document, shown, parameters.Color!.Value);
                }

                placement = new FormPlacement(fill.Form.Form, StampContent.Placement(fill, geometry, center), parameters.Opacity);
            }

            PageStamper.Place(page, [placement], StampDepth.Below, StampContent.BackgroundLayer);
        }

        progress.Report(new OperationProgress(pages.Count, pages.Count, Stage));
    }
}
