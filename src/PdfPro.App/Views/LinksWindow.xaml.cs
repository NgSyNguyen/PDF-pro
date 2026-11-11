using PdfPro.App.ViewModels.Structure;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>The window of <see cref="LinksViewModel"/>; the result is read from the view model.</summary>
public partial class LinksWindow : FluentWindow
{
    public LinksWindow(LinksViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
