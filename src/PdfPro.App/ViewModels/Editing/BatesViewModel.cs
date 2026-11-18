using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GongSolutions.Wpf.DragDrop;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Merging;
using PdfPro.Core.Overlay;
using PdfPro.Core.Stamping;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Stamping;

namespace PdfPro.App.ViewModels.Editing;

/// <summary>The confirmed Bates dialog: the files in order, how to number them, and where the copies go.</summary>
public sealed record BatesDialogResult(IReadOnlyList<string> Inputs, BatesParams Parameters, string OutputFolder, bool OpenFolderWhenDone);

/// <summary>
/// "Số Bates" (stamp.bates): the files in order (add, drop from Explorer, drag to reorder, sort by name, remove), each
/// with the numbers its pages will get; the label (prefix, digits, suffix, first number), its place and look; and the
/// folder the numbered copies go to. The numbers run on from file to file without gaps; the files themselves are never
/// changed. Must be used on the UI thread.
/// </summary>
public sealed partial class BatesViewModel : ObservableObject, IDropTarget
{
    private static readonly BatesOperation Operation = new();

    private readonly IBatesService _bates;
    private readonly IFileDialogService _fileDialogs;
    private bool _adding;

    /// <param name="bates">Reads the files.</param>
    /// <param name="fileDialogs">Picks files and the output folder.</param>
    /// <param name="initial">Files already in the list (the open document).</param>
    public BatesViewModel(IBatesService bates, IFileDialogService fileDialogs, IEnumerable<BatesItemViewModel> initial)
    {
        ArgumentNullException.ThrowIfNull(bates);
        ArgumentNullException.ThrowIfNull(fileDialogs);
        ArgumentNullException.ThrowIfNull(initial);
        _bates = bates;
        _fileDialogs = fileDialogs;
        Items = [.. initial];
        OutputFolder = DefaultFolder() ?? string.Empty;
        Items.CollectionChanged += (_, _) => Recompute();
        Recompute();
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<BatesItemViewModel> Items { get; }

    /// <summary>Numbers go at the top or the bottom of the pages.</summary>
    public IReadOnlyList<StampOption<StampAnchor>> Anchors { get; } = StampDialogViewModel.AnchorOptions(
    [
        StampAnchor.TopLeft, StampAnchor.TopCenter, StampAnchor.TopRight,
        StampAnchor.BottomLeft, StampAnchor.BottomCenter, StampAnchor.BottomRight,
    ]);

    /// <summary>Families that can write Vietnamese into PDFs.</summary>
    public IReadOnlyList<string> FontNames => field ??= [.. FontLibrary.Instance.Families.Select(f => f.Name)];

    public IReadOnlyList<ColorSwatch> Swatches => StampDialogViewModel.StandardSwatches;

    /// <summary>Text before the number ("HS-").</summary>
    [ObservableProperty]
    public partial string Prefix { get; set; } = string.Empty;

    /// <summary>Text after the number.</summary>
    [ObservableProperty]
    public partial string Suffix { get; set; } = string.Empty;

    /// <summary>Minimum number of digits (zeros are added in front).</summary>
    [ObservableProperty]
    public partial double Digits { get; set; } = 6;

    /// <summary>The number of the first page of the first file.</summary>
    [ObservableProperty]
    public partial double StartNumber { get; set; } = 1;

    [ObservableProperty]
    public partial StampAnchor Anchor { get; set; } = StampAnchor.BottomRight;

    [ObservableProperty]
    public partial string FontFamily { get; set; } = FontFamilies.Default;

    [ObservableProperty]
    public partial double FontSize { get; set; } = 10;

    [ObservableProperty]
    public partial bool Bold { get; set; } = true;

    [ObservableProperty]
    public partial string Color { get; set; } = "#000000";

    [ObservableProperty]
    public partial string OutputFolder { get; set; }

    /// <summary>Replace files of the same name in the output folder (otherwise " (2)" is added).</summary>
    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    [ObservableProperty]
    public partial bool OpenFolderWhenDone { get; set; } = true;

    /// <summary>The label of the first page ("HS-000001").</summary>
    [ObservableProperty]
    public partial string? SampleText { get; private set; }

    /// <summary>"3 tệp · 27 trang · HS-000001 → HS-000027".</summary>
    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    /// <summary>Something worth knowing that does not stop the run (numbers longer than the digits asked for).</summary>
    [ObservableProperty]
    public partial string? WarningText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? ErrorText { get; private set; }

    public BatesDialogResult? Result { get; private set; }

    /// <summary>Reads and appends files (or inserts them at <paramref name="index"/>); unusable files are explained and left out.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _adding = true;
        RunCommand.NotifyCanExecuteChanged();
        try
        {
            var position = Math.Clamp(index ?? Items.Count, 0, Items.Count);
            foreach (var path in paths)
            {
                if (await _bates.AddAsync(path) is { } file)
                {
                    Items.Insert(position++, new BatesItemViewModel(file));
                }
            }

            // Nothing chosen yet: propose a folder next to the first file.
            if (string.IsNullOrWhiteSpace(OutputFolder) && DefaultFolder() is { } folder)
            {
                OutputFolder = folder;
            }
        }
        finally
        {
            _adding = false;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        ArgumentNullException.ThrowIfNull(dropInfo);
        if (dropInfo.Data is BatesItemViewModel)
        {
            dropInfo.Effects = DragDropEffects.Move;
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        }
        else if (DroppedPdfFiles(dropInfo).Count > 0)
        {
            dropInfo.Effects = DragDropEffects.Copy;
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        }
        else
        {
            dropInfo.Effects = DragDropEffects.None;
        }
    }

    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        ArgumentNullException.ThrowIfNull(dropInfo);
        if (dropInfo.Data is BatesItemViewModel item && Items.IndexOf(item) is var from and >= 0)
        {
            var to = Math.Clamp(dropInfo.InsertIndex, 0, Items.Count);
            Move(from, to > from ? to - 1 : to);
            return;
        }

        var files = DroppedPdfFiles(dropInfo);
        if (files.Count > 0)
        {
            _ = AddPathsAsync(files, dropInfo.InsertIndex);
        }
    }

    partial void OnPrefixChanged(string value) => Recompute();

    partial void OnSuffixChanged(string value) => Recompute();

    partial void OnDigitsChanged(double value) => Recompute();

    partial void OnStartNumberChanged(double value) => Recompute();

    partial void OnFontFamilyChanged(string value) => Recompute();

    partial void OnFontSizeChanged(double value) => Recompute();

    partial void OnBoldChanged(bool value) => Recompute();

    partial void OnColorChanged(string value) => Recompute();

    partial void OnOutputFolderChanged(string value) => Recompute();

    partial void OnOverwriteChanged(bool value) => Recompute();

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var paths = _fileDialogs.PickPdfFiles();
        if (paths.Count > 0)
        {
            await AddPathsAsync(paths);
        }
    }

    [RelayCommand]
    private void Remove(BatesItemViewModel? item)
    {
        if (item is not null)
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void MoveUp(BatesItemViewModel? item)
    {
        if (item is not null && Items.IndexOf(item) is var index and > 0)
        {
            Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveDown(BatesItemViewModel? item)
    {
        if (item is not null && Items.IndexOf(item) is var index && index >= 0 && index < Items.Count - 1)
        {
            Move(index, index + 1);
        }
    }

    /// <summary>"Sắp theo tên": natural order of the file names (2 before 10), as Explorer sorts.</summary>
    [RelayCommand]
    private void SortByName()
    {
        var sorted = Items.OrderBy(i => i.FileName, NaturalComparer.Instance).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Items.IndexOf(sorted[i]);
            if (current != i)
            {
                Items.Move(current, i);
            }
        }
    }

    [RelayCommand]
    private void PickFolder()
    {
        var initial = string.IsNullOrWhiteSpace(OutputFolder) ? DefaultFolder() : OutputFolder.Trim();
        if (_fileDialogs.PickFolder(Strings.Bates_PickFolder_Title, initial) is { } folder)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand]
    private void SetColor(string hex) => Color = hex;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (ErrorText is not null || Items.Count == 0)
        {
            return;
        }

        Result = new BatesDialogResult([.. Items.Select(i => i.File.Path)], Parameters(), Path.GetFullPath(OutputFolder.Trim()), OpenFolderWhenDone);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanRun() => !_adding && ErrorText is null && Items.Count > 0;

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, false);
    }

    private void Move(int from, int to)
    {
        if (from != to && to >= 0 && to < Items.Count)
        {
            Items.Move(from, to);
        }
    }

    private BatesParams Parameters() => new(
        Prefix,
        Suffix,
        (int)Math.Clamp(Math.Round(Digits), 1, BatesFormat.MaxDigits),
        (long)Math.Clamp(Math.Round(StartNumber), 0, 999_999_999_999),
        Anchor,
        BatesParams.DefaultStyle with
        {
            FontFamily = FontFamily,
            FontSize = Math.Clamp(FontSize, OverlayLimits.MinFontSize, OverlayLimits.MaxFontSize),
            Color = RgbColor.TryParse(Color, out var color) ? color.Value : RgbColor.Black,
            Bold = Bold,
        },
        Overwrite: Overwrite);

    private void Recompute()
    {
        var parameters = Parameters();
        var format = new BatesFormat(parameters.Prefix, parameters.Digits, parameters.Suffix);
        SampleText = format.Label(parameters.StartNumber);
        WarningText = null;
        SummaryText = null;
        foreach (var item in Items)
        {
            item.NumbersText = null;
        }

        if (Items.Count == 0)
        {
            Show(Strings.Bates_Error_NoFiles);
            return;
        }

        var ranges = BatesNumbering.Plan([.. Items.Select(i => i.PageCount)], parameters.StartNumber);
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].NumbersText = Format(Strings.Bates_Range, format.Label(ranges[i].First), format.Label(ranges[i].Last));
        }

        var last = ranges[^1].Last;
        SummaryText = Format(Strings.Bates_Summary, Items.Count, Items.Sum(i => i.PageCount), format.Label(parameters.StartNumber), format.Label(last));
        if (format.Overflows(last))
        {
            WarningText = Format(Strings.Bates_Warning_Digits, format.Label(last), parameters.Digits);
        }

        var validation = Operation.Validate(parameters, new Core.Documents.DocumentInfo(1));
        if (!validation.IsValid)
        {
            Show(validation.Errors.Any(e => e.MemberName is nameof(BatesParams.Prefix) or nameof(BatesParams.Suffix))
                ? Format(Strings.Bates_Error_Affix, BatesOperation.MaxAffixLength)
                : Strings.Stamp_Error_Invalid);
            return;
        }

        if (!RgbColor.TryParse(Color, out _))
        {
            Show(Strings.Stamp_Error_Color);
            return;
        }

        if (!IsUsableFolder(OutputFolder))
        {
            Show(Strings.Bates_Error_Output);
            return;
        }

        // Copies keep the files' names, so with "Ghi đè" a file in the output folder would be replaced by its own copy.
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputFolder.Trim()));
        if (Overwrite && Items.Any(i => string.Equals(Path.GetDirectoryName(i.File.Path), output, StringComparison.OrdinalIgnoreCase)))
        {
            Show(Strings.Bates_Error_OutputIsInput);
            return;
        }

        Show(error: null);
    }

    private void Show(string? error)
    {
        ErrorText = error;
        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"…\Đã đánh số Bates" next to the first file, or null without files.</summary>
    private string? DefaultFolder()
        => Items.Count > 0 && Path.GetDirectoryName(Items[0].File.Path) is { Length: > 0 } folder ? Path.Combine(folder, Strings.Bates_DefaultFolder) : null;

    private static bool IsUsableFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = path.Trim();
            return Path.IsPathFullyQualified(trimmed) && trimmed.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static List<string> DroppedPdfFiles(IDropInfo dropInfo)
        => dropInfo.Data is IDataObject data && data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? [.. files.Where(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase))]
            : [];

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}

/// <summary>A file of the Bates list, with the numbers its pages get.</summary>
public sealed partial class BatesItemViewModel(BatesFile file) : ObservableObject
{
    public BatesFile File { get; } = file ?? throw new ArgumentNullException(nameof(file));

    public string FileName => File.FileName;

    public int PageCount => File.PageCount;

    public string PageCountText => string.Format(CultureInfo.CurrentCulture, Strings.Merge_PageCount, PageCount);

    /// <summary>"HS-000001 – HS-000012"; null while the list cannot be numbered.</summary>
    [ObservableProperty]
    public partial string? NumbersText { get; internal set; }
}
