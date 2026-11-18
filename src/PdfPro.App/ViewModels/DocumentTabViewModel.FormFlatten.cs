using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Operations.Forms;
using PdfPro.App.Services;

namespace PdfPro.App.ViewModels;

/// <summary>"Làm phẳng biểu mẫu" and "Xóa dữ liệu" of a document tab.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>
    /// "Làm phẳng biểu mẫu": after a confirmation, the fields' values are drawn into the pages and the form is removed
    /// (forms.flatten, the next revision, one undoable step).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFlattenForm))]
    private async Task FlattenFormAsync()
    {
        var count = Forms.Fields.Count;
        var choice = await _messages.AskAsync(
            Strings.FormFlatten_Title,
            string.Format(CultureInfo.CurrentCulture, Strings.FormFlatten_Message, count),
            Strings.FormFlatten_Confirm,
            null,
            Strings.Common_Cancel);
        if (choice != DialogChoice.Primary || !CanFlattenForm())
        {
            return;
        }

        RequestContentChange(new ContentChangeRequest(_operations.Get<FlattenFormOperation>(), new FlattenFormParams(), Strings.History_FlattenForm));
    }

    private bool CanFlattenForm() => HasFormFields() && CanChangeContent();

    /// <summary>"Xóa dữ liệu": every field back to its default value (forms.reset, one undoable step).</summary>
    [RelayCommand(CanExecute = nameof(CanImportFormData))]
    private void ResetForm()
    {
        var values = ResetFormOperation.Defaults(Forms.Fields);
        if (values.Count > 0)
        {
            Apply(_operations.Get<ResetFormOperation>(), new ResetFormParams(values));
        }
    }
}
