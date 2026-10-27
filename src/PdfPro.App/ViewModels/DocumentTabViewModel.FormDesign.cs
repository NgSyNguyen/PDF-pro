using CommunityToolkit.Mvvm.Input;
using PdfPro.App.ViewModels.Forms;

namespace PdfPro.App.ViewModels;

/// <summary>"Thiết kế biểu mẫu" of a document tab.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>The form designer: fields as frames over the pages, placed, moved, resized, changed, deleted.</summary>
    public FormDesignerViewModel FormDesigner { get; }

    /// <summary>The form designer is on (the ribbon button shows it pressed).</summary>
    public bool IsDesigningForm => FormDesigner.IsActive;

    /// <summary>The properties panel shows something to change (the page editor's objects or a field).</summary>
    public bool ShowsProperties => Editor.IsActive || FormDesigner.IsActive;

    /// <summary>
    /// "Thiết kế biểu mẫu": turns the form designer on or off. It takes the viewer's mouse: the page editor closes
    /// (nothing may be waiting in it), content editing and annotation tools turn off, the organizer gives way. A form
    /// that is XFA only cannot be designed here.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleFormDesign))]
    private void ToggleFormDesign()
    {
        if (FormDesigner.IsActive)
        {
            FormDesigner.IsActive = false;
            return;
        }

        if (Editor.IsActive)
        {
            Editor.IsActive = false;
        }

        IsOrganizing = false;
        Annotations.StopTool();
        ContentEditor.IsActive = false;
        TextSelection.Clear();
        FormDesigner.IsActive = true;
    }

    private bool CanToggleFormDesign() => FormDesigner.IsActive || (CanChangeContent() && !Forms.IsXfaOnly);

    private void OnFormDesignerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FormDesignerViewModel.IsActive))
        {
            if (FormDesigner.IsActive)
            {
                ContentEditor.IsActive = false;
            }

            UpdateFormsActive();
            OnPropertyChanged(nameof(IsDesigningForm));
            OnPropertyChanged(nameof(ShowsProperties));
            ToggleFormDesignCommand.NotifyCanExecuteChanged();
            ToggleContentEditingCommand.NotifyCanExecuteChanged();
            NotifyAnnotationCommands();
        }
    }
}
