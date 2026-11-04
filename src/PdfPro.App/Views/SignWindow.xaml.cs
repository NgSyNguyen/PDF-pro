using System.Windows;
using PdfPro.App.ViewModels.Security;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>
/// "Ký số". The .pfx password is handed from its box to <see cref="SignDialogViewModel"/> as it is typed (never bound);
/// the box is cleared when the window closes.
/// </summary>
public partial class SignWindow : FluentWindow
{
    private readonly SignDialogViewModel _viewModel;

    public SignWindow(SignDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Closed += (_, _) =>
        {
            _viewModel.Forget();
            PfxPasswordInput.Password = string.Empty;
        };
    }

    private void OnPfxPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.SetPfxPassword(PfxPasswordInput.Password);
}
