using System.Windows;
using PdfPro.App.ViewModels.Security;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>
/// "Mật khẩu &amp; quyền". Passwords are handed from the boxes to <see cref="SecurityViewModel"/> as they are typed
/// (never bound); the boxes are cleared when the window closes.
/// </summary>
public partial class SecurityWindow : FluentWindow
{
    private readonly SecurityViewModel _viewModel;

    public SecurityWindow(SecurityViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Loaded += (_, _) => (viewModel.IsViewOnly ? (UIElement)UnlockInput : ProtectChoice.IsChecked == true ? OpenInput : ProtectChoice).Focus();
        Closed += (_, _) => Clear();
    }

    private void OnUnlockChanged(object sender, RoutedEventArgs e) => _viewModel.SetUnlockPassword(UnlockInput.Password);

    private void OnOpenChanged(object sender, RoutedEventArgs e) => _viewModel.SetOpenPassword(OpenInput.Password);

    private void OnOpenConfirmChanged(object sender, RoutedEventArgs e) => _viewModel.SetOpenConfirmation(OpenConfirmInput.Password);

    private void OnOwnerChanged(object sender, RoutedEventArgs e) => _viewModel.SetOwnerPassword(OwnerInput.Password);

    private void OnOwnerConfirmChanged(object sender, RoutedEventArgs e) => _viewModel.SetOwnerConfirmation(OwnerConfirmInput.Password);

    private void Clear()
    {
        _viewModel.Forget();
        foreach (var box in new[] { UnlockInput, OpenInput, OpenConfirmInput, OwnerInput, OwnerConfirmInput })
        {
            box.Password = string.Empty;
        }
    }
}
