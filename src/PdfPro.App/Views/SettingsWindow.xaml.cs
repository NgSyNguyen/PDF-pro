using System.Windows;
using PdfPro.App.ViewModels;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
