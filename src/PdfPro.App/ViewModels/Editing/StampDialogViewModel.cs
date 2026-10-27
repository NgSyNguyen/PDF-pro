using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Editing;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Stamping;
using PdfPro.Operations.Fonts;

namespace PdfPro.App.ViewModels.Editing;

/// <summary>What a stamp dialog asks for once confirmed: the operation, its parameters and the step's name.</summary>
public sealed record StampChoice(IContentOperation Operation, object Parameters, string Description);

/// <summary>A value of a list with the name the user sees.</summary>
public sealed record StampOption<T>(T Value, string Label);

/// <summary>
/// The common part of the stamp dialogs (watermark, background, header and footer, page numbers, removing stamps):
/// the pages it applies to, a live preview of the page the user is on — the real operation run on that page and
/// rendered by PDFium, 250 ms after the last change — and Áp dụng / Hủy. A derived dialog builds the parameters for
/// the whole document and says what the previewed page alone gets.
/// </summary>
public abstract partial class StampDialogViewModel : ObservableObject, IAsyncDisposable
{
    public const double PreviewWidth = 320;

    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(250);

    private readonly IStampPreview? _preview;
    private readonly IDebouncer _debouncer;
    private CancellationTokenSource? _rendering;

    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="currentPage">The page shown in the preview (0-based).</param>
    /// <param name="selected">Pages selected when the dialog opened (for "các trang đã chọn").</param>
    /// <param name="preview">Renders the previewed page with a stamp (null: no preview).</param>
    /// <param name="debouncers">Delays the preview until typing pauses.</param>
    protected StampDialogViewModel(int pageCount, int currentPage, IReadOnlyList<int> selected, IStampPreview? preview, IDebouncerFactory debouncers)
    {
        ArgumentNullException.ThrowIfNull(debouncers);
        PageCount = pageCount;
        CurrentPage = Math.Clamp(currentPage, 0, Math.Max(0, pageCount - 1));
        _preview = preview;
        _debouncer = debouncers.Create();
        Target = new PageTargetViewModel(pageCount, selected);
        Target.Changed += (_, _) => Changed();
    }

    public event EventHandler<bool>? CloseRequested;

    public abstract string Title { get; }

    public int PageCount { get; }

    public int CurrentPage { get; }

    /// <summary>"Xem trước trang 3".</summary>
    public string PreviewLabel => string.Format(CultureInfo.CurrentCulture, Strings.Stamp_PreviewOf, CurrentPage + 1);

    public PageTargetViewModel Target { get; }

    /// <summary>Families that can write Vietnamese into PDFs.</summary>
    public IReadOnlyList<string> FontFamilies => field ??= [.. FontLibrary.Instance.Families.Select(f => f.Name)];

    public IReadOnlyList<ColorSwatch> Swatches { get; } =
    [
        new("#000000"), new("#808080"), new("#A0A0A0"), new("#DC2626"), new("#EA580C"),
        new("#CA8A04"), new("#16A34A"), new("#0EA5E9"), new("#1D4ED8"), new("#7C3AED"),
    ];

    [ObservableProperty]
    public partial ImageSource? Preview { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; protected set; }

    /// <summary>The confirmed choice, once Áp dụng was pressed.</summary>
    public StampChoice? Result { get; private set; }

    public async ValueTask DisposeAsync()
    {
        _debouncer.Dispose();
        _rendering?.Cancel();
        if (_preview is not null)
        {
            await _preview.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Builds the choice for the whole document, or null with <paramref name="error"/> (shown to the user).</summary>
    protected abstract StampChoice? Build(string pages, out string? error);

    /// <summary>
    /// What the previewed page alone gets, as parameters for a one-page document: the operation's parameters with
    /// page-dependent text written out; null when the page gets nothing (it is shown as it is).
    /// </summary>
    protected abstract object? PreviewParameters(StampChoice choice);

    /// <summary>Something changed: check it and refresh the preview after a short pause.</summary>
    protected void Changed()
    {
        ErrorText = Choose(out var error) is null ? error : null;
        _debouncer.Schedule(PreviewDelay, () => _ = RefreshPreviewAsync());
    }

    /// <summary>Starts the first preview (called by the derived dialog once its fields are set).</summary>
    protected void Start() => Changed();

    /// <summary>A "#RRGGBB" colour, black when it is not one.</summary>
    protected static RgbColor ColorOf(string hex) => RgbColor.TryParse(hex, out var color) ? color.Value : RgbColor.Black;

    /// <summary>"1, 2, 3", "i, ii, iii", "I, II, III".</summary>
    protected static IReadOnlyList<StampOption<PageNumberStyle>> NumberStyleOptions { get; } =
    [
        new(PageNumberStyle.Arabic, Strings.NumberStyle_Arabic),
        new(PageNumberStyle.LowerRoman, Strings.NumberStyle_LowerRoman),
        new(PageNumberStyle.UpperRoman, Strings.NumberStyle_UpperRoman),
    ];

    /// <summary>Places on the page with their names ("Dưới giữa").</summary>
    protected static IReadOnlyList<StampOption<StampAnchor>> AnchorOptions(IEnumerable<StampAnchor> anchors)
        => [.. anchors.Select(a => new StampOption<StampAnchor>(a, a switch
        {
            StampAnchor.TopLeft => Strings.Anchor_TopLeft,
            StampAnchor.TopCenter => Strings.Anchor_TopCenter,
            StampAnchor.TopRight => Strings.Anchor_TopRight,
            StampAnchor.MiddleLeft => Strings.Anchor_MiddleLeft,
            StampAnchor.Center => Strings.Anchor_Center,
            StampAnchor.MiddleRight => Strings.Anchor_MiddleRight,
            StampAnchor.BottomLeft => Strings.Anchor_BottomLeft,
            StampAnchor.BottomCenter => Strings.Anchor_BottomCenter,
            _ => Strings.Anchor_BottomRight,
        }))];

    /// <summary>Points from millimetres.</summary>
    protected static double Points(double millimetres) => millimetres * 72 / 25.4;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Choose(out var error) is { } choice)
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

    private StampChoice? Choose(out string? error)
    {
        if (Target.Pages(out var pageError) is not { } pages)
        {
            error = pageError ?? Strings.Stamp_Error_Pages;
            return null;
        }

        return Build(pages, out error);
    }

    private async Task RefreshPreviewAsync()
    {
        if (_preview is null)
        {
            return;
        }

        _rendering?.Cancel();
        var rendering = _rendering = new CancellationTokenSource();
        var choice = Choose(out _);
        var parameters = choice is null ? null : PreviewParameters(choice);
        try
        {
            var image = await _preview.RenderAsync(parameters is null ? null : choice!.Operation, parameters, PreviewWidth, rendering.Token);
            if (!rendering.IsCancellationRequested)
            {
                Preview = image;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer preview replaced this one.
        }
    }
}
