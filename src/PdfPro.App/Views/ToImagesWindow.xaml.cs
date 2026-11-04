using PdfPro.App.ViewModels.Converting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"PDF sang ảnh"; the choice is read from <see cref="ToImagesViewModel.Result"/>.</summary>
public partial class ToImagesWindow : FluentWindow
{
    public ToImagesWindow(ToImagesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
