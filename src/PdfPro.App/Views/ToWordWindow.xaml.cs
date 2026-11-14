using PdfPro.App.ViewModels.Converting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"PDF sang Word"; the choice is read from <see cref="ToWordViewModel.Result"/>.</summary>
public partial class ToWordWindow : FluentWindow
{
    public ToWordWindow(ToWordViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
