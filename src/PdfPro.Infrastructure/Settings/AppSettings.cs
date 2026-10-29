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

    public const int DefaultRenderCacheMegabytes = 512;
    public const int MinRenderCacheMegabytes = 128;
    public const int MaxRenderCacheMegabytes = 4096;

    public static AppSettings Default { get; } = new();

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public AppThemeMode Theme { get; init; } = AppThemeMode.System;

    public string Language { get; init; } = AppLanguages.Vietnamese;

    /// <summary>
    /// Recently opened documents, newest first (full paths). Personal data: it stays in the user profile and is
    /// never written to the log.
    /// </summary>
    public IReadOnlyList<string> RecentFiles { get; init; } = [];

    /// <summary>Memory budget for rendered pages, in megabytes.</summary>
    public int RenderCacheMegabytes { get; init; } = DefaultRenderCacheMegabytes;

    /// <summary>
    /// The name annotations are signed with (shown to the readers of the document); blank: the Windows user name.
    /// Personal data: never written to the log.
    /// </summary>
    public string AnnotationAuthor { get; init; } = string.Empty;

    /// <summary>The RFC 3161 time-stamping authority digital signatures are timestamped by.</summary>
    public string TimestampServer { get; init; } = DefaultTimestampServer;

    /// <summary>Digital signatures get a timestamp from <see cref="TimestampServer"/> (needs the network).</summary>
    public bool UseTimestamp { get; init; } = true;

    /// <summary>A free public time-stamping authority (DigiCert).</summary>
    public const string DefaultTimestampServer = "http://timestamp.digicert.com";
}

/// <summary>Most-recently-used list of documents (pure list operations; no file system access).</summary>
public static class RecentFileList
{
    public const int Capacity = 10;

    /// <summary>Puts <paramref name="path"/> first, removing an older entry for the same file.</summary>
    public static IReadOnlyList<string> Add(IReadOnlyList<string>? current, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return Normalize([fullPath, .. current ?? []]);
    }

    public static IReadOnlyList<string> Remove(IReadOnlyList<string>? current, string path)
        => (current ?? []).Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>Drops entries for which <paramref name="exists"/> is false (moved or deleted files).</summary>
    public static IReadOnlyList<string> RemoveMissing(IReadOnlyList<string>? current, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        return (current ?? []).Where(exists).ToArray();
    }

    /// <summary>No blanks, no duplicates (Windows paths are case-insensitive), at most <see cref="Capacity"/>.</summary>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? current)
        => (current ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Capacity)
            .ToArray();
}
