using System.ComponentModel;
using PdfPro.Core.Commands;

namespace PdfPro.Core.Tests.Commands;

public sealed class UndoRedoStackTests
{
    private readonly FakeDocument _document = new();
    private readonly UndoRedoStack<FakeDocument> _stack;

    public UndoRedoStackTests() => _stack = new UndoRedoStack<FakeDocument>(_document, maxDepth: 5);

    [Fact]
    public void NewStack_IsEmptyAndAtSavePoint()
    {
        _stack.CanUndo.ShouldBeFalse();
        _stack.CanRedo.ShouldBeFalse();
        _stack.NextUndoDescription.ShouldBeNull();
        _stack.NextRedoDescription.ShouldBeNull();
        _stack.IsAtSavePoint.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsMaxDepthBelowOne(int maxDepth)
        => Should.Throw<ArgumentOutOfRangeException>(() => new UndoRedoStack<FakeDocument>(_document, maxDepth));

    [Fact]
    public void Execute_RunsCommandAndMakesItUndoable()
    {
        _stack.Execute(new AddItemCommand("a"));

        _document.Items.ShouldBe(["a"]);
        _stack.CanUndo.ShouldBeTrue();
        _stack.CanRedo.ShouldBeFalse();
        _stack.NextUndoDescription.ShouldBe("Add a");
    }

    [Fact]
    public void Undo_RevertsLastCommandAndMakesItRedoable()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.Execute(new AddItemCommand("b"));

        _stack.Undo();

        _document.Items.ShouldBe(["a"]);
        _stack.NextUndoDescription.ShouldBe("Add a");
        _stack.NextRedoDescription.ShouldBe("Add b");
    }

    [Fact]
    public void Redo_ReappliesUndoneCommandsInOrder()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.Execute(new AddItemCommand("b"));
        _stack.Undo();
        _stack.Undo();

        _stack.Redo();
        _stack.Redo();

        _document.Items.ShouldBe(["a", "b"]);
        _stack.CanRedo.ShouldBeFalse();
        _stack.UndoCount.ShouldBe(2);
    }

    [Fact]
    public void Execute_AfterUndo_DiscardsRedoBranch()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.Execute(new AddItemCommand("b"));
        _stack.Undo();

        _stack.Execute(new AddItemCommand("c"));

        _stack.CanRedo.ShouldBeFalse();
        _stack.RedoHistory.ShouldBeEmpty();
        _document.Items.ShouldBe(["a", "c"]);
    }

    [Fact]
    public void Execute_BeyondMaxDepth_DropsOldestSteps()
    {
        foreach (var item in new[] { "1", "2", "3", "4", "5", "6", "7" })
        {
            _stack.Execute(new AddItemCommand(item));
        }

        _stack.UndoCount.ShouldBe(5);
        _stack.UndoHistory.ShouldBe(["Add 7", "Add 6", "Add 5", "Add 4", "Add 3"]);

        while (_stack.CanUndo)
        {
            _stack.Undo();
        }

        // The two oldest steps can no longer be undone.
        _document.Items.ShouldBe(["1", "2"]);
    }

    [Fact]
    public void Histories_AreOrderedFromNextStep()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.Execute(new AddItemCommand("b"));
        _stack.Execute(new AddItemCommand("c"));
        _stack.Undo();
        _stack.Undo();

        _stack.UndoHistory.ShouldBe(["Add a"]);
        _stack.RedoHistory.ShouldBe(["Add b", "Add c"]);
    }

    [Fact]
    public void Undo_WhenNothingToUndo_Throws()
        => Should.Throw<InvalidOperationException>(_stack.Undo);

    [Fact]
    public void Redo_WhenNothingToRedo_Throws()
        => Should.Throw<InvalidOperationException>(_stack.Redo);

    [Fact]
    public void Execute_WhenDoThrows_LeavesHistoryUnchangedAndRaisesNothing()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.Undo();
        var raised = 0;
        _stack.StateChanged += (_, _) => raised++;
        _stack.PropertyChanged += (_, _) => raised++;

        Should.Throw<InvalidOperationException>(() => _stack.Execute(new FailingCommand(failOnDo: true)));

        _stack.UndoCount.ShouldBe(0);
        _stack.RedoCount.ShouldBe(1, "a failed command must not clear the redo branch");
        raised.ShouldBe(0);
    }

    [Fact]
    public void Undo_WhenCommandThrows_KeepsStepUndoable()
    {
        _stack.Execute(new FailingCommand(failOnDo: false, failOnUndo: true));

        Should.Throw<InvalidOperationException>(_stack.Undo);

        _stack.CanUndo.ShouldBeTrue();
        _stack.CanRedo.ShouldBeFalse();
    }

    [Fact]
    public void Execute_FromInsideRunningCommand_Throws()
    {
        var error = Should.Throw<InvalidOperationException>(() => _stack.Execute(new ReentrantCommand(_stack)));

        error.Message.ShouldContain("while a command is running");
        _stack.CanUndo.ShouldBeFalse();
        _document.Items.ShouldBeEmpty();
    }

    [Fact]
    public void PropertyChanged_IsRaisedOnlyWhenValuesChange()
    {
        var changes = new List<string?>();
        _stack.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        _stack.Execute(new AddItemCommand("a"));
        changes.ShouldContain(nameof(UndoRedoStack<FakeDocument>.CanUndo));
        changes.ShouldContain(nameof(UndoRedoStack<FakeDocument>.IsAtSavePoint));
        changes.ShouldNotContain(nameof(UndoRedoStack<FakeDocument>.CanRedo));

        changes.Clear();
        _stack.Execute(new AddItemCommand("b"));
        changes.ShouldNotContain(nameof(UndoRedoStack<FakeDocument>.CanUndo), "CanUndo was already true");
        changes.ShouldContain(nameof(UndoRedoStack<FakeDocument>.NextUndoDescription));

        changes.Clear();
        _stack.Undo();
        changes.ShouldContain(nameof(UndoRedoStack<FakeDocument>.CanRedo));
    }

    [Fact]
    public void StateChanged_IsRaisedForEverySuccessfulChange()
    {
        var raised = 0;
        _stack.StateChanged += (_, _) => raised++;

        _stack.Execute(new AddItemCommand("a"));
        _stack.Undo();
        _stack.Redo();
        _stack.MarkSavePoint();
        _stack.Clear();

        raised.ShouldBe(5);
    }

    [Fact]
    public void SavePoint_TracksWhetherDocumentMatchesSavedState()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.IsAtSavePoint.ShouldBeFalse();

        _stack.Undo();
        _stack.IsAtSavePoint.ShouldBeTrue("undoing back to the opened state is not a change");

        _stack.Redo();
        _stack.MarkSavePoint();
        _stack.IsAtSavePoint.ShouldBeTrue();

        _stack.Undo();
        _stack.IsAtSavePoint.ShouldBeFalse();

        _stack.Redo();
        _stack.IsAtSavePoint.ShouldBeTrue();
    }

    [Fact]
    public void StepsThatDoNotChangeContent_LeaveTheSavePointWhereItIs()
    {
        _stack.Execute(new AddNoteCommand("n1"));
        _stack.IsAtSavePoint.ShouldBeTrue();
        _stack.CanUndo.ShouldBeTrue();

        _stack.Execute(new AddItemCommand("a"));
        _stack.IsAtSavePoint.ShouldBeFalse();
        _stack.Execute(new AddNoteCommand("n2"));
        _stack.MarkSavePoint();
        _stack.Execute(new AddNoteCommand("n3"));
        _stack.IsAtSavePoint.ShouldBeTrue();

        _stack.Undo();
        _stack.Undo();
        _stack.IsAtSavePoint.ShouldBeTrue("the note saved with 'a' is not content");
        _stack.Undo();
        _stack.IsAtSavePoint.ShouldBeFalse();
        _stack.Redo();
        _stack.Redo();
        _stack.Redo();
        _stack.IsAtSavePoint.ShouldBeTrue();
    }

    [Fact]
    public void SavePointPosition_GivesTheFirstEquivalentPosition()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.MarkSavePoint();
        _stack.Execute(new AddNoteCommand("n"));
        _stack.SavePointPosition.ShouldBe(1);

        var other = new UndoRedoStack<FakeDocument>(new FakeDocument(), maxDepth: 5);
        other.Execute(new AddNoteCommand("n"));
        other.SavePointPosition.ShouldBe(0);
        other.Execute(new AddItemCommand("a"));
        other.SavePointPosition.ShouldBe(0);
    }

    [Fact]
    public void DroppingOldNotes_KeepsTheSavePointReachable()
    {
        for (var i = 0; i < 7; i++)
        {
            _stack.Execute(new AddNoteCommand($"n{i}"));
        }

        _stack.UndoCount.ShouldBe(5);
        _stack.IsAtSavePoint.ShouldBeTrue();
        _stack.SavePointPosition.ShouldBe(0);
        _stack.Execute(new AddItemCommand("a"));
        _stack.Undo();
        _stack.IsAtSavePoint.ShouldBeTrue();
    }

    [Fact]
    public void CompositeCommand_ChangesContentWhenAnyPartDoes()
    {
        new CompositeCommand<FakeDocument>("notes", [new AddNoteCommand("a"), new AddNoteCommand("b")]).ChangesContent.ShouldBeFalse();
        new CompositeCommand<FakeDocument>("mixed", [new AddNoteCommand("a"), new AddItemCommand("b")]).ChangesContent.ShouldBeTrue();
        ((IUndoableCommand<FakeDocument>)new AddItemCommand("c")).ChangesContent.ShouldBeTrue();
    }

    [Fact]
    public void SavePoint_InDiscardedRedoBranch_IsNeverReachedAgain()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.MarkSavePoint();
        _stack.Undo();
        _stack.Execute(new AddItemCommand("b"));

        _stack.Undo();
        _stack.IsAtSavePoint.ShouldBeFalse();
        _stack.Redo();
        _stack.IsAtSavePoint.ShouldBeFalse();
    }

    [Fact]
    public void SavePoint_DroppedByMaxDepth_IsNeverReachedAgain()
    {
        // Saved state = empty history; push enough steps to trim the bottom of the stack.
        for (var i = 0; i < 6; i++)
        {
            _stack.Execute(new AddItemCommand(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        while (_stack.CanUndo)
        {
            _stack.Undo();
        }

        _stack.IsAtSavePoint.ShouldBeFalse("the first step can no longer be undone");
    }

    [Fact]
    public void Clear_RemovesHistoryButKeepsSavePointStatus()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.Execute(new AddItemCommand("b"));
        _stack.Undo();

        _stack.Clear();

        _stack.CanUndo.ShouldBeFalse();
        _stack.CanRedo.ShouldBeFalse();
        _stack.IsAtSavePoint.ShouldBeFalse("the document still differs from the saved file");
        _document.Items.ShouldBe(["a"]);
    }

    [Fact]
    public void ManySteps_UndoAll_RestoresOriginalState()
    {
        var stack = new UndoRedoStack<FakeDocument>(_document);
        for (var i = 0; i < 30; i++)
        {
            stack.Execute(new AddItemCommand($"item{i}"));
        }

        while (stack.CanUndo)
        {
            stack.Undo();
        }

        _document.Items.ShouldBeEmpty();
        stack.IsAtSavePoint.ShouldBeTrue();
        stack.RedoCount.ShouldBe(30);
    }

    [Fact]
    public void Stack_ImplementsINotifyPropertyChangedForBinding()
        => _stack.ShouldBeAssignableTo<INotifyPropertyChanged>();

    [Fact]
    public void CommandLists_AreOrderedForSavingAndReplaying()
    {
        var a = new AddItemCommand("a");
        var b = new AddItemCommand("b");
        var c = new AddItemCommand("c");
        _stack.Execute(a);
        _stack.Execute(b);
        _stack.Execute(c);
        _stack.Undo();
        _stack.Undo();

        _stack.UndoCommands.ShouldBe([a]);
        _stack.RedoCommands.ShouldBe([b, c]);
    }

    [Fact]
    public void SavePointPosition_CountsStepsFromTheOldestReachableState()
    {
        _stack.SavePointPosition.ShouldBe(0);

        _stack.Execute(new AddItemCommand("a"));
        _stack.SavePointPosition.ShouldBe(0, "the opened state is still the saved one");

        _stack.Execute(new AddItemCommand("b"));
        _stack.MarkSavePoint();
        _stack.SavePointPosition.ShouldBe(2);

        _stack.Undo();
        _stack.Undo();
        _stack.SavePointPosition.ShouldBe(2, "the saved state is two redos away");
    }

    [Fact]
    public void SavePointPosition_IsNullOnceTheSavedStateIsGone()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.MarkSavePoint();
        _stack.Undo();
        _stack.Execute(new AddItemCommand("b"));
        _stack.SavePointPosition.ShouldBeNull("the saved state was on the discarded redo branch");

        var trimmed = new UndoRedoStack<FakeDocument>(new FakeDocument(), maxDepth: 2);
        trimmed.Execute(new AddItemCommand("x"));
        trimmed.Execute(new AddItemCommand("y"));
        trimmed.Execute(new AddItemCommand("z"));
        trimmed.SavePointPosition.ShouldBeNull("the opened state was dropped by the depth limit");
    }

    [Fact]
    public void SavePointPosition_AfterClearAtTheSavePoint_IsZero()
    {
        _stack.Execute(new AddItemCommand("a"));
        _stack.MarkSavePoint();

        _stack.Clear();

        _stack.SavePointPosition.ShouldBe(0);
    }
}
