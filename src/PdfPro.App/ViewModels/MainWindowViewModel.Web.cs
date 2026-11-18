using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Web;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Core.Splitting;
using PdfPro.Core.Web;

namespace PdfPro.App.ViewModels;

/// <summary>"Trang web sang PDF": a web page or an HTML file printed into a PDF by the WebView2 Runtime.</summary>
public sealed partial class MainWindowViewModel
{
    private readonly IWebPagePrinter? _web;

    /// <summary>
    /// Says how to get the WebView2 Runtime when it is missing; else the dialog (address or file, paper, margins…), where
    /// to save, the page loaded and printed in the background, and the PDF opened. Needs no open document.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertWeb))]
    private async Task WebToPdfAsync()
    {
        if (!await WebRuntimeAsync())
        {
            return;
        }

        var dialog = new WebToPdfViewModel(_fileDialogs);
        if (!_pageDialogs.ShowWebToPdf(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        var folder = choice.Page.IsFile ? Path.GetDirectoryName(choice.Page.LocalPath) : null;
        var output = _fileDialogs.PickSavePath(WebAddress.FileNameOf(choice.Page) + ".pdf", folder);
        if (output is null)
        {
            return;
        }

        if (Documents.Any(d => d.IsSameFile(output)))
        {
            await _messages.ShowErrorAsync(Strings.WebToPdf_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(output)));
            return;
        }

        var written = await PrintWebAsync([(choice.Page, output)], choice.Settings, overwrite: true, Format(Strings.WebToPdf_Done, Path.GetFileName(output)));
        if (written.Count == 1 && choice.OpenWhenDone)
        {
            await OpenFilesAsync(written);
        }
    }

    private bool CanConvertWeb() => _web is not null && _activeSave is null && !StatusBar.IsTaskRunning;

    /// <summary>
    /// HTML files given to open (Ctrl+O, dropped, from Explorer) become A4 PDFs next to them ("name.pdf", or
    /// "name (2).pdf" when that is taken), which open. The PDFs written, each at the place of its file.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ConvertWebToOpenAsync(IReadOnlyList<string> pages)
    {
        if (!await WebRuntimeAsync())
        {
            return new Dictionary<string, string>();
        }

        var jobs = new List<(Uri Page, string Output)>();
        foreach (var page in pages)
        {
            var folder = Path.GetDirectoryName(page)!;
            var name = FileNames.MakeUnique(
                [Path.GetFileNameWithoutExtension(page)],
                n => File.Exists(Path.Combine(folder, n + ".pdf")) || jobs.Any(j => string.Equals(j.Output, Path.Combine(folder, n + ".pdf"), StringComparison.OrdinalIgnoreCase)))[0];
            jobs.Add((new Uri(page), Path.Combine(folder, name + ".pdf")));
        }

        var written = await PrintWebAsync(jobs, new WebPrintSettings(), overwrite: false, Strings.Open_Web_Done);
        return pages.Select((p, i) => (Page: p, jobs[i].Output))
            .Where(p => written.Contains(p.Output, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(p => p.Page, p => p.Output, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the WebView2 Runtime prints; otherwise the user was told how to install it.</summary>
    private async Task<bool> WebRuntimeAsync()
    {
        if (_web is null)
        {
            return false;
        }

        if (_web.RuntimeVersion is null)
        {
            _logger.LogInformation("The WebView2 Runtime is not installed");
            await _messages.ShowErrorAsync(Strings.WebToPdf_Error_Title, Strings.WebToPdf_Error_NoRuntime);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Prints each page into its PDF, one after the other in the background; pages that fail are named at the end. The
    /// PDFs written (none when cancelled).
    /// </summary>
    private async Task<IReadOnlyList<string>> PrintWebAsync(IReadOnlyList<(Uri Page, string Output)> jobs, WebPrintSettings settings, bool overwrite, string doneText)
    {
        var web = _web!;
        var written = new List<string>();
        var failures = new List<(string Name, string Reason)>();
        var done = await RunOptimizingAsync(
            jobs.Count == 1 ? Strings.WebToPdf_Progress : Format(Strings.WebToPdf_ProgressMany, jobs.Count),
            async (progress, token) =>
            {
                for (var i = 0; i < jobs.Count; i++)
                {
                    var (page, output) = jobs[i];
                    try
                    {
                        var result = await web.PrintAsync(page, output, settings, overwrite, jobs.Count == 1 ? progress : null, token);
                        written.Add(result.Output);
                    }
                    catch (Exception ex) when (jobs.Count > 1 && WebFailure(ex) is { } reason)
                    {
                        _logger.LogWarning("A web page did not become a PDF: {ErrorType} {Failure}", ex.GetType().Name, (ex as WebPrintException)?.Failure);
                        failures.Add((page.IsFile ? Path.GetFileName(page.LocalPath) : page.Host, reason));
                    }

                    progress.Report(new Core.Operations.OperationProgress(i + 1, jobs.Count));
                }
            },
            doneText,
            Strings.WebToPdf_Error_Title,
            WebFailure);
        _logger.LogInformation("Web pages were printed to PDF: {Written} written, {Failed} failed", written.Count, failures.Count);
        if (done && failures.Count > 0)
        {
            await _messages.ShowErrorAsync(
                Strings.WebToPdf_Error_Title,
                Format(Strings.WebToPdf_Failed, written.Count, jobs.Count) + Environment.NewLine
                    + string.Join(Environment.NewLine, failures.Take(10).Select(f => Format(Strings.OfficeToPdf_Failed_Line, f.Name, f.Reason))));
        }

        return done ? written : [];
    }

    /// <summary>Why a page did not become a PDF, in words; null for failures explained elsewhere.</summary>
    private static string? WebFailure(Exception failure) => failure switch
    {
        WebPrintException { Failure: WebPrintFailure.RuntimeMissing } => Strings.WebToPdf_Error_NoRuntime,
        WebPrintException { Failure: WebPrintFailure.Unreachable } => Strings.WebToPdf_Error_Unreachable,
        WebPrintException { Failure: WebPrintFailure.Certificate } => Strings.WebToPdf_Error_Certificate,
        WebPrintException { Failure: WebPrintFailure.HttpError } web => Format(Strings.WebToPdf_Error_Http, web.HttpStatus),
        WebPrintException { Failure: WebPrintFailure.TimedOut } => Strings.WebToPdf_Error_TimedOut,
        WebPrintException => Strings.WebToPdf_Error_PrintFailed,
        IOException or UnauthorizedAccessException => Strings.WebToPdf_Error_Write,
        _ => null,
    };
}
