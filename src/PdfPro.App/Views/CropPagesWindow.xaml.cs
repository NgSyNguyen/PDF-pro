using PdfPro.App.ViewModels.Organizer;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Cắt lề"; the parameters are read from <see cref="CropPagesViewModel.Result"/>.</summary>
public partial class CropPagesWindow : FluentWindow
{
    public CropPagesWindow(CropPagesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Loaded += (_, _) =>
        {
            LeftBox.Focus();
            LeftBox.SelectAll();
        };
        Closed += (_, _) => viewModel.Close();
    }
}
