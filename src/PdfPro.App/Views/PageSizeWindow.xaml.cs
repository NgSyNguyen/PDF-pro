using PdfPro.App.ViewModels.Organizer;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Đổi khổ giấy"; the parameters are read from <see cref="PageSizeViewModel.Result"/>.</summary>
public partial class PageSizeWindow : FluentWindow
{
    public PageSizeWindow(PageSizeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Loaded += (_, _) => FormatBox.Focus();
    }
}
