using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GongSolutions.Wpf.DragDrop;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Core.Text;
using PdfPro.Operations.Converting;

namespace PdfPro.App.ViewModels.Converting;

/// <summary>What "Ảnh sang PDF" was confirmed with.</summary>
/// <param name="Files">The pictures, in page order.</param>
/// <param name="Parameters">Page size and margin.</param>
/// <param name="OutputPath">The PDF to write (full path).</param>
/// <param name="OpenWhenDone">Open the PDF once written.</param>
public sealed record FromImagesDialogResult(IReadOnlyList<string> Files, FromImagesParams Parameters, string OutputPath, bool OpenWhenDone);

/// <summary>A picture of the list.</summary>
public sealed class FromImagesItemViewModel(string path, long size)
{
    public string Path { get; } = path;

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>"1,2 MB".</summary>
    public string SizeText { get; } = ByteSizeFormatter.Format(size, CultureInfo.CurrentCulture);
}

/// <summary>
/// "Ảnh sang PDF" (convert.fromImages): the pictures in page order — added from the file dialog, a whole folder or
/// Explorer (each batch in natural order, "ảnh 2" before "ảnh 10"), moved, removed or sorted by name — the page size,
/// and the PDF to write.
/// </summary>
public sealed partial class FromImagesViewModel : ObservableObject, IDropTarget
{
    private readonly IFileDialogService _files;

    public FromImagesViewModel(IFileDialogService files, IEnumerable<string>? initial = null)
    {
        _files = files;
        Items = [];
        Items.CollectionChanged += (_, _) => Check();
        OpenWhenDone = true;
        MarginText = 10.ToString(CultureInfo.CurrentCulture);
        OutputPath = string.Empty;
        if (initial is not null)
        {
            AddPaths(initial);
        }

        Check();
    }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<FromImagesItemViewModel> Items { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitImage), nameof(IsA4Fit), nameof(IsA4Margins))]
    public partial ImagePageSize PageSize { get; set; } = ImagePageSize.FitImage;

    public bool IsFitImage
    {
        get => PageSize == ImagePageSize.FitImage;
        set => Choose(ImagePageSize.FitImage, value);
    }

    public bool IsA4Fit
    {
        get => PageSize == ImagePageSize.A4Fit;
        set => Choose(ImagePageSize.A4Fit, value);
    }

    public bool IsA4Margins
    {
        get => PageSize == ImagePageSize.A4Margins;
        set => Choose(ImagePageSize.A4Margins, value);
    }

    /// <summary>The margin of "A4 có lề", in millimetres (0–50).</summary>
    [ObservableProperty]
    public partial string MarginText { get; set; }

    [ObservableProperty]
    public partial string OutputPath { get; set; }

    [ObservableProperty]
    public partial bool OpenWhenDone { get; set; }

    /// <summary>"12 ảnh".</summary>
    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? ErrorText { get; private set; }

    public FromImagesDialogResult? Result { get; private set; }

    /// <summary>
    /// Adds the pictures among <paramref name="paths"/> (other files are left out) in natural order, at the end or at
    /// <paramref name="index"/>; the first ones added propose the PDF's name and folder.
    /// </summary>
    public void AddPaths(IEnumerable<string> paths, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var pictures = paths.Where(IsPicture).Order(NaturalComparer.Instance).ToList();
        var position = Math.Clamp(index ?? Items.Count, 0, Items.Count);
        foreach (var path in pictures)
        {
            Items.Insert(position++, new FromImagesItemViewModel(path, new FileInfo(path).Length));
        }

        if (string.IsNullOrWhiteSpace(OutputPath) && Items.Count > 0 && System.IO.Path.GetDirectoryName(Items[0].Path) is { Length: > 0 } folder)
        {
            OutputPath = System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(Items[0].Path) + ".pdf");
        }
    }

    /// <summary>The page size chosen, or null with <paramref name="error"/> (the margin out of range).</summary>
    public FromImagesParams? Parameters(out string? error)
    {
        error = null;
        if (PageSize != ImagePageSize.A4Margins)
        {
            return new FromImagesParams(PageSize);
        }

        if (!double.TryParse(MarginText?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var margin) || margin is < 0 or > 50)
        {
            error = Strings.FromImages_Error_Margin;
            return null;
        }

        return new FromImagesParams(PageSize, margin);
    }

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        ArgumentNullException.ThrowIfNull(dropInfo);
        if (dropInfo.Data is FromImagesItemViewModel)
        {
            dropInfo.Effects = DragDropEffects.Move;
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
        }
        else if (DroppedFiles(dropInfo).Any(IsPicture))
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
        if (dropInfo.Data is FromImagesItemViewModel item && Items.IndexOf(item) is var from and >= 0)
        {
            var to = Math.Clamp(dropInfo.InsertIndex, 0, Items.Count);
            Items.Move(from, to > from ? to - 1 : to);
            return;
        }

        AddPaths(DroppedFiles(dropInfo), dropInfo.InsertIndex);
    }

    partial void OnPageSizeChanged(ImagePageSize value) => Check();

    partial void OnMarginTextChanged(string value) => Check();

    partial void OnOutputPathChanged(string value) => Check();

    [RelayCommand]
    private void AddFiles() => AddPaths(_files.PickImageFiles(Strings.FromImages_Pick_Title));

    /// <summary>"Thêm thư mục": every picture directly in a folder (scans saved one per page).</summary>
    [RelayCommand]
    private void AddFolder()
    {
        if (_files.PickFolder(Strings.FromImages_Folder_Pick_Title, Items.Count > 0 ? System.IO.Path.GetDirectoryName(Items[^1].Path) : null) is not { } folder)
        {
            return;
        }

        try
        {
            AddPaths(Directory.EnumerateFiles(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = Strings.FromImages_Error_Folder;
        }
    }

    [RelayCommand]
    private void Remove(FromImagesItemViewModel? item)
    {
        if (item is not null)
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void MoveUp(FromImagesItemViewModel? item)
    {
        if (item is not null && Items.IndexOf(item) is var index and > 0)
        {
            Items.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveDown(FromImagesItemViewModel? item)
    {
        if (item is not null && Items.IndexOf(item) is var index && index >= 0 && index < Items.Count - 1)
        {
            Items.Move(index, index + 1);
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
        var folder = string.IsNullOrWhiteSpace(OutputPath) ? null : System.IO.Path.GetDirectoryName(OutputPath);
        var name = string.IsNullOrWhiteSpace(OutputPath) ? Strings.FromImages_DefaultName + ".pdf" : System.IO.Path.GetFileName(OutputPath);
        if (_files.PickSavePath(name, folder) is { } path)
        {
            OutputPath = path;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (Parameters(out _) is not { } parameters || ErrorText is not null)
        {
            return;
        }

        Result = new FromImagesDialogResult([.. Items.Select(i => i.Path)], parameters, System.IO.Path.GetFullPath(OutputPath.Trim()), OpenWhenDone);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanRun() => ErrorText is null;

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, false);
    }

    private void Choose(ImagePageSize size, bool chosen)
    {
        if (chosen)
        {
            PageSize = size;
        }
    }

    private void Check()
    {
        SummaryText = string.Format(CultureInfo.CurrentCulture, Strings.FromImages_Summary, Items.Count);
        _ = Parameters(out var error);
        ErrorText = Items.Count == 0 ? Strings.FromImages_Error_None
            : error ?? (IsUsablePath(OutputPath) ? null : Strings.FromImages_Error_Output);
    }

    private static bool IsPicture(string path)
        => FromImagesOperation.Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()) && File.Exists(path);

    private static IEnumerable<string> DroppedFiles(IDropInfo dropInfo)
        => dropInfo.Data is IDataObject data && data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    private static bool IsUsablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return System.IO.Path.IsPathFullyQualified(path.Trim()) && path.Trim().IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0
                && string.Equals(System.IO.Path.GetExtension(path.Trim()), ".pdf", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
