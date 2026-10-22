using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Organizing;

/// <param name="Pages">Pages to mark as first pages of split files (or to unmark).</param>
/// <param name="Marked">False removes the marks.</param>
public sealed record MarkSplitParams([property: Required] string Pages, bool Marked = true);

/// <param name="Page">1-based page number.</param>
/// <param name="FileName">The file name chosen for the file it starts, or null.</param>
public sealed record SplitMarkEntry(int Page, string? FileName = null);

/// <param name="Marks">Every mark the document should have (an empty list removes them all).</param>
public sealed record SetSplitMarksParams([property: Required] IReadOnlyList<SplitMarkEntry> Marks);

/// <summary>
/// "Trang đầu tệp" (Ctrl+M): marks or unmarks pages as the first page of a file for a split by marks. Marks are not
/// content: the step is undoable but does not make the document unsaved.
/// </summary>
public sealed class MarkSplitOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<MarkSplitParams>(materializer, loader)
{
    public const string OperationId = "organize.markSplit";

    public override string Id => OperationId;

    public override bool ChangesContent => false;

    protected override OperationValidationResult ValidateCore(MarkSplitParams parameters, DocumentInfo document)
        => CheckPages(parameters.Pages, document.PageCount, nameof(parameters.Pages));

    protected override PagePlan Apply(PagePlan plan, MarkSplitParams parameters, PlanEditContext context)
        => plan.MarkSplit(PageRanges.Parse(parameters.Pages, plan.Count), parameters.Marked);
}

/// <summary>
/// Replaces every split mark at once, with file names (the split dialog's list of first pages and names, "Bỏ mọi
/// dấu", turning a computed split into marks). Not content, like <see cref="MarkSplitOperation"/>.
/// </summary>
public sealed class SetSplitMarksOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<SetSplitMarksParams>(materializer, loader)
{
    public const string OperationId = "organize.setSplitMarks";

    public override string Id => OperationId;

    public override bool ChangesContent => false;

    protected override OperationValidationResult ValidateCore(SetSplitMarksParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        var pages = new HashSet<int>();
        foreach (var mark in parameters.Marks)
        {
            if (mark is null || mark.Page < 1 || mark.Page > document.PageCount)
            {
                errors.Add(new OperationValidationError(nameof(parameters.Marks), $"Pages must be between 1 and {document.PageCount}."));
            }
            else if (!pages.Add(mark.Page))
            {
                errors.Add(new OperationValidationError(nameof(parameters.Marks), $"Page {mark.Page} is marked twice."));
            }
            else if (mark.FileName is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > SplitMark.MaxFileNameLength))
            {
                errors.Add(new OperationValidationError(nameof(parameters.Marks), $"The file name of page {mark.Page} is blank or too long."));
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    protected override PagePlan Apply(PagePlan plan, SetSplitMarksParams parameters, PlanEditContext context)
        => plan.WithSplitMarks(parameters.Marks.ToDictionary(m => m.Page - 1, m => new SplitMark(m.FileName)));
}
