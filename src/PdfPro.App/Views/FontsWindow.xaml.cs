using PdfPro.App.ViewModels.Extracting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Phông chữ": the fonts of the document, listed in the background.</summary>
public partial class FontsWindow : FluentWindow
{
    public FontsWindow(FontsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
