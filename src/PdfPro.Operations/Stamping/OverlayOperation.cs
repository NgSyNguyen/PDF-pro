using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Operations.Fonts;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Stamping;

/// <param name="Items">The objects to write into their pages, in drawing order (later ones on top).</param>
public sealed record OverlayParams([property: Required, MinLength(1), MaxLength(OverlayOperation.MaxItems)] IReadOnlyList<OverlayItem> Items);

/// <summary>
/// "Áp dụng" of the page editor (<see cref="OperationKind.Content"/>): writes text boxes, pictures, signatures, shapes,
/// whiteouts and lines into their pages, over the existing content. Text is embedded with subset fonts and stays
/// searchable. In a session one call is one revision, so one undo step removes everything applied at once.
/// </summary>
public sealed class OverlayOperation : DocumentContentOperation<OverlayParams>
{
    public const string OperationId = "edit.overlay";

    /// <summary>Resource key of the progress stage.</summary>
    public const string DrawStage = "Stage_ApplyOverlay";

    public const int MaxItems = 10000;

    public override string Id => OperationId;

    public override string Stage => DrawStage;

    protected override OperationValidationResult ValidateCore(OverlayParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        for (var i = 0; i < parameters.Items.Count; i++)
        {
            var member = $"{nameof(parameters.Items)}[{i}]";
            if (parameters.Items[i] is not { } item)
            {
                errors.Add(new OperationValidationError(member, "The object is missing."));
                continue;
            }

            errors.AddRange(item.Validate(document.PageCount).Select(problem => new OperationValidationError(member, problem)));
            if (item is TextOverlay { Style.FontFamily: { Length: > 0 } family } && !FontLibrary.Instance.Contains(family))
            {
                errors.Add(new OperationValidationError(member, "The font is not available on this computer."));
            }

            if (item is ImageOverlay { ImagePath: { Length: > 0 } path } && !File.Exists(path))
            {
                errors.Add(new OperationValidationError(member, "The image file does not exist."));
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    protected override void Apply(PdfDocument document, string inputPath, OverlayParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        // Objects of one page go in one stream, in their order; pages are visited in document order.
        var byPage = parameters.Items.Select((item, order) => (item, order)).GroupBy(x => x.item.PageIndex).OrderBy(g => g.Key).ToArray();
        for (var i = 0; i < byPage.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(i, byPage.Length, Stage));
            var page = document.Pages[byPage[i].Key];
            var geometry = PageStamper.Geometry(page);
            var placements = byPage[i].OrderBy(x => x.order).Select(x => OverlayPainter.Paint(document, geometry, x.item)).ToArray();
            PageStamper.Place(page, placements, StampDepth.Above);
        }

        progress.Report(new OperationProgress(byPage.Length, byPage.Length, Stage));
    }
}
