using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.ViewModels.Organizer;

/// <summary>
/// "Cắt lề": margins in millimetres cut from the pages as they are displayed (on top of an earlier crop), or the crop
/// removed; a preview of the first chosen page shows what stays. <see cref="Result"/> holds the operation's parameters
/// once the user confirms.
/// </summary>
public sealed partial class CropPagesViewModel : ObservableObject
{
    /// <summary>Size of the preview box (view units).</summary>
    public const double PreviewBoxWidth = 300;
    public const double PreviewBoxHeight = 380;

    private readonly IReadOnlyList<VirtualPage> _pages;
    private readonly Func<VirtualPage, CancellationToken, Task<ImageSource?>> _loadPreview;
    private CancellationTokenSource? _previewLoad;
    private bool _syncingMargins;

    /// <param name="pages">Every page of the document as displayed.</param>
    /// <param name="selected">The pages the command was started on.</param>
    /// <param name="loadPreview">Loads the image of a page as displayed (upright, the view turns it), or null.</param>
    public CropPagesViewModel(IReadOnlyList<VirtualPage> pages, IReadOnlyList<int> selected, Func<VirtualPage, CancellationToken, Task<ImageSource?>> loadPreview)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(loadPreview);
        _pages = pages;
        _loadPreview = loadPreview;
        Target = new PageTargetViewModel(pages.Count, selected);
        Target.Changed += (_, _) => UpdatePreviewPage();
        LeftText = TopText = RightText = BottomText = "0";
        UpdatePreviewPage();
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public PageTargetViewModel Target { get; }

    /// <summary>The four margins follow the left one.</summary>
    [ObservableProperty]
    public partial bool IsUniform { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeptLeft), nameof(KeptTop), nameof(KeptWidth), nameof(KeptHeight), nameof(KeptRight), nameof(KeptBottom), nameof(ShadeRightWidth), nameof(ShadeBottomHeight))]
    public partial string LeftText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeptLeft), nameof(KeptTop), nameof(KeptWidth), nameof(KeptHeight), nameof(KeptRight), nameof(KeptBottom), nameof(ShadeRightWidth), nameof(ShadeBottomHeight))]
    public partial string TopText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeptLeft), nameof(KeptTop), nameof(KeptWidth), nameof(KeptHeight), nameof(KeptRight), nameof(KeptBottom), nameof(ShadeRightWidth), nameof(ShadeBottomHeight))]
    public partial string RightText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeptLeft), nameof(KeptTop), nameof(KeptWidth), nameof(KeptHeight), nameof(KeptRight), nameof(KeptBottom), nameof(ShadeRightWidth), nameof(ShadeBottomHeight))]
    public partial string BottomText { get; set; }

    /// <summary>"Bỏ cắt lề": the chosen pages show whole again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditMargins), nameof(KeptLeft), nameof(KeptTop), nameof(KeptWidth), nameof(KeptHeight), nameof(KeptRight), nameof(KeptBottom), nameof(ShadeRightWidth), nameof(ShadeBottomHeight))]
    public partial bool IsReset { get; set; }

    public bool CanEditMargins => !IsReset;

    /// <summary>The page the preview shows: the first chosen page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewWidth), nameof(PreviewHeight), nameof(PreviewAngle), nameof(PreviewIsBlank), nameof(PreviewSize),
        nameof(KeptLeft), nameof(KeptTop), nameof(KeptWidth), nameof(KeptHeight), nameof(KeptRight), nameof(KeptBottom), nameof(ShadeRightWidth), nameof(ShadeBottomHeight))]
    public partial VirtualPage? PreviewPage { get; private set; }

    [ObservableProperty]
    public partial ImageSource? PreviewImage { get; private set; }

    /// <summary>Size of the previewed page in the preview (view units, as displayed).</summary>
    public double PreviewWidth => PreviewPage is { } page ? page.DisplaySize.Width * PreviewScale(page) : 0;

    public double PreviewHeight => PreviewPage is { } page ? page.DisplaySize.Height * PreviewScale(page) : 0;

    /// <summary>Clockwise angle the view turns the upright preview image by.</summary>
    public double PreviewAngle => (PreviewPage?.QuarterTurns ?? 0) * 90;

    public bool PreviewIsBlank => PreviewPage?.IsBlank == true;

    /// <summary>"210 × 297 mm" of the previewed page as displayed.</summary>
    public string PreviewSize => PreviewPage is { } page
        ? string.Format(CultureInfo.CurrentCulture, Strings.Insert_SizeMillimetres, Math.Round(PaperSizes.ToMillimetres(page.DisplaySize.Width)), Math.Round(PaperSizes.ToMillimetres(page.DisplaySize.Height)))
        : string.Empty;

    /// <summary>The part of the preview that stays (view units); the rest is shaded.</summary>
    public double KeptLeft => Kept().X;

    public double KeptTop => Kept().Y;

    public double KeptWidth => Kept().Width;

    public double KeptHeight => Kept().Height;

    public double KeptRight => KeptLeft + KeptWidth;

    public double KeptBottom => KeptTop + KeptHeight;

    /// <summary>Width of the shaded band right of the kept area.</summary>
    public double ShadeRightWidth => Math.Max(0, PreviewWidth - KeptRight);

    /// <summary>Height of the shaded band below the kept area.</summary>
    public double ShadeBottomHeight => Math.Max(0, PreviewHeight - KeptBottom);

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The parameters of "organize.crop", set when the user confirmed.</summary>
    public CropPagesParams? Result { get; private set; }

    /// <summary>Stops loading the preview (the window closed).</summary>
    public void Close() => CancelPreview();

    /// <summary>Loads the preview of <see cref="PreviewPage"/> (the window calls this once shown).</summary>
    public async Task LoadPreviewAsync()
    {
        CancelPreview();
        if (PreviewPage is not { IsBlank: false } page)
        {
            PreviewImage = null;
            return;
        }

        var load = new CancellationTokenSource();
        _previewLoad = load;
        try
        {
            var image = await _loadPreview(page, load.Token);
            if (ReferenceEquals(_previewLoad, load))
            {
                PreviewImage = image;
            }
        }
        catch (OperationCanceledException)
        {
            // Another page is previewed now, or the window closed.
        }
    }

    partial void OnLeftTextChanged(string value) => FollowLeft(value);

    partial void OnIsUniformChanged(bool value) => FollowLeft(LeftText);

    [RelayCommand]
    private void Apply()
    {
        var pages = Target.Pages(out var targetError);
        if (pages is null)
        {
            Error = targetError;
            return;
        }

        if (IsReset)
        {
            Finish(new CropPagesParams(pages, 0, 0, 0, 0, Reset: true));
            return;
        }

        if (Margins() is not { } margins)
        {
            Error = string.Format(CultureInfo.CurrentCulture, Strings.Crop_Error_Margin, MaxMarginMillimetres);
            return;
        }

        if (margins.IsZero)
        {
            Error = Strings.Crop_Error_Nothing;
            return;
        }

        foreach (var index in Target.Indices)
        {
            var page = _pages[index];
            if (!PageCrop.Fits(page.BaseSize, page.QuarterTurns * 90, page.Crop, margins))
            {
                Error = string.Format(CultureInfo.CurrentCulture, Strings.Crop_Error_TooLarge, index + 1);
                return;
            }
        }

        Finish(new CropPagesParams(pages, margins.Left, margins.Top, margins.Right, margins.Bottom));
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelPreview();
        CloseRequested?.Invoke(this, false);
    }

    private static double MaxMarginMillimetres => Math.Round(PaperSizes.ToMillimetres(PageLimits.MaximumSide));

    private static double PreviewScale(VirtualPage page)
        => Math.Min(PreviewBoxWidth / page.DisplaySize.Width, PreviewBoxHeight / page.DisplaySize.Height);

    /// <summary>The typed margins in points, or null while one is not a number from 0 up.</summary>
    private PageMargins? Margins()
    {
        double? Read(string text)
            => double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var millimetres)
                && millimetres >= 0 && millimetres <= MaxMarginMillimetres
                ? millimetres / PaperSizes.MillimetresPerPoint
                : null;

        return Read(LeftText) is { } left && Read(TopText) is { } top && Read(RightText) is { } right && Read(BottomText) is { } bottom
            ? new PageMargins(left, top, right, bottom)
            : null;
    }

    /// <summary>The kept area of the previewed page, in preview units (the whole page while the margins are invalid).</summary>
    private ViewRect Kept()
    {
        if (PreviewPage is not { } page)
        {
            return default;
        }

        var scale = PreviewScale(page);
        var size = page.DisplaySize;
        var margins = IsReset ? default : Margins() ?? default;
        var width = Math.Max(0, size.Width - margins.Left - margins.Right);
        var height = Math.Max(0, size.Height - margins.Top - margins.Bottom);
        return new ViewRect(Math.Min(margins.Left, size.Width) * scale, Math.Min(margins.Top, size.Height) * scale, width * scale, height * scale);
    }

    private void FollowLeft(string value)
    {
        if (!IsUniform || _syncingMargins)
        {
            return;
        }

        _syncingMargins = true;
        try
        {
            TopText = RightText = BottomText = value;
        }
        finally
        {
            _syncingMargins = false;
        }
    }

    private void UpdatePreviewPage()
    {
        var indices = Target.Indices;
        var page = indices.Count > 0 ? _pages[indices[0]] : null;
        if (!ReferenceEquals(page, PreviewPage))
        {
            PreviewPage = page;
            PreviewImage = null;
            _ = LoadPreviewAsync();
        }
    }

    private void Finish(CropPagesParams result)
    {
        Result = result;
        Error = null;
        CancelPreview();
        CloseRequested?.Invoke(this, true);
    }

    private void CancelPreview()
    {
        if (_previewLoad is { } load)
        {
            _previewLoad = null;
            load.Cancel();
            load.Dispose();
        }
    }
}
