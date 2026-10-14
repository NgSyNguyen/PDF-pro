using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.App.Resources;

namespace PdfPro.App.ViewModels;

/// <summary>A tab in the central workspace: the home tab now, document tabs from Phase 2.</summary>
public abstract class WorkspaceTabViewModel : ObservableObject
{
    public abstract string Title { get; }

    public virtual bool CanClose => false;
}

/// <summary>Start page shown when no document is open: open button, drop zone, recent files.</summary>
public sealed class HomeTabViewModel : WorkspaceTabViewModel
{
    public HomeTabViewModel()
    {
        RecentFiles.CollectionChanged += OnRecentFilesChanged;
    }

    public override string Title => Strings.Tab_Home;

    /// <summary>Opening documents arrives with the PDFium engine in Phase 2.</summary>
    public ICommand OpenFileCommand { get; } = UnavailableCommand.Instance;

    public ObservableCollection<string> RecentFiles { get; } = [];

    public bool HasNoRecentFiles => RecentFiles.Count == 0;

    private void OnRecentFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasNoRecentFiles));
}

public enum SidebarPanel
{
    Thumbnails,
    Bookmarks,
}

/// <summary>Left sidebar: page thumbnails or the bookmark tree.</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThumbnailsSelected), nameof(IsBookmarksSelected))]
    public partial SidebarPanel SelectedPanel { get; set; }

    public bool IsThumbnailsSelected
    {
        get => SelectedPanel == SidebarPanel.Thumbnails;
        set
        {
            if (value)
            {
                SelectedPanel = SidebarPanel.Thumbnails;
            }
        }
    }

    public bool IsBookmarksSelected
    {
        get => SelectedPanel == SidebarPanel.Bookmarks;
        set
        {
            if (value)
            {
                SelectedPanel = SidebarPanel.Bookmarks;
            }
        }
    }
}

/// <summary>Bottom status bar: current page, zoom and background task progress.</summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private const string NoValue = "—";

    public StatusBarViewModel()
    {
        PageText = Format(Strings.Status_Page, NoValue);
        ZoomText = Format(Strings.Status_Zoom, NoValue);
        TaskText = Strings.Status_Ready;
    }

    [ObservableProperty]
    public partial string PageText { get; set; }

    [ObservableProperty]
    public partial string ZoomText { get; set; }

    [ObservableProperty]
    public partial string TaskText { get; set; }

    [ObservableProperty]
    public partial bool IsTaskRunning { get; set; }

    /// <summary>Progress of the running task in [0, 1].</summary>
    [ObservableProperty]
    public partial double TaskProgress { get; set; }

    private static string Format(string template, string value) => string.Format(CultureInfo.CurrentCulture, template, value);
}
