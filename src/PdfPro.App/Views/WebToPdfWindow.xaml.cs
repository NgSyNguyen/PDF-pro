using PdfPro.App.ViewModels.Converting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Trang web sang PDF"; the choice is read from <see cref="WebToPdfViewModel.Result"/>.</summary>
public partial class WebToPdfWindow : FluentWindow
{
    public WebToPdfWindow(WebToPdfViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Loaded += (_, _) => AddressBox.Focus();
    }
}
