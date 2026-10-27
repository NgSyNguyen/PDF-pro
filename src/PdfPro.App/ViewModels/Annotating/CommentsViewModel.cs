using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Text;
using PdfPro.Operations;
using PdfPro.Operations.Annotating;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Annotating;

/// <summary>Which comments the list shows.</summary>
public enum CommentKindFilter
{
    All,

    /// <summary>Notes, text boxes, attachments.</summary>
    Notes,

    /// <summary>Highlight, underline, strike-out, squiggly.</summary>
    Markup,

    /// <summary>Pen strokes, shapes, lines, polygons.</summary>
    Drawings,

    Stamps,
}

/// <summary>
/// One comment of the list: an annotation of the session (it can be answered, given a review state or deleted here) or
/// one already in the file (read only for now). Shows its kind, page, author, date, text, marked text, review state
/// and conversation.
/// </summary>
public sealed partial class CommentItemViewModel : ObservableObject
{
    private readonly CommentsViewModel _owner;

    internal CommentItemViewModel(
        CommentsViewModel owner,
        int pageIndex,
        Guid pageKey,
        AnnotationTarget? target,
        int? fileIndex,
        CommentKindFilter group,
        string kind,
        string author,
        DateTimeOffset? modified,
        string contents,
        string? markedText,
        ReviewState state,
        IReadOnlyList<CommentReplyRecord> replies,
        PdfRect bounds)
    {
        _owner = owner;
        PageIndex = pageIndex;
        PageKey = pageKey;
        Target = target;
        FileIndex = fileIndex;
        Group = group;
        KindLabel = kind;
        Author = author.Length > 0 ? author : Strings.Annotation_NoAuthor;
        Modified = modified;
        Contents = TextNormalizer.ToNfc(contents);
        MarkedText = markedText is null ? null : TextNormalizer.ToNfc(markedText);
        State = state;
        Replies = replies;
        Bounds = bounds;
    }

    public int PageIndex { get; }

    public Guid PageKey { get; }

    /// <summary>The annotation of the session, or null for one already in the file.</summary>
    public AnnotationTarget? Target { get; }

    /// <summary>The annotation's place in its page's /Annots for one already in the file, or null.</summary>
    public int? FileIndex { get; }

    public CommentKindFilter Group { get; }

    public string KindLabel { get; }

    public string Author { get; }

    public DateTimeOffset? Modified { get; }

    public string Contents { get; }

    public string? MarkedText { get; }

    public ReviewState State { get; }

    public IReadOnlyList<CommentReplyRecord> Replies { get; }

    /// <summary>Where it is on its page (PDF user space).</summary>
    public PdfRect Bounds { get; }

    /// <summary>Made in this session: it can be answered, given a state and deleted here.</summary>
    public bool CanEdit => Target is not null;

    public bool HasContents => Contents.Length > 0;

    public bool HasMarkedText => !string.IsNullOrEmpty(MarkedText);

    public bool HasReplies => Replies.Count > 0;

    /// <summary>"Trang 3".</summary>
    public string PageLabel => string.Format(CultureInfo.CurrentCulture, Strings.Search_ResultPage, PageIndex + 1);

    /// <summary>"Lan · 27/10/2026 09:30".</summary>
    public string Byline => Modified is { } date ? $"{Author} · {date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}" : Author;

    /// <summary>"Đã chấp nhận", or null without a review state.</summary>
    public string? StateText => CommentsViewModel.StateLabel(State);

    public string AutomationName => $"{KindLabel}, {PageLabel}, {Byline}: {Contents}";

    /// <summary>The answer being typed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplyCommand))]
    public partial string ReplyDraft { get; set; } = string.Empty;

    /// <summary>The comment as exported.</summary>
    public CommentRecord ToRecord() => new(PageIndex + 1, KindLabel, Author, Modified, Contents, MarkedText, StateText, Replies);

    [RelayCommand(CanExecute = nameof(CanReply))]
    private void Reply()
    {
        if (_owner.Reply(this, ReplyDraft))
        {
            ReplyDraft = string.Empty;
        }
    }

    private bool CanReply() => CanEdit && !string.IsNullOrWhiteSpace(ReplyDraft);

    /// <summary>Sets the review state ("Accepted", "Rejected", "Cancelled", "Completed", "None").</summary>
    [RelayCommand]
    private void SetState(string? state)
    {
        if (CanEdit && Enum.TryParse<ReviewState>(state, out var value))
        {
            _owner.SetState(this, value);
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (CanEdit)
        {
            _owner.Delete(this);
        }
    }
}

/// <summary>
/// "Bình luận": every comment of the document — the annotations made in this session and those already in the file —
/// page by page, top to bottom, with their conversation; filtered by kind, author, review state and text. Choosing a
/// comment shows it (and selects it when it was made in this session). Comments of the session can be answered, given
/// a review state and deleted, each one undoable step. The list can be exported as Markdown or CSV. It is read in the
/// background while the panel is open, again when the pages change. Must be used on the UI thread.
/// </summary>
public sealed partial class CommentsViewModel : ObservableObject
{
    private readonly DocumentViewerViewModel _viewer;
    private readonly AnnotationToolsViewModel _tools;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly Func<IPlanOperation, object, bool> _apply;
    private readonly OperationCatalog _operations;
    private readonly Func<string> _author;
    private readonly IFileDialogService _files;
    private readonly IMessageService _messages;
    private readonly Func<string> _documentName;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private List<CommentItemViewModel> _all = [];
    private CancellationTokenSource? _loading;
    private bool _stale = true;
    private bool _isActive;

    public CommentsViewModel(
        DocumentViewerViewModel viewer,
        AnnotationToolsViewModel tools,
        Func<Guid, PageGeometry?> geometryOf,
        Func<IPlanOperation, object, bool> apply,
        OperationCatalog operations,
        Func<string> author,
        IFileDialogService files,
        IMessageService messages,
        Func<string> documentName,
        ILogger logger,
        TimeProvider? time = null)
    {
        _viewer = viewer;
        _tools = tools;
        _geometryOf = geometryOf;
        _apply = apply;
        _operations = operations;
        _author = author;
        _files = files;
        _messages = messages;
        _documentName = documentName;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The comments shown (filtered), page by page.</summary>
    public ObservableCollection<CommentItemViewModel> Items { get; } = [];

    public IReadOnlyList<SettingOption<CommentKindFilter>> KindOptions { get; } =
    [
        new(CommentKindFilter.All, Strings.Comments_Filter_AllKinds),
        new(CommentKindFilter.Notes, Strings.Comments_Filter_Notes),
        new(CommentKindFilter.Markup, Strings.Comments_Filter_Markup),
        new(CommentKindFilter.Drawings, Strings.Comments_Filter_Drawings),
        new(CommentKindFilter.Stamps, Strings.Comments_Filter_Stamps),
    ];

    public IReadOnlyList<SettingOption<string>> StateOptions { get; } =
    [
        new(string.Empty, Strings.Comments_Filter_AllStates),
        new(nameof(ReviewState.None), Strings.Comments_State_None),
        new(nameof(ReviewState.Accepted), Strings.Comments_State_Accepted),
        new(nameof(ReviewState.Rejected), Strings.Comments_State_Rejected),
        new(nameof(ReviewState.Cancelled), Strings.Comments_State_Cancelled),
        new(nameof(ReviewState.Completed), Strings.Comments_State_Completed),
    ];

    /// <summary>"Mọi tác giả" and each author of the document's comments.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingOption<string>> AuthorOptions { get; private set; } = [new(string.Empty, Strings.Comments_Filter_AllAuthors)];

    [ObservableProperty]
    public partial CommentKindFilter KindFilter { get; set; }

    /// <summary>The author shown (empty: everyone).</summary>
    [ObservableProperty]
    public partial string AuthorFilter { get; set; } = string.Empty;

    /// <summary>The review state shown (empty: any).</summary>
    [ObservableProperty]
    public partial string StateFilter { get; set; } = string.Empty;

    /// <summary>Words the comment, its marked text, its author or an answer must contain (marks and case ignored).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>"12 bình luận" or "Không có bình luận".</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial CommentItemViewModel? SelectedItem { get; set; }

    /// <summary>The panel is shown: the list is read (again when stale).</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value) && value && _stale)
            {
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>The pages or their annotations changed: the list is read again (at once while shown).</summary>
    public void Invalidate()
    {
        _stale = true;
        if (_isActive)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Reads every comment of the document; a read already running is stopped first.</summary>
    public async Task RefreshAsync()
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        _stale = false;
        IsLoading = true;
        try
        {
            var items = await LoadAsync(loading.Token);
            if (_loading != loading)
            {
                return;
            }

            var selected = SelectedItem;
            _all = items;
            AuthorOptions =
            [
                new(string.Empty, Strings.Comments_Filter_AllAuthors),
                .. items.Select(i => i.Author).Distinct(StringComparer.CurrentCulture).Order(StringComparer.CurrentCulture).Select(a => new SettingOption<string>(a, a)),
            ];
            ApplyFilter();
            SelectedItem = selected is null ? null : Items.FirstOrDefault(i => SameComment(i, selected));
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
            // A newer read replaced this one.
        }
        finally
        {
            if (_loading == loading)
            {
                _loading = null;
                IsLoading = false;
            }

            loading.Dispose();
        }
    }

    /// <summary>"Xuất…": the comments shown, as Markdown or CSV (chosen by the file's extension).</summary>
    [RelayCommand]
    public async Task ExportAsync()
    {
        var name = Path.GetFileNameWithoutExtension(_documentName());
        if (_files.PickCommentExportPath($"{name} - {Strings.Comments_Title}") is not { } path)
        {
            return;
        }

        var records = Items.Select(i => i.ToRecord()).ToArray();
        var labels = new CommentExportLabels(
            Strings.Comments_Title, Strings.Comments_Export_Page, Strings.Comments_Export_Kind, Strings.Comments_Export_Author, Strings.Comments_Export_Date,
            Strings.Comments_Export_Contents, Strings.Comments_Export_MarkedText, Strings.Comments_Export_State, Strings.Comments_Export_Reply, Strings.Comments_None);
        var csv = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);
        var text = csv ? CommentExport.ToCsv(records, labels) : CommentExport.ToMarkdown(_documentName(), records, labels, CultureInfo.CurrentCulture);
        try
        {
            // A spreadsheet reads Vietnamese right only with the byte order mark.
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: csv));
            _logger.LogInformation("Comments exported: {Count} comment(s) as {Format}", records.Length, csv ? "CSV" : "Markdown");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The comments could not be exported: {ErrorType}", ex.GetType().Name);
            await _messages.ShowErrorAsync(Strings.Comments_Export_ErrorTitle, Strings.Comments_Export_Error);
        }
    }

    internal static string? StateLabel(ReviewState state) => state switch
    {
        ReviewState.Accepted => Strings.Comments_State_Accepted,
        ReviewState.Rejected => Strings.Comments_State_Rejected,
        ReviewState.Cancelled => Strings.Comments_State_Cancelled,
        ReviewState.Completed => Strings.Comments_State_Completed,
        _ => null,
    };

    /// <summary>Adds an answer to a comment of the session (one undoable step).</summary>
    internal bool Reply(CommentItemViewModel item, string text)
    {
        var contents = TextNormalizer.ToNfc(text).Trim();
        if (contents.Length == 0 || contents.Length > AnnotationItem.MaxContentsLength)
        {
            return false;
        }

        var now = _time.GetLocalNow();
        return Update(item, a => a with { Replies = [.. a.Replies, new AnnotationReply(NewId(), _author(), now, contents)] });
    }

    /// <summary>Sets the review state of a comment of the session (one undoable step).</summary>
    internal void SetState(CommentItemViewModel item, ReviewState state)
    {
        if (state == item.State)
        {
            return;
        }

        var now = _time.GetLocalNow();
        Update(item, a => a with { Reviews = [.. a.Reviews, new AnnotationReview(NewId(), _author(), now, state)] });
    }

    internal void Delete(CommentItemViewModel item)
    {
        if (item.Target is { } target && _viewer.Document.IndexOf(target.PageKey) is var index and >= 0)
        {
            _apply(_operations.Get<DeleteAnnotationsOperation>(), new DeleteAnnotationsParams(index + 1, [target.Id]));
        }
    }

    partial void OnKindFilterChanged(CommentKindFilter value) => ApplyFilter();

    partial void OnAuthorFilterChanged(string value) => ApplyFilter();

    partial void OnStateFilterChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedItemChanged(CommentItemViewModel? value)
    {
        if (value is not null)
        {
            Show(value);
        }
    }

    private static string NewId() => $"pdfpro-{Guid.NewGuid():N}";

    private static bool SameComment(CommentItemViewModel a, CommentItemViewModel b)
        => a.PageKey == b.PageKey && a.FileIndex == b.FileIndex && a.Target?.Id == b.Target?.Id;

    private bool Update(CommentItemViewModel item, Func<AnnotationItem, AnnotationItem> change)
    {
        if (item.Target is not { } target || _viewer.Document.IndexOf(target.PageKey) is not (var index and >= 0)
            || _viewer.Document.Pages[index].Ref.FindAnnotation(target.Id) is not { } annotation)
        {
            return false;
        }

        return _apply(_operations.Get<UpdateAnnotationsOperation>(), new UpdateAnnotationsParams(index + 1, [change(annotation)]));
    }

    /// <summary>Scrolls the comment into view (its top-left corner with some room) and selects it when it is the session's.</summary>
    private void Show(CommentItemViewModel item)
    {
        if (item.PageIndex >= _viewer.PageCount)
        {
            return;
        }

        if (item.Target is { } target)
        {
            _tools.Select(target);
        }

        if (_geometryOf(item.PageKey) is not { } geometry)
        {
            _viewer.NavigateTo(item.PageIndex);
            return;
        }

        var shown = new PageCoordinateMapper(geometry, _viewer.Layout.Scale).PdfRectToScreen(item.Bounds);
        _viewer.NavigateTo(item.PageIndex, new ViewPoint(Math.Max(0, shown.X - 48), Math.Max(0, shown.Y - 48)));
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        TextSearch? finder = null;
        if (search.Length > 0)
        {
            TextSearch.TryCreate(search, new TextSearchOptions(MatchCase: false, WholeWord: false, Diacritics: DiacriticsMatching.Auto, UseRegex: false), out finder, out _);
        }

        var state = Enum.TryParse<ReviewState>(StateFilter, out var parsed) ? parsed : (ReviewState?)null;
        Items.Clear();
        foreach (var item in _all)
        {
            if ((KindFilter == CommentKindFilter.All || item.Group == KindFilter)
                && (AuthorFilter.Length == 0 || string.Equals(item.Author, AuthorFilter, StringComparison.CurrentCulture))
                && (state is null || item.State == state)
                && (finder is null || Matches(finder, item)))
            {
                Items.Add(item);
            }
        }

        StatusText = _all.Count == 0
            ? Strings.Comments_None
            : Items.Count == _all.Count
                ? string.Format(CultureInfo.CurrentCulture, Strings.Comments_Count, _all.Count)
                : string.Format(CultureInfo.CurrentCulture, Strings.Comments_CountFiltered, Items.Count, _all.Count);
    }

    private static bool Matches(TextSearch finder, CommentItemViewModel item)
    {
        string[] texts = [item.Contents, item.MarkedText ?? string.Empty, item.Author, .. item.Replies.Select(r => r.Contents)];
        return texts.Any(text => text.Length > 0 && finder.FindAll(Single(text)).Count > 0);
    }

    /// <summary>A text as one line of characters (the search works on page text).</summary>
    private static PageText Single(string text)
        => new([.. text.EnumerateRunes().Select(r => new TextChar(r.ToString(), default))]);

    private async Task<List<CommentItemViewModel>> LoadAsync(CancellationToken cancellationToken)
    {
        var document = _viewer.Document;
        var items = new List<CommentItemViewModel>();
        for (var index = 0; index < document.Pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Pages[index];
            var onPage = new List<CommentItemViewModel>();
            if (page.Source is { } source)
            {
                try
                {
                    var found = await source.GetPageAnnotationsAsync(page.SourcePageIndex, PdfiumPriority.Low, cancellationToken);
                    onPage.AddRange(FromFile(index, page.Key, found));
                }
                catch (PdfDocumentException ex)
                {
                    _logger.LogWarning("The annotations of a page could not be read: {Failure}", ex.Failure);
                }
                catch (ObjectDisposedException)
                {
                    return items; // the document closed meanwhile
                }
            }

            onPage.AddRange(page.Ref.Annotations.Select(a => FromSession(index, page.Key, a)));
            items.AddRange(InReadingOrder(page.Key, onPage));
        }

        return items;
    }

    /// <summary>Top to bottom, then left to right, as the page is shown.</summary>
    private IEnumerable<CommentItemViewModel> InReadingOrder(Guid pageKey, List<CommentItemViewModel> items)
    {
        if (_geometryOf(pageKey) is not { } geometry)
        {
            return items.OrderByDescending(i => i.Bounds.Top).ThenBy(i => i.Bounds.Left);
        }

        var mapper = new PageCoordinateMapper(geometry, 1);
        return items.Select(i => (Item: i, Shown: mapper.PdfRectToScreen(i.Bounds))).OrderBy(x => Math.Round(x.Shown.Y)).ThenBy(x => x.Shown.X).Select(x => x.Item);
    }

    private CommentItemViewModel FromSession(int pageIndex, Guid pageKey, AnnotationItem annotation)
    {
        var (group, kind) = annotation switch
        {
            MarkupAnnotation { Kind: MarkupKind.Underline } => (CommentKindFilter.Markup, Strings.Cmd_Underline),
            MarkupAnnotation { Kind: MarkupKind.StrikeOut } => (CommentKindFilter.Markup, Strings.Cmd_Strikeout),
            MarkupAnnotation { Kind: MarkupKind.Squiggly } => (CommentKindFilter.Markup, Strings.Cmd_Squiggly),
            MarkupAnnotation => (CommentKindFilter.Markup, Strings.Cmd_Highlight),
            NoteAnnotation => (CommentKindFilter.Notes, Strings.Cmd_StickyNote),
            FreeTextAnnotation => (CommentKindFilter.Notes, Strings.Cmd_TextBox),
            InkAnnotation => (CommentKindFilter.Drawings, Strings.Cmd_Ink),
            ShapeAnnotation { Shape: NoteShape.Circle } => (CommentKindFilter.Drawings, Strings.Editor_Tool_Ellipse),
            ShapeAnnotation => (CommentKindFilter.Drawings, Strings.Editor_Tool_Rectangle),
            LineAnnotation { EndEnding: not LineEnding.None } or LineAnnotation { StartEnding: not LineEnding.None } => (CommentKindFilter.Drawings, Strings.Editor_Tool_Arrow),
            LineAnnotation => (CommentKindFilter.Drawings, Strings.Editor_Tool_Line),
            PolyAnnotation { Closed: true } => (CommentKindFilter.Drawings, Strings.Annotation_Tool_Polygon),
            PolyAnnotation => (CommentKindFilter.Drawings, Strings.Annotation_Tool_Polyline),
            StampAnnotation stamp => (CommentKindFilter.Stamps, stamp.Text is { } words ? $"{Strings.Cmd_StampAnnotation}: {words.Split('\n')[0]}" : Strings.Cmd_StampAnnotation),
            _ => (CommentKindFilter.All, Strings.Comments_Kind_Other),
        };
        var replies = annotation.Replies.Select(r => new CommentReplyRecord(r.Author, r.Modified, r.Contents)).ToArray();
        var marked = annotation is MarkupAnnotation markup ? markup.MarkedText : null;
        return new CommentItemViewModel(this, pageIndex, pageKey, new AnnotationTarget(pageKey, annotation.Id), null, group, kind, annotation.Author, annotation.Modified, annotation.Contents, marked, annotation.State, replies, annotation.Bounds);
    }

    /// <summary>The comments of a page of the file, with their answers and review states gathered under them.</summary>
    private IEnumerable<CommentItemViewModel> FromFile(int pageIndex, Guid pageKey, IReadOnlyList<FileAnnotation> annotations)
    {
        var byIndex = annotations.ToDictionary(a => a.Index);
        int Root(FileAnnotation annotation)
        {
            // Answers to answers belong to the first comment of the thread; a broken chain stops where it breaks.
            var current = annotation;
            for (var depth = 0; current.InReplyTo is { } parent && byIndex.TryGetValue(parent, out var up) && depth < 64; depth++)
            {
                current = up;
            }

            return current.Index;
        }

        var answers = annotations.Where(a => a.IsReply && a.IsComment).GroupBy(Root).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var annotation in annotations.Where(a => a.IsComment && !a.IsReply && !a.IsHidden))
        {
            var thread = answers.GetValueOrDefault(annotation.Index) ?? [];
            var replies = thread.Where(a => a.State is null).Select(a => new CommentReplyRecord(a.Author ?? string.Empty, a.Modified, a.Contents ?? string.Empty)).ToArray();
            var state = thread.Where(a => a.State is not null && a.StateModel is null or "Review")
                .Select(a => Enum.TryParse<ReviewState>(a.State, out var s) ? s : ReviewState.None)
                .LastOrDefault();
            var (group, kind) = annotation.Kind switch
            {
                FileAnnotationKind.Highlight => (CommentKindFilter.Markup, Strings.Cmd_Highlight),
                FileAnnotationKind.Underline => (CommentKindFilter.Markup, Strings.Cmd_Underline),
                FileAnnotationKind.StrikeOut => (CommentKindFilter.Markup, Strings.Cmd_Strikeout),
                FileAnnotationKind.Squiggly => (CommentKindFilter.Markup, Strings.Cmd_Squiggly),
                FileAnnotationKind.Text => (CommentKindFilter.Notes, Strings.Cmd_StickyNote),
                FileAnnotationKind.FreeText => (CommentKindFilter.Notes, Strings.Cmd_TextBox),
                FileAnnotationKind.FileAttachment => (CommentKindFilter.Notes, Strings.Comments_Kind_Attachment),
                FileAnnotationKind.Caret => (CommentKindFilter.Notes, Strings.Comments_Kind_Caret),
                FileAnnotationKind.Ink => (CommentKindFilter.Drawings, Strings.Cmd_Ink),
                FileAnnotationKind.Square => (CommentKindFilter.Drawings, Strings.Editor_Tool_Rectangle),
                FileAnnotationKind.Circle => (CommentKindFilter.Drawings, Strings.Editor_Tool_Ellipse),
                FileAnnotationKind.Line => (CommentKindFilter.Drawings, Strings.Editor_Tool_Line),
                FileAnnotationKind.Polygon => (CommentKindFilter.Drawings, Strings.Annotation_Tool_Polygon),
                FileAnnotationKind.PolyLine => (CommentKindFilter.Drawings, Strings.Annotation_Tool_Polyline),
                FileAnnotationKind.Stamp => (CommentKindFilter.Stamps, Strings.Cmd_StampAnnotation),
                _ => (CommentKindFilter.All, Strings.Comments_Kind_Other),
            };
            yield return new CommentItemViewModel(this, pageIndex, pageKey, null, annotation.Index, group, kind, annotation.Author ?? string.Empty, annotation.Modified, annotation.Contents ?? string.Empty, null, state, replies, annotation.Rect);
        }
    }
}
