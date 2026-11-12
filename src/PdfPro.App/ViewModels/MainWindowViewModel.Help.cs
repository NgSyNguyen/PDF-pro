using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services;

namespace PdfPro.App.ViewModels;

/// <summary>Tệp › Trợ giúp (SPEC Phase 13): the shortcut sheet (F1), the quick-start guide, "Giới thiệu".</summary>
public sealed partial class MainWindowViewModel
{
    [RelayCommand]
    private void ShowShortcuts() => _pageDialogs.ShowShortcuts(new ShortcutsViewModel());

    /// <summary>Brings the guide back on the start page and shows the start page.</summary>
    [RelayCommand]
    private async Task ShowGuideAsync()
    {
        Home.ShowsGuide = true;
        SelectedTab = Home;
        await RememberGuideAsync(hidden: false);
    }

    [RelayCommand]
    private async Task HideGuideAsync()
    {
        Home.ShowsGuide = false;
        await RememberGuideAsync(hidden: true);
    }

    [RelayCommand]
    private void ShowAbout()
        => _pageDialogs.ShowAbout(new AboutViewModel(
            _buildInfo,
            Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md"),
            _clipboard ?? new WpfClipboardService(),
            () => _shell.OpenFolder(_paths.LogsDirectory)));

    private async Task RememberGuideAsync(bool hidden)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { GuideHidden = hidden });
        }
        catch (IOException ex)
        {
            _logger.LogWarning("The guide setting could not be saved: {ErrorType}", ex.GetType().Name);
        }
    }
}
