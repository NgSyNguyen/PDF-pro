using System.Windows;
using System.Windows.Input;
using PdfPro.App.ViewModels;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>Main window. Code-behind only handles view concerns: focus (Ctrl+G) and drag-and-drop plumbing.</summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>Ctrl+G: focus the page number box of the active document.</summary>
    public static readonly RoutedCommand GoToPageCommand = new(nameof(GoToPageCommand), typeof(MainWindow));

    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

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
