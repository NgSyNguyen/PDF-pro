using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;

namespace PdfPro.App.ViewModels.Organizer;

/// <summary>
/// A page of the organizer grid. Its image is loaded only while the grid shows it; the image is upright and the view
/// turns it by <see cref="Angle"/>, so turning a page shows at once.
/// </summary>
public sealed partial class PageTileViewModel : ObservableObject, IRealizable, IThumbnailItem
{
    private readonly PageOrganizerViewModel _owner;

    internal PageTileViewModel(PageOrganizerViewModel owner, VirtualPage page, int index, double boxWidth, SplitMarkBadge badge)
    {
        _owner = owner;
        Key = page.Key;
        Update(page, index, boxWidth, badge);
    }

    public Guid Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number), nameof(AutomationName))]
    public partial int Index { get; private set; }

    /// <summary>1-based page number shown under the tile.</summary>
    public int Number => Index + 1;

    public string AutomationName => string.Format(CultureInfo.CurrentCulture, Strings.Page_AutomationName, Number);

    /// <summary>The box the page is fitted into (the tile size).</summary>
    [ObservableProperty]
    public partial double BoxWidth { get; private set; }

    [ObservableProperty]
    public partial double BoxHeight { get; private set; }

    /// <summary>The page's size in the box, as displayed (turned), known before the image loads.</summary>
    [ObservableProperty]
    public partial double Width { get; private set; }

    [ObservableProperty]
    public partial double Height { get; private set; }

    /// <summary>Clockwise angle the view turns the upright image by (the plan's rotation).</summary>
    [ObservableProperty]
    public partial double Angle { get; private set; }

    /// <summary>Blank pages have no image: the white tile is the page.</summary>
    [ObservableProperty]
    public partial bool IsBlank { get; private set; }

    [ObservableProperty]
    public partial ImageSource? Image { get; internal set; }

    /// <summary>The page could not be rendered; the view shows a warning icon.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; internal set; }

    /// <summary>"Tệp 3" when the page starts a file of a split by marks; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplitStart))]
    public partial string? SplitBadge { get; private set; }

    /// <summary>The page starts a file of a split by marks (the document has marks).</summary>
    public bool IsSplitStart => SplitBadge is not null;

    [ObservableProperty]
    public partial string? SplitToolTip { get; private set; }

    /// <summary>The page belongs to an even-numbered file of the split (tinted, to tell files apart).</summary>
    [ObservableProperty]
    public partial bool IsAlternateFile { get; private set; }

    internal VirtualPage Page { get; private set; } = null!;

    internal ThumbnailRequest? ImageRequest { get; set; }

    ImageSource? IThumbnailItem.Image { get => Image; set => Image = value; }

    ThumbnailRequest? IThumbnailItem.ImageRequest { get => ImageRequest; set => ImageRequest = value; }

    bool IThumbnailItem.LoadFailed { get => LoadFailed; set => LoadFailed = value; }

    public void Realize(double dpiScale) => _owner.Realize(this, dpiScale);

    public void Release() => _owner.Release(this);

    internal void Update(VirtualPage page, int index, double boxWidth, SplitMarkBadge badge)
    {
        Page = page;
        Index = index;
        Angle = page.QuarterTurns * 90;
        IsBlank = page.IsBlank;
        SplitBadge = badge.Text;
        SplitToolTip = badge.ToolTip;
        IsAlternateFile = badge.IsAlternate;
        Resize(boxWidth);
    }

    internal void Resize(double boxWidth)
    {
        BoxWidth = boxWidth;
        BoxHeight = ThumbnailGeometry.BoxHeightFor(boxWidth);
        var size = ThumbnailGeometry.SizeFor(Page.DisplaySize, boxWidth);
        Width = size.Width;
        Height = size.Height;
    }
}
