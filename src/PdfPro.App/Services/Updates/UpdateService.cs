using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.Settings;
using Velopack;
using Velopack.Sources;

namespace PdfPro.App.Services.Updates;

/// <summary>A newer version found by a check.</summary>
/// <param name="Version">Its version ("1.0.1").</param>
/// <param name="Info">What Velopack needs to download it (opaque to the app).</param>
public sealed record AvailableUpdate(string Version, object Info);

/// <summary>Updates of an installed PDF_Pro (seam for tests).</summary>
public interface IUpdateService
{
    /// <summary>Installed by Setup.exe (a build run from its folder cannot update itself).</summary>
    bool CanUpdate { get; }

    /// <summary>The newer version of the channel chosen in Settings, or null.</summary>
    /// <exception cref="UpdateCheckException">The releases could not be read (offline, GitHub unreachable…).</exception>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads it (a delta when possible); the percentage as it goes.</summary>
    /// <exception cref="UpdateCheckException">The download failed.</exception>
    Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken cancellationToken);

    /// <summary>Installs the downloaded update once PDF_Pro has closed (the user's work is never interrupted).</summary>
    void InstallOnExit(AvailableUpdate update);
}

/// <summary>Checking or downloading an update failed; the message never contains paths.</summary>
public sealed class UpdateCheckException(Exception innerException) : Exception("The update could not be checked or downloaded.", innerException);

/// <summary>Where updates come from: the project's GitHub releases.</summary>
public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Beta = "beta";

    public const string Repository = "https://github.com/NgSyNguyen/PDF-pro";

    /// <summary>
    /// The choice in force: the one made in Settings, or, when none was made, the channel of the running version
    /// ("1.0.0-beta.2+abc" is a beta, "1.0.0" or "1.0.0+abc" stable).
    /// </summary>
    public static string Resolve(string? choice, string runningVersion)
    {
        if (choice is Stable or Beta)
        {
            return choice;
        }

        var version = runningVersion ?? string.Empty;
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return (metadata >= 0 ? version[..metadata] : version).Contains('-', StringComparison.Ordinal) ? Beta : Stable;
    }

    /// <summary>The Velopack channel of a choice on this processor: "win", "win-arm64", "beta", "beta-arm64".</summary>
    public static string Of(string choice, Architecture architecture)
        => (choice == Beta ? "beta" : "win") + (architecture == Architecture.Arm64 ? "-arm64" : string.Empty);
}

/// <summary>
/// Velopack against the GitHub releases of PDF_Pro (SPEC Phase 13): stable releases, or pre-releases too on the beta
/// channel. Only the version and the release files are fetched; nothing about the user or their documents is sent.
/// </summary>
internal sealed class VelopackUpdateService(ISettingsService settings, ILogger<VelopackUpdateService> logger) : IUpdateService
{
    public bool CanUpdate
    {
        get
        {
            try
            {
                return Manager().IsInstalled;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manager = Manager();
            var info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Update check on {Channel}: {Found}", Choice, info?.TargetFullRelease.Version.ToString() ?? "none");
            return info is null ? null : new AvailableUpdate(info.TargetFullRelease.Version.ToString(), info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("Update check failed: {ErrorType}", ex.GetType().Name);
            throw new UpdateCheckException(ex);
        }
    }

    public async Task DownloadAsync(AvailableUpdate update, IProgress<int> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            await Manager().DownloadUpdatesAsync((UpdateInfo)update.Info, progress.Report, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("Update download failed: {ErrorType}", ex.GetType().Name);
            throw new UpdateCheckException(ex);
        }
    }

    public void InstallOnExit(AvailableUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Manager().WaitExitThenApplyUpdates(((UpdateInfo)update.Info).TargetFullRelease, true, false, []);
    }

    private string Choice => UpdateChannels.Resolve(settings.Current.UpdateChannel, Hosting.AppBuildInfo.Current.Version);

    private UpdateManager Manager()
    {
        var choice = Choice;
        return new UpdateManager(
            new GithubSource(UpdateChannels.Repository, null, choice == UpdateChannels.Beta),
            new UpdateOptions { ExplicitChannel = UpdateChannels.Of(choice, RuntimeInformation.ProcessArchitecture) });
    }
}
