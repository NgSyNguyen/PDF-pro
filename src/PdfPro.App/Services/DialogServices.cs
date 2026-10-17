using System.Windows;
using Microsoft.Win32;
using PdfPro.App.Resources;
using PdfPro.App.Views;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;

namespace PdfPro.App.Services;

internal sealed class MessageService : IMessageService
{
    public Task ShowErrorAsync(string title, string message) => ShowAsync(title, message);

    public Task ShowInfoAsync(string title, string message) => ShowAsync(title, message);

    private static async Task ShowAsync(string title, string message)
    {
        var dialog = new WpfUiMessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            CloseButtonText = Strings.Common_Close,
        };

        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }

        await dialog.ShowDialogAsync();
    }
}

internal sealed class FileDialogService : IFileDialogService
{
    public IReadOnlyList<string> PickPdfFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Open_Dialog_Title,
            Filter = Strings.Open_Dialog_Filter,
            Multiselect = true,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileNames : [];
    }
}

internal sealed class PasswordPrompt : IPasswordPrompt
{
    public string? RequestPassword(string fileName, bool isRetry)
    {
        var window = new PasswordWindow(fileName, isRetry);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true ? window.TakePassword() : null;
    }
}
