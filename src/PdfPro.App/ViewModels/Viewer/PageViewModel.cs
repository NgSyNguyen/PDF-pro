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
/// Images are dropped when the page scrolls out of view — the render cache keeps them. The same instance follows the
/// page (by <see cref="Key"/>) when the page plan moves it, so its image stays on screen.
/// </summary>
public sealed partial class PageViewModel : ObservableObject
{
    public PageViewModel(int index)
        : this(Guid.NewGuid(), index)
    {
    }

    public PageViewModel(Guid key, int index)
    {
        Key = key;
        MoveTo(index);
    }

    /// <summary>The page plan's key of the page.</summary>
    public Guid Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number), nameof(AutomationName))]
    public partial int Index { get; private set; }

    /// <summary>1-based page number shown to the user.</summary>
    public int Number => Index + 1;

    public string AutomationName => string.Format(CultureInfo.CurrentCulture, Strings.Page_AutomationName, Number);

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    /// <summary>The plan's rotation of the page, in clockwise quarter turns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageAngle))]
    public partial int QuarterTurns { get; private set; }

    /// <summary>The rotation <see cref="Image"/> was rendered with (an older image, or an upright thumbnail).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageAngle))]
    public partial int ImageQuarterTurns { get; set; }

    /// <summary>How far the view turns <see cref="Image"/> so it matches the page until a render with the right rotation arrives.</summary>
    public double ImageAngle => ((((QuarterTurns - ImageQuarterTurns) % 4) + 4) % 4) * 90;

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

    /// <summary>The page moved in the plan and/or turned.</summary>
    internal void MoveTo(int index, int quarterTurns = 0)
    {
        Index = index;
        if (QuarterTurns != quarterTurns)
        {
            QuarterTurns = quarterTurns;
            ClearDetail(); // the tile no longer matches the turned page
        }
    }

    internal void ClearImages()
    {
        Image = null;
        ImageKey = null;
        ImageQuarterTurns = QuarterTurns;
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
