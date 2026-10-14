namespace PdfPro.Core.Commands;

/// <summary>
/// Groups several commands into a single undo step. Commands run in order and are undone in reverse order.
/// If one command fails part-way, the commands already applied are rolled back before the error propagates,
/// so the context is left as it was before the call.
/// </summary>
public sealed class CompositeCommand<TContext> : IUndoableCommand<TContext>
{
    private readonly IUndoableCommand<TContext>[] _commands;

    public CompositeCommand(string description, IEnumerable<IUndoableCommand<TContext>> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(commands);

        _commands = commands.ToArray();
        if (_commands.Length == 0)
        {
            throw new ArgumentException("A composite command needs at least one command.", nameof(commands));
        }

        if (Array.Exists(_commands, c => c is null))
        {
            throw new ArgumentException("Commands must not contain null.", nameof(commands));
        }

        Description = description;
    }

    public string Description { get; }

    public IReadOnlyList<IUndoableCommand<TContext>> Commands => _commands;

    public void Do(TContext context)
    {
        var applied = 0;
        try
        {
            for (; applied < _commands.Length; applied++)
            {
                _commands[applied].Do(context);
            }
        }
        catch (Exception error)
        {
            // Undo what was applied, newest first.
            Compensate(error, applied, i => _commands[applied - 1 - i].Undo(context));
            throw;
        }
    }

    public void Undo(TContext context)
    {
        var undone = 0;
        try
        {
            for (; undone < _commands.Length; undone++)
            {
                _commands[_commands.Length - 1 - undone].Undo(context);
            }
        }
        catch (Exception error)
        {
            // Re-apply what was undone, oldest first, so the composite stays fully applied.
            var firstUndone = _commands.Length - undone;
            Compensate(error, undone, i => _commands[firstUndone + i].Do(context));
            throw;
        }
    }

    private static void Compensate(Exception original, int count, Action<int> step)
    {
        for (var i = 0; i < count; i++)
        {
            try
            {
                step(i);
            }
            catch (Exception compensationError)
            {
                throw new AggregateException(
                    "A composite command failed and its partial changes could not be reverted.",
                    original,
                    compensationError);
            }
        }
    }
}
