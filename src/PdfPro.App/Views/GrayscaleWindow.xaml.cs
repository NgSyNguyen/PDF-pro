using PdfPro.App.ViewModels.Optimizing;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Thang xám"; the parameters are read from <see cref="GrayscaleViewModel.Result"/>.</summary>
public partial class GrayscaleWindow : FluentWindow
{
    public GrayscaleWindow(GrayscaleViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
