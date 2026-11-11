using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Structure;
using PdfPro.Operations.Structure;

namespace PdfPro.App.ViewModels;

/// <summary>
/// The structure of the active document: "Bookmark" (structure.bookmarks), "Thuộc tính tài liệu" (structure.metadata),
/// "Nhãn trang" (structure.pageLabels), "Liên kết" (structure.links). Each reads the document as it is shown, opens its
/// dialog, and writes what was confirmed as the next revision (one undoable step).
/// </summary>
public sealed partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanChangeStructure))]
    private Task BookmarksAsync()
        => WithStructureAsync(Strings.Bookmarks_Title, "bookmarks", async (document, input) =>
        {
            var bookmarks = await Task.Run(() => DocumentStructureReader.ReadBookmarks(input));
            var dialog = new BookmarksEditorViewModel(document.FileName, input, document.Viewer.PageCount, document.Viewer.CurrentPageIndex, bookmarks, _logger);
            if (_pageDialogs.ShowBookmarks(dialog) && dialog.Result is { } result)
            {
                document.ChangeStructure<SetBookmarksOperation>(result);
            }
        });

    [RelayCommand(CanExecute = nameof(CanChangeStructure))]
    private Task DocumentDescriptionAsync()
        => WithStructureAsync(Strings.DocInfo_Title, "description", async (document, input) =>
        {
            var structure = await Task.Run(() => DocumentStructureReader.Read(input));
            var dialog = new DocumentDescriptionViewModel(document.FileName, structure);
            if (_pageDialogs.ShowDocumentDescription(dialog) && dialog.Result is { } result)
            {
                document.ChangeStructure<SetMetadataOperation>(result);
            }
        });

    [RelayCommand(CanExecute = nameof(CanChangeStructure))]
    private Task PageLabelsAsync()
        => WithStructureAsync(Strings.Labels_Title, "labels", async (document, input) =>
        {
            var labels = await Task.Run(() => DocumentStructureReader.ReadLabels(input));
            var dialog = new PageLabelsViewModel(document.FileName, document.Viewer.PageCount, document.Viewer.CurrentPageIndex, labels);
            if (_pageDialogs.ShowPageLabels(dialog) && dialog.Result is { } result)
            {
                document.ChangeStructure<SetPageLabelsOperation>(result);
            }
        });

    [RelayCommand(CanExecute = nameof(CanChangeStructure))]
    private Task LinksAsync()
        => WithStructureAsync(Strings.Links_Title, "links", async (document, input) =>
        {
            var structure = await Task.Run(() => DocumentStructureReader.Read(input));
            var dialog = new LinksViewModel(document.FileName, input, structure.Links, _logger);
            if (_pageDialogs.ShowLinks(dialog) && dialog.Result is { } result)
            {
                document.ChangeStructure<EditLinksOperation>(result);
            }
        });

    private bool CanChangeStructure() => ActiveDocument is { IsReadOnly: false, CanChangeAttachments: true } && _activeSave is null && !StatusBar.IsTaskRunning;

    /// <summary>The document as it is shown, written to a file when it has unsaved changes; a protected one is refused.</summary>
    private async Task WithStructureAsync(string title, string purpose, Func<DocumentTabViewModel, string, Task> work)
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var (input, workingFolder) = await DocumentFileAsync(document, purpose);
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(title, Strings.Optimize_Error_Unreadable);
                return;
            }

            await work(document, input);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The structure of a document could not be read: {ErrorType}", ex.GetType().Name);
            await _messages.ShowErrorAsync(title, Strings.Optimize_Error_Unreadable);
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }
    }
}
