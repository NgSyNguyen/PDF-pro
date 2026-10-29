using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Forms;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Forms;

/// <summary>A field the page seems to ask for, shown in the designer until the user takes or drops it.</summary>
public sealed partial class FieldSuggestionViewModel(Guid pageKey, FieldSuggestion suggestion) : ObservableObject
{
    public Guid PageKey { get; } = pageKey;

    public FieldSuggestion Suggestion { get; } = suggestion;

    /// <summary>The field will be placed ("Thêm các trường đã chọn"); a click toggles it.</summary>
    [ObservableProperty]
    public partial bool IsAccepted { get; set; } = true;
}

/// <summary>"Nhận diện trường" (forms.autodetect) in the form designer.</summary>
public sealed partial class FormDesignerViewModel
{
    /// <summary>Documents longer than this are searched on the current page only.</summary>
    public const int MaxDetectPages = 50;

    private readonly List<FieldSuggestionViewModel> _suggestions = [];
    private CancellationTokenSource? _detecting;

    /// <summary>The fields proposed by the last search, on every page searched.</summary>
    public IReadOnlyList<FieldSuggestionViewModel> Suggestions => _suggestions;

    public bool HasSuggestions => _suggestions.Count > 0;

    /// <summary>The pages are being searched for blanks.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    public partial bool IsDetecting { get; private set; }

    /// <summary>"5 đề xuất — 4 được chọn", or that nothing was found; null before any search.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearSuggestionsCommand))]
    public partial string? SuggestionStatus { get; private set; }

    /// <summary>The proposals on a page.</summary>
    public IEnumerable<FieldSuggestionViewModel> SuggestionsOn(Guid pageKey) => _suggestions.Where(s => s.PageKey == pageKey);

    /// <summary>
    /// Searches the pages of the form's file (the current page only in long documents) for blanks, lines and boxes, and
    /// shows what it finds as proposals, all taken until clicked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDetect))]
    private async Task DetectAsync()
    {
        _detecting?.Cancel();
        var detecting = _detecting = new CancellationTokenSource();
        IsDetecting = true;
        try
        {
            var pages = _viewer.Document.Pages.Where(p => p.Source is not null && _forms.SourcePageOf(p.Key) is not null).ToList();
            if (pages.Count > MaxDetectPages)
            {
                var current = _viewer.Document.Pages[Math.Clamp(_viewer.CurrentPageIndex, 0, _viewer.PageCount - 1)];
                pages = pages.Where(p => p.Key == current.Key).ToList();
            }

            var found = new List<FieldSuggestionViewModel>();
            foreach (var page in pages)
            {
                var sourcePage = page.SourcePageIndex;
                try
                {
                    var text = await page.Source!.GetPageTextLayerAsync(sourcePage, PdfiumPriority.Normal, detecting.Token);
                    var objects = await page.Source.GetPageObjectsAsync(sourcePage, PdfiumPriority.Normal, detecting.Token);
                    var taken = _forms.Fields.SelectMany(f => f.Widgets).Where(w => w.Page == sourcePage).Select(w => w.Rect).ToList();
                    found.AddRange(FormAutodetect.Suggest(text, objects, taken).Select(s => new FieldSuggestionViewModel(page.Key, s)));
                }
                catch (Exception ex) when (ex is PdfDocumentException or ObjectDisposedException)
                {
                    // A page that cannot be read (or a revision replaced meanwhile) has nothing to propose.
                }
            }

            if (_detecting != detecting)
            {
                return;
            }

            SetSuggestions(found);
            SuggestionStatus = found.Count == 0 ? Strings.FormDesigner_NoSuggestions : SuggestionStatus;
        }
        catch (OperationCanceledException)
        {
            // A newer search, or the mode was left.
        }
        finally
        {
            if (_detecting == detecting)
            {
                IsDetecting = false;
            }
        }
    }

    private bool CanDetect() => IsActive && !IsDetecting;

    /// <summary>Places the proposals still taken as new fields named after their labels (one revision).</summary>
    [RelayCommand(CanExecute = nameof(HasAcceptedSuggestions))]
    private void AcceptSuggestions()
    {
        var names = _forms.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var specs = new List<FormFieldSpec>();
        foreach (var item in _suggestions.Where(s => s.IsAccepted))
        {
            if (_viewer.Document.IndexOf(item.PageKey) is not (var index and >= 0))
            {
                continue;
            }

            var suggestion = item.Suggestion;
            var name = SuggestionName(suggestion, names);
            names.Add(name);
            specs.Add(new FormFieldSpec(name, suggestion.Kind, index, Round(suggestion.Rect), ToolTip: suggestion.Label.Length > 0 ? suggestion.Label : null));
        }

        if (specs.Count > 0 && Request(new DesignFormParams(Add: specs)))
        {
            SetSuggestions([]);
        }
    }

    private bool HasAcceptedSuggestions() => _suggestions.Any(s => s.IsAccepted);

    /// <summary>Drops every proposal (and the note that nothing was found).</summary>
    [RelayCommand(CanExecute = nameof(CanClearSuggestions))]
    private void ClearSuggestions()
    {
        SetSuggestions([]);
        SuggestionStatus = null;
    }

    private bool CanClearSuggestions() => SuggestionStatus is not null;

    /// <summary>The proposal under a point of a page, or null.</summary>
    private FieldSuggestionViewModel? SuggestionAt(Guid pageKey, PdfPoint point)
    {
        var tolerance = 2 / _viewer.Layout.Scale;
        return SuggestionsOn(pageKey).LastOrDefault(s =>
            point.X >= s.Suggestion.Rect.Left - tolerance && point.X <= s.Suggestion.Rect.Right + tolerance
            && point.Y >= s.Suggestion.Rect.Bottom - tolerance && point.Y <= s.Suggestion.Rect.Top + tolerance);
    }

    /// <summary>A click on a proposal takes or drops it. False when there is none under the point.</summary>
    private bool ToggleSuggestionAt(Guid pageKey, PdfPoint point)
    {
        if (SuggestionAt(pageKey, point) is not { } hit)
        {
            return false;
        }

        hit.IsAccepted = !hit.IsAccepted;
        UpdateSuggestionStatus();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The label as a field name (no dots: they would make it a child), else the kind's name; never taken.</summary>
    private static string SuggestionName(FieldSuggestion suggestion, HashSet<string> taken)
    {
        var label = suggestion.Label.Replace('.', ' ').Trim();
        var stem = label.Length > 0
            ? label
            : suggestion.Kind == FormFieldKind.CheckBox ? Strings.FormDesigner_Name_CheckBox : Strings.FormDesigner_Name_Text;
        if (label.Length > 0 && !taken.Contains(stem))
        {
            return stem;
        }

        for (var i = label.Length > 0 ? 2 : 1; ; i++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{stem} {i}");
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    private void SetSuggestions(IReadOnlyList<FieldSuggestionViewModel> suggestions)
    {
        _suggestions.Clear();
        _suggestions.AddRange(suggestions);
        UpdateSuggestionStatus();
        OnPropertyChanged(nameof(Suggestions));
        OnPropertyChanged(nameof(HasSuggestions));
        AcceptSuggestionsCommand.NotifyCanExecuteChanged();
        ClearSuggestionsCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSuggestionStatus()
    {
        SuggestionStatus = _suggestions.Count == 0
            ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.FormDesigner_Suggestions, _suggestions.Count, _suggestions.Count(s => s.IsAccepted));
        AcceptSuggestionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The mode was left or the pages changed: proposals no longer hold.</summary>
    private void DropSuggestions()
    {
        _detecting?.Cancel();
        _detecting = null;
        IsDetecting = false;
        if (_suggestions.Count > 0 || SuggestionStatus is not null)
        {
            SetSuggestions([]);
            SuggestionStatus = null;
        }
    }
}
