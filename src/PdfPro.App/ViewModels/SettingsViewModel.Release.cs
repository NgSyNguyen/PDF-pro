using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Updates;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.ViewModels;

/// <summary>Updates (on start, stable or beta) and Explorer's right-click menu (SPEC Phase 13).</summary>
public sealed partial class SettingsViewModel
{
    private Services.IShellIntegration? _shell;

    public IReadOnlyList<SettingOption<string>> UpdateChannelOptions { get; } =
    [
        new(UpdateChannels.Stable, Strings.Settings_Update_Stable),
        new(UpdateChannels.Beta, Strings.Settings_Update_Beta),
    ];

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial string UpdateChannel { get; set; } = UpdateChannels.Stable;

    public bool HasShellIntegration => _shell is not null;

    [ObservableProperty]
    public partial bool ExplorerContextMenu { get; set; }

    private void InitializeRelease(AppSettings current, Services.IShellIntegration? shell)
    {
        _shell = shell;
        CheckForUpdates = current.CheckForUpdates;
        UpdateChannel = UpdateChannels.Resolve(current.UpdateChannel, Hosting.AppBuildInfo.Current.Version);
        ExplorerContextMenu = TryRead(() => shell?.HasContextMenu == true);
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_isInitialized)
        {
            PendingSave = SaveAsync(s => s with { CheckForUpdates = value });
        }
    }

    partial void OnUpdateChannelChanged(string value)
    {
        if (_isInitialized && value is UpdateChannels.Stable or UpdateChannels.Beta)
        {
            PendingSave = SaveAsync(s => s with { UpdateChannel = value });
        }
    }

    partial void OnExplorerContextMenuChanged(bool value)
    {
        if (!_isInitialized || _shell is null)
        {
            return;
        }

        try
        {
            _shell.SetContextMenu(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            _logger.LogWarning("The Explorer menu could not be changed: {ErrorType}", ex.GetType().Name);
        }
    }
}
