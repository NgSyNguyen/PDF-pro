using PdfPro.App.ViewModels.Converting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Office sang PDF"; the choice is read from <see cref="OfficeToPdfViewModel.Result"/>.</summary>
public partial class OfficeToPdfWindow : FluentWindow
{
    public OfficeToPdfWindow(OfficeToPdfViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
