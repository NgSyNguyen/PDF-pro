using System.Windows.Input;

namespace PdfPro.App.ViewModels;

/// <summary>
/// A window-level command (ribbon button, keyboard shortcut) that runs a command of the active document tab, and is
/// disabled when no document is active or the document's own command cannot run.
/// </summary>
public sealed class ActiveDocumentCommand : ICommand
{
    private readonly Func<DocumentTabViewModel, ICommand> _select;
    private ICommand? _target;

    public ActiveDocumentCommand(Func<DocumentTabViewModel, ICommand> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        _select = select;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>Follows another document tab (or none).</summary>
    public void Attach(DocumentTabViewModel? document)
    {
        if (_target is not null)
        {
            _target.CanExecuteChanged -= OnTargetCanExecuteChanged;
        }

        _target = document is null ? null : _select(document);
        if (_target is not null)
        {
            _target.CanExecuteChanged += OnTargetCanExecuteChanged;
        }

        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CanExecute(object? parameter) => _target?.CanExecute(parameter) ?? false;

    public void Execute(object? parameter)
    {
        if (_target is { } target && target.CanExecute(parameter))
        {
            target.Execute(parameter);
        }
    }

    private void OnTargetCanExecuteChanged(object? sender, EventArgs e) => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
