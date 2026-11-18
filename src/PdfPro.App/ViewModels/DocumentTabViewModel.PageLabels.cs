using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.Core.Structure;
using PdfPro.Operations.Structure;

namespace PdfPro.App.ViewModels;

/// <summary>
/// The page labels of the file ("i, ii, 1, 2…", "Phụ lục 1") in the viewer's page box. They are read in the
/// background from the base file (again after each revision) and shown while the pages keep the order they describe —
/// saving drops them otherwise (PagePlan.KeepsPageOrder).
/// </summary>
public sealed partial class DocumentTabViewModel
{
    private string? _labelsPath;
    private IReadOnlyList<PageLabelRange> _fileLabels = [];

    /// <summary>Reads the labels when the base file changed, and shows them when the pages are in their order.</summary>
    private void UpdatePageLabels()
    {
        var path = Workspace.SourcePathOf(Workspace.BaseDocument);
        if (!string.Equals(path, _labelsPath, StringComparison.Ordinal))
        {
            _labelsPath = path;
            _fileLabels = [];
            if (path is not null)
            {
                _ = ReadPageLabelsAsync(path);
            }
        }

        Viewer.PageLabels = Workspace.Session.Plan.KeepsPageOrder(Workspace.Session.BaseSourceId, Workspace.BaseDocument.PageCount)
            ? _fileLabels
            : [];
    }

    private async Task ReadPageLabelsAsync(string path)
    {
        try
        {
            var labels = await Task.Run(() => DocumentStructureReader.ReadLabels(path));
            if (string.Equals(path, _labelsPath, StringComparison.Ordinal) && labels.Count > 0)
            {
                _fileLabels = labels;
                UpdatePageLabels();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Pages are numbered instead: nothing the user needs to hear about.
            _logger.LogInformation("The page labels could not be read: {ErrorType}", ex.GetType().Name);
        }
    }
}
