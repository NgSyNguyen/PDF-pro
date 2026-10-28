using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Sessions;
using PdfPro.Operations.Security;

namespace PdfPro.App.ViewModels;

/// <summary>"Làm sạch tài liệu" (security.sanitize) of a document tab.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>
    /// "Làm sạch": what to remove is chosen in a dialog, then the window writes the cleaned document as the next
    /// revision (one undoable step) and the report says what went; it can be saved as a text file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeContent))]
    private void Sanitize()
    {
        var dialog = new SanitizeViewModel(Workspace.OriginalDocument.Features.SignatureCount > 0);
        if (!_services.PageDialogs.ShowSanitize(dialog) || dialog.Result is not { } parameters)
        {
            return;
        }

        var description = Workspace.Describe(new HistoryRecord(SanitizeOperation.OperationId, parameters, []), Workspace.Session.Plan);
        RequestContentChange(new ContentChangeRequest(_operations.Get<SanitizeOperation>(), parameters, description, applied =>
        {
            if (applied && SanitizeOperation.ReportOf(parameters) is { } report)
            {
                _ = ShowSanitizeReportAsync(report);
            }
        }));
    }

    /// <summary>The report in a message, with "Lưu báo cáo…" to keep it as a text file.</summary>
    internal async Task ShowSanitizeReportAsync(SanitizeReport report)
    {
        var text = Describe(report);
        if (report.IsEmpty)
        {
            await _messages.ShowInfoAsync(Strings.Sanitize_Done_Title, text);
            return;
        }

        if (await _messages.AskAsync(Strings.Sanitize_Done_Title, text, Strings.Sanitize_SaveReport, null, Strings.Common_Close) != DialogChoice.Primary
            || _services.FileDialogs.PickReportSavePath($"{Path.GetFileNameWithoutExtension(FileName)} - {Strings.Sanitize_ReportName}") is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, $"{Strings.Sanitize_Done_Title} — {FileName}{Environment.NewLine}{text}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The sanitize report could not be saved");
            await _messages.ShowErrorAsync(Strings.Sanitize_Done_Title, Strings.Sanitize_ReportError);
        }
    }

    /// <summary>The report for the user: one line per kind of thing removed.</summary>
    internal static string Describe(SanitizeReport report)
    {
        if (report.IsEmpty && report.PagesLeftAsTheyWere.Count == 0)
        {
            return Strings.Sanitize_Done_Nothing;
        }

        var text = new StringBuilder(Strings.Sanitize_Done_Intro);
        void Line(string line) => text.AppendLine().Append("• ").Append(line);
        void Count(long count, string template)
        {
            if (count > 0)
            {
                Line(string.Format(CultureInfo.CurrentCulture, template, count));
            }
        }

        if (report.Metadata.Count > 0)
        {
            Line(string.Format(CultureInfo.CurrentCulture, Strings.Sanitize_Report_Metadata, string.Join(", ", report.Metadata)));
        }

        Count(report.MetadataStreams, Strings.Sanitize_Report_MetadataStreams);
        Count(report.Scripts, Strings.Sanitize_Report_Scripts);
        Count(report.Attachments, Strings.Sanitize_Report_Attachments);
        Count(report.RiskyActions, Strings.Sanitize_Report_RiskyActions);
        Count(report.ExternalLinks, Strings.Sanitize_Report_ExternalLinks);
        Count(report.HiddenLayers, Strings.Sanitize_Report_HiddenLayers);
        Count(report.HiddenContent, Strings.Sanitize_Report_HiddenContent);
        Count(report.OutsideObjects, Strings.Sanitize_Report_Outside);
        Count(report.Thumbnails, Strings.Sanitize_Report_Thumbnails);
        Count(report.PrivateData, Strings.Sanitize_Report_PrivateData);
        Count(report.OldRevisions, Strings.Sanitize_Report_Revisions);
        Count(report.TrailingBytes, Strings.Sanitize_Report_Trailing);
        if (report.PagesLeftAsTheyWere.Count > 0)
        {
            Line(string.Format(CultureInfo.CurrentCulture, Strings.Sanitize_Report_PagesKept, string.Join(", ", report.PagesLeftAsTheyWere)));
        }

        return text.ToString();
    }
}
