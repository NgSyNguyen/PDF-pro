using PdfPro.App.ViewModels.Forms;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Trộn dữ liệu"; the choice is read from <see cref="FormMergeViewModel.Result"/>.</summary>
public partial class FormMergeWindow : FluentWindow
{
    public FormMergeWindow(FormMergeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Loaded += (_, _) => BrowseData.Focus();
    }
}
