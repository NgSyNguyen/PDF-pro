using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Editing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Stamping;
using PdfPro.Core.Text;
using PdfPro.Operations.Stamping;

namespace PdfPro.App.ViewModels.Editing;

/// <summary>What the stamp dialogs need from the document besides its pages.</summary>
/// <param name="FileName">The document's file name without extension ({filename}).</param>
/// <param name="Title">The document title ({title}), or null.</param>
/// <param name="CurrentPage">The page the user is on (0-based): the previewed page.</param>
/// <param name="Selected">Pages selected in the organizer, if any.</param>
public sealed record StampContext(int PageCount, string FileName, string? Title, int CurrentPage, IReadOnlyList<int> Selected);

/// <summary>"Hình mờ" (stamp.watermark): text or a picture, place or tiled, turned, see-through, over or under the content.</summary>
public sealed partial class WatermarkDialogViewModel : StampDialogViewModel
{
    private readonly WatermarkOperation _operation;
    private readonly IFileDialogService _files;

    public WatermarkDialogViewModel(StampContext context, WatermarkOperation operation, IFileDialogService files, IStampPreview? preview, IDebouncerFactory debouncers)
        : base(context.PageCount, context.CurrentPage, context.Selected, preview, debouncers)
    {
        _operation = operation;
        _files = files;
        Text = Strings.Watermark_DefaultText;
        Start();
    }

    public override string Title => Strings.Watermark_Title;

    public IReadOnlyList<StampOption<StampAnchor>> Anchors { get; } = AnchorOptions(Enum.GetValues<StampAnchor>());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesText))]
    public partial bool UsesPicture { get; set; }

    /// <summary>The other choice of <see cref="UsesPicture"/>, for the radio buttons.</summary>
    public bool UsesText
    {
        get => !UsesPicture;
        set => UsesPicture = !value;
    }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PictureName))]
    public partial string? PicturePath { get; private set; }

    /// <summary>The chosen picture's file name, or "Chưa chọn ảnh".</summary>
    public string PictureName => PicturePath is null ? Strings.Watermark_NoPicture : Path.GetFileName(PicturePath);

    [ObservableProperty]
    public partial string FontFamily { get; set; } = FontFamilies.Default;

    [ObservableProperty]
    public partial double FontSize { get; set; } = 60;

    [ObservableProperty]
    public partial bool Bold { get; set; } = true;

    [ObservableProperty]
    public partial string Color { get; set; } = "#A0A0A0";

    [ObservableProperty]
    public partial double OpacityPercent { get; set; } = 30;

    /// <summary>Degrees counter-clockwise (45 rises from bottom left to top right).</summary>
    [ObservableProperty]
    public partial double Rotation { get; set; } = 45;

    [ObservableProperty]
    public partial StampAnchor Anchor { get; set; } = StampAnchor.Center;

    [ObservableProperty]
    public partial bool Tile { get; set; }

    /// <summary>Width of a picture as a percentage of the page width.</summary>
    [ObservableProperty]
    public partial double PictureScalePercent { get; set; } = 50;

    [ObservableProperty]
    public partial bool BehindContent { get; set; }

    partial void OnUsesPictureChanged(bool value) => Changed();

    partial void OnTextChanged(string value) => Changed();

    partial void OnFontFamilyChanged(string value) => Changed();

    partial void OnFontSizeChanged(double value) => Changed();

    partial void OnBoldChanged(bool value) => Changed();

    partial void OnColorChanged(string value) => Changed();

    partial void OnOpacityPercentChanged(double value) => Changed();

    partial void OnRotationChanged(double value) => Changed();

    partial void OnAnchorChanged(StampAnchor value) => Changed();

    partial void OnTileChanged(bool value) => Changed();

    partial void OnPictureScalePercentChanged(double value) => Changed();

    partial void OnBehindContentChanged(bool value) => Changed();

    [RelayCommand]
    private void SetColor(string hex) => Color = hex;

    [RelayCommand]
    private void PickPicture()
    {
        if (_files.PickImageFile(Strings.Editor_PickImage_Title) is { } path)
        {
            PicturePath = path;
            UsesPicture = true;
            Changed();
        }
    }

    protected override StampChoice? Build(string pages, out string? error)
    {
        error = UsesPicture
            ? PicturePath is null ? Strings.Watermark_Error_Picture : null
            : string.IsNullOrWhiteSpace(Text) ? Strings.Watermark_Error_Text : null;
        if (error is not null)
        {
            return null;
        }

        var style = WatermarkParams.DefaultStyle with
        {
            FontFamily = FontFamily,
            FontSize = Math.Clamp(FontSize, OverlayLimits.MinFontSize, OverlayLimits.MaxFontSize),
            Color = ColorOf(Color),
            Bold = Bold,
        };
        var parameters = new WatermarkParams(
            UsesPicture ? null : Text,
            UsesPicture ? PicturePath : null,
            style,
            Math.Clamp(PictureScalePercent, 1, 400) / 100,
            Math.Clamp(OpacityPercent, 0, 100) / 100,
            Math.Clamp(Rotation, -360, 360),
            Anchor,
            Tile: Tile,
            BehindContent: BehindContent,
            Pages: pages);
        var validation = _operation.Validate(parameters, new Core.Documents.DocumentInfo(PageCount));
        error = validation.IsValid ? null : Strings.Stamp_Error_Invalid;
        return validation.IsValid ? new StampChoice(_operation, parameters, Strings.History_Watermark) : null;
    }

    protected override object? PreviewParameters(StampChoice choice)
        => Target.Indices.Contains(CurrentPage) ? (WatermarkParams)choice.Parameters with { Pages = null } : null;
}

/// <summary>"Nền trang" (stamp.background): a colour or a picture under the content.</summary>
public sealed partial class BackgroundDialogViewModel : StampDialogViewModel
{
    private readonly BackgroundOperation _operation;
    private readonly IFileDialogService _files;

    public BackgroundDialogViewModel(StampContext context, BackgroundOperation operation, IFileDialogService files, IStampPreview? preview, IDebouncerFactory debouncers)
        : base(context.PageCount, context.CurrentPage, context.Selected, preview, debouncers)
    {
        _operation = operation;
        _files = files;
        Start();
    }

    public override string Title => Strings.Background_Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesText))]
    public partial bool UsesPicture { get; set; }

    /// <summary>The other choice of <see cref="UsesPicture"/>, for the radio buttons.</summary>
    public bool UsesText
    {
        get => !UsesPicture;
        set => UsesPicture = !value;
    }

    [ObservableProperty]
    public partial string Color { get; set; } = "#FFF7E0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PictureName))]
    public partial string? PicturePath { get; private set; }

    /// <summary>The chosen picture's file name, or "Chưa chọn ảnh".</summary>
    public string PictureName => PicturePath is null ? Strings.Watermark_NoPicture : Path.GetFileName(PicturePath);

    [ObservableProperty]
    public partial bool KeepProportions { get; set; }

    [ObservableProperty]
    public partial double OpacityPercent { get; set; } = 100;

    partial void OnUsesPictureChanged(bool value) => Changed();

    partial void OnColorChanged(string value) => Changed();

    partial void OnKeepProportionsChanged(bool value) => Changed();

    partial void OnOpacityPercentChanged(double value) => Changed();

    [RelayCommand]
    private void SetColor(string hex)
    {
        UsesPicture = false;
        Color = hex;
    }

    [RelayCommand]
    private void PickPicture()
    {
        if (_files.PickImageFile(Strings.Editor_PickImage_Title) is { } path)
        {
            PicturePath = path;
            UsesPicture = true;
            Changed();
        }
    }

    protected override StampChoice? Build(string pages, out string? error)
    {
        error = UsesPicture && PicturePath is null ? Strings.Watermark_Error_Picture : null;
        if (!UsesPicture && !RgbColor.TryParse(Color, out _))
        {
            error = Strings.Stamp_Error_Color;
        }

        if (error is not null)
        {
            return null;
        }

        var parameters = new BackgroundParams(
            UsesPicture ? null : ColorOf(Color),
            UsesPicture ? PicturePath : null,
            KeepProportions ? BackgroundFit.Fit : BackgroundFit.Stretch,
            Math.Clamp(OpacityPercent, 0, 100) / 100,
            pages);
        return new StampChoice(_operation, parameters, Strings.History_Background);
    }

    protected override object? PreviewParameters(StampChoice choice)
        => Target.Indices.Contains(CurrentPage) ? (BackgroundParams)choice.Parameters with { Pages = null } : null;
}

/// <summary>"Đầu & chân trang" (stamp.headerFooter): six places with page numbers, date, file name and title.</summary>
public sealed partial class HeaderFooterDialogViewModel : StampDialogViewModel
{
    private readonly HeaderFooterOperation _operation;
    private readonly StampContext _context;

    public HeaderFooterDialogViewModel(StampContext context, HeaderFooterOperation operation, IStampPreview? preview, IDebouncerFactory debouncers)
        : base(context.PageCount, context.CurrentPage, context.Selected, preview, debouncers)
    {
        _operation = operation;
        _context = context;
        BottomCenter = Strings.HeaderFooter_DefaultFooter;
        Start();
    }

    public override string Title => Strings.HeaderFooter_Title;

    public IReadOnlyList<StampOption<PageNumberStyle>> NumberStyles { get; } = NumberStyleOptions;

    [ObservableProperty]
    public partial string TopLeft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TopCenter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TopRight { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BottomLeft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BottomCenter { get; set; }

    [ObservableProperty]
    public partial string BottomRight { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FontFamily { get; set; } = FontFamilies.Default;

    [ObservableProperty]
    public partial double FontSize { get; set; } = 9;

    [ObservableProperty]
    public partial string Color { get; set; } = "#000000";

    /// <summary>Distance from the left and right edges, in millimetres.</summary>
    [ObservableProperty]
    public partial double MarginSides { get; set; } = 12.7;

    /// <summary>Distance from the top and bottom edges, in millimetres.</summary>
    [ObservableProperty]
    public partial double MarginEnds { get; set; } = 8.5;

    [ObservableProperty]
    public partial bool SkipFirstPage { get; set; }

    [ObservableProperty]
    public partial double StartNumber { get; set; } = 1;

    [ObservableProperty]
    public partial PageNumberStyle NumberStyle { get; set; }

    partial void OnTopLeftChanged(string value) => Changed();

    partial void OnTopCenterChanged(string value) => Changed();

    partial void OnTopRightChanged(string value) => Changed();

    partial void OnBottomLeftChanged(string value) => Changed();

    partial void OnBottomCenterChanged(string value) => Changed();

    partial void OnBottomRightChanged(string value) => Changed();

    partial void OnFontFamilyChanged(string value) => Changed();

    partial void OnFontSizeChanged(double value) => Changed();

    partial void OnColorChanged(string value) => Changed();

    partial void OnMarginSidesChanged(double value) => Changed();

    partial void OnMarginEndsChanged(double value) => Changed();

    partial void OnSkipFirstPageChanged(bool value) => Changed();

    partial void OnStartNumberChanged(double value) => Changed();

    partial void OnNumberStyleChanged(PageNumberStyle value) => Changed();

    [RelayCommand]
    private void SetColor(string hex) => Color = hex;

    protected override StampChoice? Build(string pages, out string? error)
    {
        var parameters = new HeaderFooterParams(
            Blank(TopLeft),
            Blank(TopCenter),
            Blank(TopRight),
            Blank(BottomLeft),
            Blank(BottomCenter),
            Blank(BottomRight),
            HeaderFooterParams.DefaultStyle with { FontFamily = FontFamily, FontSize = Math.Clamp(FontSize, OverlayLimits.MinFontSize, OverlayLimits.MaxFontSize), Color = ColorOf(Color) },
            Points(Math.Clamp(MarginSides, 0, 500)),
            Points(Math.Clamp(MarginEnds, 0, 500)),
            SkipFirstPage,
            (int)Math.Clamp(Math.Round(StartNumber), -1_000_000, 1_000_000),
            NumberStyle,
            FileName: _context.FileName,
            Pages: pages);
        var validation = _operation.Validate(parameters, new Core.Documents.DocumentInfo(PageCount));
        error = validation.IsValid
            ? null
            : validation.Errors.Any(e => e.MemberName is "Style") ? Strings.Stamp_Error_Invalid : Strings.HeaderFooter_Error_Text;
        return validation.IsValid ? new StampChoice(_operation, parameters, Strings.History_HeaderFooter) : null;
    }

    protected override object? PreviewParameters(StampChoice choice)
    {
        var parameters = (HeaderFooterParams)choice.Parameters;
        var texts = HeaderFooterOperation.TextsOn(parameters, CurrentPage, PageCount, _context.FileName, _context.Title, DateTime.Now);
        if (texts.Count == 0)
        {
            return null;
        }

        string? On(StampAnchor anchor) => texts.TryGetValue(anchor, out var text) ? StampTemplate.Escape(text) : null;
        return parameters with
        {
            TopLeft = On(StampAnchor.TopLeft),
            TopCenter = On(StampAnchor.TopCenter),
            TopRight = On(StampAnchor.TopRight),
            BottomLeft = On(StampAnchor.BottomLeft),
            BottomCenter = On(StampAnchor.BottomCenter),
            BottomRight = On(StampAnchor.BottomRight),
            SkipFirstPage = false,
            Pages = null,
        };
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
}

/// <summary>"Đánh số trang" (stamp.pageNumbers): 1, 2, 3 / i, ii / "Trang 1/10", from a chosen page with a chosen number.</summary>
public sealed partial class PageNumbersDialogViewModel : StampDialogViewModel
{
    private readonly PageNumbersOperation _operation;
    private readonly StampContext _context;

    public PageNumbersDialogViewModel(StampContext context, PageNumbersOperation operation, IStampPreview? preview, IDebouncerFactory debouncers)
        : base(context.PageCount, context.CurrentPage, context.Selected, preview, debouncers)
    {
        _operation = operation;
        _context = context;
        Patterns = ["{page}", Strings.PageNumbers_Pattern_Page, Strings.PageNumbers_Pattern_PageOf, "{page}/{pages}", "- {page} -"];
        Pattern = Patterns[0];
        Start();
    }

    public override string Title => Strings.PageNumbers_Title;

    public IReadOnlyList<string> Patterns { get; }

    public IReadOnlyList<StampOption<PageNumberStyle>> NumberStyles { get; } = NumberStyleOptions;

    public IReadOnlyList<StampOption<StampAnchor>> Anchors { get; } = AnchorOptions(
    [
        StampAnchor.TopLeft, StampAnchor.TopCenter, StampAnchor.TopRight,
        StampAnchor.BottomLeft, StampAnchor.BottomCenter, StampAnchor.BottomRight,
    ]);

    [ObservableProperty]
    public partial string Pattern { get; set; }

    [ObservableProperty]
    public partial PageNumberStyle NumberStyle { get; set; }

    [ObservableProperty]
    public partial StampAnchor Anchor { get; set; } = StampAnchor.BottomCenter;

    [ObservableProperty]
    public partial double FirstPage { get; set; } = 1;

    [ObservableProperty]
    public partial double StartNumber { get; set; } = 1;

    [ObservableProperty]
    public partial string FontFamily { get; set; } = FontFamilies.Default;

    [ObservableProperty]
    public partial double FontSize { get; set; } = 10;

    [ObservableProperty]
    public partial string Color { get; set; } = "#000000";

    partial void OnPatternChanged(string value) => Changed();

    partial void OnNumberStyleChanged(PageNumberStyle value) => Changed();

    partial void OnAnchorChanged(StampAnchor value) => Changed();

    partial void OnFirstPageChanged(double value) => Changed();

    partial void OnStartNumberChanged(double value) => Changed();

    partial void OnFontFamilyChanged(string value) => Changed();

    partial void OnFontSizeChanged(double value) => Changed();

    partial void OnColorChanged(string value) => Changed();

    [RelayCommand]
    private void SetColor(string hex) => Color = hex;

    protected override StampChoice? Build(string pages, out string? error)
    {
        var parameters = new PageNumbersParams(
            Pattern ?? string.Empty,
            NumberStyle,
            Anchor,
            PageNumbersParams.DefaultStyle with { FontFamily = FontFamily, FontSize = Math.Clamp(FontSize, OverlayLimits.MinFontSize, OverlayLimits.MaxFontSize), Color = ColorOf(Color) },
            FirstPage: (int)Math.Clamp(Math.Round(FirstPage), 1, int.MaxValue),
            StartNumber: (int)Math.Clamp(Math.Round(StartNumber), -1_000_000, 1_000_000),
            Pages: pages,
            FileName: _context.FileName);
        var validation = _operation.Validate(parameters, new Core.Documents.DocumentInfo(PageCount));
        error = validation.IsValid
            ? null
            : validation.Errors.Any(e => e.MemberName is "FirstPage")
                ? string.Format(CultureInfo.CurrentCulture, Strings.PageNumbers_Error_FirstPage, PageCount)
                : validation.Errors.Any(e => e.MemberName is "Pattern") ? Strings.HeaderFooter_Error_Text : Strings.Stamp_Error_Invalid;
        return validation.IsValid ? new StampChoice(_operation, parameters, Strings.History_PageNumbers) : null;
    }

    protected override object? PreviewParameters(StampChoice choice)
    {
        var parameters = (PageNumbersParams)choice.Parameters;
        return PageNumbersOperation.TextOn(parameters, CurrentPage, PageCount, _context.FileName, _context.Title) is { } text
            ? parameters with { Pattern = StampTemplate.Escape(text), FirstPage = 1, Pages = null }
            : null;
    }
}

/// <summary>A kind of stamp the document has, and whether to take it off.</summary>
public sealed partial class StampKindViewModel(StampKind kind, string label) : ObservableObject
{
    public StampKind Kind { get; } = kind;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;
}

/// <summary>"Gỡ dấu đã đóng" (stamp.remove): the kinds of PDF_Pro stamps the document has, checked to be removed.</summary>
public sealed partial class RemoveStampsDialogViewModel : StampDialogViewModel
{
    private readonly RemoveStampsOperation _operation;

    /// <param name="found">How many pages carry each kind (from <see cref="RemoveStampsOperation.Scan"/>).</param>
    public RemoveStampsDialogViewModel(StampContext context, IReadOnlyDictionary<StampKind, int> found, RemoveStampsOperation operation, IStampPreview? preview, IDebouncerFactory debouncers)
        : base(context.PageCount, context.CurrentPage, context.Selected, preview, debouncers)
    {
        ArgumentNullException.ThrowIfNull(found);
        _operation = operation;
        Kinds = [.. found.OrderBy(f => f.Key).Select(f => new StampKindViewModel(f.Key, string.Format(CultureInfo.CurrentCulture, Label(f.Key), f.Value)))];
        foreach (var kind in Kinds)
        {
            kind.PropertyChanged += (_, _) => Changed();
        }

        Start();
    }

    public override string Title => Strings.RemoveStamps_Title;

    public IReadOnlyList<StampKindViewModel> Kinds { get; }

    public bool FoundNone => Kinds.Count == 0;

    protected override StampChoice? Build(string pages, out string? error)
    {
        var kinds = Kinds.Where(k => k.IsChecked).Select(k => k.Kind).ToArray();
        error = Kinds.Count == 0 ? Strings.RemoveStamps_None : kinds.Length == 0 ? Strings.RemoveStamps_Error_NoKind : null;
        return error is null ? new StampChoice(_operation, new RemoveStampsParams(kinds, pages), Strings.History_RemoveStamps) : null;
    }

    protected override object? PreviewParameters(StampChoice choice)
        => Target.Indices.Contains(CurrentPage) ? (RemoveStampsParams)choice.Parameters with { Pages = null } : null;

    private static string Label(StampKind kind) => kind switch
    {
        StampKind.Watermark => Strings.RemoveStamps_Watermark,
        StampKind.Background => Strings.RemoveStamps_Background,
        StampKind.HeaderFooter => Strings.RemoveStamps_HeaderFooter,
        StampKind.PageNumbers => Strings.RemoveStamps_PageNumbers,
        _ => Strings.RemoveStamps_Bates,
    };
}
