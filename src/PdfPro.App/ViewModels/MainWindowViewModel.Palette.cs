using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace PdfPro.App.ViewModels;

/// <summary>"Tìm lệnh" (Ctrl+K): every ribbon command found by name, run after the palette closes, remembered.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The keyboard shortcuts of the window (MainWindow.xaml), shown next to their commands.</summary>
    private static readonly Dictionary<string, string> Shortcuts = new(StringComparer.Ordinal)
    {
        ["file.open"] = "Ctrl+O",
        ["file.save"] = "Ctrl+S",
        ["file.saveAs"] = "Ctrl+Shift+S",
        ["file.print"] = "Ctrl+P",
        ["file.properties"] = "Ctrl+D",
        ["file.close"] = "Ctrl+W",
        ["edit.undo"] = "Ctrl+Z",
        ["edit.redo"] = "Ctrl+Y",
        ["organize.rotateRight"] = "Ctrl+R",
        ["organize.rotateLeft"] = "Ctrl+Shift+R",
        ["organize.markSplit"] = "Ctrl+M",
        ["search.find"] = "Ctrl+F",
    };

    /// <summary>The commands the palette lists: those of the ribbon that exist, each once.</summary>
    public IReadOnlyList<PaletteEntry> PaletteEntries()
        => [.. RibbonTabs
            .SelectMany(tab => tab.Groups.SelectMany(group => group.Commands
                .Where(c => c.IsAvailable && c.Id != "tools.commandPalette")
                .Select(c => new PaletteEntry(c.Id, c.Label, $"{tab.Header} › {group.Header}", Shortcuts.GetValueOrDefault(c.Id), c.Icon, c.Command))))
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
