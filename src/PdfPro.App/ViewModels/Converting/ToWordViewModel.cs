using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Operations.Converting;

namespace PdfPro.App.ViewModels.Converting;

/// <summary>What "PDF sang Word" was confirmed with.</summary>
/// <param name="Parameters">Pages, tables and pictures of convert.toWord.</param>
/// <param name="OpenWhenDone">Open the Word document in its program when written.</param>
public sealed record ToWordDialogResult(ToWordParams Parameters, bool OpenWhenDone);

/// <summary>
/// "PDF sang Word" (convert.toWord): the pages, whether ruled tables become Word tables and pictures come along, and
/// whether to open the document once written. Where it is saved is asked next.
/// </summary>
public sealed partial class ToWordViewModel : ObservableObject
{
    /// <param name="fileName">The document's file name.</param>
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="selected">Pages selected in the organizer, if any (0-based).</param>
    public ToWordViewModel(string fileName, int pageCount, IReadOnlyList<int> selected)
    {
        FileName = fileName;
        Target = new PageTargetViewModel(pageCount, selected);
        Target.Changed += (_, _) => Check();
        Check();
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public PageTargetViewModel Target { get; }

    [ObservableProperty]
    public partial bool Tables { get; set; } = true;

    [ObservableProperty]
    public partial bool Pictures { get; set; } = true;

    [ObservableProperty]
    public partial bool OpenWhenDone { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; private set; }

    public ToWordDialogResult? Result { get; private set; }

    /// <summary>The confirmed choice, or null with <paramref name="error"/>.</summary>
    public ToWordDialogResult? Choice(out string? error)
    {
        error = null;
        if (Target.Pages(out var pageError) is not { } pages)
        {
            error = pageError ?? Strings.Stamp_Error_Pages;
            return null;
        }

        return new ToWordDialogResult(new ToWordParams(Target.Indices.Count == Target.PageCount ? null : pages, Tables, Pictures), OpenWhenDone);
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Choice(out var error) is { } choice)
        {
            Result = choice;
            CloseRequested?.Invoke(this, true);
        }
        else
        {
            ErrorText = error;
        }
    }

    private bool CanApply() => ErrorText is null;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private void Check() => ErrorText = Choice(out var error) is null ? error : null;
}
