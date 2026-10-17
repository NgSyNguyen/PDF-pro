using System.Windows.Controls;
using System.Windows.Input;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Views;

/// <summary>
/// Document view. Code-behind only routes input to the view model: Ctrl + wheel zooms around the mouse,
/// Home/End/PageUp/PageDown navigate by page (arrow keys and the wheel scroll through the ScrollViewer).
/// </summary>
public partial class DocumentView : UserControl
{
    private const double WheelDeltaPerNotch = 120.0;

    public DocumentView() => InitializeComponent();

    private DocumentViewerViewModel? Viewer => Scroller.DataContext as DocumentViewerViewModel;

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

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => Scroller.Focus();
}
