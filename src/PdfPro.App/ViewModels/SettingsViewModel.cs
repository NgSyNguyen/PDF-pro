using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Localization;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.ViewModels;

/// <summary>An entry of a settings combo box.</summary>
public sealed record SettingOption<T>(T Value, string Label)
{
    /// <summary>Screen readers announce combo box items through ToString.</summary>
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IAppThemeService _theme;
    private readonly IAppLifetime _lifetime;
    private readonly ILocalizationService _localization;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly bool _isInitialized;

    public SettingsViewModel(
        ISettingsService settings,
        IAppThemeService theme,
        IAppLifetime lifetime,
        ILocalizationService localization,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _theme = theme;
        _lifetime = lifetime;
        _localization = localization;
        _logger = logger;

        ThemeOptions =
        [
            new(AppThemeMode.System, Strings.Settings_Theme_System),
            new(AppThemeMode.Light, Strings.Settings_Theme_Light),
            new(AppThemeMode.Dark, Strings.Settings_Theme_Dark),
        ];
        LanguageOptions =
        [
            new(AppLanguages.Vietnamese, Strings.Settings_Language_Vietnamese),
            new(AppLanguages.English, Strings.Settings_Language_English),
        ];

        SelectedTheme = settings.Current.Theme;
        SelectedLanguage = settings.Current.Language;
        IsRestartRequired = SelectedLanguage != localization.ActiveLanguage;
        _isInitialized = true;
    }

    public IReadOnlyList<SettingOption<AppThemeMode>> ThemeOptions { get; }

    public IReadOnlyList<SettingOption<string>> LanguageOptions { get; }

    [ObservableProperty]
    public partial AppThemeMode SelectedTheme { get; set; }

    [ObservableProperty]
    public partial string SelectedLanguage { get; set; }

    /// <summary>The chosen language differs from the one the UI is running in.</summary>
    [ObservableProperty]
    public partial bool IsRestartRequired { get; private set; }

    [ObservableProperty]
    public partial bool HasSaveError { get; private set; }

    /// <summary>The most recent save; tests and the restart command wait for it.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    partial void OnSelectedThemeChanged(AppThemeMode value)
    {
        if (!_isInitialized)
        {
            return;
        }

        // Applied immediately, before the file write completes.
        _theme.Apply(value);
        PendingSave = SaveAsync(s => s with { Theme = value });
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        if (!_isInitialized)
        {
            return;
        }

        IsRestartRequired = value != _localization.ActiveLanguage;
        PendingSave = SaveAsync(s => s with { Language = value });
    }

    [RelayCommand]
    private async Task RestartNowAsync()
    {
        await PendingSave;
        _lifetime.Restart();
    }

    private async Task SaveAsync(Func<AppSettings, AppSettings> update)
    {
        try
        {
            await _settings.UpdateAsync(update);
            HasSaveError = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings change could not be persisted");
            HasSaveError = true;
        }
    }
}
