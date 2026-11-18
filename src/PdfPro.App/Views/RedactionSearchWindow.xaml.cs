using PdfPro.App.ViewModels.Security;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Tìm &amp; đánh dấu"; the marks are read from <see cref="RedactionSearchViewModel.Result"/>.</summary>
public partial class RedactionSearchWindow : FluentWindow
{
    public RedactionSearchWindow(RedactionSearchViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Closed += (_, _) => viewModel.Dispose();
        Loaded += (_, _) => FirstPattern.Focus();
    }
}
