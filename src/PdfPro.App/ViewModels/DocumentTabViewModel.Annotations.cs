using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Annotating;
using PdfPro.Core.Organizing;
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
    /// "Làm phẳng": once confirmed, the window draws the comments into the page content (the next revision, one undoable
    /// step) — of the whole document, or of the current page (the selected pages in the organizer).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeContent))]
    private async Task FlattenAnnotationsAsync()
    {
        Annotations.StopTool();
        var targets = TargetPageIndices;
        var only = targets.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, Strings.Flatten_OnlyPage, targets[0] + 1)
            : string.Format(CultureInfo.CurrentCulture, Strings.Flatten_OnlySelected, targets.Count);
        var choice = await _messages.AskAsync(Strings.Flatten_Title, Strings.Flatten_Message, Strings.Flatten_All, targets.Count > 0 ? only : null, Strings.Common_Cancel);
        if (choice == DialogChoice.Cancel || !CanChangeContent())
        {
            return;
        }

        var parameters = new FlattenAnnotationsParams(choice == DialogChoice.Secondary ? PageRanges.Format(targets) : null);
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
