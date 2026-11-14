using PdfPro.App.ViewModels.Converting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Ảnh sang PDF"; the choice is read from <see cref="FromImagesViewModel.Result"/>.</summary>
public partial class FromImagesWindow : FluentWindow
{
    public FromImagesWindow(FromImagesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
