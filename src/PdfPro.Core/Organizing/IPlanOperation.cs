using PdfPro.Core.Operations;

namespace PdfPro.Core.Organizing;

/// <summary>
/// A page operation (<see cref="OperationKind.PlanOnly"/>): reorder, rotate, delete, insert, crop… In a document
/// session it only edits the page plan, instantly and without writing files; run on files (CLI, batch) it applies the
/// same edit to an identity plan and materializes the result.
/// </summary>
public interface IPlanOperation : IPdfOperation
{
    /// <summary>Applies the operation to a plan. Returns the same plan instance when nothing changes.</summary>
    /// <exception cref="OperationValidationException">The parameters do not fit this plan.</exception>
    PagePlan ApplyToPlan(PagePlan plan, object parameters, PlanEditContext context);
}
