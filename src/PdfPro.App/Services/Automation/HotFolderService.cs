using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PdfPro.App.Resources;
using PdfPro.Infrastructure.Instances;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Automation;

namespace PdfPro.App.Services.Automation;

/// <summary>The hot folder of the app, run in the background as the settings say.</summary>
public interface IHotFolderService
{
    bool IsRunning { get; }

    /// <summary>What it is doing, in the user's language.</summary>
    string Status { get; }

    /// <summary>It started, stopped or dealt with a file (raised on any thread).</summary>
    event EventHandler? Changed;

    /// <summary>Starts, restarts or stops it as the settings say.</summary>
    Task ApplyAsync(AppSettings settings);
}

/// <summary>
/// Runs <see cref="HotFolder"/> while the app runs, with the folder, workflow and output folder of the settings;
/// started with the host, stopped with it. What it does is logged as counts only (file names may be personal).
/// </summary>
public sealed class HotFolderService(AutomationServices automation, ISettingsService settings, Infrastructure.AppPaths paths, ILogger<HotFolderService> logger) : IHotFolderService, IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HotFolder? _folder;
    private int _done;
    private int _failed;

    public event EventHandler? Changed;

    public bool IsRunning => _folder?.IsRunning == true;

    public string Status { get; private set; } = Strings.HotFolder_Status_Off;

    public async Task ApplyAsync(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            Status = Strings.HotFolder_Status_Off;
            if (!current.HotFolderEnabled)
            {
                return;
            }

            if (current.HotFolderPath.Trim() is not { Length: > 0 } path || !Directory.Exists(path))
            {
                Status = Strings.HotFolder_Status_NoFolder;
                return;
            }

            if (Workflow(current.HotFolderWorkflow) is not { } workflow)
            {
                Status = Strings.HotFolder_Status_NoWorkflow;
                return;
            }

            var output = current.HotFolderOutput.Trim() is { Length: > 0 } chosen ? chosen : null;
            var folder = new HotFolder(automation.Runner, new HotFolderOptions(path, workflow, output, WorkingFolder: Path.Combine(paths.CacheDirectory, "automation")));
            try
            {
                folder.Start();
            }
            catch (Exception ex) when (ex is WorkflowFailedException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning("The hot folder cannot start: {ErrorType}", ex.GetType().Name);
                Status = Strings.HotFolder_Status_Invalid;
                await folder.DisposeAsync().ConfigureAwait(false);
                return;
            }

            folder.Processed += OnProcessed;
            _folder = folder;
            (_done, _failed) = (0, 0);
            Status = Running();
            logger.LogInformation("The hot folder started");
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => ApplyAsync(settings.Current);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>A built-in workflow by its id, or a saved one by its file.</summary>
    private Workflow? Workflow(string chosen)
    {
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return null;
        }

        if (WorkflowLibrary.BuiltIn().FirstOrDefault(e => e.TemplateId == chosen) is { } builtIn)
        {
            return builtIn.Workflow;
        }

        return automation.Library.List().FirstOrDefault(e => string.Equals(e.Path, chosen, StringComparison.OrdinalIgnoreCase))?.Workflow;
    }

    private async Task StopCoreAsync()
    {
        if (_folder is { } folder)
        {
            folder.Processed -= OnProcessed;
            _folder = null;
            await folder.DisposeAsync().ConfigureAwait(false);
            logger.LogInformation("The hot folder stopped: {Done} done, {Failed} failed", _done, _failed);
        }
    }

    private void OnProcessed(object? sender, HotFolderEvent e)
    {
        if (e.Succeeded)
        {
            Interlocked.Increment(ref _done);
        }
        else
        {
            Interlocked.Increment(ref _failed);
            logger.LogWarning("A hot folder file failed: {ErrorType}", e.Error is { } error ? BatchRunner.Innermost(error).GetType().Name : "unknown");
        }

        Status = Running();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string Running() => string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.HotFolder_Status_Running, _done, _failed);
}

/// <summary>Starting PDF_Pro with Windows (to watch the hot folder): the Run key of the current user.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void Set(bool enabled);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run, "PDF_Pro" = "PdfPro.exe" --background.</summary>
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PDF_Pro";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } program)
        {
            key.SetValue(ValueName, $"\"{program}\" {StartupArguments.BackgroundOption}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
