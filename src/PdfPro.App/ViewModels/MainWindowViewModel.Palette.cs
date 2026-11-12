using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace PdfPro.App.ViewModels;

/// <summary>"Tìm lệnh" (Ctrl+K): every ribbon command found by name, run after the palette closes, remembered.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The commands the palette lists: those of the ribbon that exist, each once.</summary>
    public IReadOnlyList<PaletteEntry> PaletteEntries()
        => [.. RibbonTabs
            .SelectMany(tab => tab.Groups.SelectMany(group => group.Commands
                .Where(c => c.IsAvailable && c.Id != "tools.commandPalette")
                .Select(c => new PaletteEntry(c.Id, c.Label, $"{tab.Header} › {group.Header}", KeyboardShortcuts.OfCommands.GetValueOrDefault(c.Id), c.Icon, c.Command))))
            .DistinctBy(e => e.Command)];

    [RelayCommand]
    private async Task CommandPaletteAsync()
    {
        var palette = new CommandPaletteViewModel(PaletteEntries(), _settings.Current.RecentCommands);
        if (!_pageDialogs.ShowCommandPalette(palette) || palette.Chosen is not { } chosen)
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(s => s with { RecentCommands = CommandPaletteViewModel.Remember(s.RecentCommands, chosen.Id) });
        }
        catch (IOException ex)
        {
            // Remembering is a convenience: the command still runs.
            _logger.LogWarning("The recent commands could not be saved: {ErrorType}", ex.GetType().Name);
        }

        if (chosen.Command.CanExecute(null))
        {
            chosen.Command.Execute(null);
        }
    }
}
