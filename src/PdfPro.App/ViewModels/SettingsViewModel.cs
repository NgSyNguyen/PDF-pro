using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Localization;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Text;
using PdfPro.Infrastructure.Caching;
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
    private readonly RenderCache _renderCache;
    private readonly ThumbnailDiskCache _thumbnailCache;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly bool _isInitialized;

    public SettingsViewModel(
        ISettingsService settings,
        IAppThemeService theme,
        IAppLifetime lifetime,
        ILocalizationService localization,
        RenderCache renderCache,
        ThumbnailDiskCache thumbnailCache,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _theme = theme;
        _lifetime = lifetime;
        _localization = localization;
        _renderCache = renderCache;
        _thumbnailCache = thumbnailCache;
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
        RenderCacheMegabytes = settings.Current.RenderCacheMegabytes;
        DiskCacheSize = Strings.Settings_Measuring;
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

    public double MinRenderCacheMegabytes => AppSettings.MinRenderCacheMegabytes;

    public double MaxRenderCacheMegabytes => AppSettings.MaxRenderCacheMegabytes;

    /// <summary>Memory for rendered pages; applied at once (the cache shrinks or grows) and saved.</summary>
    [ObservableProperty]
    public partial double? RenderCacheMegabytes { get; set; }

    /// <summary>Size of the thumbnail cache on disk ("12 MB"); null until measured.</summary>
    [ObservableProperty]
    public partial string? DiskCacheSize { get; private set; }

    /// <summary>Result of the last "clear cache", shown under the button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCacheMessage))]
    public partial string? CacheMessage { get; private set; }

    public bool HasCacheMessage => CacheMessage is not null;

    /// <summary>Measures the thumbnail cache off the UI thread (the window calls it when it opens).</summary>
    public async Task LoadCacheSizeAsync()
    {
        var bytes = await Task.Run(_thumbnailCache.GetSize);
        DiskCacheSize = ByteSizeFormatter.Format(bytes, CultureInfo.CurrentCulture);
    }

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

    partial void OnRenderCacheMegabytesChanged(double? value)
    {
        if (!_isInitialized || value is not { } megabytes || !double.IsFinite(megabytes))
        {
            return;
        }

        var clamped = (int)Math.Clamp(Math.Round(megabytes), AppSettings.MinRenderCacheMegabytes, AppSettings.MaxRenderCacheMegabytes);
        if (clamped != megabytes)
        {
            RenderCacheMegabytes = clamped; // saves through the nested change
            return;
        }

        if (clamped != _settings.Current.RenderCacheMegabytes)
        {
            PendingSave = SaveAsync(s => s with { RenderCacheMegabytes = clamped });
        }
    }

    /// <summary>Drops rendered pages from memory and every thumbnail stored on disk.</summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        var inMemory = _renderCache.TotalBytes;
        _renderCache.Clear();
        var onDisk = await Task.Run(_thumbnailCache.Clear);
        _logger.LogInformation("Caches cleared: {MemoryBytes} bytes in memory, {DiskBytes} bytes on disk", inMemory, onDisk);
        CacheMessage = string.Format(CultureInfo.CurrentCulture, Strings.Settings_CacheCleared, ByteSizeFormatter.Format(inMemory + onDisk, CultureInfo.CurrentCulture));
        await LoadCacheSizeAsync();
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
