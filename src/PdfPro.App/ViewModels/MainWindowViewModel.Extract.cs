using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Extracting;
using PdfPro.Operations.Extracting;

namespace PdfPro.App.ViewModels;

/// <summary>"Trích bảng" (extract.tables), "Trích ảnh" (extract.images), "Tệp đính kèm" (extract.attachments), "Phông chữ" (extract.fonts).</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The table window on the active document as it is shown (a copy is written when it has changes not saved), its
    /// tables looked for at once in the background. A protected document is refused, as by the other exports.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExtractTables))]
    private async Task ExtractTablesAsync()
    {
        if (_extracting is not { } extracting || ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.Tables_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var (input, workingFolder) = await DocumentFileAsync(document, "tables");
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.Tables_Title, Strings.Optimize_Error_Unreadable);
                return;
            }

            var folder = Path.GetDirectoryName(document.FilePath) is { Length: > 0 } near ? near : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var source = new TablesSource(document.FileName, input, document.Viewer.PageCount, document.Viewer.CurrentPageIndex, folder);
            await using var dialog = new TablesViewModel(source, extracting, _fileDialogs, _shell, _logger);
            _ = dialog.FindCommand.ExecuteAsync(null);
            _pageDialogs.ShowTables(dialog);
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }
    }

    private bool CanExtractTables() => _extracting is not null && CanLinearize();

    /// <summary>
    /// "Trích ảnh": the dialog (pages, smallest size, repeats, folder), then the images of the document as it is shown
    /// written in the background as stored; what was left out is said, and the folder opens when asked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExtractImages))]
    private async Task ExtractImagesAsync()
    {
        if (_extracting is not { Images: { } images } || ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.ExtractImages_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(document.FileName);
        var near = Path.GetDirectoryName(document.FilePath) is { Length: > 0 } folder ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        IReadOnlyList<int> selected = document.IsOrganizing && document.Organizer is { } organizer ? organizer.SelectedIndices : [];
        var dialog = new ExtractImagesViewModel(document.FileName, document.Viewer.PageCount, selected, Path.Combine(near, Format(Strings.ExtractImages_Folder_Default, stem)), _fileDialogs);
        if (!_pageDialogs.ShowExtractImages(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        var (input, workingFolder) = await DocumentFileAsync(document, "images");
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.ExtractImages_Title, Strings.Optimize_Error_Unreadable);
                return;
            }

            ExtractImagesResult? result = null;
            var done = await RunOptimizingAsync(
                Format(Strings.ExtractImages_Progress, document.FileName),
                async (progress, token) => result = await images.WriteAsync(input, null, choice.OutputFolder, stem, choice.Parameters, progress, token),
                Format(Strings.ExtractImages_Done, choice.OutputFolder),
                Strings.ExtractImages_Title);
            if (!done || result is null)
            {
                return;
            }

            _logger.LogInformation("Images were taken out: {Count} written, {Duplicates} repeats, {Small} small, {Unreadable} unreadable", result.Files.Count, result.Duplicates, result.TooSmall, result.Unreadable);
            await _messages.ShowInfoAsync(Strings.ExtractImages_Title, ExtractImagesMessage(result));
            if (choice.OpenFolder && result.Files.Count > 0)
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

    private bool CanExtractImages() => _extracting?.Images is not null && CanLinearize();

    /// <summary>
    /// "Tệp đính kèm": the attachments of the document as it is shown, listed in a dialog where they are saved, opened
    /// (PDF only) or changed; changes become the next revision of the document (one undoable step).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowAttachments))]
    private async Task AttachmentsAsync()
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var (input, workingFolder) = document.Workspace.Protection is null ? await DocumentFileAsync(document, "attachments") : (null, null);
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.Attachments_Title, document.Workspace.Protection is null ? Strings.Optimize_Error_Unreadable : Strings.Optimize_Error_Protected);
                return;
            }

            var canChange = !document.IsReadOnly && document.CanChangeAttachments;
            var dialog = new AttachmentsViewModel(document.FileName, input, canChange, Path.Combine(_paths.CacheDirectory, "attachments"), _fileDialogs, _logger);
            await dialog.LoadAsync();
            if (_pageDialogs.ShowAttachments(dialog) && dialog.Result is { } changes && Tabs.Contains(document))
            {
                document.EditAttachments(changes);
            }

            if (dialog.OpenAfterClose is { } attachment && File.Exists(attachment))
            {
                await OpenFilesAsync([attachment], pdfOnly: true);
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

    private bool CanShowAttachments() => HasActiveDocument && _activeSave is null && !StatusBar.IsTaskRunning;

    /// <summary>"Phông chữ": the fonts of the document as it is shown, and whether they are embedded.</summary>
    [RelayCommand(CanExecute = nameof(CanShowFonts))]
    private async Task FontsAsync()
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        if (document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.Fonts_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var (input, workingFolder) = await DocumentFileAsync(document, "fonts");
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.Fonts_Title, Strings.Optimize_Error_Unreadable);
                return;
            }

            var dialog = new FontsViewModel(document.FileName, input, _logger);
            await dialog.LoadAsync();
            _pageDialogs.ShowFonts(dialog);
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }
    }

    private bool CanShowFonts() => HasActiveDocument && _activeSave is null && !StatusBar.IsTaskRunning;

    /// <summary>"Đã lưu 12 ảnh." and what was left out.</summary>
    private static string ExtractImagesMessage(ExtractImagesResult result)
    {
        var text = new System.Text.StringBuilder(result.Files.Count == 0 ? Strings.ExtractImages_None : Format(Strings.ExtractImages_Written, result.Files.Count));
        if (result.Duplicates > 0)
        {
            text.Append(' ').Append(Format(Strings.ExtractImages_Duplicates, result.Duplicates));
        }

        if (result.TooSmall > 0)
        {
            text.Append(' ').Append(Format(Strings.ExtractImages_TooSmall, result.TooSmall));
        }

        if (result.Unreadable > 0)
        {
            text.Append(' ').Append(Format(Strings.ExtractImages_Unreadable, result.Unreadable));
        }

        return text.ToString();
    }
}
