using PdfPro.App.ViewModels.Ocr;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Nhận dạng chữ (OCR)"; the choice is read from <see cref="OcrViewModel.Result"/>.</summary>
public partial class OcrWindow : FluentWindow
{
    public OcrWindow(OcrViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
