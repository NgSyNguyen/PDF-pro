using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Updates;

namespace PdfPro.App.ViewModels;

/// <summary>
/// Updates (SPEC Phase 13): an installed PDF_Pro looks for a newer version a while after it starts (when Settings
/// allow), or when asked from "Giới thiệu"; the user decides, the download runs in the status bar, and the update is
/// installed once PDF_Pro closes — never in the middle of the user's work.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>"Kiểm tra cập nhật".</summary>
    [RelayCommand]
    private Task CheckForUpdatesAsync() => CheckForUpdatesAsync(manual: true);

    /// <summary>Looks for a newer version; a check the user did not ask for stays silent unless it finds one.</summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updates is not { CanUpdate: true } updates)
        {
            if (manual)
            {
                await _messages.ShowInfoAsync(Strings.Update_Title, Strings.Update_NotInstalled);
            }

            return;
        }

        if (!manual && !_settings.Current.CheckForUpdates)
        {
            return;
        }

        AvailableUpdate? update;
        try
        {
            update = await updates.CheckAsync(CancellationToken.None);
        }
        catch (UpdateCheckException)
        {
            if (manual)
            {
                await _messages.ShowErrorAsync(Strings.Update_Title, Strings.Update_Error_Check);
            }

            return;
        }

        if (update is null)
        {
            if (manual)
            {
                await _messages.ShowInfoAsync(Strings.Update_Title, string.Format(CultureInfo.CurrentCulture, Strings.Update_UpToDate, _buildInfo.Version));
            }

            return;
        }

        var answer = await _messages.AskAsync(
            Strings.Update_Title,
            string.Format(CultureInfo.CurrentCulture, Strings.Update_Available, update.Version, _buildInfo.Version),
            Strings.Update_Download,
            null,
            Strings.Update_Later);
        if (answer != DialogChoice.Primary)
        {
            return;
        }

        var task = StatusBar.StartTask(string.Format(CultureInfo.CurrentCulture, Strings.Update_Downloading, update.Version));
        try
        {
            await updates.DownloadAsync(update, new Progress<int>(p => task.Report(p / 100.0, string.Format(CultureInfo.CurrentCulture, Strings.Update_Downloading, update.Version))), task.Token);
            updates.InstallOnExit(update);
            task.Complete(string.Format(CultureInfo.CurrentCulture, Strings.Update_Ready, update.Version));
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Update_Cancelled);
        }
        catch (UpdateCheckException)
        {
            task.Complete(Strings.Update_Error_Download);
            await _messages.ShowErrorAsync(Strings.Update_Title, Strings.Update_Error_Download);
        }
    }
}
