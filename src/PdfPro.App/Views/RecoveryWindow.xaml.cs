using PdfPro.App.ViewModels;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>The crash recovery dialog; the choice is read from <see cref="RecoveryViewModel"/> after it closes.</summary>
public partial class RecoveryWindow : FluentWindow
{
    public RecoveryWindow(RecoveryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
    }
}
