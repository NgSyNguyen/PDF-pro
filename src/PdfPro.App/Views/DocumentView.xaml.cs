using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Annotating;
using PdfPro.App.ViewModels.Editing;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Views;

/// <summary>
/// Document view. Code-behind only routes input to the view model: Ctrl + wheel zooms around the mouse,
/// Home/End/PageUp/PageDown navigate by page (arrow keys and the wheel scroll through the ScrollViewer). While the
/// page editor is open, Delete, the arrow keys (1 point, with Shift 10), Ctrl+C, Ctrl+V, Ctrl+A and Esc act on its
/// objects; otherwise the mouse selects text (the text cursor shows over it) and Ctrl+C copies it, Ctrl+A selects the
/// whole document's text, Esc clears the selection.
/// </summary>
public partial class DocumentView : UserControl
{
    private const double WheelDeltaPerNotch = 120.0;

    public DocumentView() => InitializeComponent();

    private DocumentViewerViewModel? Viewer => Scroller.DataContext as DocumentViewerViewModel;

    private DocumentTabViewModel? Document => DataContext as DocumentTabViewModel;

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || Viewer is not { } viewer)
        {
            return;
        }

        var position = e.GetPosition(Surface);
        viewer.ZoomAtPoint(e.Delta / WheelDeltaPerNotch, new ViewPoint(position.X, position.Y));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Document is { Editor.IsActive: true } document && HandleEditorKey(document, e.Key))
        {
            e.Handled = true;
            return;
        }

        if (Document is { Editor.IsActive: false } annotating && HandleAnnotationKey(annotating.Annotations, e.Key))
        {
            e.Handled = true;
            return;
        }

        if (Document is { Editor.IsActive: false } reader && HandleSelectionKey(reader.TextSelection, e.Key))
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None || Viewer is not { } viewer)
        {
            return;
        }

        var command = e.Key switch
        {
            Key.PageDown => viewer.NextPageCommand,
            Key.PageUp => viewer.PreviousPageCommand,
            Key.Home => viewer.FirstPageCommand,
            Key.End => viewer.LastPageCommand,
            _ => null,
        };

        if (command is not null)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Delete removes the selected annotation; Esc closes a comment, turns a tool off, or drops the selection.</summary>
    private static bool HandleAnnotationKey(AnnotationToolsViewModel annotations, Key key)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        switch (key)
        {
            case Key.Delete when annotations.CanDelete:
                annotations.DeleteCommand.Execute(null);
                return true;
            case Key.Escape:
                return annotations.Escape();
            default:
                return false;
        }
    }

    /// <summary>The comment box takes the keyboard as soon as it opens.</summary>
    private void OnContentsPopupOpened(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() =>
        {
            ContentsBox.Focus();
            ContentsBox.CaretIndex = ContentsBox.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>Clicking away from the comment keeps what was written (as other PDF editors do).</summary>
    private void OnContentsPopupClosed(object? sender, EventArgs e)
    {
        if (Document is { Annotations.IsEditingContents: true } document)
        {
            document.Annotations.CommitContentsCommand.Execute(null);
        }

        Scroller.Focus();
    }

    /// <summary>Esc closes the comment without keeping it; Ctrl+Enter keeps it.</summary>
    private void OnContentsKeyDown(object sender, KeyEventArgs e)
    {
        if (Document is not { } document)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            document.Annotations.CancelContentsCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            document.Annotations.CommitContentsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool HandleSelectionKey(TextSelectionViewModel selection, Key key)
    {
        var control = Keyboard.Modifiers == ModifierKeys.Control;
        switch (key)
        {
            case Key.C when control && selection.HasSelection:
                _ = CopyAsync(selection);
                return true;
            case Key.A when control:
                selection.SelectAll();
                return true;
            case Key.Escape when selection.HasSelection:
                selection.Clear();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Puts the selected text on the Windows clipboard (left as it was when the clipboard is busy).</summary>
    private static async Task CopyAsync(TextSelectionViewModel selection)
    {
        var text = await selection.GetTextAsync();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program holds the clipboard; copying again will work.
        }
    }

    private void OnSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Document is { Editor.IsActive: false } document)
        {
            var position = e.GetPosition(Surface);
            var point = new ViewPoint(position.X, position.Y);
            if (document.Annotations.Press(point, e.ClickCount))
            {
                // A note was placed or an annotation picked (and maybe grabbed to move it).
                if (document.Annotations.IsMoving)
                {
                    Surface.CaptureMouse();
                }

                return;
            }

            _ = PressAsync(document.TextSelection, point, e.ClickCount);
        }
    }

    private async Task PressAsync(TextSelectionViewModel selection, ViewPoint point, int clickCount)
    {
        if (await selection.PressAsync(point, clickCount) && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            Surface.CaptureMouse();
        }
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (Document is not { Editor.IsActive: false } document)
        {
            Surface.ClearValue(CursorProperty);
            return;
        }

        var position = e.GetPosition(Surface);
        var point = new ViewPoint(position.X, position.Y);
        if (document.Annotations.IsMoving && e.LeftButton == MouseButtonState.Pressed)
        {
            document.Annotations.DragTo(point);
            return;
        }

        if (document.TextSelection.IsDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            document.TextSelection.DragTo(point);
            return;
        }

        if (document.Annotations.Tool == AnnotationTool.Note)
        {
            Surface.Cursor = Cursors.Cross;
        }
        else if (document.TextSelection.IsOverText(point))
        {
            Surface.Cursor = Cursors.IBeam;
        }
        else
        {
            Surface.ClearValue(CursorProperty);
        }
    }

    private void OnSurfaceMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Document is { } document)
        {
            if (document.TextSelection.IsDragging)
            {
                document.TextSelection.Release();
                _ = document.Annotations.SelectionReleasedAsync(); // marked at once while a markup tool is on
            }

            document.Annotations.Release();
        }

        if (Surface.IsMouseCaptured)
        {
            Surface.ReleaseMouseCapture();
        }
    }

    private static bool HandleEditorKey(DocumentTabViewModel document, Key key)
    {
        var editor = document.Editor;
        if (editor.EditingText is not null)
        {
            // Typing: every key belongs to the text box (it ends typing on Esc itself).
            return false;
        }

        var control = Keyboard.Modifiers == ModifierKeys.Control;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var currentPage = document.Workspace.Pages.Pages is { Count: > 0 } pages
            ? pages[Math.Clamp(document.Viewer.CurrentPageIndex, 0, pages.Count - 1)].Key
            : Guid.Empty;
        switch (key)
        {
            case Key.Delete when editor.HasSelection:
                editor.DeleteCommand.Execute(null);
                return true;
            case Key.Left when !control:
                return editor.Nudge(-step, 0);
            case Key.Right when !control:
                return editor.Nudge(step, 0);
            case Key.Up when !control:
                return editor.Nudge(0, -step);
            case Key.Down when !control:
                return editor.Nudge(0, step);
            case Key.C when control && editor.HasSelection:
                editor.CopyCommand.Execute(null);
                MarkClipboard();
                return true;
            case Key.V when control:
                Paste(document, currentPage);
                return true;
            case Key.A when control:
                editor.SelectAll(currentPage);
                return true;
            case Key.Escape when editor.Tool != OverlayTool.Select:
                editor.Tool = OverlayTool.Select;
                return true;
            case Key.Escape when editor.HasSelection:
                editor.ClearSelection();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Objects copied in the editor stay in the editor; the Windows clipboard gets a marker so that a later Ctrl+V knows
    /// whether to paste them or what another program copied since.
    /// </summary>
    private static void MarkClipboard()
    {
        try
        {
            Clipboard.SetDataObject(new DataObject(EditorClipboardFormat, Guid.NewGuid().ToString("N")), copy: false);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy: pasting falls back to the editor's own copy.
        }
    }

    /// <summary>Ctrl+V: the editor's own objects, or a picture or text another program copied.</summary>
    private static void Paste(DocumentTabViewModel document, Guid currentPage)
    {
        try
        {
            if (!Clipboard.ContainsData(EditorClipboardFormat) && Clipboard.ContainsImage() && Clipboard.GetImage() is { } picture)
            {
                _ = document.PastePictureAsync(picture);
                return;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy: paste what the editor copied.
        }

        document.Editor.PasteOn(currentPage);
    }

    private const string EditorClipboardFormat = "PdfPro.EditorObjects";

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => Scroller.Focus();
}
