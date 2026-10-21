using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.App.Resources;
using PdfPro.Core.Organizing;

namespace PdfPro.App.ViewModels.Organizer;

/// <summary>Which pages a dialog's change applies to.</summary>
public enum PageTarget
{
    Selected,
    All,
    Odd,
    Even,
    Range,
}

/// <summary>
/// "Áp dụng cho" of the crop and page size dialogs: the selected pages (or the current page), every page, odd or even
/// pages, or a typed range.
/// </summary>
public sealed partial class PageTargetViewModel : ObservableObject
{
    private readonly IReadOnlyList<int> _selected;

    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="selected">The pages the command was started on (0-based, in order; may be empty).</param>
    public PageTargetViewModel(int pageCount, IReadOnlyList<int> selected)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        ArgumentNullException.ThrowIfNull(selected);
        PageCount = pageCount;
        _selected = [.. selected.Distinct().Order()];
        Target = _selected.Count > 0 ? PageTarget.Selected : PageTarget.All;
        RangeText = _selected.Count > 0 ? PageRanges.Format(_selected) : string.Empty;
    }

    /// <summary>The selection or a page range changed: previews and checks follow.</summary>
    public event EventHandler? Changed;

    public int PageCount { get; }

    public bool HasSelection => _selected.Count > 0;

    /// <summary>"Các trang đã chọn (3)".</summary>
    public string SelectedLabel => string.Format(CultureInfo.CurrentCulture, Strings.Target_Selected, _selected.Count);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelected), nameof(IsAll), nameof(IsOdd), nameof(IsEven), nameof(IsRange))]
    public partial PageTarget Target { get; set; }

    public bool IsSelected
    {
        get => Target == PageTarget.Selected;
        set => Choose(PageTarget.Selected, value);
    }

    public bool IsAll
    {
        get => Target == PageTarget.All;
        set => Choose(PageTarget.All, value);
    }

    public bool IsOdd
    {
        get => Target == PageTarget.Odd;
        set => Choose(PageTarget.Odd, value);
    }

    public bool IsEven
    {
        get => Target == PageTarget.Even;
        set => Choose(PageTarget.Even, value);
    }

    public bool IsRange
    {
        get => Target == PageTarget.Range;
        set => Choose(PageTarget.Range, value);
    }

    [ObservableProperty]
    public partial string RangeText { get; set; }

    /// <summary>The chosen pages (0-based, in order), or empty while the range is not valid.</summary>
    public IReadOnlyList<int> Indices => TryResolve(out var indices, out _) ? indices : [];

    /// <summary>The chosen pages as the operation's page range, or null with <paramref name="error"/>.</summary>
    public string? Pages(out string? error)
    {
        if (!TryResolve(out var indices, out error))
        {
            return null;
        }

        return Target switch
        {
            PageTarget.Odd => "odd",
            PageTarget.Even => "even",
            _ => PageRanges.Format(indices),
        };
    }

    partial void OnTargetChanged(PageTarget value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnRangeTextChanged(string value)
    {
        if (Target == PageTarget.Range)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryResolve(out IReadOnlyList<int> indices, out string? error)
    {
        error = null;
        switch (Target)
        {
            case PageTarget.Selected when _selected.Count > 0:
                indices = _selected;
                return true;
            case PageTarget.Odd:
                indices = [.. Enumerable.Range(0, PageCount).Where(i => i % 2 == 0)];
                return true;
            case PageTarget.Even:
                indices = [.. Enumerable.Range(0, PageCount).Where(i => i % 2 == 1)];
                if (indices.Count == 0)
                {
                    error = Strings.Target_Error_NoEvenPages;
                    return false;
                }

                return true;
            case PageTarget.Range:
                if (PageRanges.TryParse(RangeText, PageCount, out var parsed, out var problem))
                {
                    indices = parsed;
                    return true;
                }

                indices = [];
                error = problem.Kind switch
                {
                    PageRangeErrorKind.Empty => Strings.Organizer_Range_Empty,
                    PageRangeErrorKind.OutOfRange => string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_OutOfRange, problem.Token, PageCount),
                    _ => string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_Invalid, problem.Token),
                };
                return false;
            default:
                indices = [.. Enumerable.Range(0, PageCount)];
                return true;
        }
    }

    private void Choose(PageTarget target, bool selected)
    {
        if (selected)
        {
            Target = target;
        }
    }
}
