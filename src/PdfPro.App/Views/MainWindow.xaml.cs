using PdfPro.App.ViewModels;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
