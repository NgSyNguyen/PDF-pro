using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Operations;
using PdfPro.Operations.Editing;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Editing;

/// <summary>A drawing of a page's content, chosen in the viewer: its page, the file it was read from, and what it is.</summary>
/// <param name="PageKey">The plan's page.</param>
/// <param name="Source">The file it was read from (a new revision makes it stale).</param>
/// <param name="Object">The object as read.</param>
public sealed record ContentObjectTarget(Guid PageKey, IPdfDocument Source, PageObjectInfo Object)
{
    public bool IsText => Object.Kind == PageObjectKind.Text;

    public bool IsImage => Object.Kind == PageObjectKind.Image;
}

/// <summary>
/// "Sửa nội dung": the drawings of the pages (text, images, vector paths) as PDFium lists them. Moving the mouse frames
/// the one under it; a click selects it, a drag moves it, Delete removes it, the tool bar turns it; a double-click on
/// text (or "Sửa chữ") opens its words for typing in place; an image can be replaced by another picture or saved as a
/// file. Every change is written by the window as the next revision (edit.content), one undoable step. Text is only
/// edited within one text object (PDF has no paragraphs). Must be used on the UI thread.
/// </summary>
public sealed partial class ContentEditorViewModel : ObservableObject
{
    /// <summary>Slack around objects when picking them, in screen pixels.</summary>
    public const double PickTolerancePixels = 3;

    /// <summary>A drag shorter than this (points) does not move an object.</summary>
    public const double MoveThreshold = 1;

    /// <summary>Paths or shadings covering more than this share of the page are picked only when nothing else is there.</summary>
    private const double BackgroundShare = 0.8;

    private readonly DocumentViewerViewModel _viewer;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly Func<ContentChangeRequest, bool> _requestContentChange;
    private readonly OperationCatalog _operations;
    private readonly Func<IPdfDocument, string?> _sourcePath;
    private readonly Func<string> _imagesFolder;
    private readonly IFileDialogService _files;
    private readonly IMessageService _messages;
    private readonly ILogger _logger;
    private readonly Dictionary<(IPdfDocument Source, int Page), IReadOnlyList<PageObjectInfo>> _objects = [];
    private readonly HashSet<(IPdfDocument Source, int Page)> _reading = [];
    private Move? _move;

    /// <param name="viewer">The viewer whose pages are edited.</param>
    /// <param name="geometryOf">The page space of a page as it will be written, when known.</param>
    /// <param name="requestContentChange">Asks the window to write the next revision; false when the document cannot change now.</param>
    /// <param name="operations">edit.content.</param>
    /// <param name="sourcePath">The file of an opened source (to read which characters its fonts have).</param>
    /// <param name="imagesFolder">The session folder pictures are copied into.</param>
    /// <param name="files">Asks for pictures and where to save images.</param>
    /// <param name="messages">Explains failures and font replacements.</param>
    /// <param name="logger">Logs failures (never the document's words).</param>
    public ContentEditorViewModel(
        DocumentViewerViewModel viewer,
        Func<Guid, PageGeometry?> geometryOf,
        Func<ContentChangeRequest, bool> requestContentChange,
        OperationCatalog operations,
        Func<IPdfDocument, string?> sourcePath,
        Func<string> imagesFolder,
        IFileDialogService files,
        IMessageService messages,
        ILogger logger)
    {
        _viewer = viewer;
        _geometryOf = geometryOf;
        _requestContentChange = requestContentChange;
        _operations = operations;
        _sourcePath = sourcePath;
        _imagesFolder = imagesFolder;
        _files = files;
        _messages = messages;
        _logger = logger;
    }

    /// <summary>Objects, the hovered or selected one, or a move changed: pages redraw their frames.</summary>
    public event EventHandler? Changed;

    /// <summary>The mode is on (the viewer's mouse goes to it).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>The object under the mouse, or null.</summary>
    [ObservableProperty]
    public partial ContentObjectTarget? Hovered { get; private set; }

    /// <summary>The selected object, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsTextSelected), nameof(IsImageSelected), nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(RotateLeftCommand), nameof(RotateRightCommand), nameof(EditTextCommand), nameof(ReplaceImageCommand), nameof(SaveImageCommand))]
    public partial ContentObjectTarget? Selected { get; private set; }

    public bool HasSelection => Selected is not null;

    public bool IsTextSelected => Selected?.IsText == true;

    public bool IsImageSelected => Selected?.IsImage == true;

    /// <summary>"Khối chữ · Times New Roman 12 pt", "Ảnh 480 × 320", "Hình vẽ".</summary>
    public string? SelectionText => Selected?.Object switch
    {
        { Kind: PageObjectKind.Text, Text: { } text } info => string.Format(CultureInfo.CurrentCulture, Strings.ContentEditor_TextBlock, FontLabel(text.FontName), Math.Round(text.SizeOnPage(info.Matrix), 1)),
        { Kind: PageObjectKind.Image, Image: { } image } => string.Format(CultureInfo.CurrentCulture, Strings.ContentEditor_Image, image.PixelWidth, image.PixelHeight),
        { Kind: PageObjectKind.Form } => Strings.ContentEditor_Form,
        { } => Strings.ContentEditor_Drawing,
        null => null,
    };

    /// <summary>The text object whose words are being typed, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingText))]
    public partial ContentObjectTarget? EditingText { get; private set; }

    public bool IsEditingText => EditingText is not null;

    /// <summary>The words as typed so far.</summary>
    [ObservableProperty]
    public partial string TextDraft { get; set; } = string.Empty;

    /// <summary>Where the typing box goes: the text object's box in viewport coordinates.</summary>
    [ObservableProperty]
    public partial ViewRect TextBox { get; private set; }

    /// <summary>Longer words are broken into lines as wide as the old text (otherwise they run on).</summary>
    [ObservableProperty]
    public partial bool WrapText { get; set; }

    /// <summary>A move is under way (the mouse is captured for it).</summary>
    public bool IsMoving => _move is not null;

    /// <summary>The objects of a page once read; otherwise they are read in the background and <see cref="Changed"/> follows.</summary>
    public IReadOnlyList<PageObjectInfo> ObjectsOn(Guid pageKey)
    {
        if (!IsActive || IndexOf(pageKey) is not { } index || _viewer.Document.Pages[index] is not { Source: { } source } page)
        {
            return [];
        }

        var key = (source, page.SourcePageIndex);
        if (_objects.TryGetValue(key, out var objects))
        {
            return objects;
        }

        if (_reading.Add(key))
        {
            _ = ReadAsync(key);
        }

        return [];
    }

    /// <summary>The frame of an object on a page as shown now (the selected one moved by the drag in progress).</summary>
    public PdfRect Shown(Guid pageKey, ContentObjectTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var bounds = target.Object.Bounds;
        return _move is { Offset: var offset } && offset != default && target.PageKey == pageKey && ReferenceEquals(target, _move.Target)
            ? new PdfRect(bounds.Left + offset.X, bounds.Bottom + offset.Y, bounds.Right + offset.X, bounds.Top + offset.Y)
            : bounds;
    }

    /// <summary>The page space of a page as it will be written, when known.</summary>
    public PageGeometry? GeometryOf(Guid pageKey) => _geometryOf(pageKey);

    /// <summary>The mouse moved over <paramref name="viewport"/> (no button): the object under it is framed.</summary>
    public void HoverAt(ViewPoint viewport)
    {
        var hovered = IsActive && Locate(viewport) is { } found ? Pick(found.Page, found.Point) : null;
        if (hovered?.Object != Hovered?.Object || hovered?.PageKey != Hovered?.PageKey)
        {
            Hovered = hovered;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Mouse pressed: selects the object under it and starts moving it; a double-click on text opens its words. False
    /// when the mode is off.
    /// </summary>
    public bool Press(ViewPoint viewport, int clickCount)
    {
        if (!IsActive)
        {
            return false;
        }

        if (EditingText is not null)
        {
            CommitText();
        }

        var found = Locate(viewport);
        var picked = found is { } at ? Pick(at.Page, at.Point) : null;
        Selected = picked;
        if (picked is not null && found is { } place)
        {
            if (clickCount >= 2 && picked.IsText)
            {
                BeginEditText(picked);
            }
            else
            {
                _move = new Move(picked, place.Index, place.Point, default);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void DragTo(ViewPoint viewport)
    {
        if (_move is not { } move || PointOn(move.PageIndex, viewport) is not { } point)
        {
            return;
        }

        _move = move with { Offset = new PdfPoint(point.X - move.Start.X, point.Y - move.Start.Y) };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The mouse was released: a real drag moves the object (the next revision).</summary>
    public void Release()
    {
        if (_move is not { } move)
        {
            return;
        }

        _move = null;
        if (Math.Abs(move.Offset.X) >= MoveThreshold || Math.Abs(move.Offset.Y) >= MoveThreshold)
        {
            Edit(move.Target, target => new MoveObjectEdit(target, Math.Round(move.Offset.X, 3), Math.Round(move.Offset.Y, 3)));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Esc: drops the typing, else the selection. False when there was nothing to drop.</summary>
    public bool Escape()
    {
        if (EditingText is not null)
        {
            CancelText();
            return true;
        }

        if (Selected is not null)
        {
            Selected = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    /// <summary>The pages changed (an edit, undo, redo): what was read from files no longer shown is dropped.</summary>
    public void Refresh()
    {
        var shown = _viewer.Document.Pages.Where(p => p.Source is not null).Select(p => (p.Source!, p.SourcePageIndex)).ToHashSet();
        foreach (var key in _objects.Keys.Where(k => !shown.Contains(k)).ToList())
        {
            _objects.Remove(key);
        }

        if (Selected is { } selected && !IsShown(selected))
        {
            Selected = null;
            _move = null;
        }

        if (Hovered is { } hovered && !IsShown(hovered))
        {
            Hovered = null;
        }

        if (EditingText is { } editing && !IsShown(editing))
        {
            EditingText = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the words of a text object for typing, over the object.</summary>
    public void BeginEditText(ContentObjectTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsText || ViewportRect(target) is not { } rect)
        {
            return;
        }

        TextDraft = TextNormalizer.ToNfc(target.Object.Text?.Text ?? string.Empty).TrimEnd();
        TextBox = rect;
        EditingText = target;
    }

    /// <summary>Writes the typed words into the text object (the next revision); a font that lacks letters is replaced, and the user is told.</summary>
    [RelayCommand]
    public void CommitText()
    {
        if (EditingText is not { } target)
        {
            return;
        }

        EditingText = null;
        var text = TextNormalizer.ToNfc(TextDraft.Replace("\r\n", "\n", StringComparison.Ordinal)).TrimEnd();
        if (string.Equals(text, TextNormalizer.ToNfc(target.Object.Text?.Text ?? string.Empty).TrimEnd(), StringComparison.Ordinal))
        {
            return;
        }

        var missing = MissingCharacters(target, text);
        var overflow = WrapText ? TextOverflow.Wrap : TextOverflow.Overflow;
        Edit(target, reference => new ReplaceTextEdit(reference, text, overflow), applied =>
        {
            if (applied && missing.Length > 0)
            {
                _ = _messages.ShowInfoAsync(
                    Strings.ContentEditor_FontReplaced_Title,
                    string.Format(CultureInfo.CurrentCulture, Strings.ContentEditor_FontReplaced, FontLabel(target.Object.Text?.FontName ?? string.Empty), missing));
            }
        });
    }

    [RelayCommand]
    public void CancelText() => EditingText = null;

    /// <summary>"Sửa chữ": opens the selected text object's words.</summary>
    [RelayCommand(CanExecute = nameof(IsTextSelected))]
    private void EditText()
    {
        if (Selected is { } selected)
        {
            BeginEditText(selected);
        }
    }

    /// <summary>Removes the selected object (the next revision).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void Delete()
    {
        if (Selected is { } selected && Edit(selected, target => new DeleteObjectEdit(target)))
        {
            Selected = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Turns the selected object a quarter turn to the left as seen on the page.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RotateLeft() => Turn(90);

    /// <summary>Turns the selected object a quarter turn to the right as seen on the page.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RotateRight() => Turn(-90);

    /// <summary>"Thay ảnh…": puts another picture in the selected image's frame, keeping the picture's shape.</summary>
    [RelayCommand(CanExecute = nameof(IsImageSelected))]
    private void ReplaceImage()
    {
        if (Selected is not { IsImage: true } selected || _files.PickImageFile(Strings.Editor_PickImage_Title) is not { } path)
        {
            return;
        }

        try
        {
            var picture = Services.Editing.ImageImporter.ImportUpright(path, _imagesFolder());
            Edit(selected, target => new ReplaceImageEdit(target, picture.Path));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning("A picture could not be read: {ErrorType}", ex.GetType().Name);
            _ = _messages.ShowErrorAsync(Strings.Editor_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.Editor_Error_Image, Path.GetFileName(path)));
        }
    }

    /// <summary>"Lưu ảnh…": writes the selected image as stored in the file (JPEG as is, otherwise PNG).</summary>
    [RelayCommand(CanExecute = nameof(IsImageSelected))]
    private async Task SaveImageAsync()
    {
        if (Selected is not { IsImage: true } selected || IndexOf(selected.PageKey) is not { } index)
        {
            return;
        }

        try
        {
            var image = await selected.Source.ExtractImageAsync(_viewer.Document.Pages[index].SourcePageIndex, selected.Object);
            var name = string.Format(CultureInfo.CurrentCulture, Strings.ContentEditor_ImageFileName, index + 1, selected.Object.Index + 1) + image.Extension;
            if (_files.PickImageSavePath(name, image.Extension) is not { } path)
            {
                return;
            }

            await File.WriteAllBytesAsync(path, image.Data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or PdfDocumentException or ObjectDisposedException)
        {
            _logger.LogWarning("An image could not be saved: {ErrorType}", ex.GetType().Name);
            _ = _messages.ShowErrorAsync(Strings.ContentEditor_Error_Title, Strings.ContentEditor_Error_SaveImage);
        }
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (!value)
        {
            if (EditingText is not null)
            {
                CommitText();
            }

            Selected = null;
            Hovered = null;
            _move = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Times New Roman" from "ABCDEF+TimesNewRomanPSMT"-like names.</summary>
    private static string FontLabel(string fontName)
    {
        var name = fontName.Length > 7 && fontName[6] == '+' ? fontName[7..] : fontName;
        return name.Length > 0 ? name : Strings.ContentEditor_UnknownFont;
    }

    private void Turn(double degrees)
    {
        if (Selected is { } selected)
        {
            Edit(selected, target => new RotateObjectEdit(target, degrees));
        }
    }

    /// <summary>Asks the window for one change to an object; false when the document cannot change now.</summary>
    private bool Edit(ContentObjectTarget target, Func<PageObjectRef, ContentEdit> make, Action<bool>? completed = null)
    {
        if (IndexOf(target.PageKey) is not { } index)
        {
            return false;
        }

        var parameters = new EditContentParams([make(PageObjectRef.Of(index + 1, target.Object))]);
        return _requestContentChange(new ContentChangeRequest(_operations.Get<EditContentOperation>(), parameters, HistoryDescriptions.Describe(parameters), completed));
    }

    /// <summary>The letters the object's font lacks (read from its file; unknown counts as none, the operation decides).</summary>
    private string MissingCharacters(ContentObjectTarget target, string text)
    {
        if (_sourcePath(target.Source) is not { } path || IndexOf(target.PageKey) is not { } index)
        {
            return string.Empty;
        }

        try
        {
            return EditContentOperation.MissingCharacters(path, _viewer.Document.Pages[index].SourcePageIndex, target.Object.Text?.FontName ?? string.Empty, text.Replace("\n", string.Empty, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning("The fonts of a page could not be read: {ErrorType}", ex.GetType().Name);
            return string.Empty;
        }
    }

    /// <summary>The topmost object under a point (backgrounds covering most of the page last), or null.</summary>
    private ContentObjectTarget? Pick(VirtualPage page, PdfPoint point)
    {
        if (page.Source is not { } source)
        {
            return null;
        }

        var objects = ObjectsOn(page.Key);
        var tolerance = PickTolerancePixels / _viewer.Layout.Scale;
        var area = page.BaseSize.Width * page.BaseSize.Height;
        PageObjectInfo? background = null;
        for (var i = objects.Count - 1; i >= 0; i--)
        {
            var item = objects[i];
            var box = item.Bounds;
            if (item.Text?.IsInvisible == true || point.X < box.Left - tolerance || point.X > box.Right + tolerance || point.Y < box.Bottom - tolerance || point.Y > box.Top + tolerance)
            {
                continue;
            }

            if (item.Kind is PageObjectKind.Path or PageObjectKind.Shading && area > 0 && box.Width * box.Height > BackgroundShare * area)
            {
                background ??= item;
                continue;
            }

            return new ContentObjectTarget(page.Key, source, item);
        }

        return background is null ? null : new ContentObjectTarget(page.Key, source, background);
    }

    private bool IsShown(ContentObjectTarget target)
        => IndexOf(target.PageKey) is { } index && _viewer.Document.Pages[index].Source == target.Source;

    private async Task ReadAsync((IPdfDocument Source, int Page) key)
    {
        try
        {
            _objects[key] = await key.Source.GetPageObjectsAsync(key.Page, PdfiumPriority.Normal);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (PdfDocumentException ex)
        {
            _logger.LogWarning("The drawings of a page could not be read: {Failure}", ex.Failure);
            _objects[key] = [];
        }
        catch (ObjectDisposedException)
        {
            // The revision was replaced meanwhile.
        }
        finally
        {
            _reading.Remove(key);
        }
    }

    private int? IndexOf(Guid pageKey) => _viewer.Document.IndexOf(pageKey) is var index and >= 0 ? index : null;

    /// <summary>The page under a viewport point and the point on it in PDF space, when its geometry is known.</summary>
    private (int Index, VirtualPage Page, PdfPoint Point)? Locate(ViewPoint viewport)
    {
        var extent = new ViewPoint(viewport.X + _viewer.ScrollOffset.X, viewport.Y + _viewer.ScrollOffset.Y);
        if (_viewer.Layout.GetPageAt(extent) is not { } index || index < 0 || index >= _viewer.PageCount)
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

        var bounds = _viewer.Layout.GetPageBounds(index);
        var mapper = new PageCoordinateMapper(geometry, _viewer.Layout.Scale);
        return mapper.ScreenToPdf(new ViewPoint(viewport.X + _viewer.ScrollOffset.X - bounds.X, viewport.Y + _viewer.ScrollOffset.Y - bounds.Y));
    }

    /// <summary>An object's box in viewport coordinates, when its page is laid out.</summary>
    private ViewRect? ViewportRect(ContentObjectTarget target)
    {
        if (IndexOf(target.PageKey) is not { } index || _geometryOf(target.PageKey) is not { } geometry)
        {
            return null;
        }

        var page = _viewer.Layout.GetPageBounds(index);
        var shown = new PageCoordinateMapper(geometry, _viewer.Layout.Scale).PdfRectToScreen(target.Object.Bounds);
        return new ViewRect(page.X + shown.X - _viewer.ScrollOffset.X, page.Y + shown.Y - _viewer.ScrollOffset.Y, shown.Width, shown.Height);
    }

    private sealed record Move(ContentObjectTarget Target, int PageIndex, PdfPoint Start, PdfPoint Offset);
}
