using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Converting;

namespace PdfPro.App.ViewModels;

/// <summary>"PDF sang ảnh" (convert.toImages).</summary>
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
}
