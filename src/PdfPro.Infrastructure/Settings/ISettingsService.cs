namespace PdfPro.Infrastructure.Settings;

public interface ISettingsService
{
    /// <summary>Settings in effect. Loaded once at startup; defaults when the file is missing or unreadable.</summary>
    AppSettings Current { get; }

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    event EventHandler<AppSettings>? Changed;

    /// <summary>Applies <paramref name="update"/> to the current settings, then persists them atomically.</summary>
    /// <exception cref="IOException">The file could not be written; <see cref="Current"/> is still updated.</exception>
    Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default);
}
