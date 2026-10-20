using System.Windows;
using Microsoft.Win32;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels;
using PdfPro.App.Views;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;

namespace PdfPro.App.Services;

internal sealed class MessageService : IMessageService
{
    public Task ShowErrorAsync(string title, string message) => ShowAsync(title, message);

    public Task ShowInfoAsync(string title, string message) => ShowAsync(title, message);

    public async Task<DialogChoice> AskAsync(string title, string message, string primary, string? secondary, string cancel)
    {
        var dialog = Create(title, message, cancel);
        dialog.PrimaryButtonText = primary;
        dialog.PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary;
        dialog.IsPrimaryButtonEnabled = true;
        if (secondary is not null)
        {
            dialog.SecondaryButtonText = secondary;
            dialog.IsSecondaryButtonEnabled = true;
        }

        return await dialog.ShowDialogAsync() switch
        {
            Wpf.Ui.Controls.MessageBoxResult.Primary => DialogChoice.Primary,
            Wpf.Ui.Controls.MessageBoxResult.Secondary => DialogChoice.Secondary,
            _ => DialogChoice.Cancel,
        };
    }

    private static async Task ShowAsync(string title, string message) => await Create(title, message, Strings.Common_Close).ShowDialogAsync();

    private static WpfUiMessageBox Create(string title, string message, string closeButton)
    {
        var dialog = new WpfUiMessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            CloseButtonText = closeButton,
        };

        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }

        return dialog;
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

    public string? PickSavePath(string suggestedFileName, string? initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.Save_Dialog_Title,
            Filter = Strings.Save_Dialog_Filter,
            FileName = suggestedFileName,
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }
}

internal sealed class RecoveryDialog : IRecoveryDialog
{
    public RecoveryDecision Ask(IReadOnlyList<RecoveryItem> items)
    {
        var viewModel = new RecoveryViewModel(items);
        var window = new RecoveryWindow(viewModel);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            window.Owner = owner;
        }

        window.ShowDialog();
        return viewModel.TakeDecision();
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
