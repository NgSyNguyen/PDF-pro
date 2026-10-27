using CommunityToolkit.Mvvm.Input;
using PdfPro.App.ViewModels.Editing;

namespace PdfPro.App.ViewModels;

/// <summary>"Sửa nội dung" of a document tab.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>
    /// "Sửa nội dung": turns the content editing of the viewer on or off. It takes the viewer's mouse: the page editor
    /// closes (nothing may be waiting in it), the organizer gives way and annotation tools turn off.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleContentEditing))]
    private void ToggleContentEditing()
    {
        if (ContentEditor.IsActive)
        {
            ContentEditor.IsActive = false;
            return;
        }

        if (Editor.IsActive)
        {
            Editor.IsActive = false;
        }

        IsOrganizing = false;
        Annotations.StopTool();
        TextSelection.Clear();
        ContentEditor.IsActive = true;
    }

    private bool CanToggleContentEditing() => ContentEditor.IsActive || CanChangeContent();

    private void OnContentEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContentEditorViewModel.IsActive))
        {
            UpdateFormsActive();
            OnPropertyChanged(nameof(IsEditingContent));
            ToggleContentEditingCommand.NotifyCanExecuteChanged();
            NotifyAnnotationCommands();
        }
    }

    /// <summary>The form's inputs take the mouse only while no other mode uses the viewer.</summary>
    private void UpdateFormsActive()
        => Forms.IsActive = !ContentEditor.IsActive && !Editor.IsActive && !IsOrganizing && Annotations.Tool == Annotating.AnnotationTool.None;

    /// <summary>The content editing mode is on (the ribbon button shows it pressed).</summary>
    public bool IsEditingContent => ContentEditor.IsActive;
}
