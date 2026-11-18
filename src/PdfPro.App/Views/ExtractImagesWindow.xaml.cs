using PdfPro.App.ViewModels.Extracting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Trích ảnh"; the choice is read from <see cref="ExtractImagesViewModel.Result"/>.</summary>
public partial class ExtractImagesWindow : FluentWindow
{
    public ExtractImagesWindow(ExtractImagesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
