using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Editing;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations;
using PdfPro.Operations.Annotating;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Annotating;

/// <summary>What a press on the page does in the viewer.</summary>
public enum AnnotationTool
{
    /// <summary>Select text, or an annotation by clicking it.</summary>
    None,
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,

    /// <summary>A click places a sticky note and opens its comment.</summary>
    Note,

    /// <summary>A click or a drag makes a text box, then its text is typed.</summary>
    FreeText,

    /// <summary>Free-hand strokes, one annotation each (the tool stays on).</summary>
    Ink,
    Rectangle,
    Ellipse,
    Line,
    Arrow,

    /// <summary>Clicks add corners, a double-click closes it.</summary>
    Polygon,

    /// <summary>Clicks add points, a double-click ends it.</summary>
    Polyline,

    /// <summary>A click places a stamp ("ĐÃ DUYỆT" and the date).</summary>
    Stamp,
}

/// <summary>An annotation of the session: its page and its name there.</summary>
public sealed record AnnotationTarget(Guid PageKey, string Id);

/// <summary>
/// Annotating in the viewer ("Chú thích"): marking selected text (highlight, underline, strike-out, squiggly — at once
/// when text is selected, otherwise each text selection made with the tool), placing sticky notes and writing their
/// comment, selecting an annotation with a click, moving it with the mouse and deleting it. Every change is one undoable
/// page operation (annotate.add / update / delete) — or, for an annotation already in the file, the next revision
/// (annotate.editFile). Annotations are signed with the author name of the
/// settings. Points arrive in viewport coordinates and become page points through <see cref="PageCoordinateMapper"/>.
/// Must be used on the UI thread.
/// </summary>
public sealed partial class AnnotationToolsViewModel : ObservableObject
{
    /// <summary>Slack around thin annotations when picking them, in screen pixels.</summary>
    public const double PickTolerancePixels = 4;

    /// <summary>A drag shorter than this (points) does not move an annotation.</summary>
    public const double MoveThreshold = 1;

    public static readonly RgbColor HighlightColor = new(255, 235, 59);
    public static readonly RgbColor MarkColor = new(220, 38, 38);
    public static readonly RgbColor NoteColor = new(255, 214, 0);

    private readonly DocumentViewerViewModel _viewer;
    private readonly TextSelectionViewModel _selection;
    private readonly TextLayerCache _layers;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly Func<Guid, Task<PageGeometry?>> _loadGeometry;
    private readonly Func<IPlanOperation, object, bool> _apply;
    private readonly OperationCatalog _operations;
    private readonly Func<string> _author;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private Move? _move;
    private Draw? _draw;
    private FreeTextAnnotation? _newTextBox;
    private AnnotationTarget? _openContents;

    /// <param name="viewer">The viewer whose pages are annotated.</param>
    /// <param name="selection">Text selected in the viewer (marked by the markup tools).</param>
    /// <param name="layers">The pages' characters.</param>
    /// <param name="geometryOf">The page space of a page as it will be written, when known.</param>
    /// <param name="loadGeometry">Reads that page space.</param>
    /// <param name="apply">Applies a page operation as one undoable step (false when refused).</param>
    /// <param name="operations">The annotate.* operations.</param>
    /// <param name="author">The name annotations are signed with.</param>
    /// <param name="logger">Logs pages whose text cannot be read.</param>
    /// <param name="time">The clock annotations are dated with.</param>
    public AnnotationToolsViewModel(
        DocumentViewerViewModel viewer,
        TextSelectionViewModel selection,
        TextLayerCache layers,
        Func<Guid, PageGeometry?> geometryOf,
        Func<Guid, Task<PageGeometry?>> loadGeometry,
        Func<IPlanOperation, object, bool> apply,
        OperationCatalog operations,
        Func<string> author,
        ILogger logger,
        TimeProvider? time = null)
    {
        _viewer = viewer;
        _selection = selection;
        _layers = layers;
        _geometryOf = geometryOf;
        _loadGeometry = loadGeometry;
        _apply = apply;
        _operations = operations;
        _author = author;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Annotations, the selected one or a move in progress changed: pages redraw them.</summary>
    public event EventHandler? Changed;

    /// <summary>Faces for drawing text boxes and stamps on screen (null where nothing is drawn, as in tests).</summary>
    public ScreenFonts? Fonts { get; init; }

    /// <summary>Pictures of picture stamps.</summary>
    public ScreenImages Images { get; init; } = new();

    [ObservableProperty]
    public partial AnnotationTool Tool { get; set; }

    /// <summary>The selected annotation of the session (Delete removes it), or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial AnnotationTarget? Selected { get; private set; }

    /// <summary>The annotation whose comment is being written, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingContents))]
    public partial AnnotationTarget? EditingContents { get; private set; }

    public bool IsEditingContents => EditingContents is not null;

    /// <summary>The comment as typed so far.</summary>
    [ObservableProperty]
    public partial string ContentsDraft { get; set; } = string.Empty;

    /// <summary>"Lan · 27/10/2026 09:30" over the comment being written.</summary>
    [ObservableProperty]
    public partial string? ContentsHeader { get; private set; }

    /// <summary>A markup tool is chosen: text selections made now are marked.</summary>
    public bool IsMarkupTool => Tool is AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut or AnnotationTool.Squiggly;

    /// <summary>A tool is on (the bar above the pages says which).</summary>
    public bool IsToolActive => Tool != AnnotationTool.None;

    /// <summary>"Đang dùng: Tô sáng — Esc để tắt" (with how to draw a polygon).</summary>
    public string? ToolText => Tool switch
    {
        AnnotationTool.None => SelectedFile is { } file ? (file.Annotation.IsMovable ? Strings.Annotation_FileSelected : Strings.Annotation_FileSelectedFixed) : null,
        AnnotationTool.Polygon or AnnotationTool.Polyline => string.Format(CultureInfo.CurrentCulture, Strings.Annotation_ToolActive, NameOf(Tool)) + " · " + Strings.Annotation_PolygonHint,
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Annotation_ToolActive, NameOf(Tool)),
    };

    /// <summary>The tool bar shows: a tool is on, or an annotation is selected (its colour, comment, delete).</summary>
    public bool IsBarVisible => IsToolActive || Selected is not null || SelectedFile is not null;

    public bool HasSelection => Selected is not null;

    /// <summary>An annotation is selected, of the session or of the file (Delete removes it).</summary>
    public bool HasAnySelection => Selected is not null || SelectedFile is not null;

    /// <summary>The session's annotations of a page, in drawing order.</summary>
    public IReadOnlyList<AnnotationItem> AnnotationsOn(Guid pageKey)
    {
        var document = _viewer.Document;
        var index = document.IndexOf(pageKey);
        return index < 0 ? [] : document.Pages[index].Ref.Annotations;
    }

    /// <summary>The selected annotation as it is now, or null.</summary>
    public AnnotationItem? SelectedItem => Selected is { } target ? Find(target) : null;

    /// <summary>
    /// How an annotation is shown right now: moved by the drag in progress when it is the one being moved, as it is
    /// otherwise.
    /// </summary>
    public AnnotationItem Shown(Guid pageKey, AnnotationItem annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return _move is { } move && move.Target.PageKey == pageKey && move.Target.Id == annotation.Id && move.Offset != default
            ? annotation.MoveBy(move.Offset.X, move.Offset.Y)
            : annotation;
    }

    /// <summary>
    /// The page space of a page as it will be written, when known; otherwise it is read and <see cref="Changed"/>
    /// follows.
    /// </summary>
    public PageGeometry? GeometryOf(Guid pageKey)
    {
        if (_geometryOf(pageKey) is { } geometry)
        {
            return geometry;
        }

        _ = LoadGeometryAsync(pageKey);
        return null;
    }

    /// <summary>
    /// Mouse pressed at <paramref name="viewport"/>. With the note tool it places a note and opens its comment; with no
    /// tool a press on an annotation of the session selects it (a double-click opens a note's comment) and starts
    /// moving it. Returns false when the press is not for annotations (text selection takes it).
    /// </summary>
    public bool Press(ViewPoint viewport, int clickCount)
    {
        if (IsMarkupTool || Locate(viewport) is not { } found)
        {
            return false;
        }

        if (Tool == AnnotationTool.Note)
        {
            AddNote(found.Page, found.Point);
            return true;
        }

        if (IsDrawingTool)
        {
            return PressDrawing(found.Index, found.Page, found.Point, clickCount);
        }

        var tolerance = PickTolerancePixels / _viewer.Layout.Scale;
        if (AnnotationHitTest.At(AnnotationsOn(found.Page.Key), found.Point, tolerance) is not { } hit)
        {
            if (PressFile(found.Index, found.Page, found.Point, tolerance))
            {
                return true;
            }

            Selected = null;
            SelectedFile = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        var target = new AnnotationTarget(found.Page.Key, hit.Id);
        SelectedFile = null;
        Selected = target;
        if (clickCount >= 2 && hit is NoteAnnotation)
        {
            _openContents = target; // opened once the mouse is released
        }
        else if (hit is not MarkupAnnotation)
        {
            // Marked text stays on its text; everything else can be dragged.
            _move = new Move(target, found.Index, found.Point, default);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>A move is under way (the mouse is captured for it).</summary>
    public bool IsMoving => _move is not null || _fileMove is not null;

    /// <summary>The mouse moved to <paramref name="viewport"/> while moving or drawing an annotation.</summary>
    public void DragTo(ViewPoint viewport)
    {
        if (_draw is not null)
        {
            DrawTo(viewport);
            return;
        }

        if (DragFile(viewport))
        {
            return;
        }

        if (_move is not { } move || PointOn(move.PageIndex, viewport) is not { } point)
        {
            return;
        }

        _move = move with { Offset = new PdfPoint(point.X - move.Start.X, point.Y - move.Start.Y) };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The mouse was released: a move becomes one undoable step; a new or double-clicked note opens its comment.</summary>
    public void Release()
    {
        if (_openContents is { } open)
        {
            _openContents = null;
            BeginEditContents(open);
        }

        if (_draw is not null)
        {
            ReleaseDrawing();
            return;
        }

        if (ReleaseFile())
        {
            return;
        }

        if (_move is not { } move)
        {
            return;
        }

        _move = null;
        if (Math.Abs(move.Offset.X) >= MoveThreshold || Math.Abs(move.Offset.Y) >= MoveThreshold)
        {
            if (Find(move.Target) is { } item && IndexOf(move.Target.PageKey) is { } index)
            {
                var moved = item.MoveBy(move.Offset.X, move.Offset.Y) with { Modified = _time.GetLocalNow() };
                _apply(_operations.Get<UpdateAnnotationsOperation>(), new UpdateAnnotationsParams(index + 1, [moved]));
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A text selection made with a markup tool was finished: it is marked at once.</summary>
    public async Task SelectionReleasedAsync()
    {
        if (IsMarkupTool && _selection.HasSelection)
        {
            await MarkSelectionAsync(KindOf(Tool));
        }
    }

    /// <summary>
    /// Marks the selected text (one annotation per page, one quad per line, one undoable step); false when no text is
    /// selected. The selection is cleared once marked.
    /// </summary>
    public async Task<bool> MarkSelectionAsync(MarkupKind kind)
    {
        if (!_selection.HasSelection)
        {
            return false;
        }

        var now = _time.GetLocalNow();
        var author = _author();
        var pages = new List<PageAnnotations>();
        for (var index = 0; index < _viewer.PageCount; index++)
        {
            if (_selection.RangeOn(index) is not { } range || await LayerAsync(index) is not { } layer)
            {
                continue;
            }

            var clamped = new TextRange(Math.Clamp(range.Start, 0, layer.Count), Math.Clamp(range.End, 0, layer.Count));
            var rects = layer.Rects(clamped);
            if (rects.Count == 0)
            {
                continue;
            }

            var (color, opacity) = kind == MarkupKind.Highlight ? (HighlightColor, 1.0) : (MarkColor, 1.0);
            var text = layer.CopyText(clamped);
            pages.Add(new PageAnnotations(index + 1, [new MarkupAnnotation(NewId(), color, opacity, author, now, now, string.Empty, kind, [.. rects.Select(TextQuad.Of)], text.Length > 0 ? text : null)]));
        }

        if (pages.Count == 0 || !_apply(_operations.Get<AddAnnotationsOperation>(), new AddAnnotationsParams(pages)))
        {
            return false;
        }

        _selection.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Opens the comment of an annotation (the text of a text box) for writing.</summary>
    public void BeginEditContents(AnnotationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Find(target) is not { } item)
        {
            return;
        }

        ContentsDraft = item.Contents;
        ContentsHeader = string.Format(CultureInfo.CurrentCulture, Strings.Annotation_Header, item.Author.Length > 0 ? item.Author : Strings.Annotation_NoAuthor, item.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        EditingContents = target;
    }

    /// <summary>
    /// Keeps the comment as written (one undoable step when it changed) and closes it. A new text box is added with
    /// its text (nothing is added when none was typed); a text box grows to hold its text.
    /// </summary>
    [RelayCommand]
    public void CommitContents()
    {
        if (EditingContents is not { } target)
        {
            return;
        }

        EditingContents = null;
        var text = CleanContents(ContentsDraft);
        if (_newTextBox is { } draft)
        {
            _newTextBox = null;
            AddTextBox(target.PageKey, draft, text);
        }
        else
        {
            SetContents(target, text);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the comment of an annotation of the session (the text of a text box, which is refitted) as one undoable
    /// step; false when it is gone or the change is refused. The same text changes nothing.
    /// </summary>
    public bool SetContents(AnnotationTarget target, string text)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(text);
        text = CleanContents(text);
        if (Find(target) is not { } item || IndexOf(target.PageKey) is not { } index)
        {
            return false;
        }

        if (string.Equals(item.Contents, text, StringComparison.Ordinal))
        {
            return true;
        }

        var changed = item is FreeTextAnnotation box ? Fit(target.PageKey, box, text) : item with { Contents = text };
        return _apply(_operations.Get<UpdateAnnotationsOperation>(), new UpdateAnnotationsParams(index + 1, [changed with { Modified = _time.GetLocalNow() }]));
    }

    /// <summary>Typed text as a comment: NFC, line feeds, no trailing blanks, at most the longest comment.</summary>
    internal static string CleanContents(string text)
    {
        var clean = TextNormalizer.ToNfc(text.Replace("\r\n", "\n", StringComparison.Ordinal)).TrimEnd();
        return clean[..Math.Min(clean.Length, AnnotationItem.MaxContentsLength)];
    }

    /// <summary>Closes the comment without keeping what was typed (a new text box is not added).</summary>
    [RelayCommand]
    public void CancelContents()
    {
        _newTextBox = null;
        EditingContents = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ribbon "Tô sáng", "Gạch chân"…: marks the selected text at once, otherwise the tool stays on for the next text
    /// selections. The note tool places notes with the next clicks. Choosing the active tool again turns it off.
    /// </summary>
    [RelayCommand]
    public async Task UseToolAsync(AnnotationTool tool)
    {
        if (tool is AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut or AnnotationTool.Squiggly
            && await MarkSelectionAsync(KindOf(tool)))
        {
            return;
        }

        Tool = Tool == tool ? AnnotationTool.None : tool;
    }

    /// <summary>Removes the selected annotation (one undoable step; the file's as the next revision).</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    public void Delete()
    {
        if (Selected is null && DeleteFile())
        {
            return;
        }

        if (Selected is not { } target || IndexOf(target.PageKey) is not { } index || Find(target) is null)
        {
            return;
        }

        Selected = null;
        _apply(_operations.Get<DeleteAnnotationsOperation>(), new DeleteAnnotationsParams(index + 1, [target.Id]));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool CanDelete => Selected is not null || SelectedFile is not null;

    /// <summary>
    /// Esc: closes the comment, else drops a polygon being drawn, else turns the tool off, else clears the selection.
    /// False when there was nothing to undo.
    /// </summary>
    public bool Escape()
    {
        if (EditingContents is not null)
        {
            CancelContents();
            return true;
        }

        if (_draw is not null)
        {
            _draw = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (Tool != AnnotationTool.None)
        {
            Tool = AnnotationTool.None;
            return true;
        }

        if (Selected is not null || SelectedFile is not null)
        {
            Selected = null;
            SelectedFile = null;
            _fileMove = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    /// <summary>The pages changed (an edit, undo, redo): the selection is dropped when its annotation is gone.</summary>
    public void Refresh()
    {
        RefreshFile();
        if (Selected is { } target && Find(target) is null)
        {
            Selected = null;
        }

        if (EditingContents is { } editing && _newTextBox is null && Find(editing) is null)
        {
            EditingContents = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects an annotation of the session (chosen in the comments list).</summary>
    public void Select(AnnotationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        SelectedFile = null;
        Selected = Find(target) is null ? null : target;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Turns the tool off ("Xong" on the tool bar).</summary>
    [RelayCommand]
    public void StopTool() => Tool = AnnotationTool.None;

    partial void OnToolChanged(AnnotationTool value)
    {
        if (_draw is not null)
        {
            _draw = null; // a polygon left unfinished is dropped
            Changed?.Invoke(this, EventArgs.Empty);
        }

        OnPropertyChanged(nameof(IsMarkupTool));
        OnPropertyChanged(nameof(IsDrawingTool));
        OnPropertyChanged(nameof(IsToolActive));
        OnPropertyChanged(nameof(IsBarVisible));
        OnPropertyChanged(nameof(ToolText));
        OnPropertyChanged(nameof(HasWidth));
    }

    partial void OnSelectedChanged(AnnotationTarget? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasAnySelection));
        OnPropertyChanged(nameof(IsBarVisible));
        EditCommentCommand.NotifyCanExecuteChanged();
    }

    private static string NameOf(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Highlight => Strings.Cmd_Highlight,
        AnnotationTool.Underline => Strings.Cmd_Underline,
        AnnotationTool.StrikeOut => Strings.Cmd_Strikeout,
        AnnotationTool.Squiggly => Strings.Cmd_Squiggly,
        AnnotationTool.Note => Strings.Cmd_StickyNote,
        AnnotationTool.FreeText => Strings.Cmd_TextBox,
        AnnotationTool.Ink => Strings.Cmd_Ink,
        AnnotationTool.Rectangle => Strings.Editor_Tool_Rectangle,
        AnnotationTool.Ellipse => Strings.Editor_Tool_Ellipse,
        AnnotationTool.Line => Strings.Editor_Tool_Line,
        AnnotationTool.Arrow => Strings.Editor_Tool_Arrow,
        AnnotationTool.Polygon => Strings.Annotation_Tool_Polygon,
        AnnotationTool.Polyline => Strings.Annotation_Tool_Polyline,
        AnnotationTool.Stamp => Strings.Cmd_StampAnnotation,
        _ => string.Empty,
    };

    private static MarkupKind KindOf(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Underline => MarkupKind.Underline,
        AnnotationTool.StrikeOut => MarkupKind.StrikeOut,
        AnnotationTool.Squiggly => MarkupKind.Squiggly,
        _ => MarkupKind.Highlight,
    };

    private static string NewId() => $"pdfpro-{Guid.NewGuid():N}";

    private void AddNote(VirtualPage page, PdfPoint at)
    {
        var now = _time.GetLocalNow();
        var note = new NoteAnnotation(NewId(), NoteColor, 1, _author(), now, now, string.Empty, at);
        if (IndexOf(page.Key) is not { } index || !_apply(_operations.Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(index + 1, [note])))
        {
            return;
        }

        // One note at a time: the tool goes back to selecting, and the comment opens once the mouse is released.
        Tool = AnnotationTool.None;
        var target = new AnnotationTarget(page.Key, note.Id);
        Selected = target;
        _openContents = target;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AnnotationItem? Find(AnnotationTarget target)
    {
        var document = _viewer.Document;
        var index = document.IndexOf(target.PageKey);
        return index < 0 ? null : document.Pages[index].Ref.FindAnnotation(target.Id);
    }

    private int? IndexOf(Guid pageKey) => _viewer.Document.IndexOf(pageKey) is var index and >= 0 ? index : null;

    /// <summary>The page under a viewport point and the point on it in PDF space, when its geometry is known.</summary>
    private (int Index, VirtualPage Page, PdfPoint Point)? Locate(ViewPoint viewport)
    {
        var layout = _viewer.Layout;
        var extent = new ViewPoint(viewport.X + _viewer.ScrollOffset.X, viewport.Y + _viewer.ScrollOffset.Y);
        if (layout.GetPageAt(extent) is not { } index || index < 0 || index >= _viewer.PageCount)
        {
            return null;
        }

        return PointOn(index, viewport) is { } point ? (index, _viewer.Document.Pages[index], point) : null;
    }

    /// <summary>A viewport point on page <paramref name="index"/> (even outside it), in its PDF space.</summary>
    private PdfPoint? PointOn(int index, ViewPoint viewport)
    {
        if (index < 0 || index >= _viewer.PageCount || _geometryOf(_viewer.Document.Pages[index].Key) is not { } geometry)
        {
            return null;
        }

        var layout = _viewer.Layout;
        var bounds = layout.GetPageBounds(index);
        var mapper = new PageCoordinateMapper(geometry, layout.Scale);
        return mapper.ScreenToPdf(new ViewPoint(viewport.X + _viewer.ScrollOffset.X - bounds.X, viewport.Y + _viewer.ScrollOffset.Y - bounds.Y));
    }

    private async Task LoadGeometryAsync(Guid pageKey)
    {
        if (await _loadGeometry(pageKey) is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<PageText?> LayerAsync(int pageIndex)
    {
        try
        {
            return await _layers.GetAsync(_viewer.Document.Pages[pageIndex], PdfiumPriority.High);
        }
        catch (PdfDocumentException ex)
        {
            _logger.LogWarning("The text of a page could not be read for marking: {Failure}", ex.Failure);
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null; // the document closed meanwhile
        }
    }

    /// <summary>A drag of an annotation: what, on which page, from where, and how far so far (points).</summary>
    private sealed record Move(AnnotationTarget Target, int PageIndex, PdfPoint Start, PdfPoint Offset);
}
