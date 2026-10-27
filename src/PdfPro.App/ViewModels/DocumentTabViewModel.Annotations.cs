using CommunityToolkit.Mvvm.Input;
using PdfPro.App.ViewModels.Annotating;
using PdfPro.Core.Sessions;
using PdfPro.Operations.Annotating;

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

    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private Task TextBoxAsync() => UseAnnotationToolAsync(AnnotationTool.FreeText);

    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private Task InkAsync() => UseAnnotationToolAsync(AnnotationTool.Ink);

    /// <summary>"Hình học": rectangles first; the tool bar offers the other shapes.</summary>
    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private Task ShapesAsync() => UseAnnotationToolAsync(AnnotationTool.Rectangle);

    [RelayCommand(CanExecute = nameof(CanAnnotate))]
    private Task StampAnnotationAsync() => UseAnnotationToolAsync(AnnotationTool.Stamp);

    /// <summary>
    /// "Làm phẳng": the dialog (which comments, on which pages — the whole document by default), then the window draws
    /// them into the page content (the next revision, one undoable step).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeContent))]
    private void FlattenAnnotations()
    {
        Annotations.StopTool();
        var dialog = new FlattenViewModel(Viewer.PageCount, TargetPageIndices);
        if (!_services.PageDialogs.ShowFlatten(dialog) || dialog.Result is not { } parameters || !CanChangeContent())
        {
            return;
        }

        var description = Workspace.Describe(new HistoryRecord(FlattenAnnotationsOperation.OperationId, parameters, []), Workspace.Session.Plan);
        ContentChangeRequested?.Invoke(this, new ContentChangeRequest(_operations.Get<FlattenAnnotationsOperation>(), parameters, description));
    }

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
        TextBoxCommand.NotifyCanExecuteChanged();
        InkCommand.NotifyCanExecuteChanged();
        ShapesCommand.NotifyCanExecuteChanged();
        StampAnnotationCommand.NotifyCanExecuteChanged();
        FlattenAnnotationsCommand.NotifyCanExecuteChanged();
    }
}
