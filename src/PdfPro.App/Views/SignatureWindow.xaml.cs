using PdfPro.App.ViewModels.Editing;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Chữ ký tay"; the signature is read from <see cref="SignatureViewModel.Result"/>.</summary>
public partial class SignatureWindow : FluentWindow
{
    public SignatureWindow(SignatureViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
