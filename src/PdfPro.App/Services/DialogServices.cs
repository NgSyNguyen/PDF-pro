using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Organizer;
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

    public string? PickPdfFile(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = Strings.Open_Dialog_Filter,
            Multiselect = false,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickImageFile(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = Strings.Image_Dialog_Filter,
            Multiselect = false,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickImageSavePath(string suggestedFileName, string extension)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.ContentEditor_SaveImage_Title,
            Filter = string.Format(CultureInfo.CurrentCulture, Strings.ContentEditor_SaveImage_Filter, extension),
            FileName = suggestedFileName,
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
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

    public string? PickCommentExportPath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.Comments_ExportDialog_Title,
            Filter = Strings.Comments_ExportDialog_Filter,
            FileName = suggestedFileName,
            DefaultExt = ".md",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickReportSavePath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.Sanitize_ReportDialog_Title,
            Filter = Strings.Sanitize_ReportDialog_Filter,
            FileName = suggestedFileName,
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickFormDataSavePath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.FormData_ExportDialog_Title,
            Filter = Strings.FormData_ExportDialog_Filter,
            FileName = suggestedFileName,
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickFormDataFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.FormData_ImportDialog_Title,
            Filter = Strings.FormData_ImportDialog_Filter,
            Multiselect = false,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickTableFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.FormMerge_DataDialog_Title,
            Filter = Strings.FormMerge_DataDialog_Filter,
            Multiselect = false,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickCertificateFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Sign_CertificateDialog_Title,
            Filter = Strings.Sign_CertificateDialog_Filter,
            Multiselect = false,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        var existing = initialDirectory;
        while (!string.IsNullOrEmpty(existing) && !System.IO.Directory.Exists(existing))
        {
            existing = System.IO.Path.GetDirectoryName(existing);
        }

        if (!string.IsNullOrEmpty(existing))
        {
            dialog.InitialDirectory = existing;
        }

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
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

internal sealed class PageDialogService : IPageDialogService
{
    public bool ShowInsertBlankPages(InsertBlankPagesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new InsertBlankPagesWindow(viewModel));
    }

    public bool ShowInsertPages(InsertPagesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new InsertPagesWindow(viewModel));
    }

    public bool ShowCropPages(CropPagesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new CropPagesWindow(viewModel));
    }

    public bool ShowPageSize(PageSizeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new PageSizeWindow(viewModel));
    }

    public bool ShowSplit(ViewModels.Splitting.SplitViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new SplitWindow(viewModel));
    }

    public bool ShowMerge(ViewModels.Merging.MergeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new MergeWindow(viewModel));
    }

    public bool ShowInterleave(InterleaveViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new InterleaveWindow(viewModel));
    }

    public bool ShowSheetLayout(SheetLayoutViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new SheetLayoutWindow(viewModel));
    }

    public bool ShowSignature(ViewModels.Editing.SignatureViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new SignatureWindow(viewModel));
    }

    public bool ShowStamp(ViewModels.Editing.StampDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new StampWindow(viewModel));
    }

    public bool ShowBates(ViewModels.Editing.BatesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new BatesWindow(viewModel));
    }

    public bool ShowFlatten(ViewModels.Annotating.FlattenViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new FlattenWindow(viewModel));
    }

    public bool ShowFormMerge(ViewModels.Forms.FormMergeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new FormMergeWindow(viewModel));
    }

    public bool ShowSecurity(ViewModels.Security.SecurityViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new SecurityWindow(viewModel));
    }

    public bool ShowRedactionSearch(ViewModels.Security.RedactionSearchViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new RedactionSearchWindow(viewModel));
    }

    public bool ShowRedactionApply(ViewModels.Security.RedactionApplyViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new RedactionApplyWindow(viewModel));
    }

    public bool ShowSanitize(ViewModels.Security.SanitizeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new SanitizeWindow(viewModel));
    }

    public bool ShowSign(ViewModels.Security.SignDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return Show(new SignWindow(viewModel));
    }

    private static bool Show(Window window)
    {
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true;
    }
}
