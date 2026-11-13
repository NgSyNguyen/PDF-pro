using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Security;
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

    /// <summary>
    /// "Thêm bookmark" (Ctrl+B): the bookmark editor with a new bookmark where the selected text starts, titled with its
    /// first line — or at the page shown, titled with its number — to be named and placed before it is applied.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeStructure))]
    private async Task AddBookmarkAsync()
    {
        if (ActiveDocument is not { } shown)
        {
            return;
        }

        // Read before pending edits are applied: a new revision clears the selection.
        var (title, page, top) = await ProposedBookmarkAsync(shown);
        await WithStructureAsync(Strings.Bookmarks_Title, "bookmarks", async (document, input) =>
        {
            var bookmarks = await Task.Run(() => DocumentStructureReader.ReadBookmarks(input));
            var dialog = new BookmarksEditorViewModel(document.FileName, input, document.Viewer.PageCount, page, bookmarks, _logger);
            dialog.Propose(title, page, top);
            if (_pageDialogs.ShowBookmarks(dialog) && dialog.Result is { } result)
            {
                document.ChangeStructure<SetBookmarksOperation>(result);
            }
        });
    }

    /// <summary>The title, page and height of a bookmark made from the viewer.</summary>
    private static async Task<(string Title, int Page, double? Top)> ProposedBookmarkAsync(DocumentTabViewModel document)
    {
        if (document.TextSelection.Start() is { } start)
        {
            var line = FirstLine(await document.TextSelection.GetTextAsync());
            return (line.Length > 0 ? line : Format(Strings.Bookmarks_PageOf, start.Page + 1), start.Page, start.Top);
        }

        var page = document.Viewer.CurrentPageIndex;
        return (Format(Strings.Bookmarks_PageOf, page + 1), page, null);
    }

    /// <summary>The first line of a text with its spaces made single, cut at <see cref="MaxBookmarkTitle"/> characters.</summary>
    internal static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length <= MaxBookmarkTitle ? line : line[..MaxBookmarkTitle].TrimEnd() + "…";
    }

    private const int MaxBookmarkTitle = 160;

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

    /// <summary>"Liên kết": the links of the document; a link can also be made over the text selected in the viewer.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeStructure))]
    private async Task LinksAsync()
    {
        if (ActiveDocument is not { } shown)
        {
            return;
        }

        // Read before pending edits are applied: a new revision clears the selection.
        var selection = shown.TextSelection.Areas() is { Count: > 0 } areas
            ? new LinkSelection(FirstLine(await shown.TextSelection.GetTextAsync()), areas)
            : null;
        await OpenLinksAsync(selection);
    }

    /// <summary>
    /// "Vẽ liên kết": the user drags the area of a new link on a page (a click gives an area of a short line), then the
    /// links dialog offers a link over that area. A document that cannot be changed is refused before the area is drawn.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeStructure))]
    private async Task DrawLinkAsync()
    {
        if (ActiveDocument is not { } shown)
        {
            return;
        }

        if (shown.IsReadOnly || shown.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.Links_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (await shown.PlaceAreaAsync(PlacementPurpose.Link) is not { } box || ActiveDocument != shown)
        {
            return;
        }

        var text = string.Format(CultureInfo.CurrentCulture, Strings.Links_Area, box.PageIndex + 1);
        await OpenLinksAsync(new LinkSelection(text, [(box.PageIndex, box.Rect)], IsArea: true));
    }

    /// <summary>The links dialog on the document as it is shown, offering a link over <paramref name="selection"/> when given.</summary>
    private Task OpenLinksAsync(LinkSelection? selection)
        => WithStructureAsync(Strings.Links_Title, "links", async (document, input) =>
        {
            var structure = await Task.Run(() => DocumentStructureReader.Read(input));
            var dialog = new LinksViewModel(document.FileName, input, structure.Links, _logger, document.Viewer.PageCount, selection);
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
