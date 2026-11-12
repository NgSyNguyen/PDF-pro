using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.App.Resources;
using Wpf.Ui.Controls;

namespace PdfPro.App.ViewModels;

/// <summary>A ribbon tab ("Tệp", "Trang"…) made of command groups.</summary>
public sealed record RibbonTabViewModel(string Id, string Header, IReadOnlyList<RibbonGroupViewModel> Groups);

/// <summary>A captioned group of ribbon buttons.</summary>
public sealed record RibbonGroupViewModel(string Header, IReadOnlyList<RibbonCommandViewModel> Commands);

/// <summary>
/// One ribbon button. Commands that are not implemented yet are shown disabled with an explanatory tooltip.
/// </summary>
public sealed partial class RibbonCommandViewModel : ObservableObject
{
    /// <param name="toolTip">What the button does, when the label alone does not say it; null shows the label.</param>
    public RibbonCommandViewModel(string id, string label, SymbolRegular icon, ICommand? command = null, string? toolTip = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        Id = id;
        Label = label;
        Icon = icon;
        IsAvailable = command is not null;
        Command = command ?? UnavailableCommand.Instance;
        _toolTip = IsAvailable ? toolTip ?? label : $"{label} — {Strings.Cmd_NotAvailableYet}";
        Shortcut = KeyboardShortcuts.OfCommands.GetValueOrDefault(id);
        ToolTip = WithShortcut(_toolTip);
    }

    private string _toolTip;

    /// <summary>Stable identifier ("file.open"), used by tests and later by the command palette.</summary>
    public string Id { get; }

    public string Label { get; }

    public SymbolRegular Icon { get; }

    public ICommand Command { get; }

    public bool IsAvailable { get; }

    /// <summary>The command's keyboard shortcut ("Ctrl+O"), or null; in the tooltip and given to UI Automation.</summary>
    public string? Shortcut { get; }

    /// <summary>What the button does, with its shortcut.</summary>
    [ObservableProperty]
    public partial string ToolTip { get; private set; }

    /// <summary>Changes the tooltip of an available command (e.g. "Hoàn tác: Xoay 1 trang"); the shortcut stays.</summary>
    public void SetToolTip(string toolTip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolTip);
        if (IsAvailable)
        {
            _toolTip = toolTip;
            ToolTip = WithShortcut(toolTip);
        }
    }

    /// <summary>The tooltip with its shortcut, unless the tooltip already names it.</summary>
    private string WithShortcut(string toolTip)
        => Shortcut is { } keys && !toolTip.Contains(keys, StringComparison.Ordinal) ? $"{toolTip} ({keys})" : toolTip;
}

/// <summary>Command of a feature that ships in a later phase: never executable.</summary>
public sealed class UnavailableCommand : ICommand
{
    private UnavailableCommand()
    {
    }

    public static UnavailableCommand Instance { get; } = new();

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => false;

    public void Execute(object? parameter)
    {
    }
}
