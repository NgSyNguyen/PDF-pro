using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Automation;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.ViewModels;

/// <summary>
/// "Thư mục theo dõi" (hot folder, SPEC Phase 12): the folder, the workflow (a built-in one or one of the library), the
/// output folder, starting with Windows and the notification area. Each change is saved and applied at once.
/// </summary>
public sealed partial class SettingsViewModel
{
    private IHotFolderService? _hotFolder;
    private IStartupRegistration? _startup;

    public bool HasHotFolder => _hotFolder is not null;

    public IReadOnlyList<SettingOption<string>> HotFolderWorkflows { get; private set; } = [];

    [ObservableProperty]
    public partial bool HotFolderEnabled { get; set; }

    [ObservableProperty]
    public partial string HotFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HotFolderWorkflow { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HotFolderOutput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    /// <summary>What the hot folder is doing.</summary>
    [ObservableProperty]
    public partial string? HotFolderStatus { get; private set; }

    private void InitializeHotFolder(AppSettings current, IHotFolderService? hotFolder, IStartupRegistration? startup, IWorkflowLibrary? workflows)
    {
        _hotFolder = hotFolder;
        _startup = startup;
        HotFolderWorkflows = workflows is null
            ? []
            : [.. workflows.List().Select(e => new SettingOption<string>(e.TemplateId ?? e.Path ?? string.Empty, e.Workflow.Name)).Where(o => o.Value.Length > 0)];
        HotFolderEnabled = current.HotFolderEnabled;
        HotFolderPath = current.HotFolderPath;
        HotFolderWorkflow = current.HotFolderWorkflow.Length > 0 ? current.HotFolderWorkflow : HotFolderWorkflows.FirstOrDefault()?.Value ?? string.Empty;
        HotFolderOutput = current.HotFolderOutput;
        MinimizeToTray = current.MinimizeToTray;
        StartWithWindows = TryRead(() => startup?.IsEnabled == true);
        if (hotFolder is not null)
        {
            HotFolderStatus = hotFolder.Status;
            hotFolder.Changed += (_, _) => HotFolderStatus = hotFolder.Status;
        }
    }

    partial void OnHotFolderEnabledChanged(bool value) => SaveHotFolder();

    partial void OnHotFolderPathChanged(string value) => SaveHotFolder();

    partial void OnHotFolderWorkflowChanged(string value) => SaveHotFolder();

    partial void OnHotFolderOutputChanged(string value) => SaveHotFolder();

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_isInitialized)
        {
            PendingSave = SaveAsync(s => s with { MinimizeToTray = value });
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_isInitialized || _startup is null)
        {
            return;
        }

        try
        {
            _startup.Set(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            _logger.LogWarning("Starting with Windows could not be set: {ErrorType}", ex.GetType().Name);
            HotFolderStatus = Strings.HotFolder_Status_StartupFailed;
        }
    }

    [RelayCommand]
    private void PickHotFolder()
    {
        if (_files?.PickFolder(Strings.Settings_HotFolder_Pick_Title, HotFolderPath.Length > 0 ? HotFolderPath : null) is { } folder)
        {
            HotFolderPath = folder;
        }
    }

    [RelayCommand]
    private void PickHotFolderOutput()
    {
        if (_files?.PickFolder(Strings.Settings_HotFolder_Output_Pick_Title, HotFolderOutput.Length > 0 ? HotFolderOutput : null) is { } folder)
        {
            HotFolderOutput = folder;
        }
    }

    private static bool TryRead(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Saves the hot folder settings as shown (all together: the workflow first proposed counts too), then starts, restarts or stops it.</summary>
    private void SaveHotFolder()
    {
        if (!_isInitialized)
        {
            return;
        }

        var (enabled, path, workflow, output) = (HotFolderEnabled, (HotFolderPath ?? string.Empty).Trim(), HotFolderWorkflow ?? string.Empty, (HotFolderOutput ?? string.Empty).Trim());
        PendingSave = SaveAndApplyAsync(s => s with { HotFolderEnabled = enabled, HotFolderPath = path, HotFolderWorkflow = workflow, HotFolderOutput = output });
    }

    private async Task SaveAndApplyAsync(Func<AppSettings, AppSettings> update)
    {
        await SaveAsync(update);
        if (_hotFolder is { } hotFolder)
        {
            await hotFolder.ApplyAsync(_settings.Current);
            HotFolderStatus = hotFolder.Status;
        }
    }
}
