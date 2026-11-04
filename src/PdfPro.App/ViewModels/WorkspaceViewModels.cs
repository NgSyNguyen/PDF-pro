using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public HomeTabViewModel(ICommand openFileCommand, ICommand openRecentFileCommand, ICommand mergeCommand)
    {
        OpenFileCommand = openFileCommand;
        OpenRecentFileCommand = openRecentFileCommand;
        MergeCommand = mergeCommand;
        RecentFiles.CollectionChanged += OnRecentFilesChanged;
    }

    public override string Title => Strings.Tab_Home;

    public override SymbolRegular Icon => SymbolRegular.Home24;

    public ICommand OpenFileCommand { get; }

    /// <summary>Takes the full path of a recent file as parameter.</summary>
    public ICommand OpenRecentFileCommand { get; }

    /// <summary>"Gộp tệp PDF" without opening a document first.</summary>
    public ICommand MergeCommand { get; }

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
    Search,
    Comments,
    Signatures,
}

/// <summary>Left sidebar: page thumbnails or the bookmark tree.</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThumbnailsSelected), nameof(IsBookmarksSelected), nameof(IsSearchSelected), nameof(IsCommentsSelected), nameof(IsSignaturesSelected))]
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

    public bool IsSearchSelected
    {
        get => SelectedPanel == SidebarPanel.Search;
        set
        {
            if (value)
            {
                SelectedPanel = SidebarPanel.Search;
            }
        }
    }

    public bool IsCommentsSelected
    {
        get => SelectedPanel == SidebarPanel.Comments;
        set
        {
            if (value)
            {
                SelectedPanel = SidebarPanel.Comments;
            }
        }
    }

    public bool IsSignaturesSelected
    {
        get => SelectedPanel == SidebarPanel.Signatures;
        set
        {
            if (value)
            {
                SelectedPanel = SidebarPanel.Signatures;
            }
        }
    }
}

/// <summary>
/// Bottom status bar: the running background task (text, progress, cancel button). Page and zoom controls belong
/// to the active document. Must be used on the UI thread.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private BackgroundTask? _current;

    public StatusBarViewModel()
    {
        TaskText = Strings.Status_Ready;
    }

    [ObservableProperty]
    public partial string TaskText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelTaskCommand))]
    public partial bool IsTaskRunning { get; private set; }

    /// <summary>Progress of the running task in [0, 1].</summary>
    [ObservableProperty]
    public partial double TaskProgress { get; set; }

    /// <summary>Shows a cancellable task until <see cref="BackgroundTask.Complete"/>; one task runs at a time.</summary>
    /// <exception cref="InvalidOperationException">Another task is still running.</exception>
    public BackgroundTask StartTask(string text)
    {
        if (_current is not null)
        {
            throw new InvalidOperationException("Another background task is running.");
        }

        _current = new BackgroundTask(this);
        TaskText = text;
        TaskProgress = 0;
        IsTaskRunning = true;
        return _current;
    }

    internal void Finish(BackgroundTask task, string text)
    {
        if (!ReferenceEquals(task, _current))
        {
            return;
        }

        _current = null;
        IsTaskRunning = false;
        TaskProgress = 0;
        TaskText = text;
    }

    [RelayCommand(CanExecute = nameof(IsTaskRunning))]
    private void CancelTask() => _current?.Cancel();
}

/// <summary>A task shown in the status bar: report progress, observe <see cref="Token"/>, then complete it.</summary>
public sealed class BackgroundTask
{
    private readonly StatusBarViewModel _owner;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _completed;

    internal BackgroundTask(StatusBarViewModel owner) => _owner = owner;

    /// <summary>Cancelled when the user presses the status bar's cancel button.</summary>
    public CancellationToken Token => _cancellation.Token;

    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    /// <param name="progress">Fraction done in [0, 1].</param>
    public void Report(double progress, string text)
    {
        if (_completed || _cancellation.IsCancellationRequested)
        {
            return;
        }

        _owner.TaskProgress = Math.Clamp(progress, 0, 1);
        _owner.TaskText = text;
    }

    public void Cancel()
    {
        if (_completed || _cancellation.IsCancellationRequested)
        {
            return;
        }

        _cancellation.Cancel();
        _owner.TaskText = Strings.Status_Cancelling;
    }

    /// <summary>Ends the task; <paramref name="text"/> stays in the status bar.</summary>
    public void Complete(string text)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _owner.Finish(this, text);
        _cancellation.Dispose();
    }
}
