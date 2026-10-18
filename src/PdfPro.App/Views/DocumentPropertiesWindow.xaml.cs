using System.Windows;
using PdfPro.App.ViewModels;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

public partial class DocumentPropertiesWindow : FluentWindow
{
    public DocumentPropertiesWindow(DocumentPropertiesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
