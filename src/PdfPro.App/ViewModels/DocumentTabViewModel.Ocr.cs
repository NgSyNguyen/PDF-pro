using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels.Ocr;
using PdfPro.Core.Ocr;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Operations.Ocr;

namespace PdfPro.App.ViewModels;

/// <summary>"Nhận dạng chữ (OCR)" (ocr.run) of a document tab.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>
    /// "Nhận dạng chữ": the engine must be installed; the run is set up in a dialog (the languages and the number of
    /// pages read at once are remembered), then the window writes the recognised document as the next revision (one
    /// undoable step) with the time left in the status bar, and the report says what was read — and, when asked, the
    /// text and the hOCR are saved where the user chooses.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRecognize))]
    private async Task OcrAsync()
    {
        if (_services.Ocr is not { } ocr)
        {
            return;
        }

        if (ocr.Engine.Describe() is not { } engine)
        {
            await _messages.ShowErrorAsync(Strings.Ocr_Title, Strings.Ocr_Error_NotInstalled);
            return;
        }

        var settings = ocr.Settings?.Current;
        IReadOnlyList<int> selected = IsOrganizing && Organizer is { } organizer ? organizer.SelectedIndices : [];
        var dialog = new OcrViewModel(FileName, Viewer.PageCount, selected, engine, settings?.OcrLanguages ?? [], settings?.OcrParallelism ?? 0);
        if (!_services.PageDialogs.ShowOcr(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        await RememberAsync(ocr, choice.Parameters);
        var parameters = choice.Parameters;
        var description = Workspace.Describe(new HistoryRecord(OcrOperation.OperationId, parameters, []), Workspace.Session.Plan);
        RequestContentChange(new ContentChangeRequest(_operations.Get<OcrOperation>(), parameters, description, applied =>
        {
            if (applied && OcrOperation.ReportOf(parameters) is { } report)
            {
                _ = ShowOcrReportAsync(report, choice);
            }
        })
        {
            ShowsTimeLeft = true,
            Explain = OcrFailureMessage,
        });
    }

    private bool CanRecognize() => _services.Ocr is not null && CanChangeContent();

    /// <summary>The report in a message; then the text and the hOCR are saved when they were asked for.</summary>
    internal async Task ShowOcrReportAsync(OcrReport report, OcrDialogResult choice)
    {
        var text = DescribeOcr(report);
        if (report.Recognized.Any())
        {
            await _messages.ShowInfoAsync(Strings.Ocr_Done_Title, text);
        }
        else
        {
            await _messages.ShowErrorAsync(Strings.Ocr_Done_Title, text);
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(FileName);
        if (choice.SaveText)
        {
            await SaveOcrExportAsync(stem, hocr: false, OcrExports.Text(report));
        }

        if (choice.SaveHocr && OcrExports.Hocr(report) is { } hocr)
        {
            await SaveOcrExportAsync(stem, hocr: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(hocr));
        }
    }

    /// <summary>
    /// The report for the user: pages read and their mean confidence, then the pages worth checking (low confidence),
    /// read turned, straightened, blank, with text already, without words, and failed (pages numbered from 1).
    /// </summary>
    internal static string DescribeOcr(OcrReport report)
    {
        var culture = CultureInfo.CurrentCulture;
        var recognized = report.Recognized.ToList();
        var lines = new List<string>();
        if (recognized.Count == 0)
        {
            lines.Add(Strings.Ocr_Report_Nothing);
        }
        else
        {
            var confidence = recognized.Average(p => p.MeanConfidence ?? 0);
            lines.Add(string.Format(culture, Strings.Ocr_Report_Recognized, recognized.Count, confidence, recognized.Sum(p => p.Words), report.Duration.TotalSeconds));
        }

        void Pages(IEnumerable<OcrPageReport> pages, string template)
        {
            var indices = pages.Select(p => p.PageIndex).ToList();
            if (indices.Count > 0)
            {
                lines.Add("• " + string.Format(culture, template, PageRanges.Format(indices)));
            }
        }

        Pages(report.LowConfidence, string.Format(culture, Strings.Ocr_Report_LowConfidence, report.ConfidenceThreshold, "{0}"));
        Pages(report.Turned, Strings.Ocr_Report_Turned);
        Pages(report.Pages.Where(p => report.StraightenedPages.Contains(p.PageIndex)), Strings.Ocr_Report_Straightened);
        Pages(report.Pages.Where(p => report.RemovedHiddenText.Contains(p.PageIndex)), Strings.Ocr_Report_Replaced);
        Pages(report.Pages.Where(p => p.Outcome == OcrPageOutcome.HadText), Strings.Ocr_Report_HadText);
        Pages(report.Pages.Where(p => p.Outcome == OcrPageOutcome.Blank), Strings.Ocr_Report_Blank);
        Pages(report.Pages.Where(p => p.Outcome == OcrPageOutcome.NoText), Strings.Ocr_Report_NoText);
        Pages(report.Pages.Where(p => p.Outcome == OcrPageOutcome.Failed), Strings.Ocr_Report_Failed);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Why OCR failed, for the user; null for failures that are not the engine's.</summary>
    internal static string? OcrFailureMessage(Exception error) => error switch
    {
        OcrException { Failure: OcrFailure.NotInstalled } => Strings.Ocr_Error_NotInstalled,
        OcrException { Failure: OcrFailure.LanguageMissing } ocr => string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Error_LanguageMissing, OcrViewModel.LanguageName(ocr.Detail ?? string.Empty)),
        OcrException { Failure: OcrFailure.TimedOut } => Strings.Ocr_Error_TimedOut,
        OcrException => Strings.Ocr_Error_Failed,
        _ => null,
    };

    /// <summary>The languages and the number of pages read at once are proposed next time.</summary>
    private async Task RememberAsync(Services.Ocr.OcrServices ocr, OcrParams parameters)
    {
        if (ocr.Settings is not { } settings)
        {
            return;
        }

        try
        {
            await settings.UpdateAsync(s => s with { OcrLanguages = parameters.Languages, OcrParallelism = parameters.Parallelism });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The OCR choices could not be remembered");
        }
    }

    private async Task SaveOcrExportAsync(string stem, bool hocr, byte[] content)
    {
        if (_services.FileDialogs.PickOcrExportPath(stem, hocr) is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "What OCR recognised could not be saved ({Kind})", hocr ? "hOCR" : "text");
            await _messages.ShowErrorAsync(Strings.Ocr_Done_Title, string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Error_Save, Path.GetFileName(path)));
        }
    }
}
