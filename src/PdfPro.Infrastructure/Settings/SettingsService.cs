using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.IO;

namespace PdfPro.Infrastructure.Settings;

/// <summary>
/// Stores <see cref="AppSettings"/> as JSON. A corrupt or newer-than-supported file is renamed aside
/// (settings.json.corrupt-…) and defaults are used, so the app always starts.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly string _filePath;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;

    public SettingsService(AppPaths paths, ILogger<SettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = paths.SettingsFile;
        _logger = logger;
        _current = Load();
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current => Volatile.Read(ref _current);

    public async Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        AppSettings updated;
        ExceptionDispatchInfo? persistError = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            updated = Normalize(update(Current));
            Volatile.Write(ref _current, updated);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var json = JsonSerializer.Serialize(updated, JsonOptions);
                await AtomicFile.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The in-memory settings stay updated; the caller decides how to report the failed write.
                persistError = ExceptionDispatchInfo.Capture(ex);
            }
        }
        finally
        {
            _gate.Release();
        }

        // Raised outside the lock so handlers may call UpdateAsync themselves.
        Changed?.Invoke(this, updated);

        if (persistError is not null)
        {
            _logger.LogWarning(persistError.SourceException, "Settings could not be saved");
            persistError.Throw();
        }

        _logger.LogInformation("Settings saved (schema v{SchemaVersion})", updated.SchemaVersion);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("No settings file found, using defaults");
            return AppSettings.Default;
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file may be fine but temporarily locked: keep it and run with defaults.
            _logger.LogWarning(ex, "Settings file could not be read, using defaults");
            return AppSettings.Default;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? throw new JsonException("The settings file contains null.");

            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "Settings file schema v{FileVersion} is newer than supported v{SupportedVersion}, using defaults",
                    settings.SchemaVersion,
                    AppSettings.CurrentSchemaVersion);
                MoveAside("newer");
                return AppSettings.Default;
            }

            return Normalize(Migrate(settings));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Settings file is corrupt, using defaults");
            MoveAside("corrupt");
            return AppSettings.Default;
        }
    }

    /// <summary>Upgrades settings written by older schema versions. Version 1 is the first schema.</summary>
    private static AppSettings Migrate(AppSettings settings) => settings;

    private static AppSettings Normalize(AppSettings settings) => settings with
    {
        SchemaVersion = AppSettings.CurrentSchemaVersion,
        Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : AppThemeMode.System,
        Language = AppLanguages.Normalize(settings.Language),
        RecentFiles = RecentFileList.Normalize(settings.RecentFiles),
        RenderCacheMegabytes = Math.Clamp(
            settings.RenderCacheMegabytes,
            AppSettings.MinRenderCacheMegabytes,
            AppSettings.MaxRenderCacheMegabytes),
    };

    private void MoveAside(string reason)
    {
        var target = string.Create(
            CultureInfo.InvariantCulture,
            $"{_filePath}.{reason}-{DateTime.Now:yyyyMMdd-HHmmss}");
        try
        {
            File.Move(_filePath, target, overwrite: true);
            _logger.LogInformation("Previous settings file kept as {BackupFile}", Path.GetFileName(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not move the unusable settings file aside");
        }
    }
}
