using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Services;

/// <summary>Applies the Light/Dark/System theme to every window.</summary>
public interface IAppThemeService
{
    AppThemeMode Mode { get; }

    void Apply(AppThemeMode mode);
}

/// <summary>Opens the application's dialogs (keeps ViewModels free of window types).</summary>
public interface IDialogService
{
    void ShowSettings();

    /// <summary>The properties dialog of an open document (Ctrl+D).</summary>
    void ShowDocumentProperties(ViewModels.DocumentTabViewModel document);
}

/// <summary>Friendly error messages for unhandled exceptions.</summary>
public interface IErrorDialogService
{
    /// <summary>Shows the "something went wrong, the app keeps running" dialog. Safe to call from any thread.</summary>
    Task ShowUnexpectedErrorAsync(string incidentId);

    /// <summary>Shows a blocking message before the process terminates. Safe to call from any thread.</summary>
    void ShowFatalError(string incidentId);
}

/// <summary>Application lifetime actions. Both ask about unsaved documents first.</summary>
public interface IAppLifetime
{
    /// <summary>Closes the application. False when the user cancelled (the application keeps running).</summary>
    Task<bool> ShutdownAsync();

    /// <summary>Starts a new instance and closes this one. False when the user cancelled or no instance could start.</summary>
    Task<bool> RestartAsync();
}

/// <summary>Asks about unsaved documents before the application closes (the main window's view model).</summary>
public interface IExitGuard
{
    /// <summary>Every unsaved document is saved or discarded as the user decides. False when the user cancelled.</summary>
    Task<bool> ConfirmExitAsync();

    /// <summary>The application did not close after all: ask again next time.</summary>
    void CancelExit();
}

/// <summary>Hands paths over to Windows Explorer.</summary>
public interface IShellLauncher
{
    void OpenFolder(string path);
}

/// <summary>Opens PDF files for the UI: asks for passwords and explains failures to the user.</summary>
public interface IDocumentOpener
{
    /// <summary>
    /// Returns the opened document, or null when the user cancelled the password prompt or the file could not
    /// be opened (the user has already been told why).
    /// </summary>
    /// <param name="path">The file to open (a copy in a session folder).</param>
    /// <param name="displayName">The name shown in prompts and messages (the original file name).</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    Task<Pdfium.IPdfDocument?> OpenAsync(string path, string displayName, CancellationToken cancellationToken = default);
}

/// <summary>Asks the user for a document password. Never stores it.</summary>
public interface IPasswordPrompt
{
    /// <returns>The password, or null when the user cancelled.</returns>
    string? RequestPassword(string fileName, bool isRetry);
}

/// <summary>The button the user chose in a question.</summary>
public enum DialogChoice
{
    Primary,
    Secondary,

    /// <summary>The close button, Esc or the window's X.</summary>
    Cancel,
}

/// <summary>Modal messages for the user.</summary>
public interface IMessageService
{
    Task ShowErrorAsync(string title, string message);

    Task ShowInfoAsync(string title, string message);

    /// <summary>A question with up to two answers and a cancel button (e.g. Lưu / Không lưu / Hủy).</summary>
    /// <param name="title">Window title.</param>
    /// <param name="message">The question.</param>
    /// <param name="primary">The main answer.</param>
    /// <param name="secondary">The other answer, or null for none.</param>
    /// <param name="cancel">The cancel button text.</param>
    Task<DialogChoice> AskAsync(string title, string message, string primary, string? secondary, string cancel);
}

/// <summary>Standard file dialogs.</summary>
public interface IFileDialogService
{
    /// <returns>Selected PDF files, empty when cancelled.</returns>
    IReadOnlyList<string> PickPdfFiles();

    /// <summary>Asks where to save a PDF ("Lưu thành"); overwriting asks for confirmation.</summary>
    /// <param name="suggestedFileName">File name proposed in the dialog.</param>
    /// <param name="initialDirectory">Folder shown first, or null.</param>
    /// <returns>The chosen path, or null when cancelled.</returns>
    string? PickSavePath(string suggestedFileName, string? initialDirectory);
}
