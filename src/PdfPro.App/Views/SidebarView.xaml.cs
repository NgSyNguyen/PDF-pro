using System.Windows.Controls;

namespace PdfPro.App.Views;

/// <summary>
/// Left sidebar: page thumbnails and the bookmark tree of the active document. Code-behind only keeps the current
/// page's thumbnail in view.
/// </summary>
public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();

    private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailList.SelectedItem is { } item)
        {
            ThumbnailList.ScrollIntoView(item);
        }
    }
}
