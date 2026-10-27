using System.Windows.Controls;
using System.Windows.Input;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Editing;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Views;

/// <summary>
/// Document view. Code-behind only routes input to the view model: Ctrl + wheel zooms around the mouse,
/// Home/End/PageUp/PageDown navigate by page (arrow keys and the wheel scroll through the ScrollViewer). While the
/// page editor is open, Delete, the arrow keys (1 point, with Shift 10), Ctrl+C, Ctrl+V, Ctrl+A and Esc act on its
/// objects.
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

    private static bool HandleEditorKey(DocumentTabViewModel document, Key key)
    {
        var editor = document.Editor;
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
                return true;
            case Key.V when control:
                editor.PasteOn(currentPage);
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

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => Scroller.Focus();
}
