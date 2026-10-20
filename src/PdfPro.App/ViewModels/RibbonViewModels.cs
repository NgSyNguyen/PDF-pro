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
    public RibbonCommandViewModel(string id, string label, SymbolRegular icon, ICommand? command = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        Id = id;
        Label = label;
        Icon = icon;
        IsAvailable = command is not null;
        Command = command ?? UnavailableCommand.Instance;
        ToolTip = IsAvailable ? label : $"{label} — {Strings.Cmd_NotAvailableYet}";
    }

    /// <summary>Stable identifier ("file.open"), used by tests and later by the command palette.</summary>
    public string Id { get; }

    public string Label { get; }

    public SymbolRegular Icon { get; }

    public ICommand Command { get; }

    public bool IsAvailable { get; }

    [ObservableProperty]
    public partial string ToolTip { get; private set; }

    /// <summary>Changes the tooltip of an available command (e.g. "Hoàn tác: Xoay 1 trang").</summary>
    public void SetToolTip(string toolTip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolTip);
        if (IsAvailable)
        {
            ToolTip = toolTip;
        }
    }
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
