using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Scanning;
using PdfPro.Operations.Splitting;

namespace PdfPro.App.ViewModels;

/// <summary>The scan tools of the ribbon: blank pages, turned pages, tilted pages, dark borders.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// "Xóa trang trắng": the document as displayed is looked at in the background; the blank pages found are listed,
    /// then removed (one undoable page step) or shown selected in the organizer to look at first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task RemoveBlankPagesAsync()
    {
        if (_scan is not { } scan || ActiveDocument is not { } document)
        {
            return;
        }

        var shares = await MeasureAsync(document, "blank", Strings.Scan_Blank_Progress, (path, progress, token) => RemoveBlankPagesOperation.MeasureAsync(scan.Gate, path, progress, token));
        if (shares is null || !Tabs.Contains(document))
        {
            return;
        }

        var blank = BlankPageDetector.BlankPages(shares, BlankPageDetector.DefaultThresholdPercent).Order().ToList();
        if (blank.Count == 0)
        {
            await _messages.ShowInfoAsync(Strings.Scan_Blank_Title, Strings.Scan_Blank_None);
            return;
        }

        if (blank.Count == shares.Count)
        {
            await _messages.ShowInfoAsync(Strings.Scan_Blank_Title, Strings.Scan_Blank_All);
            return;
        }

        var choice = await _messages.AskAsync(Strings.Scan_Blank_Title, Format(Strings.Scan_Blank_Found, blank.Count, PageRanges.Format(blank)), Strings.Scan_Blank_Remove, Strings.Scan_ShowInOrganizer, Strings.Common_Cancel);
        if (choice == DialogChoice.Primary)
        {
            document.Apply(scan.RemoveBlankPages, new RemoveBlankPagesParams(PageRanges.Format(blank)));
        }
        else if (choice == DialogChoice.Secondary)
        {
            ShowInOrganizer(document, blank);
        }
    }

    /// <summary>
    /// "Xoay đúng chiều": pages scanned sideways or upside down are found (OCR's orientation detection, checked by
    /// reading when unsure), listed, then turned (one undoable page step) or shown in the organizer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task AutoRotateAsync()
    {
        if (_scan is not { } scan || ActiveDocument is not { } document)
        {
            return;
        }

        if (scan.Ocr?.Describe() is not { DetectsOrientation: true })
        {
            await _messages.ShowErrorAsync(Strings.Scan_Rotate_Title, Strings.Scan_Rotate_NoEngine);
            return;
        }

        var languages = _settings.Current.OcrLanguages;
        var workers = _settings.Current.OcrParallelism > 0 ? _settings.Current.OcrParallelism : Math.Max(1, Environment.ProcessorCount - 1);
        var turns = await MeasureAsync(document, "rotate", Strings.Scan_Rotate_Progress, (path, progress, token) => AutoRotateOperation.DetectAsync(scan.Gate, scan.Ocr!, path, languages, workers, progress, token));
        if (turns is null || !Tabs.Contains(document))
        {
            return;
        }

        if (turns.Count == 0)
        {
            await _messages.ShowInfoAsync(Strings.Scan_Rotate_Title, Strings.Scan_Rotate_None);
            return;
        }

        var list = string.Join(", ", turns.Select(t => Format(Strings.Scan_Rotate_Item, t.Page + 1, t.Degrees)));
        var choice = await _messages.AskAsync(Strings.Scan_Rotate_Title, Format(Strings.Scan_Rotate_Found, turns.Count, list), Strings.Scan_Rotate_Apply, Strings.Scan_ShowInOrganizer, Strings.Common_Cancel);
        if (choice == DialogChoice.Primary)
        {
            document.Apply(scan.AutoRotate, new AutoRotateParams(turns));
        }
        else if (choice == DialogChoice.Secondary)
        {
            ShowInOrganizer(document, [.. turns.Select(t => t.Page)]);
        }
    }

    /// <summary>"Làm thẳng trang": tilted pages are measured, listed, then straightened (one undoable content change).</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task DeskewAsync()
    {
        if (_scan is not { } scan || ActiveDocument is not { } document)
        {
            return;
        }

        var angles = await MeasureAsync(document, "deskew", Strings.Scan_Deskew_Progress, (path, progress, token) => DeskewOperation.MeasureAsync(scan.Gate, path, null, DeskewOperation.DefaultMinDegrees, progress, token));
        if (angles is null || !Tabs.Contains(document))
        {
            return;
        }

        if (angles.Count == 0)
        {
            await _messages.ShowInfoAsync(Strings.Scan_Deskew_Title, Strings.Scan_Deskew_None);
            return;
        }

        var list = string.Join(", ", angles.Select(a => Format(Strings.Scan_Deskew_Item, a.Page + 1, Math.Abs(a.Degrees))));
        var choice = await _messages.AskAsync(Strings.Scan_Deskew_Title, Format(Strings.Scan_Deskew_Found, angles.Count, list), Strings.Scan_Deskew_Apply, Strings.Scan_ShowInOrganizer, Strings.Common_Cancel);
        if (choice == DialogChoice.Primary)
        {
            var parameters = new DeskewParams(angles);
            await ChangeContentAsync(document, new ContentChangeRequest(scan.Deskew, parameters, document.Workspace.Describe(new Core.Sessions.HistoryRecord(DeskewOperation.OperationId, parameters, []), document.Workspace.Session.Plan)));
        }
        else if (choice == DialogChoice.Secondary)
        {
            ShowInOrganizer(document, [.. angles.Select(a => a.Page)]);
        }
    }

    /// <summary>"Xóa viền đen": dark bands along the edges are measured, listed, then painted white (one undoable content change).</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task CleanBordersAsync()
    {
        if (_scan is not { } scan || ActiveDocument is not { } document)
        {
            return;
        }

        var borders = await MeasureAsync(document, "borders", Strings.Scan_Borders_Progress, (path, progress, token) => CleanBordersOperation.MeasureAsync(scan.Gate, path, null, progress, token));
        if (borders is null || !Tabs.Contains(document))
        {
            return;
        }

        if (borders.Count == 0)
        {
            await _messages.ShowInfoAsync(Strings.Scan_Borders_Title, Strings.Scan_Borders_None);
            return;
        }

        var pages = borders.Select(b => b.Page).ToList();
        var choice = await _messages.AskAsync(Strings.Scan_Borders_Title, Format(Strings.Scan_Borders_Found, pages.Count, PageRanges.Format(pages)), Strings.Scan_Borders_Apply, Strings.Scan_ShowInOrganizer, Strings.Common_Cancel);
        if (choice == DialogChoice.Primary)
        {
            var parameters = new CleanBordersParams(borders);
            await ChangeContentAsync(document, new ContentChangeRequest(scan.CleanBorders, parameters, document.Workspace.Describe(new Core.Sessions.HistoryRecord(CleanBordersOperation.OperationId, parameters, []), document.Workspace.Session.Plan)));
        }
        else if (choice == DialogChoice.Secondary)
        {
            ShowInOrganizer(document, pages);
        }
    }

    private bool CanScan() => _scan is not null && ActiveDocument is { CanEditPages: true } && _activeSave is null && !StatusBar.IsTaskRunning;

    /// <summary>
    /// Writes the document as displayed (when it has changes) and measures it in the background, with progress and
    /// cancel in the status bar; null when it could not be read, was cancelled or failed (the user is told).
    /// </summary>
    private async Task<T?> MeasureAsync<T>(DocumentTabViewModel document, string purpose, string text, Func<string, IProgress<OperationProgress>, CancellationToken, Task<T>> measure)
        where T : class
    {
        var (input, workingFolder) = await DocumentFileAsync(document, purpose);
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.Scan_Error_Title, Strings.Optimize_Error_Unreadable);
                return null;
            }

            var task = StatusBar.StartTask(Format(text, document.FileName));
            var progress = new Progress<OperationProgress>(p => task.Report(p.Fraction, Format(text, document.FileName)));
            NotifyScanCommands();
            try
            {
                var result = await measure(input, progress, task.Token);
                task.Complete(string.Empty);
                return result;
            }
            catch (OperationCanceledException)
            {
                task.Complete(Strings.Scan_Cancelled);
                return null;
            }
            catch (Exception ex) when (ex is OcrException or Pdfium.PdfDocumentException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Looking at the pages for {Purpose} failed", purpose);
                task.Complete(Strings.Scan_Failed);
                await _messages.ShowErrorAsync(Strings.Scan_Error_Title, ex is OcrException ocr ? DocumentTabViewModel.OcrFailureMessage(ocr)! : Strings.Optimize_Error_Unreadable);
                return null;
            }
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }

            NotifyScanCommands();
        }
    }

    /// <summary>The organizer, with the pages found selected and the first of them shown.</summary>
    private static void ShowInOrganizer(DocumentTabViewModel document, IReadOnlyList<int> pages)
    {
        document.IsOrganizing = true;
        document.Organizer?.Select(pages, pages.Count > 0 ? pages[0] : null);
    }

    private void NotifyScanCommands()
    {
        RemoveBlankPagesCommand.NotifyCanExecuteChanged();
        AutoRotateCommand.NotifyCanExecuteChanged();
        DeskewCommand.NotifyCanExecuteChanged();
        CleanBordersCommand.NotifyCanExecuteChanged();
    }
}
