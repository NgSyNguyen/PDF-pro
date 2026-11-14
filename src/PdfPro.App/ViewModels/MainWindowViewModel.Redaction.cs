using PdfPro.App.Resources;
using PdfPro.App.Services;

namespace PdfPro.App.ViewModels;

/// <summary>Redaction marks that wait: the user decides about them before a save, a close or the exit.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Saving a document whose marks are not applied yet would keep what they cover: "Áp dụng rồi lưu" (the
    /// confirmation, then the redaction), "Lưu, giữ đánh dấu" or Hủy. True when the save goes on.
    /// </summary>
    private async Task<bool> ConfirmRedactionMarksBeforeSaveAsync(DocumentTabViewModel document)
    {
        if (!document.HasRedactionMarks)
        {
            return true;
        }

        var count = document.Redaction.Marks.Count;
        switch (await _messages.AskAsync(Strings.Redaction_Pending_Title, Format(Strings.Redaction_Pending_Save, count), Strings.Redaction_Pending_Apply, Strings.Redaction_Pending_SaveAnyway, Strings.Common_Cancel))
        {
            case DialogChoice.Primary:
                if (document.RedactionRequest(confirm: true) is not { } request)
                {
                    return false;
                }

                var applied = await ChangeContentAsync(document, request);
                request.Completed?.Invoke(applied);
                return applied;
            case DialogChoice.Secondary:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Closing a document drops its marks: the user confirms first. True when it may close.</summary>
    private async Task<bool> ConfirmDropRedactionMarksAsync(DocumentTabViewModel document)
        => !document.HasRedactionMarks
            || await _messages.AskAsync(Strings.Redaction_Pending_Title, Format(Strings.Redaction_Pending_Close, document.Redaction.Marks.Count, document.FileName), Strings.Redaction_Pending_Drop, null, Strings.Common_Cancel) == DialogChoice.Primary;
}
