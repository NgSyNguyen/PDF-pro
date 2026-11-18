using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Redaction;
using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Security;

/// <summary>A match in the review list: where, what kind, the text found; ticked to be marked.</summary>
public sealed partial class RedactionHitViewModel(Guid pageKey, RedactionHit hit, string kind) : ObservableObject
{
    public Guid PageKey { get; } = pageKey;

    public RedactionHit Hit { get; } = hit;

    public int PageNumber => Hit.PageIndex + 1;

    /// <summary>"Trang 3".</summary>
    public string PageLabel => string.Format(CultureInfo.CurrentCulture, Strings.RedactionSearch_Page, PageNumber);

    public string Kind { get; } = kind;

    /// <summary>The text found, on one line (shown to the user only).</summary>
    public string Text => string.Join(' ', Hit.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;
}

/// <summary>
/// "Tìm &amp; đánh dấu": looks for personal data by pattern (CCCD, phone, e-mail, account, date, the user's own
/// expression) on every page in the background, lists what it finds for review, and marks the ticked matches
/// (<see cref="Result"/>). The matches are personal data: shown only here, never logged.
/// </summary>
public sealed partial class RedactionSearchViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<VirtualPage> _pages;
    private readonly TextLayerCache _layers;
    private CancellationTokenSource? _searching;

    public RedactionSearchViewModel(IReadOnlyList<VirtualPage> pages, TextLayerCache layers)
    {
        _pages = pages;
        _layers = layers;
        Hits.CollectionChanged += (_, _) => OnHitsChanged();
    }

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    public partial bool NationalId { get; set; } = true;

    [ObservableProperty]
    public partial bool Phone { get; set; } = true;

    [ObservableProperty]
    public partial bool Email { get; set; } = true;

    [ObservableProperty]
    public partial bool BankAccount { get; set; } = true;

    [ObservableProperty]
    public partial bool Date { get; set; }

    /// <summary>The user's own regular expression (empty: none).</summary>
    [ObservableProperty]
    public partial string CustomExpression { get; set; } = string.Empty;

    public ObservableCollection<RedactionHitViewModel> Hits { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial bool IsSearching { get; private set; }

    /// <summary>"Đang tìm trang 3/12", "Tìm thấy 7 mục trên 2 trang", or why the search could not run.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>The ticked matches as marks, set when the user confirmed.</summary>
    public IReadOnlyList<RedactionMark>? Result { get; private set; }

    public int CheckedCount => Hits.Count(h => h.IsChecked);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var patterns = Patterns();
        TextSearch? custom = null;
        if (CustomExpression.Trim().Length > 0)
        {
            if (!RedactionFinder.TryCreateCustom(CustomExpression.Trim(), out custom, out _))
            {
                Error = Strings.RedactionSearch_Error_Expression;
                return;
            }

            patterns.Add(RedactionPattern.Custom);
        }

        if (patterns.Count == 0)
        {
            Error = Strings.RedactionSearch_Error_NoPattern;
            return;
        }

        _searching?.Cancel();
        var searching = _searching = new CancellationTokenSource();
        Error = null;
        IsSearching = true;
        Hits.Clear();
        try
        {
            for (var i = 0; i < _pages.Count; i++)
            {
                Status = string.Format(CultureInfo.CurrentCulture, Strings.RedactionSearch_Progress, i + 1, _pages.Count);
                var page = _pages[i];
                if (page.Source is null)
                {
                    continue; // a blank page has no text
                }

                PageText layer;
                try
                {
                    layer = await _layers.GetAsync(page, PdfiumPriority.Low, searching.Token);
                }
                catch (PdfDocumentException)
                {
                    continue;
                }

                IReadOnlyList<RedactionHit> found;
                try
                {
                    found = RedactionFinder.Find(layer, i, patterns, custom);
                }
                catch (RegexMatchTimeoutException)
                {
                    Error = Strings.RedactionSearch_Error_Timeout;
                    found = RedactionFinder.Find(layer, i, [.. patterns.Where(p => p != RedactionPattern.Custom)]);
                    patterns.Remove(RedactionPattern.Custom);
                }

                foreach (var hit in found)
                {
                    var item = new RedactionHitViewModel(page.Key, hit, KindOf(hit.Pattern));
                    item.PropertyChanged += (_, _) => OnHitsChanged();
                    Hits.Add(item);
                }
            }

            Status = Hits.Count == 0
                ? Strings.RedactionSearch_None
                : string.Format(CultureInfo.CurrentCulture, Strings.RedactionSearch_Found, Hits.Count, Hits.Select(h => h.PageKey).Distinct().Count());
        }
        catch (OperationCanceledException)
        {
            // The dialog closed or a new search started.
        }
        finally
        {
            if (_searching == searching)
            {
                IsSearching = false;
            }
        }
    }

    private bool CanSearch() => !IsSearching;

    [RelayCommand(CanExecute = nameof(HasChecked))]
    private void Mark()
    {
        Result = [.. Hits.Where(h => h.IsChecked).Select(h => new RedactionMark(Guid.NewGuid(), h.PageKey, h.Hit.Rects, h.Hit.Pattern))];
        Close(true);
    }

    private bool HasChecked() => !IsSearching && Hits.Any(h => h.IsChecked);

    [RelayCommand]
    private void CheckAll() => SetChecked(true);

    [RelayCommand]
    private void CheckNone() => SetChecked(false);

    private void SetChecked(bool value)
    {
        foreach (var hit in Hits)
        {
            hit.IsChecked = value;
        }
    }

    [RelayCommand]
    private void Cancel() => Close(false);

    public void Dispose()
    {
        _searching?.Cancel();
        _searching?.Dispose();
        _searching = null;
    }

    internal static string KindOf(RedactionPattern pattern) => pattern switch
    {
        RedactionPattern.NationalId => Strings.Redaction_Kind_NationalId,
        RedactionPattern.Phone => Strings.Redaction_Kind_Phone,
        RedactionPattern.Email => Strings.Redaction_Kind_Email,
        RedactionPattern.BankAccount => Strings.Redaction_Kind_BankAccount,
        RedactionPattern.Date => Strings.Redaction_Kind_Date,
        _ => Strings.Redaction_Kind_Custom,
    };

    private List<RedactionPattern> Patterns()
    {
        var patterns = new List<RedactionPattern>();
        foreach (var (on, pattern) in new[] { (NationalId, RedactionPattern.NationalId), (Phone, RedactionPattern.Phone), (Email, RedactionPattern.Email), (BankAccount, RedactionPattern.BankAccount), (Date, RedactionPattern.Date) })
        {
            if (on)
            {
                patterns.Add(pattern);
            }
        }

        return patterns;
    }

    private void OnHitsChanged()
    {
        OnPropertyChanged(nameof(CheckedCount));
        MarkCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSearchingChanged(bool value) => MarkCommand.NotifyCanExecuteChanged();

    private void Close(bool accepted)
    {
        _searching?.Cancel();
        CloseRequested?.Invoke(this, accepted);
    }
}
