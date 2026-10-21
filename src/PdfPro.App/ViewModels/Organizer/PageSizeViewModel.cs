using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.ViewModels.Organizer;

/// <summary>A paper format the page size dialog offers.</summary>
/// <param name="Format">"A4", "Letter"…, or null for a custom size.</param>
/// <param name="Label">What the list shows.</param>
public sealed record PaperFormatOption(string? Format, string Label)
{
    public override string ToString() => Label;
}

/// <summary>How the new size is turned.</summary>
public enum PageSizeOrientation
{
    /// <summary>Landscape pages get the landscape size, portrait pages the portrait one.</summary>
    LikeEachPage,
    Portrait,
    Landscape,
}

/// <summary>
/// "Đổi khổ giấy": a standard or custom size (millimetres), its orientation, whether the content is scaled to fit or
/// kept and centred, and the pages. <see cref="Result"/> holds the operation's parameters once the user confirms.
/// </summary>
public sealed partial class PageSizeViewModel : ObservableObject
{
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="selected">The pages the command was started on.</param>
    /// <param name="current">Size of the first of them as displayed (proposed as the custom size).</param>
    public PageSizeViewModel(int pageCount, IReadOnlyList<int> selected, PdfSize current)
    {
        Target = new PageTargetViewModel(pageCount, selected);
        Formats =
        [
            new("A3", "A3"),
            new("A4", "A4"),
            new("A5", "A5"),
            new("Letter", "Letter"),
            new("Legal", "Legal"),
            new(null, Strings.PageSize_Custom),
        ];
        SelectedFormat = Formats[1];
        WidthText = Millimetres(Math.Min(current.Width, current.Height));
        HeightText = Millimetres(Math.Max(current.Width, current.Height));
        Orientation = PageSizeOrientation.LikeEachPage;
        IsScaleToFit = true;
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public PageTargetViewModel Target { get; }

    public IReadOnlyList<PaperFormatOption> Formats { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    public partial PaperFormatOption SelectedFormat { get; set; }

    public bool IsCustom => SelectedFormat.Format is null;

    /// <summary>Custom width in millimetres (portrait: the short side).</summary>
    [ObservableProperty]
    public partial string WidthText { get; set; }

    [ObservableProperty]
    public partial string HeightText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLikeEachPage), nameof(IsPortrait), nameof(IsLandscape))]
    public partial PageSizeOrientation Orientation { get; set; }

    public bool IsLikeEachPage
    {
        get => Orientation == PageSizeOrientation.LikeEachPage;
        set => Choose(PageSizeOrientation.LikeEachPage, value);
    }

    public bool IsPortrait
    {
        get => Orientation == PageSizeOrientation.Portrait;
        set => Choose(PageSizeOrientation.Portrait, value);
    }

    public bool IsLandscape
    {
        get => Orientation == PageSizeOrientation.Landscape;
        set => Choose(PageSizeOrientation.Landscape, value);
    }

    /// <summary>Scale the content to the new size; otherwise keep its size, centred.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCentered))]
    public partial bool IsScaleToFit { get; set; }

    public bool IsCentered
    {
        get => !IsScaleToFit;
        set => IsScaleToFit = !value;
    }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The parameters of "organize.pageSize", set when the user confirmed.</summary>
    public ChangePageSizeParams? Result { get; private set; }

    [RelayCommand]
    private void Apply()
    {
        var pages = Target.Pages(out var targetError);
        if (pages is null)
        {
            Error = targetError;
            return;
        }

        PdfSize size;
        if (SelectedFormat.Format is { } format)
        {
            size = PaperSizes.SizeOf(format) ?? throw new InvalidOperationException($"Unknown paper format {format}.");
        }
        else if (Points(WidthText) is { } width && Points(HeightText) is { } height)
        {
            size = new PdfSize(Math.Min(width, height), Math.Max(width, height));
        }
        else
        {
            Error = string.Format(
                CultureInfo.CurrentCulture,
                Strings.PageSize_Error_Custom,
                Math.Ceiling(PaperSizes.ToMillimetres(PageLimits.MinimumSide)),
                Math.Round(PaperSizes.ToMillimetres(PageLimits.MaximumSide)));
            return;
        }

        if (Orientation == PageSizeOrientation.Landscape)
        {
            size = new PdfSize(size.Height, size.Width);
        }

        var fit = IsScaleToFit ? PageSizeFit.ScaleToFit : PageSizeFit.Center;
        Result = new ChangePageSizeParams(pages, size.Width, size.Height, fit, MatchOrientation: Orientation == PageSizeOrientation.LikeEachPage);
        Error = null;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private static string Millimetres(double points) => Math.Round(PaperSizes.ToMillimetres(points)).ToString(CultureInfo.CurrentCulture);

    /// <summary>A typed size in millimetres as points, or null when it is not a usable page side.</summary>
    private static double? Points(string text)
        => double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var millimetres)
            && millimetres / PaperSizes.MillimetresPerPoint is var points && points >= PageLimits.MinimumSide && points <= PageLimits.MaximumSide
            ? points
            : null;

    private void Choose(PageSizeOrientation orientation, bool selected)
    {
        if (selected)
        {
            Orientation = orientation;
        }
    }
}
