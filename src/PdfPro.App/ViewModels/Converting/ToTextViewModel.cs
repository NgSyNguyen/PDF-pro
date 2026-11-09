using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Operations.Converting;

namespace PdfPro.App.ViewModels.Converting;

/// <summary>What "PDF sang văn bản" was confirmed with.</summary>
/// <param name="Parameters">Format, page split and pages of convert.toText.</param>
/// <param name="OutputFolder">Where the text goes (full path).</param>
/// <param name="OpenFolder">Open the folder in Explorer when done.</param>
public sealed record ToTextDialogResult(ToTextParams Parameters, string OutputFolder, bool OpenFolder);

/// <summary>
/// "PDF sang văn bản" (convert.toText): plain text or Markdown, how pages are kept apart, the pages, and the folder —
/// with the name of what will be written.
/// </summary>
public sealed partial class ToTextViewModel : ObservableObject
{
    private readonly IFileDialogService _files;
    private readonly string _baseName;

    /// <param name="fileName">The document's file name.</param>
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="selected">Pages selected in the organizer, if any (0-based).</param>
    /// <param name="defaultFolder">The folder proposed: the document's.</param>
    /// <param name="files">Picks the folder.</param>
    public ToTextViewModel(string fileName, int pageCount, IReadOnlyList<int> selected, string defaultFolder, IFileDialogService files)
    {
        FileName = fileName;
        _baseName = Path.GetFileNameWithoutExtension(fileName);
        _files = files;
        Target = new PageTargetViewModel(pageCount, selected);
        Target.Changed += (_, _) => Check();
        OutputFolder = defaultFolder;
        Check();
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public PageTargetViewModel Target { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsMarkdown), nameof(NamesText))]
    public partial TextFileFormat Format { get; set; } = TextFileFormat.Text;

    public bool IsText
    {
        get => Format == TextFileFormat.Text;
        set => Choose(TextFileFormat.Text, value);
    }

    public bool IsMarkdown
    {
        get => Format == TextFileFormat.Markdown;
        set => Choose(TextFileFormat.Markdown, value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOneFile), nameof(IsPageBreaks), nameof(IsFilePerPage), nameof(NamesText))]
    public partial TextPageSplit Split { get; set; } = TextPageSplit.PageBreaks;

    public bool IsOneFile
    {
        get => Split == TextPageSplit.None;
        set => Choose(TextPageSplit.None, value);
    }

    public bool IsPageBreaks
    {
        get => Split == TextPageSplit.PageBreaks;
        set => Choose(TextPageSplit.PageBreaks, value);
    }

    public bool IsFilePerPage
    {
        get => Split == TextPageSplit.FilePerPage;
        set => Choose(TextPageSplit.FilePerPage, value);
    }

    /// <summary>"Sẽ ghi: hồ sơ.txt" or "Sẽ ghi: hồ sơ-001.md, hồ sơ-002.md…".</summary>
    public string NamesText
    {
        get
        {
            var extension = ToTextOperation.Extension(Format);
            var names = Split == TextPageSplit.FilePerPage ? $"{_baseName}-001{extension}, {_baseName}-002{extension}…" : _baseName + extension;
            return string.Format(CultureInfo.CurrentCulture, Strings.ToText_Names, names);
        }
    }

    [ObservableProperty]
    public partial string OutputFolder { get; set; }

    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    [ObservableProperty]
    public partial bool OpenFolderWhenDone { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; private set; }

    public ToTextDialogResult? Result { get; private set; }

    /// <summary>The confirmed choice, or null with <paramref name="error"/>.</summary>
    public ToTextDialogResult? Choice(out string? error)
    {
        error = null;
        if (Target.Pages(out var pageError) is not { } pages)
        {
            error = pageError ?? Strings.Stamp_Error_Pages;
            return null;
        }

        if (!IsUsableFolder(OutputFolder))
        {
            error = Strings.ToImages_Error_Folder;
            return null;
        }

        var parameters = new ToTextParams(Format, Split, Target.Indices.Count == Target.PageCount ? null : pages, Overwrite);
        return new ToTextDialogResult(parameters, Path.GetFullPath(OutputFolder.Trim()), OpenFolderWhenDone);
    }

    partial void OnOutputFolderChanged(string value) => Check();

    [RelayCommand]
    private void PickFolder()
    {
        if (_files.PickFolder(Strings.ToText_Folder_Pick_Title, OutputFolder) is { } folder)
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

    private void Choose(TextFileFormat format, bool chosen)
    {
        if (chosen)
        {
            Format = format;
        }
    }

    private void Choose(TextPageSplit split, bool chosen)
    {
        if (chosen)
        {
            Split = split;
        }
    }

    private void Check() => ErrorText = Choice(out var error) is null ? error : null;

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
