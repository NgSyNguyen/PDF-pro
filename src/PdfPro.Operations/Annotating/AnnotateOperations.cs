using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using PdfPro.Core.Annotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Annotating;

/// <param name="Page">1-based page number.</param>
/// <param name="Items">The annotations to add, drawn above the page's other annotations; names new on the page.</param>
public sealed record PageAnnotations([property: Range(1, int.MaxValue)] int Page, [property: Required] IReadOnlyList<AnnotationItem> Items);

/// <param name="Pages">The annotations to add, by page (text marked across pages is added in one step).</param>
public sealed record AddAnnotationsParams([property: Required] IReadOnlyList<PageAnnotations> Pages)
{
    /// <summary>Annotations for one page.</summary>
    public static AddAnnotationsParams On(int page, IReadOnlyList<AnnotationItem> items) => new([new PageAnnotations(page, items)]);

    /// <summary>How many annotations are added.</summary>
    [JsonIgnore]
    public int Count => Pages.Sum(p => p.Items?.Count ?? 0);
}

/// <param name="Page">1-based page number.</param>
/// <param name="Items">The changed annotations, replacing those of the same names (moved, recoloured, answered…).</param>
public sealed record UpdateAnnotationsParams([property: Range(1, int.MaxValue)] int Page, [property: Required] IReadOnlyList<AnnotationItem> Items);

/// <param name="Page">1-based page number.</param>
/// <param name="Ids">Names of the annotations to remove.</param>
public sealed record DeleteAnnotationsParams([property: Range(1, int.MaxValue)] int Page, [property: Required] IReadOnlyList<string> Ids);

/// <summary>
/// "Thêm chú thích": adds annotations made in the session to a page. Like the other page operations it only edits the
/// plan (instant, one undo step); the annotations are written into the file — with their appearances — when the plan
/// is saved or materialized for a content change.
/// </summary>
public sealed class AddAnnotationsOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<AddAnnotationsParams>(materializer, loader)
{
    public const string OperationId = "annotate.add";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(AddAnnotationsParams parameters, DocumentInfo document)
    {
        if (parameters.Pages is null || parameters.Pages.Count == 0)
        {
            return AnnotationChecks.Failed(nameof(parameters.Pages), "At least one page is needed.");
        }

        var results = new List<OperationValidationResult>();
        var pages = new HashSet<int>();
        foreach (var entry in parameters.Pages)
        {
            if (entry is null)
            {
                results.Add(AnnotationChecks.Failed(nameof(parameters.Pages), "A page is missing."));
                continue;
            }

            if (!pages.Add(entry.Page))
            {
                results.Add(AnnotationChecks.Failed(nameof(parameters.Pages), $"Page {entry.Page} is listed twice."));
            }

            results.Add(AnnotationChecks.Items(entry.Page, entry.Items, document.PageCount));
        }

        return Combine([.. results]);
    }

    protected override OperationValidationResult ValidateForPlan(AddAnnotationsParams parameters, PagePlan plan, PlanEditContext context)
    {
        var results = new List<OperationValidationResult>();
        foreach (var entry in parameters.Pages)
        {
            var page = plan[entry.Page - 1];
            var taken = entry.Items.Where(i => page.FindAnnotation(i.Id) is not null).Select(i => i.Id).ToArray();
            if (taken.Length > 0)
            {
                results.Add(AnnotationChecks.Failed(nameof(parameters.Pages), $"Page {entry.Page} already has annotations named {string.Join(", ", taken)}."));
            }
        }

        return Combine([.. results]);
    }

    protected override PagePlan Apply(PagePlan plan, AddAnnotationsParams parameters, PlanEditContext context)
    {
        foreach (var entry in parameters.Pages)
        {
            var index = entry.Page - 1;
            plan = plan.WithAnnotations(index, [.. plan[index].Annotations, .. entry.Items]);
        }

        return plan;
    }
}

/// <summary>"Sửa chú thích": replaces session annotations of a page by name, keeping their drawing order.</summary>
public sealed class UpdateAnnotationsOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<UpdateAnnotationsParams>(materializer, loader)
{
    public const string OperationId = "annotate.update";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(UpdateAnnotationsParams parameters, DocumentInfo document)
        => AnnotationChecks.Items(parameters.Page, parameters.Items, document.PageCount);

    protected override OperationValidationResult ValidateForPlan(UpdateAnnotationsParams parameters, PagePlan plan, PlanEditContext context)
        => AnnotationChecks.Existing(plan[parameters.Page - 1], parameters.Page, parameters.Items.Select(i => i.Id), nameof(parameters.Items));

    protected override PagePlan Apply(PagePlan plan, UpdateAnnotationsParams parameters, PlanEditContext context)
    {
        var index = parameters.Page - 1;
        var changed = parameters.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        return plan.WithAnnotations(index, [.. plan[index].Annotations.Select(a => changed.GetValueOrDefault(a.Id) ?? a)]);
    }
}

/// <summary>"Xóa chú thích": removes session annotations of a page by name.</summary>
public sealed class DeleteAnnotationsOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<DeleteAnnotationsParams>(materializer, loader)
{
    public const string OperationId = "annotate.delete";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(DeleteAnnotationsParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        AnnotationChecks.Page(parameters.Page, document.PageCount, errors);
        if (parameters.Ids is null || parameters.Ids.Count == 0)
        {
            errors.Add(new OperationValidationError(nameof(parameters.Ids), "At least one annotation must be named."));
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    protected override OperationValidationResult ValidateForPlan(DeleteAnnotationsParams parameters, PagePlan plan, PlanEditContext context)
        => AnnotationChecks.Existing(plan[parameters.Page - 1], parameters.Page, parameters.Ids, nameof(parameters.Ids));

    protected override PagePlan Apply(PagePlan plan, DeleteAnnotationsParams parameters, PlanEditContext context)
    {
        var index = parameters.Page - 1;
        var removed = parameters.Ids.ToHashSet(StringComparer.Ordinal);
        return plan.WithAnnotations(index, [.. plan[index].Annotations.Where(a => !removed.Contains(a.Id))]);
    }
}

/// <summary>Validation shared by the annotation operations.</summary>
internal static class AnnotationChecks
{
    public static OperationValidationResult Items(int page, IReadOnlyList<AnnotationItem>? items, int pageCount)
    {
        var errors = new List<OperationValidationError>();
        Page(page, pageCount, errors);
        if (items is null || items.Count == 0)
        {
            errors.Add(new OperationValidationError(nameof(PageAnnotations.Items), "At least one annotation is needed."));
            return OperationValidationResult.Failed(errors);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var member = $"{nameof(PageAnnotations.Items)}[{i}]";
            if (items[i] is not { } item)
            {
                errors.Add(new OperationValidationError(member, "An annotation is missing."));
                continue;
            }

            errors.AddRange(item.Validate().Select(problem => new OperationValidationError(member, problem)));
            if (!names.Add(item.Id))
            {
                errors.Add(new OperationValidationError(member, $"The name {item.Id} is used twice."));
            }

            if (item is StampAnnotation { ImagePath: { } image } && !File.Exists(image))
            {
                errors.Add(new OperationValidationError(member, "The stamp's picture cannot be found."));
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    public static void Page(int page, int pageCount, List<OperationValidationError> errors)
    {
        if (page < 1 || page > pageCount)
        {
            errors.Add(new OperationValidationError("Page", $"The page must be between 1 and {pageCount}."));
        }
    }

    public static OperationValidationResult Existing(PageRef page, int pageNumber, IEnumerable<string> ids, string member)
    {
        var missing = ids.Where(id => page.FindAnnotation(id) is null).ToArray();
        return missing.Length == 0
            ? OperationValidationResult.Success
            : Failed(member, $"Page {pageNumber} has no annotations named {string.Join(", ", missing)}.");
    }

    public static OperationValidationResult Failed(string member, string message)
        => OperationValidationResult.Failed(new OperationValidationError(member, message));
}
