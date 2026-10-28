using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Overlay;
using PdfPro.Core.Sessions;
using PdfPro.Operations.Security;

namespace PdfPro.App.ViewModels;

/// <summary>"Xóa thông tin" (security.redact) of a document tab.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>"Xóa thông tin": marks to redact on the pages, applied as one content change.</summary>
    public RedactionViewModel Redaction { get; }

    /// <summary>The redaction mode is on (the ribbon button shows it pressed).</summary>
    public bool IsRedacting => Redaction.IsActive;

    /// <summary>
    /// "Xóa thông tin": turns the redaction mode on or off. It takes the viewer's mouse, like the other modes: the page
    /// editor closes, the organizer gives way, annotation tools, content editing and form design turn off. Marks stay
    /// when it is turned off.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleRedaction))]
    private void ToggleRedaction()
    {
        if (Redaction.IsActive)
        {
            Redaction.IsActive = false;
            return;
        }

        StartRedacting();
    }

    private bool CanToggleRedaction() => Redaction.IsActive || CanChangeContent();

    /// <summary>"Tìm &amp; đánh dấu": personal data by pattern on every page, reviewed in a list, then marked.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeContent))]
    private void FindRedactions()
    {
        using var dialog = new RedactionSearchViewModel(Workspace.Pages.Pages, TextLayers);
        if (_services.PageDialogs.ShowRedactionSearch(dialog) && dialog.Result is { Count: > 0 } marks)
        {
            StartRedacting();
            Redaction.Add(marks);
        }
    }

    /// <summary>
    /// "Áp dụng": the confirmation (box colour, label), then the window removes the marked content for good — the next
    /// revision, one undoable step — and the report says what went.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyRedactions))]
    private void ApplyRedactions()
    {
        if (RedactionRequest(confirm: true) is { } request)
        {
            ContentChangeRequested?.Invoke(this, request);
        }
    }

    private bool CanApplyRedactions() => Redaction.HasMarks && CanChangeContent();

    /// <summary>
    /// The content change that applies the marks (after the confirmation dialog when <paramref name="confirm"/>), or
    /// null when there is nothing to apply or the user cancelled. Once applied the marks are cleared and the report shown.
    /// </summary>
    internal ContentChangeRequest? RedactionRequest(bool confirm)
    {
        var fill = RgbColor.Black;
        string? label = null;
        if (confirm)
        {
            var dialog = new RedactionApplyViewModel(Redaction.Marks.Count, Redaction.Marks.Select(m => m.PageKey).Distinct().Count());
            if (!_services.PageDialogs.ShowRedactionApply(dialog) || !dialog.Confirmed)
            {
                return null;
            }

            fill = dialog.Fill;
            label = dialog.ChosenLabel;
        }

        if (Redaction.Parameters(fill, label) is not { } parameters || !CanChangeContent())
        {
            return null;
        }

        var description = Workspace.Describe(new HistoryRecord(RedactOperation.OperationId, parameters, []), Workspace.Session.Plan);
        return new ContentChangeRequest(_operations.Get<RedactOperation>(), parameters, description, applied =>
        {
            if (applied)
            {
                Redaction.ClearAll();
                if (RedactOperation.ReportOf(parameters) is { } report)
                {
                    _ = _messages.ShowInfoAsync(Strings.Redaction_Done_Title, Describe(report));
                }
            }
        });
    }

    /// <summary>The report of a redaction for the user: what went, and that the pages are now pictures.</summary>
    internal static string Describe(RedactionReport report)
    {
        var text = new StringBuilder(string.Format(CultureInfo.CurrentCulture, Strings.Redaction_Done, report.Areas, report.Pages.Count));
        void Line(int count, string template)
        {
            if (count > 0)
            {
                text.AppendLine().Append("• ").Append(string.Format(CultureInfo.CurrentCulture, template, count));
            }
        }

        Line(report.Annotations, Strings.Redaction_Done_Annotations);
        Line(report.Fields, Strings.Redaction_Done_Fields);
        Line(report.Bookmarks, Strings.Redaction_Done_Bookmarks);
        if (report.Metadata.Count > 0 || report.Xmp)
        {
            text.AppendLine().Append("• ").Append(Strings.Redaction_Done_Metadata);
        }

        return text.ToString();
    }

    private void StartRedacting()
    {
        if (Editor.IsActive)
        {
            Editor.IsActive = false;
        }

        IsOrganizing = false;
        Annotations.StopTool();
        ContentEditor.IsActive = false;
        FormDesigner.IsActive = false;
        Redaction.IsActive = true;
    }

    private void OnRedactionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RedactionViewModel.IsActive))
        {
            UpdateFormsActive();
            OnPropertyChanged(nameof(IsRedacting));
            ToggleRedactionCommand.NotifyCanExecuteChanged();
            NotifyAnnotationCommands();
        }

        if (e.PropertyName == nameof(RedactionViewModel.HasMarks))
        {
            ApplyRedactionsCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasRedactionMarks));
        }
    }

    /// <summary>Marks wait to be applied: saving or closing asks about them first.</summary>
    public bool HasRedactionMarks => Redaction.HasMarks;
}
