using PdfPro.Core.Sessions;
using PdfPro.Operations.Extracting;

namespace PdfPro.App.ViewModels;

/// <summary>Files attached to and removed from a document tab (attachments.edit).</summary>
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
}
