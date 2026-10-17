using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.App.Resources;
using PdfPro.App.Services.Rendering;

namespace PdfPro.App.ViewModels.Viewer;

/// <summary>
/// One page in the viewer. <see cref="Image"/> is the whole page (possibly at a lower resolution while zooming or
/// for very large pages); <see cref="DetailImage"/> is a sharp render of the visible part at high zoom.
/// Images are dropped when the page scrolls out of view — the render cache keeps them.
/// </summary>
public sealed partial class PageViewModel : ObservableObject
{
    public PageViewModel(int index)
    {
        Index = index;
        Number = index + 1;
        AutomationName = string.Format(CultureInfo.CurrentCulture, Strings.Page_AutomationName, Number);
    }

    public int Index { get; }

    /// <summary>1-based page number shown to the user.</summary>
    public int Number { get; }

    public string AutomationName { get; }

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    [ObservableProperty]
    public partial ImageSource? DetailImage { get; set; }

    /// <summary>Where <see cref="DetailImage"/> goes, in view units relative to the page's top-left.</summary>
    [ObservableProperty]
    public partial Rect DetailBounds { get; set; }

    /// <summary>The page could not be rendered (damaged content); the view shows a warning instead of the page.</summary>
    [ObservableProperty]
    public partial bool RenderFailed { get; set; }

    internal RenderKey? ImageKey { get; set; }

    internal RenderKey? DetailKey { get; set; }

    /// <summary>The detail region as fractions of the page (x, y, width, height in 0..1), independent of the zoom.</summary>
    internal Rect DetailFraction { get; set; }

    internal void ClearImages()
    {
        Image = null;
        ImageKey = null;
        ClearDetail();
    }

    internal void ClearDetail()
    {
        DetailImage = null;
        DetailKey = null;
        DetailFraction = Rect.Empty;
        DetailBounds = Rect.Empty;
    }
}
