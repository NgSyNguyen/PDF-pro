using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Stamping;
using PdfPro.Operations.Organizing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Stamping;

/// <summary>
/// stamp.headerFooter: text in up to six places (left, centre, right of the top and bottom) with {page}, {pages},
/// {date}, {filename} and {title}; optionally not on the first page (a cover), numbering then starting after it.
/// </summary>
public sealed class HeaderFooterOperation : DocumentContentOperation<HeaderFooterParams>
{
    public const string OperationId = "stamp.headerFooter";

    public override string Id => OperationId;

    public override string Stage => "Stage_HeaderFooter";

    /// <summary>The six places with their texts, in anchor order.</summary>
    public static IReadOnlyList<(StampAnchor Anchor, string Member, string? Text)> Zones(HeaderFooterParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return
        [
            (StampAnchor.TopLeft, nameof(parameters.TopLeft), parameters.TopLeft),
            (StampAnchor.TopCenter, nameof(parameters.TopCenter), parameters.TopCenter),
            (StampAnchor.TopRight, nameof(parameters.TopRight), parameters.TopRight),
            (StampAnchor.BottomLeft, nameof(parameters.BottomLeft), parameters.BottomLeft),
            (StampAnchor.BottomCenter, nameof(parameters.BottomCenter), parameters.BottomCenter),
            (StampAnchor.BottomRight, nameof(parameters.BottomRight), parameters.BottomRight),
        ];
    }

    protected override OperationValidationResult ValidateCore(HeaderFooterParams parameters, DocumentInfo document)
    {
        var zones = Zones(parameters).Where(z => !string.IsNullOrWhiteSpace(z.Text)).ToArray();
        var results = new List<OperationValidationResult>
        {
            zones.Length > 0
                ? OperationValidationResult.Success
                : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.TopLeft), "At least one place needs text.")),
            StampContent.CheckStyle(parameters.Style, nameof(parameters.Style)),
            StampContent.CheckPages(parameters.Pages, document, nameof(parameters.Pages)),
        };
        results.AddRange(zones
            .Where(z => !StampTemplate.TryParse(z.Text, out _, out _, out _))
            .Select(z => OperationValidationResult.Failed(new OperationValidationError(z.Member, "The text has an unknown or unclosed {field}."))));
        return ParameterChecks.Combine([.. results]);
    }

    protected override void Apply(PdfDocument document, string inputPath, HeaderFooterParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var chosen = StampContent.Pages(parameters.Pages, document.PageCount);
        var pages = parameters.SkipFirstPage ? chosen.Skip(1).ToArray() : chosen;
        if (pages.Count == 0)
        {
            return;
        }

        var first = pages[0];
        var last = PageNumbering.Format(PageNumbering.NumberOf(pages[^1], first, parameters.StartNumber)!.Value, parameters.NumberStyle);
        var date = parameters.Date ?? DateTime.Now;
        var fileName = parameters.FileName ?? Path.GetFileNameWithoutExtension(inputPath);
        var title = string.IsNullOrWhiteSpace(document.Info.Title) ? null : document.Info.Title;
        StampValues Values(int page)
            => new(PageNumbering.Format(PageNumbering.NumberOf(page, first, parameters.StartNumber)!.Value, parameters.NumberStyle), last, date, fileName, title);

        var zones = Zones(parameters)
            .Where(z => !string.IsNullOrWhiteSpace(z.Text))
            .Select(z => (z.Anchor, Template: StampTemplate.Parse(z.Text!)))
            .Select(z => new TextZone(z.Anchor, page => z.Template.Render(Values(page))))
            .ToArray();
        TextZones.Stamp(document, pages, zones, parameters.Style ?? HeaderFooterParams.DefaultStyle, parameters.MarginX, parameters.MarginY, StampContent.HeaderFooterLayer, progress, Stage, cancellationToken);
    }
}
