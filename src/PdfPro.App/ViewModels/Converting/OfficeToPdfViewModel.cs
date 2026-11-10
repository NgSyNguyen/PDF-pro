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

/// <summary>What "Office sang PDF" was confirmed with.</summary>
/// <param name="Files">The documents, in the order they are converted.</param>
/// <param name="OutputFolder">Where the PDFs go, one per document, named after it.</param>
/// <param name="Overwrite">Replace PDFs of the same name (else "name (2).pdf").</param>
/// <param name="OpenWhenDone">Open the PDF (one document) or the folder (several) once written.</param>
public sealed record OfficeToPdfDialogResult(IReadOnlyList<string> Files, string OutputFolder, bool Overwrite, bool OpenWhenDone);

/// <summary>A document of the list.</summary>
public sealed class OfficeFileItemViewModel(string path, long size)
{
    public string Path { get; } = path;

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>"1,2 MB".</summary>
    public string SizeText { get; } = ByteSizeFormatter.Format(size, CultureInfo.CurrentCulture);
}

/// <summary>
/// "Office sang PDF" (convert.officeToPdf): the Word, Excel, PowerPoint, OpenDocument and RTF documents — added from
/// the file dialog or dropped from Explorer, removed — the folder the PDFs go to (that of the first document unless
/// chosen), and whether to replace PDFs already there. LibreOffice, which converts them, has been found before the
/// dialog opens; its place is shown.
/// </summary>
public sealed partial class OfficeToPdfViewModel : ObservableObject, IDropTarget
{
    private readonly IFileDialogService _files;

    /// <param name="files">File and folder pickers.</param>
    /// <param name="libreOffice">The soffice program that will convert (shown).</param>
    /// <param name="initial">Documents to start with (those not converted here are left out).</param>
    public OfficeToPdfViewModel(IFileDialogService files, string libreOffice, IEnumerable<string>? initial = null)
    {
        _files = files;
        LibreOfficeText = string.Format(CultureInfo.CurrentCulture, Strings.OfficeToPdf_LibreOffice, libreOffice);
        Items = [];
        Items.CollectionChanged += (_, _) => Check();
        OpenWhenDone = true;
        OutputFolder = string.Empty;
        if (initial is not null)
        {
            AddPaths(initial);
        }

        Check();
    }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<OfficeFileItemViewModel> Items { get; }

    /// <summary>"LibreOffice: C:\…\soffice.exe".</summary>
    public string LibreOfficeText { get; }

    [ObservableProperty]
    public partial string OutputFolder { get; set; }

    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    [ObservableProperty]
    public partial bool OpenWhenDone { get; set; }

    /// <summary>"3 tài liệu".</summary>
    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? ErrorText { get; private set; }

    public OfficeToPdfDialogResult? Result { get; private set; }

    /// <summary>
    /// Adds the documents among <paramref name="paths"/> that are converted here and not listed yet, in natural order,
    /// at the end or at <paramref name="index"/>; the first one added proposes its folder for the PDFs.
    /// </summary>
    public void AddPaths(IEnumerable<string> paths, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var documents = paths
            .Where(IsDocument)
            .Select(System.IO.Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)))
            .Order(NaturalComparer.Instance)
            .ToList();
        var position = Math.Clamp(index ?? Items.Count, 0, Items.Count);
        foreach (var path in documents)
        {
            Items.Insert(position++, new OfficeFileItemViewModel(path, new FileInfo(path).Length));
        }

        if (string.IsNullOrWhiteSpace(OutputFolder) && Items.Count > 0 && System.IO.Path.GetDirectoryName(Items[0].Path) is { Length: > 0 } folder)
        {
            OutputFolder = folder;
        }
    }

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        ArgumentNullException.ThrowIfNull(dropInfo);
        if (DroppedFiles(dropInfo).Any(IsDocument))
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
        AddPaths(DroppedFiles(dropInfo), dropInfo.InsertIndex);
    }

    partial void OnOutputFolderChanged(string value) => Check();

    [RelayCommand]
    private void AddFiles() => AddPaths(_files.PickOfficeFiles(Strings.OfficeToPdf_Pick_Title));

    [RelayCommand]
    private void Remove(OfficeFileItemViewModel? item)
    {
        if (item is not null)
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void PickFolder()
    {
        if (_files.PickFolder(Strings.OfficeToPdf_Folder_Pick_Title, string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder) is { } folder)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (ErrorText is not null)
        {
            return;
        }

        Result = new OfficeToPdfDialogResult([.. Items.Select(i => i.Path)], System.IO.Path.GetFullPath(OutputFolder.Trim()), Overwrite, OpenWhenDone);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanRun() => ErrorText is null;

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, false);
    }

    private void Check()
    {
        SummaryText = string.Format(CultureInfo.CurrentCulture, Strings.OfficeToPdf_Summary, Items.Count);
        ErrorText = Items.Count == 0 ? Strings.OfficeToPdf_Error_None
            : IsUsableFolder(OutputFolder) ? null : Strings.OfficeToPdf_Error_Folder;
    }

    private static bool IsDocument(string path) => OfficeToPdfOperation.IsSupported(path) && File.Exists(path);

    private static IEnumerable<string> DroppedFiles(IDropInfo dropInfo)
        => dropInfo.Data is IDataObject data && data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    private static bool IsUsableFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            return System.IO.Path.IsPathFullyQualified(folder.Trim()) && folder.Trim().IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
