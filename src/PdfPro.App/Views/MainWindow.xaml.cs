using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfPro.App.ViewModels;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>
/// Main window. Code-behind only handles view concerns: focus (Ctrl+G), drag-and-drop plumbing, hiding the sidebar
/// while a document shows its page organizer, and routing the window's X to the exit command.
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>Ctrl+G: focus the page number box of the active document.</summary>
    public static readonly RoutedCommand GoToPageCommand = new(nameof(GoToPageCommand), typeof(MainWindow));

    private GridLength _sidebarWidth;

    public MainWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        _sidebarWidth = SidebarColumn.Width;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    /// <summary>Restores and activates the window (another launch handed its files over).</summary>
    public void BringToFront()
    {
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
