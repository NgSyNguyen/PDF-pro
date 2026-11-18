using PdfPro.App.ViewModels.Structure;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>The window of <see cref="PageLabelsViewModel"/>; the result is read from the view model.</summary>
public partial class PageLabelsWindow : FluentWindow
{
    public PageLabelsWindow(PageLabelsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
