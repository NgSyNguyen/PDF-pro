using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Annotating;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Annotating;

/// <summary>An annotation already in the file, chosen on a page of the viewer.</summary>
/// <param name="PageKey">The plan's page.</param>
/// <param name="Source">The file it was read from (a new revision makes it stale).</param>
/// <param name="Annotation">The annotation as read.</param>
public sealed record FileAnnotationTarget(Guid PageKey, IPdfDocument Source, FileAnnotation Annotation);

/// <summary>
/// Annotations already in the file, in the viewer: the page image shows them, so they are read (in the background) only
/// to be picked. A click selects one (framed), a drag moves it (not marked text), Delete removes it — each written as the
/// next revision through <see cref="RequestContentChange"/> (annotate.editFile), one undoable step.
/// </summary>
public sealed partial class AnnotationToolsViewModel
{
    private readonly Dictionary<(IPdfDocument Source, int Page), IReadOnlyList<FileAnnotation>> _fileAnnotations = [];
    private readonly HashSet<(IPdfDocument Source, int Page)> _readingFile = [];
    private FileMove? _fileMove;

    /// <summary>
    /// Asks the window to change the file (annotate.editFile); false when the document cannot change now. Without it the
    /// annotations of the file are not picked.
    /// </summary>
    public Func<ContentChangeRequest, bool>? RequestContentChange { get; init; }

    /// <summary>The selected annotation of the file (Delete removes it), or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial FileAnnotationTarget? SelectedFile { get; private set; }

    /// <summary>
    /// The annotations already in the file of a page, once read; otherwise they are read in the background and
    /// <see cref="Changed"/> follows. Empty for blank pages.
    /// </summary>
    public IReadOnlyList<FileAnnotation> FileAnnotationsOn(Guid pageKey)
    {
        if (RequestContentChange is null || IndexOf(pageKey) is not { } index || _viewer.Document.Pages[index] is not { Source: { } source } page)
        {
            return [];
        }

        var key = (source, page.SourcePageIndex);
        if (_fileAnnotations.TryGetValue(key, out var annotations))
        {
            return annotations;
        }

        if (_readingFile.Add(key))
        {
            _ = ReadFileAnnotationsAsync(key);
        }

        return [];
    }

    /// <summary>The frame of the selected annotation of the file on a page, where the drag in progress has it, or null.</summary>
    public PdfRect? FileSelectionOn(Guid pageKey)
    {
        if (SelectedFile is not { } target || target.PageKey != pageKey)
        {
            return null;
        }

        var rect = target.Annotation.Rect;
        return _fileMove is { Offset: var offset } && offset != default
            ? new PdfRect(rect.Left + offset.X, rect.Bottom + offset.Y, rect.Right + offset.X, rect.Top + offset.Y)
            : rect;
    }

    /// <summary>A press on an annotation of the file selects it and starts moving it; false when none is there.</summary>
    private bool PressFile(int index, VirtualPage page, PdfPoint point, double tolerance)
    {
        if (page.Source is not { } source || AnnotationHitTest.At(FileAnnotationsOn(page.Key), point, tolerance) is not { } hit)
        {
            return false;
        }

        Selected = null;
        SelectedFile = new FileAnnotationTarget(page.Key, source, hit);
        if (hit.IsMovable)
        {
            _fileMove = new FileMove(SelectedFile, index, point, default);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool DragFile(ViewPoint viewport)
    {
        if (_fileMove is not { } move)
        {
            return false;
        }

        if (PointOn(move.PageIndex, viewport) is { } point)
        {
            _fileMove = move with { Offset = new PdfPoint(point.X - move.Start.X, point.Y - move.Start.Y) };
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>The mouse was released after pressing an annotation of the file: a real drag moves it in the file.</summary>
    private bool ReleaseFile()
    {
        if (_fileMove is not { } move)
        {
            return false;
        }

        _fileMove = null;
        if (Math.Abs(move.Offset.X) >= MoveThreshold || Math.Abs(move.Offset.Y) >= MoveThreshold)
        {
            EditFile(move.Target, FileAnnotationAction.Move, move.Offset.X, move.Offset.Y);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool DeleteFile()
    {
        if (SelectedFile is not { } target)
        {
            return false;
        }

        if (EditFile(target, FileAnnotationAction.Delete, 0, 0))
        {
            SelectedFile = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Moves or removes an annotation of the file: the window writes the next revision.</summary>
    private bool EditFile(FileAnnotationTarget target, FileAnnotationAction action, double dx, double dy)
    {
        if (RequestContentChange is not { } request || IndexOf(target.PageKey) is not { } index)
        {
            return false;
        }

        var annotation = target.Annotation;
        var edit = new FileAnnotationEdit(index + 1, annotation.Index, annotation.Kind, annotation.Name, annotation.Rect, action,
            Author: _author(), When: _time.GetLocalNow(), Dx: Math.Round(dx, 3), Dy: Math.Round(dy, 3));
        var parameters = new EditFileAnnotationsParams([edit]);
        return request(new ContentChangeRequest(_operations.Get<EditFileAnnotationsOperation>(), parameters, HistoryDescriptions.Describe(parameters)));
    }

    /// <summary>The pages changed: a selection read from a file that is no longer shown is dropped, with what was read from it.</summary>
    private void RefreshFile()
    {
        var shown = _viewer.Document.Pages.Where(p => p.Source is not null).Select(p => (p.Source!, p.SourcePageIndex)).ToHashSet();
        foreach (var key in _fileAnnotations.Keys.Where(k => !shown.Contains(k)).ToList())
        {
            _fileAnnotations.Remove(key);
        }

        if (SelectedFile is { } target
            && (IndexOf(target.PageKey) is not { } index || _viewer.Document.Pages[index].Source != target.Source))
        {
            SelectedFile = null;
            _fileMove = null;
        }
    }

    private async Task ReadFileAnnotationsAsync((IPdfDocument Source, int Page) key)
    {
        try
        {
            _fileAnnotations[key] = await key.Source.GetPageAnnotationsAsync(key.Page, PdfiumPriority.Normal);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (PdfDocumentException ex)
        {
            _logger.LogWarning("The annotations of a page could not be read: {Failure}", ex.Failure);
            _fileAnnotations[key] = [];
        }
        catch (ObjectDisposedException)
        {
            // The revision was replaced meanwhile.
        }
        finally
        {
            _readingFile.Remove(key);
        }
    }

    partial void OnSelectedFileChanged(FileAnnotationTarget? value)
    {
        OnPropertyChanged(nameof(HasFileSelection));
        OnPropertyChanged(nameof(HasAnySelection));
        OnPropertyChanged(nameof(IsBarVisible));
        OnPropertyChanged(nameof(ToolText));
    }

    public bool HasFileSelection => SelectedFile is not null;

    private sealed record FileMove(FileAnnotationTarget Target, int PageIndex, PdfPoint Start, PdfPoint Offset);
}
