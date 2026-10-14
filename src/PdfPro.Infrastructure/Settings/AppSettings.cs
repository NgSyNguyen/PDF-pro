namespace PdfPro.Infrastructure.Settings;

public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>UI languages shipped with the app. Vietnamese is the default.</summary>
public static class AppLanguages
{
    public const string Vietnamese = "vi";
    public const string English = "en";

    public static IReadOnlyList<string> Supported { get; } = [Vietnamese, English];

    /// <summary>Returns a supported language code, falling back to Vietnamese.</summary>
    public static string Normalize(string? code)
    {
        var normalized = code?.Trim().ToLowerInvariant();
        return normalized is not null && Supported.Contains(normalized) ? normalized : Vietnamese;
    }
}

/// <summary>
/// User preferences persisted to settings.json. Bump <see cref="CurrentSchemaVersion"/> and add a migration
/// in <see cref="SettingsService"/> when a change is not backward compatible.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public static AppSettings Default { get; } = new();

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public AppThemeMode Theme { get; init; } = AppThemeMode.System;

    public string Language { get; init; } = AppLanguages.Vietnamese;
}
