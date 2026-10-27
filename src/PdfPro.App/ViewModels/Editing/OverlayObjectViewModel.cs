using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.Core.Overlay;

namespace PdfPro.App.ViewModels.Editing;

/// <summary>The tools of the page editor.</summary>
public enum OverlayTool
{
    Select,
    Text,
    Image,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Whiteout,
    Signature,
}

/// <summary>
/// An object placed in the page editor and not applied yet. It belongs to a page by the plan's page key, so moving or
/// turning pages keeps it on its page; <see cref="Item"/>'s page index is only filled in when applying.
/// </summary>
public sealed partial class OverlayObjectViewModel : ObservableObject
{
    public OverlayObjectViewModel(Guid pageKey, OverlayItem item, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        Id = id ?? Guid.NewGuid();
        PageKey = pageKey;
        Item = item;
    }

    public Guid Id { get; }

    public Guid PageKey { get; }

    [ObservableProperty]
    public partial OverlayItem Item { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
