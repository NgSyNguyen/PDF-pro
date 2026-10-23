using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Layout;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Layout;

namespace PdfPro.App.ViewModels.Organizer;

/// <summary>Which sheet layout the dialog makes.</summary>
public enum SheetLayoutKind
{
    NUp,
    Booklet,
    Tile,
}

/// <summary>A paper format of the layout dialogs (portrait size in points).</summary>
public sealed record PaperOption(string Name, PdfSize Size)
{
    public override string ToString() => Name;
}

/// <summary>A choice with a label (orientation, pages per sheet).</summary>
public sealed record LabeledOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A page drawn on the preview sheet, in view units from the preview's top-left.</summary>
public sealed record SheetPreviewItem(double Left, double Top, double Width, double Height, string Label);

/// <summary>The confirmed layout dialog.</summary>
/// <param name="Parameters">The operation's parameters (<see cref="NUpParams"/>, <see cref="BookletParams"/> or <see cref="TileParams"/>).</param>
public sealed record SheetLayoutResult(SheetLayoutKind Kind, object Parameters, string OutputPath, bool OpenWhenDone);

/// <summary>
/// "Nhiều trang/tờ", "Sổ gập", "Chia ô": the settings of one sheet layout, a drawing of the first sheet (pages placed
/// as they will be printed), the sheet count and the new file. The document does not change. Sizes are typed in
/// millimetres. Must be used on the UI thread.
/// </summary>
public sealed partial class SheetLayoutViewModel : ObservableObject
{
    /// <summary>The longer side of the preview sheet, in view units.</summary>
    public const double PreviewSize = 260;

    private readonly IReadOnlyList<PdfSize> _pageSizes;
    private readonly IFileDialogService _fileDialogs;
    private readonly NUpOperation _nUp;
    private readonly BookletOperation _booklet;
    private readonly TileOperation _tile;
    private readonly bool _ready;

    /// <param name="kind">The layout.</param>
    /// <param name="pageSizes">Displayed size of every page of the document.</param>
    /// <param name="outputPath">The new file proposed.</param>
    public SheetLayoutViewModel(
        SheetLayoutKind kind,
        IReadOnlyList<PdfSize> pageSizes,
        string outputPath,
        IFileDialogService fileDialogs,
        NUpOperation nUp,
        BookletOperation booklet,
        TileOperation tile)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        ArgumentNullException.ThrowIfNull(fileDialogs);
        Kind = kind;
        _pageSizes = pageSizes;
        _fileDialogs = fileDialogs;
        _nUp = nUp;
        _booklet = booklet;
        _tile = tile;
        Papers = [.. new[] { "A4", "A3", "A5", "Letter", "Legal" }.Select(name => new PaperOption(name, PaperSizes.SizeOf(name)!.Value))];
        Paper = Papers[0];
        PagesPerSheetOptions = [.. NUpLayout.SupportedCounts];
        PagesPerSheet = 2;
        Orientations =
        [
            new(SheetOrientation.Auto, Strings.Layout_Orientation_Auto),
            new(SheetOrientation.Portrait, Strings.Layout_Orientation_Portrait),
            new(SheetOrientation.Landscape, Strings.Layout_Orientation_Landscape),
        ];
        Orientation = Orientations[0];
        IsRowsFirst = true;
        MarginText = Millimetres(kind == SheetLayoutKind.Tile ? PdfPro.Operations.Layout.Paper.TenMillimetres : PdfPro.Operations.Layout.Paper.FiveMillimetres);
        GapText = Millimetres(PdfPro.Operations.Layout.Paper.FiveMillimetres);
        IsLeftBinding = true;
        GutterText = "0";
        OverlapText = Millimetres(PdfPro.Operations.Layout.Paper.TenMillimetres);
        ScaleText = "100";
        ColumnsText = "2";
        RowsText = "2";
        CutMarks = true;
        Labels = true;
        PagesText = string.Empty;
        OutputPath = outputPath;
        OpenWhenDone = true;
        PreviewItems = [];
        _ready = true;
        Recompute();
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public SheetLayoutKind Kind { get; }

    public string Title => Kind switch
    {
        SheetLayoutKind.NUp => Strings.Layout_NUp_Title,
        SheetLayoutKind.Booklet => Strings.Layout_Booklet_Title,
        _ => Strings.Layout_Tile_Title,
    };

    public bool IsNUp => Kind == SheetLayoutKind.NUp;

    public bool IsBooklet => Kind == SheetLayoutKind.Booklet;

    public bool IsTile => Kind == SheetLayoutKind.Tile;

    public IReadOnlyList<PaperOption> Papers { get; }

    [ObservableProperty]
    public partial PaperOption Paper { get; set; }

    public IReadOnlyList<int> PagesPerSheetOptions { get; }

    [ObservableProperty]
    public partial int PagesPerSheet { get; set; }

    public IReadOnlyList<LabeledOption<SheetOrientation>> Orientations { get; }

    [ObservableProperty]
    public partial LabeledOption<SheetOrientation> Orientation { get; set; }

    /// <summary>Z order (rows first); false for N order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsColumnsFirst))]
    public partial bool IsRowsFirst { get; set; }

    public bool IsColumnsFirst
    {
        get => !IsRowsFirst;
        set => IsRowsFirst = !value;
    }

    [ObservableProperty]
    public partial string MarginText { get; set; }

    [ObservableProperty]
    public partial string GapText { get; set; }

    [ObservableProperty]
    public partial bool Border { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRightBinding))]
    public partial bool IsLeftBinding { get; set; }

    public bool IsRightBinding
    {
        get => !IsLeftBinding;
        set => IsLeftBinding = !value;
    }

    [ObservableProperty]
    public partial string GutterText { get; set; }

    [ObservableProperty]
    public partial string OverlapText { get; set; }

    /// <summary>Poster: fit the page to a number of sheets instead of a scale.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesScale))]
    public partial bool UsesFit { get; set; }

    public bool UsesScale
    {
        get => !UsesFit;
        set => UsesFit = !value;
    }

    [ObservableProperty]
    public partial string ScaleText { get; set; }

    [ObservableProperty]
    public partial string ColumnsText { get; set; }

    [ObservableProperty]
    public partial string RowsText { get; set; }

    [ObservableProperty]
    public partial bool CutMarks { get; set; }

    [ObservableProperty]
    public partial bool Labels { get; set; }

    /// <summary>N-up and poster: the pages to lay out; empty for all.</summary>
    [ObservableProperty]
    public partial string PagesText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string OutputPath { get; set; }

    [ObservableProperty]
    public partial bool OpenWhenDone { get; set; }

    /// <summary>The first sheet as it will be printed.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SheetPreviewItem> PreviewItems { get; private set; }

    [ObservableProperty]
    public partial double PreviewWidth { get; private set; }

    [ObservableProperty]
    public partial double PreviewHeight { get; private set; }

    /// <summary>"6 tờ in".</summary>
    [ObservableProperty]
    public partial string? SheetCountText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? ErrorText { get; private set; }

    public SheetLayoutResult? Result { get; private set; }

    partial void OnPaperChanged(PaperOption value) => Recompute();

    partial void OnPagesPerSheetChanged(int value) => Recompute();

    partial void OnOrientationChanged(LabeledOption<SheetOrientation> value) => Recompute();

    partial void OnIsRowsFirstChanged(bool value) => Recompute();

    partial void OnMarginTextChanged(string value) => Recompute();

    partial void OnGapTextChanged(string value) => Recompute();

    partial void OnBorderChanged(bool value) => Recompute();

    partial void OnIsLeftBindingChanged(bool value) => Recompute();

    partial void OnGutterTextChanged(string value) => Recompute();

    partial void OnOverlapTextChanged(string value) => Recompute();

    partial void OnUsesFitChanged(bool value) => Recompute();

    partial void OnScaleTextChanged(string value) => Recompute();

    partial void OnColumnsTextChanged(string value) => Recompute();

    partial void OnRowsTextChanged(string value) => Recompute();

    partial void OnCutMarksChanged(bool value) => Recompute();

    partial void OnLabelsChanged(bool value) => Recompute();

    partial void OnPagesTextChanged(string value) => Recompute();

    partial void OnOutputPathChanged(string value) => Recompute();

    [RelayCommand]
    private void PickOutput()
    {
        var folder = string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetDirectoryName(OutputPath);
        if (_fileDialogs.PickSavePath(Path.GetFileName(OutputPath), folder) is { } path)
        {
            OutputPath = path;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (Build() is not { } parameters)
        {
            return;
        }

        Result = new SheetLayoutResult(Kind, parameters, Path.GetFullPath(OutputPath.Trim()), OpenWhenDone);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanRun() => ErrorText is null;

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, false);
    }

    private void Recompute()
    {
        if (!_ready)
        {
            return;
        }

        if (Build() is not { } parameters)
        {
            ShowError(Strings.Layout_Error_Number);
            return;
        }

        var validation = Kind switch
        {
            SheetLayoutKind.NUp => _nUp.Validate(parameters, Document),
            SheetLayoutKind.Booklet => _booklet.Validate(parameters, Document),
            _ => _tile.Validate(parameters, Document),
        };
        if (!validation.IsValid)
        {
            ShowError(validation.Errors.Any(e => e.MemberName is "Pages") ? Strings.Layout_Error_Pages : Strings.Layout_Error_Room);
            return;
        }

        var sheets = Kind switch
        {
            SheetLayoutKind.NUp => _nUp.SheetCount(_pageSizes, (NUpParams)parameters),
            SheetLayoutKind.Booklet => _booklet.SheetCount(_pageSizes, (BookletParams)parameters),
            _ => _tile.SheetCount(_pageSizes, (TileParams)parameters),
        };
        SheetCountText = string.Format(CultureInfo.CurrentCulture, Strings.Layout_SheetCount, sheets);
        DrawPreview(parameters);
        ErrorText = IsUsablePath(OutputPath) ? null : Strings.Layout_Error_Output;
    }

    private DocumentInfo Document => new(_pageSizes.Count);

    private object? Build()
    {
        if (!TryMillimetres(MarginText, out var margin))
        {
            return null;
        }

        var paper = Paper.Size;
        switch (Kind)
        {
            case SheetLayoutKind.NUp:
                return TryMillimetres(GapText, out var gap)
                    ? new NUpParams(PagesPerSheet, paper.Width, paper.Height, Orientation.Value, IsRowsFirst ? NUpOrder.RowsFirst : NUpOrder.ColumnsFirst, margin, gap, Border, NullIfEmpty(PagesText))
                    : null;
            case SheetLayoutKind.Booklet:
                return TryMillimetres(GutterText, out var gutter)
                    ? new BookletParams(paper.Width, paper.Height, IsLeftBinding ? BookletBinding.Left : BookletBinding.Right, margin, gutter)
                    : null;
            default:
                if (!TryMillimetres(OverlapText, out var overlap))
                {
                    return null;
                }

                if (UsesFit)
                {
                    return int.TryParse(ColumnsText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var columns)
                        && int.TryParse(RowsText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var rows) && columns >= 1 && rows >= 1 && columns <= 50 && rows <= 50
                        ? new TileParams(paper.Width, paper.Height, Orientation.Value, margin, overlap, 1, columns, rows, CutMarks, Labels, NullIfEmpty(PagesText))
                        : null;
                }

                return TryNumber(ScaleText, out var percent) && percent >= 1 && percent <= 10_000
                    ? new TileParams(paper.Width, paper.Height, Orientation.Value, margin, overlap, percent / 100, 0, 0, CutMarks, Labels, NullIfEmpty(PagesText))
                    : null;
        }
    }

    /// <summary>The first sheet (N-up, booklet) or the first page cut into sheets (poster), mapped to the preview.</summary>
    private void DrawPreview(object parameters)
    {
        var pages = string.IsNullOrWhiteSpace(PagesText) ? [.. Enumerable.Range(0, _pageSizes.Count)] : PageRanges.Parse(PagesText, _pageSizes.Count);
        var items = new List<SheetPreviewItem>();
        PdfSize sheet;
        switch (parameters)
        {
            case NUpParams nUp:
            {
                var grid = NUpOperation.Grid([.. pages.Select(i => _pageSizes[i])], nUp);
                sheet = grid.Sheet;
                var mapper = Mapper(sheet);
                for (var slot = 0; slot < grid.CellsPerSheet && slot < pages.Count; slot++)
                {
                    items.Add(Item(mapper, NUpLayout.Place(grid.Cell(slot), _pageSizes[pages[slot]]), pages[slot] + 1));
                }

                break;
            }

            case BookletParams booklet:
            {
                var grid = booklet.Grid;
                sheet = grid.Sheet;
                var mapper = Mapper(sheet);
                var side = BookletLayout.Sides(_pageSizes.Count, booklet.Binding)[0];
                foreach (var (page, slot) in new[] { (side.Left, 0), (side.Right, 1) })
                {
                    if (page >= 0)
                    {
                        items.Add(Item(mapper, NUpLayout.Place(grid.Cell(slot), _pageSizes[page]), page + 1));
                    }
                }

                break;
            }

            default:
            {
                var tile = (TileParams)parameters;
                var page = _pageSizes[pages[0]];
                var (scale, options, _, _) = TileOperation.LayoutOf(page, tile);
                sheet = new PdfSize(page.Width * scale, page.Height * scale);
                var mapper = Mapper(sheet);
                foreach (var t in PosterLayout.Tiles(page, options))
                {
                    var label = TileOperation.Label(t, null);
                    var area = mapper.PdfRectToScreen(t.Region);
                    items.Add(new SheetPreviewItem(area.X, area.Y, area.Width, area.Height, label));
                }

                break;
            }
        }

        var previewScale = PreviewSize / Math.Max(sheet.Width, sheet.Height);
        PreviewWidth = sheet.Width * previewScale;
        PreviewHeight = sheet.Height * previewScale;
        PreviewItems = items;
    }

    /// <summary>PDF space of the sheet → preview units (AGENTS rule 6: through the mapper).</summary>
    private static PageCoordinateMapper Mapper(PdfSize sheet)
        => new(new PdfRect(0, 0, sheet.Width, sheet.Height), 0, PreviewSize / Math.Max(sheet.Width, sheet.Height));

    private static SheetPreviewItem Item(PageCoordinateMapper mapper, PdfRect rect, int pageNumber)
    {
        var area = mapper.PdfRectToScreen(rect);
        return new SheetPreviewItem(area.X, area.Y, area.Width, area.Height, pageNumber.ToString(CultureInfo.CurrentCulture));
    }

    private void ShowError(string message)
    {
        ErrorText = message;
        SheetCountText = null;
        PreviewItems = [];
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool TryMillimetres(string text, out double points)
    {
        points = 0;
        if (!TryNumber(text, out var millimetres) || millimetres < 0 || millimetres > 170)
        {
            return false;
        }

        points = millimetres / PaperSizes.ToMillimetres(1);
        return true;
    }

    private static bool TryNumber(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Millimetres(double points) => Math.Round(PaperSizes.ToMillimetres(points)).ToString(CultureInfo.CurrentCulture);

    private static bool IsUsablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = path.Trim();
            return Path.IsPathFullyQualified(trimmed) && Path.GetFileName(trimmed).Length > 0 && trimmed.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
