using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Core.Layout;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.ViewModels.Organizer;

/// <summary>A file holding the backs, already added to the document's session as a source.</summary>
public sealed record InterleaveBackFile(string SourceId, string Name, int PageCount);

/// <summary>
/// "Trộn mặt trước/sau": where the backs are (end of this document, or another file), whether they are in reverse
/// order, and a preview of the new page order. <see cref="Result"/> holds the operation's parameters once confirmed.
/// </summary>
public sealed partial class InterleaveViewModel : ObservableObject
{
    private const int PreviewLength = 16;

    private readonly int _pageCount;
    private readonly Func<Task<InterleaveBackFile?>> _pickBackFile;

    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="pickBackFile">Asks for the backs file and adds it to the session; null when cancelled.</param>
    public InterleaveViewModel(int pageCount, Func<Task<InterleaveBackFile?>> pickBackFile)
    {
        ArgumentNullException.ThrowIfNull(pickBackFile);
        _pageCount = pageCount;
        _pickBackFile = pickBackFile;
        BackStartText = InterleaveOperation.DefaultBackStart(pageCount).ToString(CultureInfo.CurrentCulture);
        IsSameDocument = true;
        ReverseBacks = true;
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOtherFile))]
    public partial bool IsSameDocument { get; set; }

    public bool IsOtherFile
    {
        get => !IsSameDocument;
        set => IsSameDocument = !value;
    }

    [ObservableProperty]
    public partial string BackStartText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackFileText))]
    public partial InterleaveBackFile? BackFile { get; private set; }

    public string? BackFileText => BackFile is { } file ? Format(Strings.Interleave_BackFile, file.Name, file.PageCount) : null;

    [ObservableProperty]
    public partial bool ReverseBacks { get; set; }

    /// <summary>"Thứ tự mới: 1, 6, 2, 5…".</summary>
    [ObservableProperty]
    public partial string? PreviewText { get; private set; }

    [ObservableProperty]
    public partial string? WarningText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; private set; }

    public InterleaveParams? Result { get; private set; }

    partial void OnIsSameDocumentChanged(bool value) => Recompute();

    partial void OnBackStartTextChanged(string value) => Recompute();

    partial void OnBackFileChanged(InterleaveBackFile? value) => Recompute();

    partial void OnReverseBacksChanged(bool value) => Recompute();

    [RelayCommand]
    private async Task PickBackFileAsync()
    {
        if (await _pickBackFile() is { } file)
        {
            BackFile = file;
            IsSameDocument = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Parameters() is not { } parameters)
        {
            return;
        }

        Result = parameters;
        CloseRequested?.Invoke(this, true);
    }

    private bool CanApply() => ErrorText is null;

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, false);
    }

    private InterleaveParams? Parameters()
    {
        if (IsSameDocument)
        {
            return int.TryParse(BackStartText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var start) && start >= 2 && start <= _pageCount
                ? new InterleaveParams(BackStart: start, ReverseBacks: ReverseBacks)
                : null;
        }

        return BackFile is { } file ? new InterleaveParams(BackSourceId: file.SourceId, ReverseBacks: ReverseBacks) : null;
    }

    private void Recompute()
    {
        WarningText = null;
        if (Parameters() is not { } parameters)
        {
            ErrorText = IsSameDocument ? Format(Strings.Interleave_Error_BackStart, _pageCount) : Strings.Interleave_Error_NoFile;
            PreviewText = null;
            return;
        }

        ErrorText = null;
        var fronts = IsSameDocument ? parameters.BackStart - 1 : _pageCount;
        var backs = IsSameDocument ? _pageCount - fronts : BackFile!.PageCount;
        var order = InterleaveOrder.Order(fronts, backs, parameters.ReverseBacks);
        var labels = order.Take(PreviewLength).Select(p => p.IsBack
            ? (IsSameDocument ? (fronts + p.Index + 1).ToString(CultureInfo.CurrentCulture) : "s" + (p.Index + 1).ToString(CultureInfo.CurrentCulture))
            : (p.Index + 1).ToString(CultureInfo.CurrentCulture));
        var text = string.Join(", ", labels) + (order.Count > PreviewLength ? ", …" : string.Empty);
        PreviewText = Format(Strings.Interleave_Preview, text) + (IsSameDocument ? string.Empty : Environment.NewLine + Strings.Interleave_PreviewLegend);
        if (!InterleaveOrder.IsBalanced(fronts, backs))
        {
            WarningText = Format(Strings.Interleave_Unbalanced, fronts, backs);
        }
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
