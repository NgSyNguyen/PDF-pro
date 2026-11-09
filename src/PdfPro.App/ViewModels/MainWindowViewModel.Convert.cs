using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Converting;

namespace PdfPro.App.ViewModels;

/// <summary>"PDF sang ảnh" (convert.toImages) and "Ảnh sang PDF" (convert.fromImages).</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The dialog (format, resolution, pages, folder), then the pages of the active document as it is shown written as
    /// pictures in the background; the folder opens when asked, and pages written smaller are reported.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertToImages))]
    private async Task ToImagesAsync()
    {
        if (_optimizing is not { ToImages: { } toImages } || ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.ToImages_Error_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(document.FileName);
        var near = Path.GetDirectoryName(document.FilePath) is { Length: > 0 } folder ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        IReadOnlyList<int> selected = document.IsOrganizing && document.Organizer is { } organizer ? organizer.SelectedIndices : [];
        var dialog = new ToImagesViewModel(
            document.FileName,
            document.Viewer.PageCount,
            selected,
            document.Workspace.Pages.PageSizes[0],
            Path.Combine(near, Format(Strings.ToImages_Folder_Default, stem)),
            _fileDialogs);
        if (!_pageDialogs.ShowToImages(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        var (input, workingFolder) = await DocumentFileAsync(document, "images");
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.ToImages_Error_Title, Strings.Optimize_Error_Unreadable);
                return;
            }

            ToImagesResult? result = null;
            var done = await RunOptimizingAsync(
                Format(Strings.ToImages_Progress, document.FileName),
                async (progress, token) => result = await toImages.WriteAsync(input, null, choice.OutputFolder, stem, choice.Parameters, progress, token),
                Format(Strings.ToImages_Done, choice.OutputFolder),
                Strings.ToImages_Error_Title);
            if (!done || result is null)
            {
                return;
            }

            _logger.LogInformation("Pages were written as pictures ({Format}): {PageCount} pages, {FileCount} files, {Reduced} made smaller", choice.Parameters.Format, result.PageCount, result.Files.Count, result.ReducedPages.Count);
            if (result.ReducedPages.Count > 0)
            {
                await _messages.ShowInfoAsync(Strings.ToImages_Title, Format(Strings.ToImages_Reduced, PageRanges.Format(result.ReducedPages)));
            }

            if (choice.OpenFolder)
            {
                _shell.OpenFolder(choice.OutputFolder);
            }
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }
    }

    private bool CanConvertToImages() => _optimizing?.ToImages is not null && CanLinearize();

    /// <summary>
    /// The dialog (pictures in order, page size, the PDF to write), then the PDF written in the background; a picture
    /// that cannot be used is named. Needs no open document.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertFromImages))]
    private async Task FromImagesAsync()
    {
        if (_optimizing is not { FromImages: { } fromImages })
        {
            return;
        }

        var dialog = new FromImagesViewModel(_fileDialogs);
        if (!_pageDialogs.ShowFromImages(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        if (Documents.Any(d => d.IsSameFile(choice.OutputPath)))
        {
            await _messages.ShowErrorAsync(Strings.FromImages_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(choice.OutputPath)));
            return;
        }

        FromImagesResult? result = null;
        var done = await RunOptimizingAsync(
            Format(Strings.FromImages_Progress, choice.Files.Count),
            async (progress, token) => result = await fromImages.WriteAsync(choice.Files, choice.OutputPath, choice.Parameters, progress, token),
            Format(Strings.FromImages_Done, Path.GetFileName(choice.OutputPath)),
            Strings.FromImages_Error_Title,
            ex => ex is ImageFileException picture ? PictureProblem(picture, choice.Files) : null);
        if (!done || result is null)
        {
            return;
        }

        _logger.LogInformation("Pictures were made a PDF: {PageCount} pages, {Jpegs} JPEG kept as they are", result.PageCount, result.JpegsKept);
        if (choice.OpenWhenDone)
        {
            await OpenFilesAsync([choice.OutputPath]);
        }
    }

    private bool CanConvertFromImages() => _optimizing?.FromImages is not null && _activeSave is null && !StatusBar.IsTaskRunning;

    private static string PictureProblem(ImageFileException failure, IReadOnlyList<string> files)
    {
        var name = failure.FileIndex >= 0 && failure.FileIndex < files.Count ? Path.GetFileName(files[failure.FileIndex]) : string.Empty;
        return Format(
            failure.Problem switch
            {
                ImageFileProblem.NotSupported => Strings.FromImages_Error_NotSupported,
                ImageFileProblem.TooLarge => Strings.FromImages_Error_TooLarge,
                _ => Strings.FromImages_Error_Unreadable,
            },
            name);
    }
}
