using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;

namespace PdfPro.App.ViewModels;

/// <summary>
/// The keyboard shortcuts of PDF_Pro (MainWindow.xaml, DocumentView.xaml.cs), in one place: next to the ribbon's
/// commands (tooltips, the command palette, UI Automation) and in the shortcut sheet (F1).
/// </summary>
public static class KeyboardShortcuts
{
    /// <summary>The shortcut of a ribbon command, by its id.</summary>
    public static IReadOnlyDictionary<string, string> OfCommands { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
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
        ["tools.commandPalette"] = "Ctrl+K",
        ["help.shortcuts"] = "F1",
    };

    /// <summary>The shortcut sheet: every shortcut, by group, with what it does.</summary>
    public static IReadOnlyList<ShortcutGroup> Sheet() =>
    [
        new(Strings.Shortcuts_Group_File,
        [
            new("Ctrl+O", Strings.Cmd_Open),
            new("Ctrl+S", Strings.Cmd_Save),
            new("Ctrl+Shift+S", Strings.Cmd_SaveAs),
            new("Ctrl+P", Strings.Cmd_Print),
            new("Ctrl+D", Strings.Cmd_Properties),
            new("Ctrl+W, Ctrl+F4", Strings.Cmd_Close),
        ]),
        new(Strings.Shortcuts_Group_View,
        [
            new(Strings.Shortcuts_ZoomKeys, Strings.Shortcuts_Zoom),
            new("Ctrl+0", Strings.Shortcuts_FitPage),
            new("Ctrl+1", Strings.Shortcuts_ActualSize),
            new("Ctrl+2", Strings.Shortcuts_FitWidth),
            new("Ctrl+G", Strings.Shortcuts_GoToPage),
            new("Page Up, Page Down", Strings.Shortcuts_PreviousNextPage),
            new("Home, End", Strings.Shortcuts_FirstLastPage),
        ]),
        new(Strings.Shortcuts_Group_Edit,
        [
            new("Ctrl+Z", Strings.Shortcuts_Undo),
            new("Ctrl+Y, Ctrl+Shift+Z", Strings.Shortcuts_Redo),
            new("Ctrl+R, Ctrl+Shift+R", Strings.Shortcuts_Rotate),
            new("Ctrl+M", Strings.Shortcuts_MarkSplit),
            new("Delete", Strings.Shortcuts_Delete),
            new("Esc", Strings.Shortcuts_Escape),
            new("Ctrl+Enter", Strings.Shortcuts_FinishComment),
        ]),
        new(Strings.Shortcuts_Group_Text,
        [
            new("Ctrl+F", Strings.Shortcuts_Find),
            new("F3, Shift+F3", Strings.Shortcuts_FindNextPrevious),
            new("Ctrl+A", Strings.Shortcuts_SelectAll),
            new("Ctrl+C", Strings.Shortcuts_Copy),
        ]),
        new(Strings.Shortcuts_Group_Tools,
        [
            new("Ctrl+K", Strings.Shortcuts_Palette),
            new("F1", Strings.Shortcuts_Sheet),
            new("Tab, Shift+Tab", Strings.Shortcuts_Tab),
        ]),
    ];
}

/// <summary>One shortcut of the sheet.</summary>
public sealed record ShortcutEntry(string Keys, string Action);

/// <summary>A group of the sheet.</summary>
public sealed record ShortcutGroup(string Header, IReadOnlyList<ShortcutEntry> Entries);

/// <summary>"Phím tắt" (F1): the shortcut sheet.</summary>
public sealed partial class ShortcutsViewModel
{
    public IReadOnlyList<ShortcutGroup> Groups { get; } = KeyboardShortcuts.Sheet();

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
