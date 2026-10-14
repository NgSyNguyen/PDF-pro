using System.Globalization;
using System.Windows;
using PdfPro.App.Resources;
using PdfPro.Infrastructure;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace PdfPro.App.Services;

internal sealed class ErrorDialogService(IShellLauncher shell, AppPaths paths) : IErrorDialogService
{
    public Task ShowUnexpectedErrorAsync(string incidentId)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => ShowUnexpectedErrorOnUiThreadAsync(incidentId)).Task.Unwrap();
    }

    public void ShowFatalError(string incidentId)
        => MessageBox.Show(
            string.Format(CultureInfo.CurrentCulture, Strings.Error_Fatal_Message, incidentId),
            Strings.App_Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private async Task ShowUnexpectedErrorOnUiThreadAsync(string incidentId)
    {
        var dialog = new WpfUiMessageBox
        {
            Title = Strings.Error_Unexpected_Title,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, Strings.Error_Unexpected_Message, incidentId),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
            },
            PrimaryButtonText = Strings.Error_OpenLogFolder,
            CloseButtonText = Strings.Common_Close,
        };

        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }

        var result = await dialog.ShowDialogAsync();
        if (result == WpfUiMessageBoxResult.Primary)
        {
            shell.OpenFolder(paths.LogsDirectory);
        }
    }
}
