using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Operations.Extracting;

namespace PdfPro.App.ViewModels.Extracting;

/// <summary>What "Trích ảnh" was confirmed with.</summary>
/// <param name="Parameters">Pages, smallest size and repeats of extract.images.</param>
/// <param name="OutputFolder">Where the pictures go (full path).</param>
/// <param name="OpenFolder">Open the folder in Explorer when done.</param>
public sealed record ExtractImagesDialogResult(ExtractImagesParams Parameters, string OutputFolder, bool OpenFolder);

/// <summary>
/// "Trích ảnh" (extract.images): the pages, the smallest images worth keeping (icons and rules are not), whether repeats
/// (a logo on every page) are written once, and the folder.
/// </summary>
public sealed partial class ExtractImagesViewModel : ObservableObject
{
    private readonly IFileDialogService _files;

    /// <param name="fileName">The document's file name.</param>
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="selected">Pages selected in the organizer, if any (0-based).</param>
    /// <param name="defaultFolder">The folder proposed.</param>
    /// <param name="files">Picks the folder.</param>
    public ExtractImagesViewModel(string fileName, int pageCount, IReadOnlyList<int> selected, string defaultFolder, IFileDialogService files)
    {
        FileName = fileName;
        _files = files;
        Target = new PageTargetViewModel(pageCount, selected);
        Target.Changed += (_, _) => Check();
        OutputFolder = defaultFolder;
        MinSideText = 32.ToString(CultureInfo.CurrentCulture);
        Check();
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public PageTargetViewModel Target { get; }

    /// <summary>Images smaller than this many pixels on a side are left out.</summary>
    [ObservableProperty]
    public partial string MinSideText { get; set; }

    [ObservableProperty]
    public partial bool SkipDuplicates { get; set; } = true;

    [ObservableProperty]
    public partial string OutputFolder { get; set; }

    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    [ObservableProperty]
    public partial bool OpenFolderWhenDone { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; private set; }

    public ExtractImagesDialogResult? Result { get; private set; }

    /// <summary>The confirmed choice, or null with <paramref name="error"/>.</summary>
    public ExtractImagesDialogResult? Choice(out string? error)
    {
        error = null;
        if (Target.Pages(out var pageError) is not { } pages)
        {
            error = pageError ?? Strings.Stamp_Error_Pages;
            return null;
        }

        if (!int.TryParse(MinSideText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var minSide) || minSide is < 0 or > 10_000)
        {
            error = Strings.ExtractImages_Error_MinSide;
            return null;
        }

        if (!IsUsableFolder(OutputFolder))
        {
            error = Strings.ToImages_Error_Folder;
            return null;
        }

        var parameters = new ExtractImagesParams(Target.Indices.Count == Target.PageCount ? null : pages, minSide, SkipDuplicates, Overwrite);
        return new ExtractImagesDialogResult(parameters, Path.GetFullPath(OutputFolder.Trim()), OpenFolderWhenDone);
    }

    partial void OnOutputFolderChanged(string value) => Check();

    partial void OnMinSideTextChanged(string value) => Check();

    [RelayCommand]
    private void PickFolder()
    {
        if (_files.PickFolder(Strings.ExtractImages_Folder_Pick_Title, OutputFolder) is { } folder)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Choice(out var error) is { } choice)
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

    private void Check()
    {
        if (Target is null)
        {
            return;
        }

        ErrorText = Choice(out var error) is null ? error : null;
    }

    private static bool IsUsableFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            return Path.IsPathFullyQualified(folder.Trim()) && folder.Trim().IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
