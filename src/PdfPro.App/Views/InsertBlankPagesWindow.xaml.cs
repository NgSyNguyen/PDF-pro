using PdfPro.App.ViewModels.Organizer;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Chèn trang trắng"; the parameters are read from <see cref="InsertBlankPagesViewModel.Result"/>.</summary>
public partial class InsertBlankPagesWindow : FluentWindow
{
    public InsertBlankPagesWindow(InsertBlankPagesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Loaded += (_, _) => FormatBox.Focus();
    }
}
