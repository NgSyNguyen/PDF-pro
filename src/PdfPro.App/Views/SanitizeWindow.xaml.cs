using PdfPro.App.ViewModels.Security;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Làm sạch tài liệu": what to remove.</summary>
public partial class SanitizeWindow : FluentWindow
{
    public SanitizeWindow(SanitizeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
