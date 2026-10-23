using PdfPro.App.ViewModels.Splitting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Tách"; the choice is read from <see cref="SplitViewModel.Result"/>.</summary>
public partial class SplitWindow : FluentWindow
{
    public SplitWindow(SplitViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
