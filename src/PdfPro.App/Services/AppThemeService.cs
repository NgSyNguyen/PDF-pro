using System.Windows;
using PdfPro.Infrastructure.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PdfPro.App.Services;

/// <summary>
/// Theme switching on top of WPF-UI. In "System" mode the main window is watched so the app follows
/// Windows when the user changes the system theme.
/// </summary>
public sealed class AppThemeService : IAppThemeService
{
    private const WindowBackdropType Backdrop = WindowBackdropType.Mica;

    private Window? _mainWindow;
    private bool _isWatchingSystem;

    public AppThemeMode Mode { get; private set; } = AppThemeMode.System;

    public void Apply(AppThemeMode mode)
    {
        Mode = mode;
        if (mode == AppThemeMode.System)
        {
            ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
        }
        else
        {
            var theme = mode == AppThemeMode.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme, Backdrop, updateAccent: true);
        }

        UpdateSystemWatcher();
        RefreshSecondaryWindows();
    }

    /// <summary>Connects the main window so "System" mode can follow Windows theme changes.</summary>
    public void AttachMainWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _mainWindow = window;
        if (window.IsLoaded)
        {
            UpdateSystemWatcher();
        }
        else
        {
            window.Loaded += OnMainWindowLoaded;
        }
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        ((Window)sender).Loaded -= OnMainWindowLoaded;
        UpdateSystemWatcher();
    }

    private void UpdateSystemWatcher()
    {
        // WPF-UI can only watch/unwatch loaded windows.
        if (_mainWindow is not { IsLoaded: true } window)
        {
            return;
        }

        if (Mode == AppThemeMode.System && !_isWatchingSystem)
        {
            SystemThemeWatcher.Watch(window, Backdrop, updateAccents: true);
            _isWatchingSystem = true;
        }
        else if (Mode != AppThemeMode.System && _isWatchingSystem)
        {
            SystemThemeWatcher.UnWatch(window);
            _isWatchingSystem = false;
        }
    }

    /// <summary>WPF-UI only refreshes the main window's backdrop; update the other open windows too.</summary>
    private static void RefreshSecondaryWindows()
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var theme = ApplicationThemeManager.GetAppTheme();
        foreach (Window window in application.Windows)
        {
            if (!ReferenceEquals(window, application.MainWindow) && window is FluentWindow)
            {
                WindowBackgroundManager.UpdateBackground(window, theme, Backdrop);
            }
        }
    }
}
