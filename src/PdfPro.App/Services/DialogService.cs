using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PdfPro.App.Views;

namespace PdfPro.App.Services;

internal sealed class DialogService(IServiceProvider services) : IDialogService
{
    public void ShowSettings()
    {
        var window = services.GetRequiredService<SettingsWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }
}
