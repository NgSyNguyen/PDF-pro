using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.ViewModels.Organizer;

/// <summary>Which page the inserted pages go next to.</summary>
public enum InsertAnchor
{
    FirstPage,
    LastPage,
    PageNumber,
}

/// <summary>
/// "Vị trí" of the insert dialogs: before or after the first page, the last page or page N (as in Acrobat). Starts
/// after the page the user is on (or the last selected one).
/// </summary>
public sealed partial class InsertPositionViewModel : ObservableObject
{
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="pageNumber">The 1-based page proposed as the anchor.</param>
    public InsertPositionViewModel(int pageCount, int pageNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        PageCount = pageCount;
        IsAfter = true;
        Anchor = InsertAnchor.PageNumber;
        PageNumberText = Math.Clamp(pageNumber, 1, pageCount).ToString(CultureInfo.CurrentCulture);
    }

    public int PageCount { get; }

    /// <summary>"/ 120".</summary>
    public string PageCountText => string.Format(CultureInfo.CurrentCulture, Strings.Insert_PageOf, PageCount);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBefore), nameof(InsertAt), nameof(Error))]
    public partial bool IsAfter { get; set; }

    public bool IsBefore
    {
        get => !IsAfter;
        set => IsAfter = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstPage), nameof(IsLastPage), nameof(IsPageNumber), nameof(AnchorPageNumber), nameof(InsertAt), nameof(Error))]
    public partial InsertAnchor Anchor { get; set; }

    public bool IsFirstPage
    {
        get => Anchor == InsertAnchor.FirstPage;
        set => SetAnchor(InsertAnchor.FirstPage, value);
    }

    public bool IsLastPage
    {
        get => Anchor == InsertAnchor.LastPage;
        set => SetAnchor(InsertAnchor.LastPage, value);
    }

    public bool IsPageNumber
    {
        get => Anchor == InsertAnchor.PageNumber;
        set => SetAnchor(InsertAnchor.PageNumber, value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorPageNumber), nameof(InsertAt), nameof(Error))]
    public partial string PageNumberText { get; set; }

    /// <summary>The 1-based page the pages go next to, or null while the typed number is not a page.</summary>
    public int? AnchorPageNumber => Anchor switch
    {
        InsertAnchor.FirstPage => 1,
        InsertAnchor.LastPage => PageCount,
        _ => int.TryParse(PageNumberText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var number) && number >= 1 && number <= PageCount
            ? number
            : null,
    };

    /// <summary>The 0-based position the pages are inserted at, or null (see <see cref="Error"/>).</summary>
    public int? InsertAt => AnchorPageNumber is { } page ? (IsAfter ? page : page - 1) : null;

    /// <summary>Why the position is not valid, or null.</summary>
    public string? Error => InsertAt is null
        ? string.Format(CultureInfo.CurrentCulture, Strings.Insert_Error_PageNumber, PageCount)
        : null;

    private void SetAnchor(InsertAnchor anchor, bool selected)
    {
        if (selected)
        {
            Anchor = anchor;
        }
    }
}

/// <summary>A page format the blank page dialog offers.</summary>
/// <param name="Format">"A4", "Letter"…, or null for the size of the page next to the insertion point.</param>
/// <param name="Label">What the list shows.</param>
public sealed record BlankPageFormatOption(string? Format, string Label)
{
    /// <summary>The label (what screen readers and UI automation read for the list item).</summary>
    public override string ToString() => Label;
}

/// <summary>
/// "Chèn trang trắng": size (like the adjacent page, A4, A3, Letter, Legal), orientation, count (1–1000) and position.
/// <see cref="Result"/> holds the operation's parameters once the user confirms.
/// </summary>
public sealed partial class InsertBlankPagesViewModel : ObservableObject
{
    public const int MaxCount = 1000;

    private readonly IReadOnlyList<PdfSize> _pageSizes;

    /// <param name="pageSizes">Displayed size of every page of the document.</param>
    /// <param name="pageNumber">The 1-based page proposed as the anchor.</param>
    public InsertBlankPagesViewModel(IReadOnlyList<PdfSize> pageSizes, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        _pageSizes = pageSizes;
        Position = new InsertPositionViewModel(pageSizes.Count, pageNumber);
        Position.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SizeDescription));
        Formats =
        [
            new(null, Strings.InsertBlank_SameAsAdjacent),
            new("A4", "A4"),
            new("A3", "A3"),
            new("Letter", "Letter"),
            new("Legal", "Legal"),
        ];
        SelectedFormat = Formats[0];
        CountText = "1";
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public InsertPositionViewModel Position { get; }

    public IReadOnlyList<BlankPageFormatOption> Formats { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseOrientation), nameof(SizeDescription))]
    public partial BlankPageFormatOption SelectedFormat { get; set; }

    /// <summary>"Giống trang kề" takes the adjacent page's orientation too.</summary>
    public bool CanChooseOrientation => SelectedFormat.Format is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPortrait), nameof(SizeDescription))]
    public partial bool IsLandscape { get; set; }

    public bool IsPortrait
    {
        get => !IsLandscape;
        set => IsLandscape = !value;
    }

    [ObservableProperty]
    public partial string CountText { get; set; }

    /// <summary>Why the dialog cannot be confirmed yet, or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>"210 × 297 mm".</summary>
    public string SizeDescription => PageSize is { } size
        ? string.Format(
            CultureInfo.CurrentCulture,
            Strings.Insert_SizeMillimetres,
            Math.Round(PaperSizes.ToMillimetres(size.Width)),
            Math.Round(PaperSizes.ToMillimetres(size.Height)))
        : string.Empty;

    /// <summary>The size of the new pages in points, or null while the position is not valid (adjacent size).</summary>
    public PdfSize? PageSize
    {
        get
        {
            if (SelectedFormat.Format is { } format && PaperSizes.SizeOf(format) is { } portrait)
            {
                return IsLandscape ? new PdfSize(portrait.Height, portrait.Width) : portrait;
            }

            return Position.AnchorPageNumber is { } page ? _pageSizes[page - 1] : null;
        }
    }

    /// <summary>The parameters of "organize.insertBlank", set when the user confirmed.</summary>
    public InsertBlankPagesParams? Result { get; private set; }

    [RelayCommand]
    private void Insert()
    {
        if (Position.InsertAt is not { } insertAt || PageSize is not { } size)
        {
            Error = Position.Error;
            return;
        }

        if (!int.TryParse(CountText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) || count < 1 || count > MaxCount)
        {
            Error = string.Format(CultureInfo.CurrentCulture, Strings.InsertBlank_Error_Count, MaxCount);
            return;
        }

        var width = Math.Clamp(size.Width, PageLimits.MinimumSide, PageLimits.MaximumSide);
        var height = Math.Clamp(size.Height, PageLimits.MinimumSide, PageLimits.MaximumSide);
        Result = new InsertBlankPagesParams(insertAt + 1, width, height, count);
        Error = null;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}

/// <summary>
/// "Chèn từ tệp": which pages of the chosen file (all, or a range) and where. <see cref="Result"/> holds the pages and
/// position once the user confirms.
/// </summary>
public sealed partial class InsertPagesViewModel : ObservableObject
{
    /// <param name="fileName">The chosen file's name (shown, never logged).</param>
    /// <param name="sourcePageCount">Pages of the chosen file.</param>
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="pageNumber">The 1-based page proposed as the anchor.</param>
    public InsertPagesViewModel(string fileName, int sourcePageCount, int pageCount, int pageNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourcePageCount, 1);
        FileName = fileName;
        SourcePageCount = sourcePageCount;
        IsAllPages = true;
        Position = new InsertPositionViewModel(pageCount, pageNumber);
        SourcePagesText = sourcePageCount == 1 ? "1" : string.Create(CultureInfo.InvariantCulture, $"1-{sourcePageCount}");
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public int SourcePageCount { get; }

    /// <summary>"“bao-cao.pdf” có 12 trang".</summary>
    public string SourceSummary => string.Format(CultureInfo.CurrentCulture, Strings.InsertPages_SourceSummary, FileName, SourcePageCount);

    public InsertPositionViewModel Position { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSomePages))]
    public partial bool IsAllPages { get; set; }

    public bool IsSomePages
    {
        get => !IsAllPages;
        set => IsAllPages = !value;
    }

    [ObservableProperty]
    public partial string SourcePagesText { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The pages of the file to insert (null = all) and the 0-based position, set when the user confirmed.</summary>
    public (string? SourcePages, int InsertAt)? Result { get; private set; }

    [RelayCommand]
    private void Insert()
    {
        if (Position.InsertAt is not { } insertAt)
        {
            Error = Position.Error;
            return;
        }

        string? pages = null;
        if (!IsAllPages)
        {
            if (!PageRanges.TryParse(SourcePagesText, SourcePageCount, out var indices, out var error))
            {
                Error = error.Kind == PageRangeErrorKind.OutOfRange
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_OutOfRange, error.Token, SourcePageCount)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_Invalid, error.Token);
                return;
            }

            pages = PageRanges.Format(indices);
        }

        Result = (pages, insertAt);
        Error = null;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
