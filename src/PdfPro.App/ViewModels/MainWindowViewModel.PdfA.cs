using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Security;

namespace PdfPro.App.ViewModels;

/// <summary>"PDF/A" (convert.toPdfA).</summary>
public sealed partial class MainWindowViewModel
{
    private const int ShownRules = 10;

    /// <summary>
    /// The dialog (level, veraPDF), where to save "{name} (PDF-A).pdf", then the copy of the document as it is shown made
    /// PDF/A in the background and, when asked, checked by veraPDF; the report says what was changed, what is left and
    /// veraPDF's verdict, and offers to open the copy.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertToPdfA))]
    private async Task ToPdfAAsync()
    {
        if (_optimizing is not { ToPdfA: { } toPdfA } || ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.PdfA_Error_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var veraPdfPath = _settings.Current.VeraPdfPath;
        var veraPdf = _optimizing.VeraPdf is null ? null : _optimizing.VeraPdfLocator?.Find(veraPdfPath);
        var signed = document.Workspace.OriginalDocument.Features.SignatureCount > 0;
        var dialog = new PdfAViewModel(document.FileName, document.Viewer.PageCount, signed, veraPdf);
        if (!_pageDialogs.ShowPdfA(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        var name = Format(Strings.PdfA_Name, Path.GetFileNameWithoutExtension(document.FileName)) + ".pdf";
        var output = _fileDialogs.PickSavePath(name, Path.GetDirectoryName(document.FilePath));
        if (output is null)
        {
            return;
        }

        if (Documents.Any(d => d.IsSameFile(output)))
        {
            await _messages.ShowErrorAsync(Strings.PdfA_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(output)));
            return;
        }

        var (input, workingFolder) = await DocumentFileAsync(document, "pdfa");
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.PdfA_Error_Title, Strings.Optimize_Error_Unreadable);
                return;
            }

            PdfAReport? report = null;
            PdfAValidation? validation = null;
            string? validationProblem = null;
            var done = await RunOptimizingAsync(
                Format(Strings.PdfA_Progress, document.FileName),
                async (progress, token) =>
                {
                    report = await toPdfA.ConvertAsync(input, null, output, choice.Parameters, token);
                    if (choice.Validate && _optimizing.VeraPdf is { } validator)
                    {
                        try
                        {
                            validation = await validator.ValidateAsync(output, choice.Parameters.Level, veraPdfPath, token);
                        }
                        catch (VeraPdfNotFoundException ex)
                        {
                            validationProblem = ex.Missing == VeraPdfMissing.Java ? Strings.PdfA_VeraPdf_NoJava : Strings.PdfA_VeraPdf_Missing;
                        }
                        catch (VeraPdfFailedException)
                        {
                            validationProblem = Strings.PdfA_VeraPdf_Failed;
                        }
                    }
                },
                Format(Strings.PdfA_Done, Path.GetFileName(output)),
                Strings.PdfA_Error_Title,
                ex => ex is DocumentPasswordException ? Strings.Optimize_Error_Protected : null);
            if (!done || report is null)
            {
                return;
            }

            _logger.LogInformation(
                "A PDF/A copy was written ({Level}): {PageCount} pages, {Changes} kinds of change, {Problems} problems left, {Pictures} pages made pictures, veraPDF {Verdict}",
                report.Level,
                report.PageCount,
                report.Changes.Count,
                report.Problems.Count,
                report.PicturePages.Count,
                validation is null ? "not run" : validation.IsCompliant ? "compliant" : "not compliant");
            if (await _messages.AskAsync(Strings.PdfA_Done_Title, PdfAMessage(Path.GetFileName(output), report, validation, validationProblem), Strings.Compress_Open, null, Strings.Common_Close) == DialogChoice.Primary)
            {
                await OpenFilesAsync([output]);
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

    private bool CanConvertToPdfA() => _optimizing?.ToPdfA is not null && CanLinearize();

    /// <summary>The report: the verdict, what is left, what was changed (metadata and output intent go without saying), and veraPDF's.</summary>
    internal static string PdfAMessage(string fileName, PdfAReport report, PdfAValidation? validation, string? validationProblem)
    {
        var level = report.Level == PdfALevel.A1B ? "PDF/A-1b" : "PDF/A-2b";
        var lines = new List<string>
        {
            report.HasProblems ? Format(Strings.PdfA_Report_Problems, fileName, level, report.Problems.Count) : Format(Strings.PdfA_Report_Clean, fileName, level),
        };
        lines.AddRange(report.Problems.Select(p => "• " + Format(Text("PdfA_Problem_" + p.Kind), Pages(p.Pages), p.Detail ?? string.Empty)));

        var changes = report.Changes.Where(c => c.Fix is not (PdfAFix.Metadata or PdfAFix.OutputIntent)).ToList();
        if (changes.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(Strings.PdfA_Report_Changes);
            lines.AddRange(changes.Select(c => "• " + Format(Text("PdfA_Fix_" + c.Fix), c.Count, Pages(report.PicturePages))));
        }

        lines.Add(string.Empty);
        if (validation is not null)
        {
            lines.Add(validation.IsCompliant ? Format(Strings.PdfA_VeraPdf_Pass, level) : Format(Strings.PdfA_VeraPdf_Fail, level, validation.Failures.Count));
            lines.AddRange(validation.Failures.Take(ShownRules).Select(f => $"• {f.Clause}-{f.Test}: {f.Description}"));
            if (validation.Failures.Count > ShownRules)
            {
                lines.Add(Format(Strings.PdfA_VeraPdf_More, validation.Failures.Count - ShownRules));
            }
        }
        else
        {
            lines.Add(validationProblem ?? Strings.PdfA_Report_NotValidated);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Text(string name) => Strings.ResourceManager.GetString(name, Strings.Culture) ?? name;

    /// <summary>"1-3, 7" for pages numbered from 1 (the report's numbering).</summary>
    private static string Pages(IEnumerable<int> pages) => PageRanges.Format(pages.Where(p => p > 0).Select(p => p - 1));
}
