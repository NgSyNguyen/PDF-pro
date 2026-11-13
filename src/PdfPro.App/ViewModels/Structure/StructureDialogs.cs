using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.Core.Geometry;
using PdfPro.Core.Structure;
using PdfPro.Operations.Security;
using PdfPro.Operations.Structure;

namespace PdfPro.App.ViewModels.Structure;

/// <summary>A choice with its label.</summary>
public sealed record StructureOption<T>(T Value, string Label);

/// <summary>
/// "Thuộc tính tài liệu" (structure.metadata): title, author, subject, keywords and creator (written to the information
/// dictionary and the XMP packet alike), producer and dates shown, and how the document opens: page, zoom, panel and
/// the title in the window. Only what changed is written.
/// </summary>
public sealed partial class DocumentDescriptionViewModel : ObservableObject
{
    private readonly DocumentStructure _structure;

    public DocumentDescriptionViewModel(string fileName, DocumentStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        FileName = fileName;
        _structure = structure;
        var description = structure.Description;
        (DocumentTitle, Author, Subject, Keywords, Creator) = (description.Title ?? string.Empty, description.Author ?? string.Empty, description.Subject ?? string.Empty, description.Keywords ?? string.Empty, description.Creator ?? string.Empty);
        OpenPageText = ((structure.Open.Page ?? 0) + 1).ToString(CultureInfo.CurrentCulture);
        Zoom = structure.Open.Zoom ?? OpenZoom.Default;
        PercentText = (structure.Open.Percent ?? 100).ToString(CultureInfo.CurrentCulture);
        Panel = structure.Open.Panel ?? OpenPanel.Default;
        ShowTitle = structure.Open.ShowTitle ?? false;
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public int PageCount => _structure.PageCount;

    [ObservableProperty]
    public partial string DocumentTitle { get; set; }

    [ObservableProperty]
    public partial string Author { get; set; }

    [ObservableProperty]
    public partial string Subject { get; set; }

    [ObservableProperty]
    public partial string Keywords { get; set; }

    [ObservableProperty]
    public partial string Creator { get; set; }

    public string Producer => _structure.Producer ?? "—";

    public string Created => _structure.Created?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";

    public string Modified => _structure.Modified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";

    [ObservableProperty]
    public partial string OpenPageText { get; set; }

    public IReadOnlyList<StructureOption<OpenZoom>> Zooms { get; } =
    [
        new(OpenZoom.Default, Strings.DocInfo_Zoom_Default),
        new(OpenZoom.FitPage, Strings.DocInfo_Zoom_FitPage),
        new(OpenZoom.FitWidth, Strings.DocInfo_Zoom_FitWidth),
        new(OpenZoom.ActualSize, Strings.DocInfo_Zoom_Actual),
        new(OpenZoom.Percent, Strings.DocInfo_Zoom_Percent),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPercent))]
    public partial OpenZoom Zoom { get; set; }

    public bool IsPercent => Zoom == OpenZoom.Percent;

    [ObservableProperty]
    public partial string PercentText { get; set; }

    public IReadOnlyList<StructureOption<OpenPanel>> Panels { get; } =
    [
        new(OpenPanel.Default, Strings.DocInfo_Panel_Default),
        new(OpenPanel.None, Strings.DocInfo_Panel_None),
        new(OpenPanel.Bookmarks, Strings.DocInfo_Panel_Bookmarks),
        new(OpenPanel.Thumbnails, Strings.DocInfo_Panel_Thumbnails),
        new(OpenPanel.FullScreen, Strings.DocInfo_Panel_FullScreen),
    ];

    [ObservableProperty]
    public partial OpenPanel Panel { get; set; }

    /// <summary>The window shows the title rather than the file name.</summary>
    [ObservableProperty]
    public partial bool ShowTitle { get; set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>What changed, as structure.metadata parameters; null when nothing changed.</summary>
    public SetMetadataParams? Result { get; private set; }

    /// <summary>The changes, or null with <paramref name="error"/> (a page or zoom out of range); null without error when nothing changed.</summary>
    public SetMetadataParams? Changes(out string? error)
    {
        error = null;
        if (!int.TryParse(OpenPageText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var page) || page < 1 || page > PageCount)
        {
            error = string.Format(CultureInfo.CurrentCulture, Strings.Bookmarks_Error_Page, PageCount);
            return null;
        }

        int? percent = null;
        if (Zoom == OpenZoom.Percent)
        {
            if (!int.TryParse(PercentText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value is < 10 or > 6400)
            {
                error = Strings.DocInfo_Error_Percent;
                return null;
            }

            percent = value;
        }

        var old = _structure.Description;
        var now = new DocumentDescription(DocumentTitle.Trim(), Author.Trim(), Subject.Trim(), Keywords.Trim(), Creator.Trim());
        var description = now != old with
        {
            Title = old.Title ?? string.Empty,
            Author = old.Author ?? string.Empty,
            Subject = old.Subject ?? string.Empty,
            Keywords = old.Keywords ?? string.Empty,
            Creator = old.Creator ?? string.Empty,
        }
            ? now
            : null;
        var current = _structure.Open;
        var open = new OpenSettings(page - 1, Zoom, percent, Panel, ShowTitle);
        var openChanged = (current.Page ?? 0) != page - 1 || (current.Zoom ?? OpenZoom.Default) != Zoom || (Zoom == OpenZoom.Percent && current.Percent != percent)
            || (current.Panel ?? OpenPanel.Default) != Panel || (current.ShowTitle ?? false) != ShowTitle;
        return description is null && !openChanged ? null : new SetMetadataParams(description, openChanged ? open : null);
    }

    [RelayCommand]
    private void Apply()
    {
        var changes = Changes(out var error);
        if (error is not null)
        {
            ErrorText = error;
            return;
        }

        Result = changes;
        CloseRequested?.Invoke(this, changes is not null);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}

/// <summary>A label range being edited: its first page (1-based as typed), style, prefix and first number.</summary>
public sealed partial class PageLabelRangeViewModel : ObservableObject
{
    public PageLabelRangeViewModel(PageLabelRange range)
    {
        FirstPageText = (range.FirstPage + 1).ToString(CultureInfo.CurrentCulture);
        Style = range.Style;
        Prefix = range.Prefix ?? string.Empty;
        StartText = range.Start.ToString(CultureInfo.CurrentCulture);
    }

    public event EventHandler? Changed;

    [ObservableProperty]
    public partial string FirstPageText { get; set; }

    [ObservableProperty]
    public partial PageLabelStyle Style { get; set; }

    [ObservableProperty]
    public partial string Prefix { get; set; }

    [ObservableProperty]
    public partial string StartText { get; set; }

    /// <summary>The range, or null when its numbers cannot be read.</summary>
    public PageLabelRange? Range(int pageCount)
        => int.TryParse(FirstPageText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var first) && first >= 1 && first <= pageCount
            && int.TryParse(StartText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var start) && start >= 1
            ? new PageLabelRange(first - 1, Style, string.IsNullOrEmpty(Prefix) ? null : Prefix, start)
            : null;

    partial void OnFirstPageTextChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnStyleChanged(PageLabelStyle value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnPrefixChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnStartTextChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// "Nhãn trang" (structure.pageLabels): ranges of page labels — "i, ii, iii" for the front matter, "1, 2…" for the body,
/// a prefix such as "PL-" — with a preview of the labels of the first pages.
/// </summary>
public sealed partial class PageLabelsViewModel : ObservableObject
{
    public PageLabelsViewModel(string fileName, int pageCount, int currentPage, IReadOnlyList<PageLabelRange> ranges)
    {
        FileName = fileName;
        PageCount = pageCount;
        CurrentPage = currentPage;
        foreach (var range in ranges)
        {
            Add(new PageLabelRangeViewModel(range));
        }

        Update();
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public int PageCount { get; }

    public int CurrentPage { get; }

    public ObservableCollection<PageLabelRangeViewModel> Ranges { get; } = [];

    public IReadOnlyList<StructureOption<PageLabelStyle>> Styles { get; } =
    [
        new(PageLabelStyle.Decimal, "1, 2, 3"),
        new(PageLabelStyle.LowerRoman, "i, ii, iii"),
        new(PageLabelStyle.UpperRoman, "I, II, III"),
        new(PageLabelStyle.LowerLetters, "a, b, c"),
        new(PageLabelStyle.UpperLetters, "A, B, C"),
        new(PageLabelStyle.None, Strings.Labels_Style_None),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial PageLabelRangeViewModel? Selected { get; set; }

    /// <summary>"Trang 1: i · Trang 2: ii · …" for the first pages.</summary>
    [ObservableProperty]
    public partial string? PreviewText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; private set; }

    public SetPageLabelsParams? Result { get; private set; }

    /// <summary>"Thêm khoảng": a range starting at the page shown.</summary>
    [RelayCommand]
    private void AddRange()
    {
        var item = new PageLabelRangeViewModel(new PageLabelRange(CurrentPage));
        Add(item);
        Selected = item;
        Update();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        Ranges.Remove(Selected!);
        Selected = Ranges.FirstOrDefault();
        Update();
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(IsValid))]
    private void Apply()
    {
        var ranges = Ranges.Select(r => r.Range(PageCount)).ToList();
        if (ranges.Any(r => r is null))
        {
            return;
        }

        Result = new SetPageLabelsParams([.. ranges.Select(r => r!)]);
        CloseRequested?.Invoke(this, true);
    }

    private bool IsValid() => ErrorText is null;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private void Add(PageLabelRangeViewModel item)
    {
        item.Changed += (_, _) => Update();
        Ranges.Add(item);
    }

    private void Update()
    {
        var ranges = Ranges.Select(r => r.Range(PageCount)).ToList();
        if (ranges.Any(r => r is null))
        {
            ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.Labels_Error, PageCount);
            PreviewText = null;
            return;
        }

        ErrorText = null;
        var normalized = PageLabels.Normalize([.. ranges.Select(r => r!)], PageCount);
        if (normalized.Count > 0 && normalized[0].FirstPage > 0)
        {
            normalized = [new PageLabelRange(0), .. normalized];
        }

        PreviewText = normalized.Count == 0
            ? Strings.Labels_None
            : string.Join(" · ", Enumerable.Range(0, Math.Min(PageCount, 12)).Select(p => string.Format(CultureInfo.CurrentCulture, Strings.Labels_Preview, p + 1, PageLabels.Of(normalized, p))));
    }
}

/// <summary>A link as listed: its page, the words it is over and where it goes; or a link found in the text, to make.</summary>
public sealed partial class LinkItemViewModel : ObservableObject
{
    private LinkItemViewModel(LinkInfo? existing, LinkSpec? found, int page, string? text, LinkTarget? target)
    {
        Existing = existing;
        Found = found;
        Page = page;
        Text = text ?? string.Empty;
        TargetText = target switch
        {
            { Url: { } url } => url,
            { Page: { } to } => string.Format(CultureInfo.CurrentCulture, Strings.Bookmarks_PageOf, to + 1),
            _ => Strings.Links_Other,
        };
    }

    public LinkInfo? Existing { get; }

    public LinkSpec? Found { get; }

    public bool IsNew => Found is not null;

    public int Page { get; }

    public string PageText => string.Format(CultureInfo.CurrentCulture, Strings.Bookmarks_PageOf, Page + 1);

    public string Text { get; }

    public string TargetText { get; }

    public static LinkItemViewModel Of(LinkInfo link) => new(link, null, link.Page, link.Text, link.Target);

    public static LinkItemViewModel Of(LinkSpec link, string? text = null) => new(null, link, link.Page, text, link.Target);
}

/// <summary>The text selected in the viewer when the links dialog opened: its words and where they are, line by line.</summary>
public sealed record LinkSelection(string Text, IReadOnlyList<(int Page, PdfRect Rect)> Areas);

/// <summary>
/// "Liên kết" (structure.links): the links of the document listed with the words they are over and where they go,
/// removed, and new ones made from the web and e-mail addresses written in the text (found in the background). The
/// changes are written together as one revision.
/// </summary>
public sealed partial class LinksViewModel : ObservableObject
{
    private readonly string _input;
    private readonly ILogger _logger;
    private readonly List<LinkRef> _removed = [];

    /// <param name="pageCount">Pages of the document, for links to a page.</param>
    /// <param name="selection">The text selected in the viewer, which a new link can be made over.</param>
    public LinksViewModel(string fileName, string input, IReadOnlyList<LinkInfo> links, ILogger logger, int pageCount = 0, LinkSelection? selection = null)
    {
        FileName = fileName;
        _input = input;
        _logger = logger;
        PageCount = pageCount;
        Selection = selection is { Areas.Count: > 0 } ? selection : null;
        foreach (var link in links)
        {
            Items.Add(LinkItemViewModel.Of(link));
        }

        UpdateEmpty();
    }

    public int PageCount { get; }

    /// <summary>The selected text a link can be made over, or null.</summary>
    public LinkSelection? Selection { get; }

    public bool HasSelectionToLink => Selection is not null;

    /// <summary>"“Điều 3. Hiệu lực”" — the selected words, shortened.</summary>
    public string SelectionText => Selection is { } selection
        ? string.Format(CultureInfo.CurrentCulture, Strings.Links_Selection, selection.Text.Length > 80 ? selection.Text[..80].TrimEnd() + "…" : selection.Text)
        : string.Empty;

    /// <summary>Where the new link goes, as typed: a web address, an e-mail address or a page number.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeLinkCommand))]
    public partial string NewTarget { get; set; } = string.Empty;

    /// <summary>
    /// "Tạo liên kết": a link over each line of the selected text, to what was typed — "https://…" or "www.…" (made
    /// https), an e-mail address (made mailto:), or a page number. Anything else is explained, never written.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMakeLink))]
    private void MakeLink()
    {
        if (Selection is not { } selection)
        {
            return;
        }

        if (TargetOf(NewTarget, PageCount) is not { } target)
        {
            ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.Links_Error_Target, PageCount);
            return;
        }

        ErrorText = null;
        LinkItemViewModel? first = null;
        foreach (var (page, rect) in selection.Areas)
        {
            if (!Items.Any(i => i.Found is { } other && other.Page == page && other.Rect == rect))
            {
                var item = LinkItemViewModel.Of(new LinkSpec(page, rect, target), selection.Text);
                Items.Add(item);
                first ??= item;
            }
        }

        if (first is not null)
        {
            Selected = first;
            StatusText = Strings.Links_Made;
        }

        Changed();
    }

    private bool CanMakeLink() => Selection is not null && NewTarget.Trim().Length > 0;

    /// <summary>What a typed target means: a page (1-based number), a web or e-mail address, or null.</summary>
    internal static LinkTarget? TargetOf(string typed, int pageCount)
    {
        var text = typed.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var number))
        {
            return number >= 1 && number <= pageCount ? new LinkTarget(Page: number - 1) : null;
        }

        var url = text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + text
            : !text.Contains("://", StringComparison.Ordinal) && !text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && text.Contains('@', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal) ? "mailto:" + text
            : text;
        return EditLinksOperation.IsAllowedUrl(url) ? new LinkTarget(Url: new Uri(url).AbsoluteUri) : null;
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public ObservableCollection<LinkItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial LinkItemViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    public partial bool IsDetecting { get; private set; }

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial string? EmptyText { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool HasChanges { get; private set; }

    public EditLinksParams? Result { get; private set; }

    /// <summary>"Tự nhận địa chỉ": the web and e-mail addresses of the text without a link, added as links to make.</summary>
    [RelayCommand(CanExecute = nameof(CanDetect))]
    private async Task DetectAsync()
    {
        IsDetecting = true;
        ErrorText = null;
        try
        {
            var found = await LinkDetector.FindAsync(_input, null, CancellationToken.None);
            var added = 0;
            foreach (var link in found)
            {
                if (!Items.Any(i => i.Found is { } other && other.Page == link.Page && other.Rect == link.Rect))
                {
                    Items.Add(LinkItemViewModel.Of(link));
                    added++;
                }
            }

            StatusText = added == 0 ? Strings.Links_NoneFound : string.Format(CultureInfo.CurrentCulture, Strings.Links_Found, added);
            Changed();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or DocumentPasswordException)
        {
            _logger.LogWarning("Addresses could not be looked for: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Links_Error_Read;
        }
        finally
        {
            IsDetecting = false;
        }
    }

    private bool CanDetect() => !IsDetecting;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        var item = Selected!;
        var index = Items.IndexOf(item);
        Items.Remove(item);
        if (item.Existing is { } link)
        {
            _removed.Add(new LinkRef(link.Page, link.Index, link.Rect));
        }

        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        Changed();
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void Apply()
    {
        var added = Items.Where(i => i.Found is not null).Select(i => i.Found!).ToList();
        Result = new EditLinksParams(added.Count == 0 ? null : added, _removed.Count == 0 ? null : [.. _removed]);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private void Changed()
    {
        HasChanges = _removed.Count > 0 || Items.Any(i => i.IsNew);
        UpdateEmpty();
    }

    private void UpdateEmpty() => EmptyText = Items.Count == 0 ? Strings.Links_None : null;
}
