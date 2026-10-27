using CommunityToolkit.Mvvm.Input;
using PdfPro.App.ViewModels.Annotating;

namespace PdfPro.App.ViewModels;

/// <summary>The "Chú thích" commands of a document tab.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>Highlights the selected text, or turns the highlighter on.</summary>
    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private Task HighlightAsync() => UseAnnotationToolAsync(AnnotationTool.Highlight);

    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private Task UnderlineAsync() => UseAnnotationToolAsync(AnnotationTool.Underline);

    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private Task StrikeOutAsync() => UseAnnotationToolAsync(AnnotationTool.StrikeOut);

    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private Task SquigglyAsync() => UseAnnotationToolAsync(AnnotationTool.Squiggly);

    /// <summary>The next click on a page places a sticky note.</summary>
    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private Task StickyNoteAsync() => UseAnnotationToolAsync(AnnotationTool.Note);

    /// <summary>Annotating needs an editable document shown in the viewer, with the page editor closed.</summary>
    private bool CanAnnotate() => CanEditPages && !IsOrganizing && !Editor.IsActive;

    private Task UseAnnotationToolAsync(AnnotationTool tool)
    {
        TextSelection.Release();
        return Annotations.UseToolAsync(tool);
    }

    private void NotifyAnnotationCommands()
    {
        HighlightCommand.NotifyCanExecuteChanged();
        UnderlineCommand.NotifyCanExecuteChanged();
        StrikeOutCommand.NotifyCanExecuteChanged();
        SquigglyCommand.NotifyCanExecuteChanged();
        StickyNoteCommand.NotifyCanExecuteChanged();
    }
}
