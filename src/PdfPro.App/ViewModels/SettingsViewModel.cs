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
using PdfPro.Operations.Converting;

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
    private readonly ILibreOfficeLocator? _libreOffice;
    private readonly IFileDialogService? _files;
    private readonly IVeraPdfLocator? _veraPdf;
    private readonly bool _isInitialized;

    public SettingsViewModel(
        ISettingsService settings,
        IAppThemeService theme,
        IAppLifetime lifetime,
        ILocalizationService localization,
        RenderCache renderCache,
        ThumbnailDiskCache thumbnailCache,
        ILogger<SettingsViewModel> logger,
        ILibreOfficeLocator? libreOffice = null,
        IFileDialogService? files = null,
        IVeraPdfLocator? veraPdf = null)
    {
        _settings = settings;
        _theme = theme;
        _lifetime = lifetime;
        _localization = localization;
        _renderCache = renderCache;
        _thumbnailCache = thumbnailCache;
        _logger = logger;
        _libreOffice = libreOffice;
        _files = files;
        _veraPdf = veraPdf;

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
        AnnotationAuthor = settings.Current.AnnotationAuthor;
        LibreOfficePath = settings.Current.LibreOfficePath;
        LibreOfficeStatus = DescribeLibreOffice(LibreOfficePath);
        VeraPdfPath = settings.Current.VeraPdfPath;
        VeraPdfStatus = DescribeVeraPdf(VeraPdfPath);
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

    /// <summary>The name new annotations are signed with; blank: the Windows user name. Saved when it changes.</summary>
    [ObservableProperty]
    public partial string AnnotationAuthor { get; set; } = string.Empty;

    /// <summary>Where LibreOffice is ("Office sang PDF"); blank: looked for. Saved when it changes.</summary>
    [ObservableProperty]
    public partial string LibreOfficePath { get; set; } = string.Empty;

    /// <summary>"Đang dùng: C:\…\soffice.exe", or why LibreOffice is not there; null when not looked for.</summary>
    [ObservableProperty]
    public partial string? LibreOfficeStatus { get; private set; }

    public bool CanPickLibreOffice => _files is not null;

    /// <summary>Where veraPDF is (checking PDF/A copies); blank: looked for. Saved when it changes.</summary>
    [ObservableProperty]
    public partial string VeraPdfPath { get; set; } = string.Empty;

    /// <summary>"Đang dùng: C:\…\verapdf", or what is missing; null when not looked for.</summary>
    [ObservableProperty]
    public partial string? VeraPdfStatus { get; private set; }

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

    partial void OnAnnotationAuthorChanged(string value)
    {
        var name = TextNormalizer.ToNfc(value ?? string.Empty).Trim();
        if (!_isInitialized || string.Equals(name, _settings.Current.AnnotationAuthor, StringComparison.Ordinal))
        {
            return;
        }

        PendingSave = SaveAsync(s => s with { AnnotationAuthor = name });
    }

    partial void OnLibreOfficePathChanged(string value)
    {
        var path = (value ?? string.Empty).Trim();
        LibreOfficeStatus = DescribeLibreOffice(path);
        if (!_isInitialized || string.Equals(path, _settings.Current.LibreOfficePath, StringComparison.Ordinal))
        {
            return;
        }

        PendingSave = SaveAsync(s => s with { LibreOfficePath = path });
    }

    /// <summary>"Chọn…": LibreOffice's installation folder (or its "program" folder).</summary>
    [RelayCommand]
    private void PickLibreOffice()
    {
        var current = LibreOfficePath.Trim();
        var initial = current.Length == 0 ? null : Directory.Exists(current) ? current : Path.GetDirectoryName(current);
        if (_files?.PickFolder(Strings.Settings_LibreOffice_Pick_Title, initial) is { } folder)
        {
            LibreOfficePath = folder;
        }
    }

    partial void OnVeraPdfPathChanged(string value)
    {
        var path = (value ?? string.Empty).Trim();
        VeraPdfStatus = DescribeVeraPdf(path);
        if (!_isInitialized || string.Equals(path, _settings.Current.VeraPdfPath, StringComparison.Ordinal))
        {
            return;
        }

        PendingSave = SaveAsync(s => s with { VeraPdfPath = path });
    }

    /// <summary>"Chọn…": veraPDF's installation folder.</summary>
    [RelayCommand]
    private void PickVeraPdf()
    {
        var current = VeraPdfPath.Trim();
        if (_files?.PickFolder(Strings.Settings_VeraPdf_Pick_Title, current.Length == 0 ? null : current) is { } folder)
        {
            VeraPdfPath = folder;
        }
    }

    /// <summary>What is found where <paramref name="path"/> says (or where veraPDF is installed when blank).</summary>
    private string? DescribeVeraPdf(string path)
    {
        if (_veraPdf is null)
        {
            return null;
        }

        return _veraPdf.Find(path.Length == 0 ? null : path) switch
        {
            { Java: null } => Strings.Settings_VeraPdf_NoJava,
            { } found => string.Format(CultureInfo.CurrentCulture, path.Length == 0 ? Strings.Settings_LibreOffice_Found : Strings.Settings_LibreOffice_Using, found.Folder),
            null => path.Length == 0 ? Strings.Settings_VeraPdf_Missing : Strings.Settings_VeraPdf_NotThere,
        };
    }

    /// <summary>What is found where <paramref name="path"/> says (or where LibreOffice is installed when blank).</summary>
    private string? DescribeLibreOffice(string path)
    {
        if (_libreOffice is null)
        {
            return null;
        }

        var found = _libreOffice.Find(path.Length == 0 ? null : path);
        return (found, path.Length == 0) switch
        {
            ({ } program, true) => string.Format(CultureInfo.CurrentCulture, Strings.Settings_LibreOffice_Found, program),
            ({ } program, false) => string.Format(CultureInfo.CurrentCulture, Strings.Settings_LibreOffice_Using, program),
            (null, true) => Strings.Settings_LibreOffice_Missing,
            (null, false) => Strings.Settings_LibreOffice_NotThere,
        };
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
        await _lifetime.RestartAsync();
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
