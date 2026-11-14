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

    /// <summary>
    /// The text each place gets on page <paramref name="pageIndex"/> of a document of <paramref name="pageCount"/>
    /// pages (nothing when the page gets no header or footer): what the operation writes, for previews.
    /// </summary>
    public static IReadOnlyDictionary<StampAnchor, string> TextsOn(HeaderFooterParams parameters, int pageIndex, int pageCount, string fileName, string? title, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var numbering = Numbering(parameters, pageCount);
        if (numbering is not { } plan || !plan.Pages.Contains(pageIndex))
        {
            return new Dictionary<StampAnchor, string>();
        }

        var values = plan.Values(pageIndex, date, fileName, title);
        return Zones(parameters)
            .Where(z => !string.IsNullOrWhiteSpace(z.Text) && StampTemplate.TryParse(z.Text, out _, out _, out _))
            .ToDictionary(z => z.Anchor, z => StampTemplate.Parse(z.Text!).Render(values));
    }

    protected override void Apply(PdfDocument document, string inputPath, HeaderFooterParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (Numbering(parameters, document.PageCount) is not { } plan)
        {
            return;
        }

        var date = parameters.Date ?? DateTime.Now;
        var fileName = parameters.FileName ?? Path.GetFileNameWithoutExtension(inputPath);
        var title = string.IsNullOrWhiteSpace(document.Info.Title) ? null : document.Info.Title;
        var zones = Zones(parameters)
            .Where(z => !string.IsNullOrWhiteSpace(z.Text))
            .Select(z => (z.Anchor, Template: StampTemplate.Parse(z.Text!)))
            .Select(z => new TextZone(z.Anchor, page => z.Template.Render(plan.Values(page, date, fileName, title))))
            .ToArray();
        TextZones.Stamp(document, plan.Pages, zones, parameters.Style ?? HeaderFooterParams.DefaultStyle, parameters.MarginX, parameters.MarginY, StampContent.HeaderFooterLayer, progress, Stage, cancellationToken);
    }

    /// <summary>The stamped pages (the cover left out when asked) and their numbers; null when no page is stamped.</summary>
    private static PageNumberPlan? Numbering(HeaderFooterParams parameters, int pageCount)
    {
        var chosen = StampContent.Pages(parameters.Pages, pageCount);
        var pages = parameters.SkipFirstPage ? chosen.Skip(1).ToArray() : chosen;
        return pages.Count == 0 ? null : new PageNumberPlan(pages, pages[0], parameters.StartNumber, parameters.NumberStyle);
    }
}

/// <summary>Numbers of stamped pages: <see cref="First"/> gets <see cref="StartNumber"/>, each following page one more.</summary>
internal sealed record PageNumberPlan(IReadOnlyList<int> Pages, int First, int StartNumber, PageNumberStyle Style)
{
    public string Number(int pageIndex) => PageNumbering.Format(PageNumbering.NumberOf(pageIndex, First, StartNumber)!.Value, Style);

    /// <summary>"{pages}": the number of the last stamped page.</summary>
    public string Last => Number(Pages[^1]);

    public StampValues Values(int pageIndex, DateTime date, string fileName, string? title) => new(Number(pageIndex), Last, date, fileName, title);
}
