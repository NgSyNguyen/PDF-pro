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
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Merging;

namespace PdfPro.App.ViewModels.Merging;

/// <summary>A page size choice of the merge dialog.</summary>
public sealed record MergePageSizeOption(MergePageSize Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The confirmed merge dialog.</summary>
public sealed record MergeDialogResult(IReadOnlyList<MergeSource> Sources, MergeParams Parameters, string OutputPath, bool OpenWhenDone);

/// <summary>
/// "Gộp" (SPEC "Gộp"): the files in order (add, drop from Explorer, drag to reorder, sort by name, remove), each with
/// its pages and the name it gets in bookmarks and the table of contents; the options; the result file; and a preview
/// of the page count. Protected files ask for their password when added. Must be used on the UI thread.
/// </summary>
public sealed partial class MergeViewModel : ObservableObject, IDropTarget
{
    private readonly IMergeService _merge;
    private readonly IFileDialogService _fileDialogs;
    private bool _adding;

    /// <param name="merge">Reads the files and previews the result.</param>
    /// <param name="fileDialogs">Picks files and the result path.</param>
    /// <param name="initial">Files already in the list (the open document).</param>
    /// <param name="outputPath">The result proposed, or null.</param>
    public MergeViewModel(IMergeService merge, IFileDialogService fileDialogs, IEnumerable<MergeItemViewModel> initial, string? outputPath)
    {
        ArgumentNullException.ThrowIfNull(merge);
        ArgumentNullException.ThrowIfNull(fileDialogs);
        ArgumentNullException.ThrowIfNull(initial);
        _merge = merge;
        _fileDialogs = fileDialogs;
        Items = [];
        PageSizes =
        [
            new(MergePageSize.Unchanged, Strings.Merge_PageSize_Unchanged),
            new(MergePageSize.LikeFirstPage, Strings.Merge_PageSize_LikeFirst),
            new(MergePageSize.A4, Strings.Merge_PageSize_A4),
        ];
        PageSize = PageSizes[0];
        KeepBookmarks = true;
        BookmarkPerFile = true;
        OpenWhenDone = true;
        OutputPath = outputPath ?? string.Empty;
        foreach (var item in initial)
        {
            Attach(item);
        }

        Items.CollectionChanged += (_, _) => Recompute();
        Recompute();
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<MergeItemViewModel> Items { get; }

    public IReadOnlyList<MergePageSizeOption> PageSizes { get; }

    [ObservableProperty]
    public partial bool KeepBookmarks { get; set; }

    [ObservableProperty]
    public partial bool BookmarkPerFile { get; set; }

    [ObservableProperty]
    public partial bool TableOfContents { get; set; }

    [ObservableProperty]
    public partial MergePageSizeOption PageSize { get; set; }

    [ObservableProperty]
    public partial bool StartFilesOnOddPages { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string OutputPath { get; set; }

    [ObservableProperty]
    public partial bool OpenWhenDone { get; set; }

    /// <summary>"3 tệp · 27 trang (thêm 1 trang mục lục, 2 trang trắng)".</summary>
    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? ErrorText { get; private set; }

    public MergeDialogResult? Result { get; private set; }

    /// <summary>Reads and appends files (or inserts them at <paramref name="index"/>), asking for passwords as needed.</summary>
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
                if (await _merge.AddAsync(path) is { } source)
                {
                    var item = new MergeItemViewModel(source, Path.GetFileName(source.Path));
                    item.Changed += OnItemChanged;
                    Items.Insert(position++, item);
                }
            }

            // Nothing chosen yet (merge from the home tab): propose a file next to the first one.
            if (string.IsNullOrWhiteSpace(OutputPath) && Items.Count > 0 && Path.GetDirectoryName(Items[0].Source.Path) is { Length: > 0 } folder)
            {
                OutputPath = Path.Combine(folder, DefaultFileName());
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
        if (dropInfo.Data is MergeItemViewModel)
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
        if (dropInfo.Data is MergeItemViewModel item && Items.IndexOf(item) is var from and >= 0)
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

    partial void OnKeepBookmarksChanged(bool value) => Recompute();

    partial void OnBookmarkPerFileChanged(bool value) => Recompute();

    partial void OnTableOfContentsChanged(bool value) => Recompute();

    partial void OnPageSizeChanged(MergePageSizeOption value) => Recompute();

    partial void OnStartFilesOnOddPagesChanged(bool value) => Recompute();

    partial void OnOutputPathChanged(string value) => Recompute();

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
    private void Remove(MergeItemViewModel? item)
    {
        if (item is not null && Items.Remove(item))
        {
            item.Changed -= OnItemChanged;
        }
    }

    [RelayCommand]
    private void MoveUp(MergeItemViewModel? item)
    {
        if (item is not null && Items.IndexOf(item) is var index and > 0)
        {
            Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveDown(MergeItemViewModel? item)
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
    private void PickOutput()
    {
        var folder = string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetDirectoryName(OutputPath);
        var name = string.IsNullOrWhiteSpace(OutputPath) ? DefaultFileName() : Path.GetFileName(OutputPath);
        if (_fileDialogs.PickSavePath(name, folder) is { } path)
        {
            OutputPath = path;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (ErrorText is not null || Items.Count < 2)
        {
            return;
        }

        Result = new MergeDialogResult([.. Items.Select(i => i.Source)], Parameters(), Path.GetFullPath(OutputPath.Trim()), OpenWhenDone);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanRun() => !_adding && ErrorText is null && Items.Count >= 2;

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, false);
    }

    private void Attach(MergeItemViewModel item)
    {
        item.Changed += OnItemChanged;
        Items.Add(item);
    }

    private void Move(int from, int to)
    {
        if (from != to && to >= 0 && to < Items.Count)
        {
            Items.Move(from, to);
        }
    }

    private void OnItemChanged(object? sender, EventArgs e) => Recompute();

    private MergeParams Parameters() => new(
        [.. Items.Select(i => new MergeItem(string.IsNullOrWhiteSpace(i.PagesText) ? null : i.PagesText.Trim(), string.IsNullOrWhiteSpace(i.TitleText) ? null : i.TitleText.Trim()))],
        KeepBookmarks,
        BookmarkPerFile,
        TableOfContents,
        Strings.Merge_TocHeading,
        PageSize.Value,
        StartFilesOnOddPages,
        Title: string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetFileNameWithoutExtension(OutputPath.Trim()));

    private void Recompute()
    {
        foreach (var item in Items)
        {
            item.HasPageError = !string.IsNullOrWhiteSpace(item.PagesText) && !PageRanges.TryParse(item.PagesText, item.PageCount, out _, out _);
        }

        if (Items.Count < 2)
        {
            Show(Strings.Merge_Error_TooFew, summary: null);
            return;
        }

        if (Items.FirstOrDefault(i => i.HasPageError) is { } bad)
        {
            Show(Format(Strings.Merge_Error_Pages, bad.FileName), summary: null);
            return;
        }

        string? summary;
        try
        {
            var layout = _merge.Layout([.. Items.Select(i => i.Source)], Parameters());
            summary = Format(Strings.Merge_Summary, Items.Count, layout.Plan.Count);
            if (layout.TableOfContentsPages > 0 || layout.AddedBlankPages > 0)
            {
                summary += Format(Strings.Merge_Summary_Extra, layout.TableOfContentsPages, layout.AddedBlankPages);
            }
        }
        catch (OperationValidationException)
        {
            Show(Strings.Edit_Error_Invalid, summary: null);
            return;
        }

        if (!IsUsablePath(OutputPath))
        {
            Show(Strings.Merge_Error_Output, summary);
            return;
        }

        var output = Path.GetFullPath(OutputPath.Trim());
        if (Items.Any(i => string.Equals(i.Source.Path, output, StringComparison.OrdinalIgnoreCase)))
        {
            Show(Strings.Merge_Error_OutputIsInput, summary);
            return;
        }

        Show(error: null, summary);
    }

    private void Show(string? error, string? summary)
    {
        ErrorText = error;
        SummaryText = summary;
        RunCommand.NotifyCanExecuteChanged();
    }

    private string DefaultFileName()
        => Format(Strings.Merge_DefaultName, Items.Count > 0 ? Path.GetFileNameWithoutExtension(Items[0].FileName) : string.Empty) + ".pdf";

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

    private static List<string> DroppedPdfFiles(IDropInfo dropInfo)
        => dropInfo.Data is IDataObject data && data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? [.. files.Where(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase))]
            : [];

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}

/// <summary>A file of the merge list.</summary>
public sealed partial class MergeItemViewModel : ObservableObject
{
    /// <param name="source">The file as read.</param>
    /// <param name="fileName">What the list shows (the open document: its own name).</param>
    /// <param name="title">The name proposed for bookmarks and the table of contents, or null for the file name.</param>
    public MergeItemViewModel(MergeSource source, string fileName, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        FileName = fileName;
        PagesText = string.Empty;
        TitleText = title ?? string.Empty;
    }

    /// <summary>Pages, title or their validity changed.</summary>
    public event EventHandler? Changed;

    public MergeSource Source { get; }

    public string FileName { get; }

    public int PageCount => Source.PageCount;

    public string PageCountText => string.Format(CultureInfo.CurrentCulture, Strings.Merge_PageCount, PageCount);

    public bool IsEncrypted => Source.IsEncrypted;

    /// <summary>Placeholder of the title box: the file name without extension.</summary>
    public string DefaultTitle => Path.GetFileNameWithoutExtension(FileName);

    /// <summary>"1-3, 7"; empty for every page.</summary>
    [ObservableProperty]
    public partial string PagesText { get; set; }

    /// <summary>The name in bookmarks and the table of contents; empty for the file name.</summary>
    [ObservableProperty]
    public partial string TitleText { get; set; }

    [ObservableProperty]
    public partial bool HasPageError { get; internal set; }

    partial void OnPagesTextChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnTitleTextChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Compares file names the way Explorer sorts them: digit runs by value ("2" before "10").</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                var byValue = numberX.Length != numberY.Length ? numberX.Length.CompareTo(numberY.Length) : string.CompareOrdinal(numberX, numberY);
                if (byValue != 0)
                {
                    return byValue;
                }
            }
            else
            {
                var byText = string.Compare(x[i].ToString(), y[j].ToString(), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                if (byText != 0)
                {
                    return byText;
                }

                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
