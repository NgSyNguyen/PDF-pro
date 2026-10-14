using PdfPro.Core.Commands;

namespace PdfPro.Core.Tests.Commands;

/// <summary>Simple editable context used by the command tests.</summary>
internal sealed class FakeDocument
{
    public List<string> Items { get; } = [];

    public List<string> Log { get; } = [];
}

/// <summary>Appends an item on Do and removes it on Undo.</summary>
internal sealed class AddItemCommand(string item) : IUndoableCommand<FakeDocument>
{
    public string Description => $"Add {item}";

    public void Do(FakeDocument context)
    {
        context.Items.Add(item);
        context.Log.Add($"do:{item}");
    }

    public void Undo(FakeDocument context)
    {
        context.Items.RemoveAt(context.Items.LastIndexOf(item));
        context.Log.Add($"undo:{item}");
    }
}

/// <summary>Command whose Do and/or Undo throws, to test failure handling.</summary>
internal sealed class FailingCommand(bool failOnDo, bool failOnUndo = false) : IUndoableCommand<FakeDocument>
{
    public string Description => "Failing";

    public void Do(FakeDocument context)
    {
        if (failOnDo)
        {
            throw new InvalidOperationException("Do failed");
        }

        context.Log.Add("do:failing");
    }

    public void Undo(FakeDocument context)
    {
        if (failOnUndo)
        {
            throw new InvalidOperationException("Undo failed");
        }

        context.Log.Add("undo:failing");
    }
}

/// <summary>Command that tries to change the history while it is running.</summary>
internal sealed class ReentrantCommand(UndoRedoStack<FakeDocument> stack) : IUndoableCommand<FakeDocument>
{
    public string Description => "Reentrant";

    public void Do(FakeDocument context) => stack.Execute(new AddItemCommand("nested"));

    public void Undo(FakeDocument context)
    {
    }
}
