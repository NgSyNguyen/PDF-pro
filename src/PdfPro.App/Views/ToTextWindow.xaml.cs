using PdfPro.App.ViewModels.Converting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"PDF sang văn bản"; the choice is read from <see cref="ToTextViewModel.Result"/>.</summary>
public partial class ToTextWindow : FluentWindow
{
    public ToTextWindow(ToTextViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
