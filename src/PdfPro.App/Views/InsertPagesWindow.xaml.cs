using PdfPro.App.ViewModels.Organizer;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Chèn trang từ tệp"; the choice is read from <see cref="InsertPagesViewModel.Result"/>.</summary>
public partial class InsertPagesWindow : FluentWindow
{
    public InsertPagesWindow(InsertPagesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Loaded += (_, _) => InsertButton.Focus();
    }
}
