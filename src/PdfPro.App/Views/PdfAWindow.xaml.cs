using PdfPro.App.ViewModels.Converting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"PDF/A"; the choice is read from <see cref="PdfAViewModel.Result"/>.</summary>
public partial class PdfAWindow : FluentWindow
{
    public PdfAWindow(PdfAViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
