using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Viewer;

/// <summary>A match in the results list: its page, its characters and the text around it.</summary>
public sealed class SearchResultViewModel(int pageIndex, TextRange range, SearchSnippet snippet)
{
    public int PageIndex { get; } = pageIndex;

    public TextRange Range { get; } = range;

    public SearchSnippet Snippet { get; } = snippet;

    /// <summary>"Trang 3".</summary>
    public string PageLabel => string.Format(CultureInfo.CurrentCulture, Strings.Search_ResultPage, PageIndex + 1);

    public string AutomationName => $"{PageLabel}: {Snippet.Before}{Snippet.Match}{Snippet.After}";
}

/// <summary>
/// "Tìm kiếm" (Ctrl+F): finds the query in the text of every page in the background — case, whole words, tone marks
/// (Auto: a query without marks ignores them) and regular expressions as chosen — and lists the matches as they come
/// with their context; the matches are highlighted on the pages, F3 / Shift+F3 go to the next / previous one (selecting
/// it, so Ctrl+C copies it). Pages without a text layer are counted: they are pictures that need OCR. A new search or
/// a change of the pages stops the running one. Must be used on the UI thread.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly DocumentViewerViewModel _viewer;
    private readonly TextLayerCache _layers;
    private readonly TextSelectionViewModel _selection;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly Func<Guid, Task<PageGeometry?>> _loadGeometry;
    private readonly ILogger _logger;
    private readonly Dictionary<int, List<TextRange>> _matches = [];
    private CancellationTokenSource? _running;
    private string? _searched;

    public SearchViewModel(
        DocumentViewerViewModel viewer,
        TextLayerCache layers,
        TextSelectionViewModel selection,
        Func<Guid, PageGeometry?> geometryOf,
        Func<Guid, Task<PageGeometry?>> loadGeometry,
        ILogger logger)
    {
        _viewer = viewer;
        _layers = layers;
        _selection = selection;
        _geometryOf = geometryOf;
        _loadGeometry = loadGeometry;
        _logger = logger;
    }

    /// <summary>The matches or the current one changed: pages redraw their highlights.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<SearchResultViewModel> Results { get; } = [];

    public IReadOnlyList<StampOption<DiacriticsMatching>> DiacriticsOptions { get; } =
    [
        new(DiacriticsMatching.Auto, Strings.Search_Diacritics_Auto),
        new(DiacriticsMatching.Ignore, Strings.Search_Diacritics_Ignore),
        new(DiacriticsMatching.Match, Strings.Search_Diacritics_Match),
    ];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    [ObservableProperty]
    public partial DiacriticsMatching Diacritics { get; set; }

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsSearching { get; private set; }

    /// <summary>0 to 1 while searching.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>"Đang tìm trang 12/300…", "25 kết quả trên 7 trang", "Không tìm thấy".</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>The query cannot be searched (an invalid expression).</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>"3 trang là ảnh, không có lớp chữ — cần OCR để tìm trong đó".</summary>
    [ObservableProperty]
    public partial string? ScannedPagesText { get; private set; }

    [ObservableProperty]
    public partial SearchResultViewModel? SelectedResult { get; set; }

    /// <summary>Searches the whole document for <see cref="Query"/>; a search already running is stopped first.</summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        Stop();
        Reset();
        if (!TextSearch.TryCreate(Query, Options(), out var search, out var error))
        {
            ErrorText = string.IsNullOrWhiteSpace(Query) ? null : Strings.Search_Error_Invalid;
            return;
        }

        var running = _running = new CancellationTokenSource();
        _searched = Query;
        IsSearching = true;
        try
        {
            await RunAsync(search, running.Token);
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            // Stopped by the user, or replaced by a newer search (which shows its own state).
            if (_running == running)
            {
                StatusText = Strings.Search_Stopped;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            if (_running == running)
            {
                ErrorText = Strings.Search_Error_Timeout;
                StatusText = null;
            }
        }
        finally
        {
            if (_running == running)
            {
                _running = null;
                IsSearching = false;
                Progress = 0;
            }

            running.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(IsSearching))]
    public void Stop() => _running?.Cancel();

    /// <summary>F3: the next match (searching first when the query is new).</summary>
    [RelayCommand]
    public async Task NextAsync() => await StepAsync(+1);

    /// <summary>Shift+F3: the previous match.</summary>
    [RelayCommand]
    public async Task PreviousAsync() => await StepAsync(-1);

    /// <summary>Closes the search: stops it and removes its highlights.</summary>
    [RelayCommand]
    public void Clear()
    {
        Stop();
        Reset();
        _searched = null;
    }

    /// <summary>The pages changed: matches no longer fit them, so the search runs again (when there was one).</summary>
    public void Invalidate()
    {
        var hadSearch = _searched is not null;
        Stop();
        Reset();
        if (hadSearch && !string.IsNullOrWhiteSpace(Query))
        {
            _ = SearchAsync();
        }
    }

    /// <summary>Where the matches are on a page, in its PDF space (one rectangle per line of each match).</summary>
    public IReadOnlyList<PdfRect> MatchRectsOn(Guid pageKey)
    {
        var index = _viewer.Document.IndexOf(pageKey);
        if (index < 0 || !_matches.TryGetValue(index, out var ranges) || _layers.TryGet(_viewer.Document.Pages[index]) is not { } layer)
        {
            return [];
        }

        return [.. ranges.SelectMany(layer.Rects)];
    }

    /// <summary>Where the current match is on a page; empty when it is on another one.</summary>
    public IReadOnlyList<PdfRect> CurrentRectsOn(Guid pageKey)
    {
        var index = _viewer.Document.IndexOf(pageKey);
        if (SelectedResult is not { } current || current.PageIndex != index || _layers.TryGet(_viewer.Document.Pages[index]) is not { } layer)
        {
            return [];
        }

        return layer.Rects(current.Range);
    }

    partial void OnSelectedResultChanged(SearchResultViewModel? value)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        if (value is not null)
        {
            _ = ShowAsync(value);
        }
    }

    partial void OnMatchCaseChanged(bool value) => RepeatSearch();

    partial void OnWholeWordChanged(bool value) => RepeatSearch();

    partial void OnDiacriticsChanged(DiacriticsMatching value) => RepeatSearch();

    partial void OnUseRegexChanged(bool value) => RepeatSearch();

    /// <summary>An option changed after a search: search again with it.</summary>
    private void RepeatSearch()
    {
        if (_searched is not null)
        {
            _ = SearchAsync();
        }
    }

    private async Task StepAsync(int direction)
    {
        if (_searched != Query || (Results.Count == 0 && !IsSearching))
        {
            await SearchAsync();
            return; // the search shows its first match
        }

        if (Results.Count == 0)
        {
            return;
        }

        var current = SelectedResult is { } selected ? Results.IndexOf(selected) : -1;
        var next = current < 0 ? (direction > 0 ? 0 : Results.Count - 1) : (current + direction + Results.Count) % Results.Count;
        SelectedResult = Results[next];
    }

    private async Task RunAsync(TextSearch search, CancellationToken cancellationToken)
    {
        var pages = _viewer.Document.Pages;
        var pagesWithMatches = 0;
        var scanned = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Progress = (double)i / pages.Count;
            StatusText = Format(Strings.Search_Progress, i + 1, pages.Count);
            PageText layer;
            try
            {
                layer = await _layers.GetAsync(pages[i], PdfiumPriority.Low, cancellationToken);
            }
            catch (PdfDocumentException ex)
            {
                _logger.LogWarning("The text of a page could not be read for searching: {Failure}", ex.Failure);
                continue;
            }

            if (!layer.HasText && !pages[i].IsBlank)
            {
                scanned++;
            }

            var matches = await Task.Run(() => search.FindAll(layer), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count == 0)
            {
                continue;
            }

            pagesWithMatches++;
            _matches[i] = [.. matches];
            foreach (var match in matches)
            {
                Results.Add(new SearchResultViewModel(i, match, TextSearch.Snippet(layer, match)));
            }

            if (SelectedResult is null)
            {
                SelectedResult = Results[0];
            }
            else
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        StatusText = Results.Count == 0 ? Strings.Search_NoMatches : Format(Strings.Search_Done, Results.Count, pagesWithMatches);
        ScannedPagesText = scanned > 0 ? Format(Strings.Search_ScannedPages, scanned) : null;
    }

    /// <summary>Scrolls the match into view and selects it (Ctrl+C copies it).</summary>
    private async Task ShowAsync(SearchResultViewModel result)
    {
        if (result.PageIndex >= _viewer.PageCount)
        {
            return;
        }

        _selection.Select(result.PageIndex, result.Range);
        var page = _viewer.Document.Pages[result.PageIndex];
        var geometry = _geometryOf(page.Key) ?? await _loadGeometry(page.Key);
        var layer = _layers.TryGet(page);
        if (SelectedResult != result || geometry is null || layer?.Rects(result.Range) is not [var first, ..])
        {
            _viewer.NavigateTo(result.PageIndex);
            return;
        }

        var shown = new PageCoordinateMapper(geometry.Value, _viewer.Layout.Scale).PdfRectToScreen(first);
        _viewer.NavigateTo(result.PageIndex, new ViewPoint(Math.Max(0, shown.X - 48), Math.Max(0, shown.Y - 48)));
    }

    private void Reset()
    {
        Results.Clear();
        _matches.Clear();
        SelectedResult = null;
        StatusText = null;
        ErrorText = null;
        ScannedPagesText = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private TextSearchOptions Options() => new(MatchCase, WholeWord, Diacritics, UseRegex);

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
