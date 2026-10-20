using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdfPro.App.ViewModels;
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

    public void ShowDocumentProperties(DocumentTabViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var viewModel = new DocumentPropertiesViewModel(document.Document, document.Workspace.Pages.PageSizes, document.FilePath, services.GetRequiredService<ILogger<DocumentPropertiesViewModel>>());
        var window = new DocumentPropertiesWindow(viewModel) { Owner = Application.Current.MainWindow };
        _ = viewModel.LoadAsync(); // fills in the Info dictionary and file size while the dialog is open
        window.ShowDialog();
    }
}
