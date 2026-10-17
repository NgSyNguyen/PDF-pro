using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.App.Resources;
using Wpf.Ui.Controls;

namespace PdfPro.App.ViewModels;

/// <summary>A tab in the central workspace: the home tab or an open document.</summary>
public abstract class WorkspaceTabViewModel : ObservableObject
{
    public abstract string Title { get; }

    public virtual string ToolTip => Title;

    public abstract SymbolRegular Icon { get; }

    public virtual bool CanClose => false;
}

/// <summary>Start page: open button, drop zone, recent files.</summary>
public sealed class HomeTabViewModel : WorkspaceTabViewModel
{
    public HomeTabViewModel(ICommand openFileCommand, ICommand openRecentFileCommand)
    {
        OpenFileCommand = openFileCommand;
        OpenRecentFileCommand = openRecentFileCommand;
        RecentFiles.CollectionChanged += OnRecentFilesChanged;
    }

    public override string Title => Strings.Tab_Home;

    public override SymbolRegular Icon => SymbolRegular.Home24;

    public ICommand OpenFileCommand { get; }

    /// <summary>Takes the full path of a recent file as parameter.</summary>
    public ICommand OpenRecentFileCommand { get; }

    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];

    public bool HasNoRecentFiles => RecentFiles.Count == 0;

    private void OnRecentFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasNoRecentFiles));
}

/// <summary>An entry of the recent files list.</summary>
public sealed record RecentFileViewModel(string Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
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

/// <summary>Bottom status bar: background task progress (page and zoom controls belong to the active document).</summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    public StatusBarViewModel()
    {
        TaskText = Strings.Status_Ready;
    }

    [ObservableProperty]
    public partial string TaskText { get; set; }

    [ObservableProperty]
    public partial bool IsTaskRunning { get; set; }

    /// <summary>Progress of the running task in [0, 1].</summary>
    [ObservableProperty]
    public partial double TaskProgress { get; set; }
}
