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
