using PdfPro.App.ViewModels.Annotating;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Làm phẳng chú thích"; the parameters are read from <see cref="FlattenViewModel.Result"/>.</summary>
public partial class FlattenWindow : FluentWindow
{
    public FlattenWindow(FlattenViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Loaded += (_, _) => FirstKind.Focus();
    }
}
