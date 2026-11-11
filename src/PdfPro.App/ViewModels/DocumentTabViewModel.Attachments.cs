using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Operations.Extracting;

namespace PdfPro.App.ViewModels;

/// <summary>Files attached to and removed from a document tab (attachments.edit), and its structure changed (structure.*).</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>Whether attachments can be changed now (an editable document, nothing waiting in the editor).</summary>
    public bool CanChangeAttachments => CanChangeContent();

    /// <summary>The attachments added and removed in the dialog, written as the next revision (one undoable step).</summary>
    public void EditAttachments(EditAttachmentsParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!CanChangeContent())
        {
            return;
        }

        var description = Workspace.Describe(new HistoryRecord(EditAttachmentsOperation.OperationId, parameters, []), Workspace.Session.Plan);
        RequestContentChange(new ContentChangeRequest(_operations.Get<EditAttachmentsOperation>(), parameters, description));
    }

    /// <summary>
    /// A structure change (bookmarks, description, page labels, links) written as the next revision (one undoable
    /// step); false when the document cannot change now.
    /// </summary>
    public bool ChangeStructure<TOperation>(object parameters)
        where TOperation : class, IContentOperation
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!CanChangeContent())
        {
            return false;
        }

        var operation = _operations.Get<TOperation>();
        var description = Workspace.Describe(new HistoryRecord(operation.Id, parameters, []), Workspace.Session.Plan);
        return RequestContentChange(new ContentChangeRequest(operation, parameters, description));
    }
}
