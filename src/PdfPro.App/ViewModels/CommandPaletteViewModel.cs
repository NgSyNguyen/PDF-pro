using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.Core.Text;
using Wpf.Ui.Controls;

namespace PdfPro.App.ViewModels;

/// <summary>A command the palette can run: a ribbon command with where it lives and its shortcut.</summary>
/// <param name="Id">The ribbon id ("file.open").</param>
/// <param name="Label">Its name.</param>
/// <param name="Category">Where it is in the ribbon ("Trang › Sắp xếp").</param>
/// <param name="Shortcut">Its keyboard shortcut ("Ctrl+O"), or null.</param>
/// <param name="Icon">Its icon.</param>
/// <param name="Command">What it runs.</param>
public sealed record PaletteEntry(string Id, string Label, string Category, string? Shortcut, SymbolRegular Icon, ICommand Command)
{
    /// <summary>It can run now (a command that needs a document is greyed out without one).</summary>
    public bool CanRun { get; } = Command.CanExecute(null);

    internal string FoldedLabel { get; } = FuzzyMatch.Fold(Label);

    internal string FoldedCategory { get; } = FuzzyMatch.Fold($"{Category} {Id}");
}

/// <summary>
/// "Tìm lệnh" (Ctrl+K, SPEC Phase 12): every command of the ribbon, found by typing part of its name without tone
/// marks ("xoa trang trang", "xtt"), its group or its id; the ones used last come first when nothing is typed.
/// Enter (or a double click) runs the chosen command after the palette closes.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    /// <summary>How many recently used commands are remembered.</summary>
    public const int MaxRecent = 8;

    /// <summary>How many matches are listed.</summary>
    public const int MaxResults = 60;

    private readonly IReadOnlyList<PaletteEntry> _entries;
    private readonly IReadOnlyList<string> _recent;

    public CommandPaletteViewModel(IReadOnlyList<PaletteEntry> entries, IReadOnlyList<string> recent)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(recent);
        _entries = entries;
        _recent = recent;
        Refresh();
    }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<PaletteEntry> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PaletteEntry? Selected { get; set; }

    /// <summary>Nothing matches what was typed.</summary>
    public bool HasNoResult => Results.Count == 0;

    /// <summary>The command chosen, run by the caller once the palette has closed.</summary>
    public PaletteEntry? Chosen { get; private set; }

    /// <summary>The recently used list with <paramref name="id"/> first.</summary>
    public static IReadOnlyList<string> Remember(IReadOnlyList<string>? recent, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return [.. new[] { id }.Concat((recent ?? []).Where(r => r != id)).Take(MaxRecent)];
    }

    [RelayCommand]
    private void MoveDown() => Move(1);

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void Run(PaletteEntry? entry)
    {
        entry ??= Selected;
        if (entry is not { CanRun: true })
        {
            return;
        }

        Chosen = entry;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, false);

    partial void OnQueryChanged(string value) => Refresh();

    private void Move(int step)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(index + step, 0, Results.Count - 1)];
    }

    private void Refresh()
    {
        var query = FuzzyMatch.Fold(Query);
        IEnumerable<PaletteEntry> found;
        if (query.Length == 0)
        {
            var recent = _recent.Select(id => _entries.FirstOrDefault(e => e.Id == id)).OfType<PaletteEntry>();
            found = recent.Concat(_entries.Except(recent).OrderBy(e => e.Category, StringComparer.CurrentCulture).ThenBy(e => e.Label, StringComparer.CurrentCulture));
        }
        else
        {
            found = _entries
                .Select(e => (Entry: e, Score: Score(query, e)))
                .Where(m => m.Score is not null)
                .OrderByDescending(m => m.Score)
                .ThenByDescending(m => m.Entry.CanRun)
                .ThenBy(m => m.Entry.Label, StringComparer.CurrentCulture)
                .Select(m => m.Entry);
        }

        Results.Clear();
        foreach (var entry in found.Take(MaxResults))
        {
            Results.Add(entry);
        }

        Selected = Results.FirstOrDefault(e => e.CanRun) ?? Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResult));
    }

    /// <summary>The name counts most; the group and the id help (a recently used command a little more).</summary>
    private int? Score(string query, PaletteEntry entry)
    {
        var label = FuzzyMatch.ScoreFolded(query, entry.FoldedLabel);
        var category = FuzzyMatch.ScoreFolded(query, entry.FoldedCategory) is { } other and >= 400 ? other - 300 : (int?)null;
        var best = label is null ? category : category is null ? label : Math.Max(label.Value, category.Value);
        return best is { } score && _recent.Contains(entry.Id) ? score + 20 : best;
    }
}
