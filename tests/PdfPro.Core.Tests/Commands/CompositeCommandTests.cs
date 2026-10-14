using PdfPro.Core.Commands;

namespace PdfPro.Core.Tests.Commands;

public sealed class CompositeCommandTests
{
    private readonly FakeDocument _document = new();

    [Fact]
    public void Do_RunsCommandsInOrder_Undo_RevertsInReverseOrder()
    {
        var composite = new CompositeCommand<FakeDocument>(
            "Add three",
            [new AddItemCommand("a"), new AddItemCommand("b"), new AddItemCommand("c")]);

        composite.Do(_document);
        composite.Undo(_document);

        _document.Log.ShouldBe(["do:a", "do:b", "do:c", "undo:c", "undo:b", "undo:a"]);
        _document.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Do_WhenACommandFails_RollsBackAppliedCommandsAndRethrows()
    {
        var composite = new CompositeCommand<FakeDocument>(
            "Mixed",
            [new AddItemCommand("a"), new AddItemCommand("b"), new FailingCommand(failOnDo: true), new AddItemCommand("d")]);

        var error = Should.Throw<InvalidOperationException>(() => composite.Do(_document));

        error.Message.ShouldBe("Do failed");
        _document.Items.ShouldBeEmpty();
        _document.Log.ShouldBe(["do:a", "do:b", "undo:b", "undo:a"]);
    }

    [Fact]
    public void Undo_WhenACommandFails_ReappliesUndoneCommandsAndRethrows()
    {
        var composite = new CompositeCommand<FakeDocument>(
            "Mixed",
            [new AddItemCommand("a"), new FailingCommand(failOnDo: false, failOnUndo: true), new AddItemCommand("c")]);
        composite.Do(_document);
        _document.Log.Clear();

        Should.Throw<InvalidOperationException>(() => composite.Undo(_document));

        _document.Items.ShouldBe(["a", "c"], "the composite must stay fully applied");
        _document.Log.ShouldBe(["undo:c", "do:c"]);
    }

    [Fact]
    public void Do_WhenRollbackAlsoFails_ThrowsAggregateWithBothErrors()
    {
        var composite = new CompositeCommand<FakeDocument>(
            "Broken",
            [new FailingCommand(failOnDo: false, failOnUndo: true), new FailingCommand(failOnDo: true)]);

        var error = Should.Throw<AggregateException>(() => composite.Do(_document));

        error.InnerExceptions.Select(e => e.Message).ShouldBe(["Do failed", "Undo failed"]);
    }

    [Fact]
    public void InsideStack_IsASingleUndoStep()
    {
        var stack = new UndoRedoStack<FakeDocument>(_document);
        stack.Execute(new CompositeCommand<FakeDocument>(
            "Add two",
            [new AddItemCommand("a"), new AddItemCommand("b")]));

        stack.UndoCount.ShouldBe(1);
        stack.NextUndoDescription.ShouldBe("Add two");

        stack.Undo();
        _document.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_RejectsEmptyCommandList()
        => Should.Throw<ArgumentException>(() => new CompositeCommand<FakeDocument>("Empty", []));

    [Fact]
    public void Constructor_RejectsNullCommand()
        => Should.Throw<ArgumentException>(() => new CompositeCommand<FakeDocument>("Null", [null!]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankDescription(string description)
        => Should.Throw<ArgumentException>(() => new CompositeCommand<FakeDocument>(description, [new AddItemCommand("a")]));
}
