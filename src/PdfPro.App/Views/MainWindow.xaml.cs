using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services.Automation;
using PdfPro.App.ViewModels;
using PdfPro.Infrastructure.Settings;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>
/// Main window. Code-behind only handles view concerns: focus (Ctrl+G), drag-and-drop plumbing, hiding the sidebar
/// while a document shows its page organizer, routing the window's X to the exit command, and the notification area
/// icon while the hot folder runs with the window minimized.
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>Ctrl+G: focus the page number box of the active document.</summary>
    public static readonly RoutedCommand GoToPageCommand = new(nameof(GoToPageCommand), typeof(MainWindow));

    /// <summary>The notification area allows 63 characters of text.</summary>
    private const int TrayTextLength = 63;

    private readonly IHotFolderService? _hotFolder;
    private readonly ISettingsService? _settings;
    private GridLength _sidebarWidth;
    private System.Windows.Forms.NotifyIcon? _tray;

    public MainWindow(MainWindowViewModel viewModel, IHotFolderService? hotFolder = null, ISettingsService? settings = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        _hotFolder = hotFolder;
        _settings = settings;
        InitializeComponent();
        _sidebarWidth = SidebarColumn.Width;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.SearchRequested += (_, _) => Sidebar.FocusSearch();
        if (hotFolder is not null)
        {
            hotFolder.Changed += (_, _) => Dispatcher.InvokeAsync(UpdateTray);
        }
    }

    /// <summary>The hot folder runs and the settings say minimizing puts the window in the notification area.</summary>
    public bool RunsInTray => _hotFolder?.IsRunning == true && _settings?.Current.MinimizeToTray != false;

    /// <summary>Hides the window behind an icon in the notification area (double click or "Mở" brings it back).</summary>
    public void HideToTray()
    {
        _tray ??= CreateTray();
        UpdateTray();
        _tray.Visible = true;
        Hide();
    }

    /// <summary>Shows the window again from the notification area.</summary>
    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (_tray is not null)
        {
            _tray.Visible = false;
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && RunsInTray)
        {
            HideToTray();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    /// <summary>Restores and activates the window (another launch handed its files over).</summary>
    public void BringToFront()
    {
        if (!IsVisible)
        {
            RestoreFromTray();
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>
    /// The window's X (or Alt+F4) with unsaved documents: the close waits while the exit command asks Lưu / Không lưu /
    /// Hủy; it closes the application once everything is saved or discarded.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || ViewModel.ExitConfirmed || !ViewModel.HasUnsavedDocuments)
        {
            return;
        }

        e.Cancel = true;
        if (ViewModel.ExitCommand.CanExecute(null))
        {
            ViewModel.ExitCommand.Execute(null);
        }
    }

    private System.Windows.Forms.NotifyIcon CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Strings.Tray_Open, null, (_, _) => RestoreFromTray());
        menu.Items.Add(Strings.Tray_Exit, null, (_, _) =>
        {
            RestoreFromTray();
            if (ViewModel.ExitCommand.CanExecute(null))
            {
                ViewModel.ExitCommand.Execute(null);
            }
        });
        var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = Environment.ProcessPath is { } program ? System.Drawing.Icon.ExtractAssociatedIcon(program) : System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => RestoreFromTray();
        return tray;
    }

    private void UpdateTray()
    {
        if (_tray is null)
        {
            return;
        }

        var text = _hotFolder is { } hotFolder ? $"{Strings.Tray_Text} — {hotFolder.Status}" : Strings.Tray_Text;
        _tray.Text = text.Length <= TrayTextLength ? text : text[..(TrayTextLength - 1)] + "…";
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsOrganizing))
        {
            ShowSidebar(!ViewModel.IsOrganizing);
        }
    }

    /// <summary>The organizer grid uses the sidebar's room; its width (set with the splitter) comes back afterwards.</summary>
    private void ShowSidebar(bool show)
    {
        var isShown = Sidebar.Visibility == Visibility.Visible;
        if (show == isShown)
        {
            return;
        }

        if (show)
        {
            SidebarColumn.MinWidth = 220;
            SidebarColumn.Width = _sidebarWidth;
        }
        else
        {
            _sidebarWidth = SidebarColumn.Width;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
        }

        Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGoToPage(object sender, ExecutedRoutedEventArgs e)
    {
        if (ViewModel.HasActiveDocument)
        {
            PageNumberBox.Focus();
            PageNumberBox.SelectAll();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            Activate();
            await ViewModel.OpenFilesAsync(files, pdfOnly: true);
        }
    }
}
