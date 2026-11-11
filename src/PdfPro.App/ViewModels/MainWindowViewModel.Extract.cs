using System.IO;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Extracting;

namespace PdfPro.App.ViewModels;

/// <summary>"Trích bảng" (extract.tables).</summary>
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
}
