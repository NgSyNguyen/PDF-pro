using System.Windows.Controls;

namespace PdfPro.App.Views;

/// <summary>
/// Left sidebar: page thumbnails, the bookmark tree and the search panel of the active document. Code-behind only keeps
/// the current page's thumbnail in view and puts the keyboard in the query box when searching is asked for.
/// </summary>
public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();

    /// <summary>Ctrl+F: the query box gets the keyboard, its text selected for typing over.</summary>
    public void FocusSearch()
    {
        // The panel becomes visible with this layout pass; focus it once it can take the keyboard.
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailList.SelectedItem is { } item)
        {
            ThumbnailList.ScrollIntoView(item);
        }
    }
}
