using System.IO;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.ViewModels.Comparing;

namespace PdfPro.App.ViewModels;

/// <summary>"So sánh tài liệu" (compare.documents).</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The comparison window, with the active document as the old version (as it is shown: a copy is written when it has
    /// changes not saved; a protected one is its file, whose password the window asks for) and another open document,
    /// saved, as the new one. A report it wrote can be opened when it closes. Needs no open document.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCompareDocuments))]
    private async Task CompareDocumentsAsync()
    {
        if (_compare is not { } services)
        {
            return;
        }

        ComparedDocument? old = null;
        string? workingFolder = null;
        if (ActiveDocument is { } document)
        {
            if (document.IsReadOnly || document.Workspace.Protection is not null)
            {
                old = File.Exists(document.FilePath) ? new ComparedDocument(document.FilePath, document.FileName) : null;
            }
            else
            {
                if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
                {
                    return;
                }

                (var path, workingFolder) = await DocumentFileAsync(document, "compare");
                old = path is null ? null : new ComparedDocument(path, document.FileName);
            }
        }

        var other = Documents.FirstOrDefault(d => !ReferenceEquals(d, ActiveDocument) && !d.IsDirty && !d.HasPendingEdits && File.Exists(d.FilePath));
        var @new = other is null ? null : new ComparedDocument(other.FilePath, other.FileName);
        string? report;
        try
        {
            await using var dialog = new CompareViewModel(services, _fileDialogs, _logger, old, @new);
            _pageDialogs.ShowCompare(dialog);
            report = dialog.OpenAfterClose;
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }

        if (report is not null && File.Exists(report))
        {
            await OpenFilesAsync([report], pdfOnly: true);
        }
    }

    private bool CanCompareDocuments() => _compare is not null;
}
