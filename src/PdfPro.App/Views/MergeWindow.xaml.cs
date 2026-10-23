using PdfPro.App.ViewModels.Merging;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Gộp"; the choice is read from <see cref="MergeViewModel.Result"/>.</summary>
public partial class MergeWindow : FluentWindow
{
    public MergeWindow(MergeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
