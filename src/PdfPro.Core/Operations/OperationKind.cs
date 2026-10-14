namespace PdfPro.Core.Operations;

/// <summary>
/// Describes how an operation changes a document session.
/// </summary>
public enum OperationKind
{
    /// <summary>Only edits the page plan (reorder, rotate, delete…). Instant, no file is written.</summary>
    PlanOnly,

    /// <summary>Changes page content and produces a new revision file.</summary>
    Content,
}
