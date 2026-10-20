using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;

namespace PdfPro.App.ViewModels;

/// <summary>One document of the recovery dialog; ticked by default.</summary>
public sealed partial class RecoveryItemViewModel : ObservableObject
{
    private readonly RecoveryItem _item;

    public RecoveryItemViewModel(RecoveryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _item = item;
        IsSelected = true;
    }

    public Guid SessionId => _item.SessionId;

    public string FileName => _item.FileName;

    public string Folder => _item.Folder;

    public string Details => string.Format(
        CultureInfo.CurrentCulture,
        Strings.Recovery_Details,
        _item.LastChange.ToString("g", CultureInfo.CurrentCulture),
        _item.Changes);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// "Khôi phục tài liệu chưa lưu": restore the ticked documents (the others are discarded), discard all after a
/// confirmation, or decide later (the window's X and Esc too: nothing is lost).
/// </summary>
public sealed partial class RecoveryViewModel : ObservableObject
{
    public RecoveryViewModel(IReadOnlyList<RecoveryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = [.. items.Select(i => new RecoveryItemViewModel(i))];
        foreach (var item in Items)
        {
            item.PropertyChanged += (_, _) => OnSelectionChanged();
        }
    }

    /// <summary>A choice was made: the window closes.</summary>
    public event EventHandler? CloseRequested;

    public IReadOnlyList<RecoveryItemViewModel> Items { get; }

    public bool HasUnchecked => Items.Any(i => !i.IsSelected);

    /// <summary>Warns that restoring discards the unticked documents.</summary>
    public string UncheckedWarning
        => string.Format(CultureInfo.CurrentCulture, Strings.Recovery_UncheckedWarning, Items.Count(i => !i.IsSelected));

    public string ConfirmDiscardText => string.Format(CultureInfo.CurrentCulture, Strings.Recovery_ConfirmDiscard, Items.Count);

    /// <summary>"Bỏ tất cả" was pressed: the footer asks for confirmation.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingDiscard { get; private set; }

    /// <summary>The user's choice; null while the dialog is open.</summary>
    public RecoveryDecision? Decision { get; private set; }

    /// <summary>The window closed: without a choice it counts as "Để sau".</summary>
    public RecoveryDecision TakeDecision() => Decision ?? RecoveryDecision.Later;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
        => Finish(new RecoveryDecision(RecoveryAction.Restore, Items.Where(i => i.IsSelected).Select(i => i.SessionId).ToHashSet()));

    private bool CanRestore() => Items.Any(i => i.IsSelected);

    [RelayCommand]
    private void DiscardAll() => IsConfirmingDiscard = true;

    [RelayCommand]
    private void ConfirmDiscardAll() => Finish(new RecoveryDecision(RecoveryAction.DiscardAll, new HashSet<Guid>()));

    [RelayCommand]
    private void CancelDiscard() => IsConfirmingDiscard = false;

    [RelayCommand]
    private void Later() => Finish(RecoveryDecision.Later);

    /// <summary>Esc: leaves the confirmation first, then means "Để sau".</summary>
    [RelayCommand]
    private void Escape()
    {
        if (IsConfirmingDiscard)
        {
            IsConfirmingDiscard = false;
        }
        else
        {
            Later();
        }
    }

    private void Finish(RecoveryDecision decision)
    {
        Decision = decision;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasUnchecked));
        OnPropertyChanged(nameof(UncheckedWarning));
        RestoreCommand.NotifyCanExecuteChanged();
    }
}
