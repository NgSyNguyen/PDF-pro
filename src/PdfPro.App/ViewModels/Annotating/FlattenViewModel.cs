using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Annotations;
using PdfPro.Operations.Annotating;

namespace PdfPro.App.ViewModels.Annotating;

/// <summary>
/// "Làm phẳng chú thích": which families of comments are drawn into the page content (all by default) and on which
/// pages (the whole document by default). <see cref="Result"/> holds the operation's parameters once confirmed.
/// </summary>
public sealed partial class FlattenViewModel : ObservableObject
{
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="selected">The pages the command was started on (the current page, or the organizer's selection).</param>
    public FlattenViewModel(int pageCount, IReadOnlyList<int> selected)
    {
        Target = new PageTargetViewModel(pageCount, selected) { Target = PageTarget.All };
        Target.Changed += (_, _) => Error = null;
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public PageTargetViewModel Target { get; }

    /// <summary>Notes, text boxes, attachments.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool Notes { get; set; } = true;

    /// <summary>Highlight, underline, strike-out, squiggly.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool Markup { get; set; } = true;

    /// <summary>Ink, shapes, lines, arrows, polygons.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool Drawings { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool Stamps { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The parameters of "annotate.flatten", set when the user confirmed.</summary>
    public FlattenAnnotationsParams? Result { get; private set; }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        var pages = Target.Pages(out var error);
        if (pages is null)
        {
            Error = error;
            return;
        }

        var groups = Groups();
        Result = new FlattenAnnotationsParams(Target.Target == PageTarget.All ? null : pages, groups.Count == 4 ? null : groups);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanApply() => Notes || Markup || Drawings || Stamps;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private List<AnnotationGroup> Groups()
    {
        var groups = new List<AnnotationGroup>();
        if (Notes)
        {
            groups.Add(AnnotationGroup.Notes);
        }

        if (Markup)
        {
            groups.Add(AnnotationGroup.Markup);
        }

        if (Drawings)
        {
            groups.Add(AnnotationGroup.Drawings);
        }

        if (Stamps)
        {
            groups.Add(AnnotationGroup.Stamps);
        }

        return groups;
    }
}
