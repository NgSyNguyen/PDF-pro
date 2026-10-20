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

/// <summary>Application lifetime actions.</summary>
public interface IAppLifetime
{
    void Shutdown();

    /// <summary>Starts a new instance and closes this one.</summary>
    void Restart();
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

/// <summary>Modal messages for the user.</summary>
public interface IMessageService
{
    Task ShowErrorAsync(string title, string message);

    Task ShowInfoAsync(string title, string message);
}

/// <summary>Standard file dialogs.</summary>
public interface IFileDialogService
{
    /// <returns>Selected PDF files, empty when cancelled.</returns>
    IReadOnlyList<string> PickPdfFiles();
}
