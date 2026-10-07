using Cairn.Rfa.Editing;

namespace Cairn.Rfa.Tests;

public class EditingHistoryTests
{
    private sealed record Doc(string Name);

    private static readonly Doc A = new("a"), B = new("b"), C = new("c"), D = new("d"), E = new("e");

    [Fact]
    public void PushUndoRedoAndLabels()
    {
        var h = new History<Doc>(A);
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
        Assert.Null(h.UndoLabel);
        h.Push(B, "to b");
        h.Push(C, "to c");
        Assert.Same(C, h.Current);
        Assert.Equal(["to c", "to b"], h.UndoLabels);
        Assert.Equal("to c", h.UndoLabel);

        Assert.True(h.Undo());
        Assert.Same(B, h.Current);
        Assert.Equal("to c", h.RedoLabel);
        Assert.Equal(["to b"], h.UndoLabels);
        Assert.True(h.Undo());
        Assert.Same(A, h.Current);
        Assert.False(h.Undo());
        Assert.Equal(["to b", "to c"], h.RedoLabels);

        Assert.True(h.Redo());
        Assert.Same(B, h.Current);
        h.Push(D, "to d");
        Assert.False(h.CanRedo);
        Assert.False(h.Redo());
        Assert.Equal(["to d", "to b"], h.UndoLabels);
    }

    [Fact]
    public void MarkUnsavedKeepsADocumentDirtyUntilSaved()
    {
        var h = new History<Doc>(A);
        Assert.False(h.IsDirty);
        h.MarkUnsaved();
        Assert.True(h.IsDirty);
        h.Push(B, "to b");
        h.Undo();
        Assert.True(h.IsDirty);
        h.MarkSaved();
        Assert.False(h.IsDirty);
    }

    [Fact]
    public void PushingTheCurrentReferenceIsANoOp()
    {
        var h = new History<Doc>(A);
        int changes = 0;
        h.Changed += (_, _) => changes++;
        h.Push(A, "nothing");
        Assert.False(h.CanUndo);
        Assert.False(h.IsDirty);
        Assert.Equal(0, changes);
        h.Push(new Doc("a"), "equal value, new reference");
        Assert.True(h.CanUndo);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void DirtyFollowsTheSavedPointThroughUndoAndRedo()
    {
        var h = new History<Doc>(A);
        Assert.False(h.IsDirty);
        h.Push(B, "b");
        Assert.True(h.IsDirty);
        h.Undo();
        Assert.False(h.IsDirty);
        h.Redo();
        h.MarkSaved();
        Assert.False(h.IsDirty);
        h.Push(C, "c");
        Assert.True(h.IsDirty);
        h.Undo();
        Assert.False(h.IsDirty);
        h.Undo();
        Assert.True(h.IsDirty);
    }

    [Fact]
    public void DroppingTheSavedSnapshotKeepsTheDocumentDirtyUntilTheNextSave()
    {
        var h = new History<Doc>(A);
        h.Push(B, "b");
        h.MarkSaved();
        h.Undo();
        h.Push(C, "c"); // discards the redo branch holding the saved snapshot B
        Assert.True(h.IsDirty);
        h.Undo();
        Assert.True(h.IsDirty);
        Assert.True(h.CanRedo);
        h.MarkSaved();
        Assert.False(h.IsDirty);
    }

    [Fact]
    public void DepthCapTrimsTheOldestSteps()
    {
        var h = new History<Doc>(A, maxDepth: 3);
        h.Push(B, "b");
        h.Push(C, "c");
        h.Push(D, "d");
        h.Push(E, "e");
        Assert.Equal(["e", "d", "c"], h.UndoLabels);
        Assert.True(h.Undo());
        Assert.True(h.Undo());
        Assert.True(h.Undo());
        Assert.False(h.Undo());
        Assert.Same(B, h.Current);
        Assert.True(h.IsDirty); // the saved snapshot A was trimmed
        Assert.Throws<ArgumentOutOfRangeException>(() => new History<Doc>(A, 0));
    }

    [Fact]
    public void CoalesceCommitIsOneStepFromThePreDragSnapshot()
    {
        var h = new History<Doc>(A);
        h.BeginCoalesce("drag");
        Assert.True(h.IsCoalescing);
        Assert.False(h.CanUndo);
        h.UpdateCoalesce(B);
        h.UpdateCoalesce(C);
        Assert.Same(C, h.Current);
        Assert.True(h.IsDirty);
        Assert.Equal("drag", h.UndoLabel);
        h.UpdateCoalesce(D);
        h.CommitCoalesce();
        Assert.False(h.IsCoalescing);
        Assert.Same(D, h.Current);
        Assert.Equal(["drag"], h.UndoLabels);
        h.Undo();
        Assert.Same(A, h.Current);
        Assert.False(h.IsDirty);
    }

    [Fact]
    public void CoalesceThatChangedNothingAddsNoStep()
    {
        var h = new History<Doc>(A);
        h.BeginCoalesce("drag");
        h.CommitCoalesce();
        Assert.False(h.CanUndo);
        h.BeginCoalesce("drag");
        h.UpdateCoalesce(B);
        h.UpdateCoalesce(A);
        h.CommitCoalesce();
        Assert.False(h.CanUndo);
        Assert.False(h.IsDirty);
    }

    [Fact]
    public void CoalesceCancelRestoresThePreDragSnapshot()
    {
        var h = new History<Doc>(A);
        h.Push(B, "b");
        h.MarkSaved();
        int changes = 0;
        h.Changed += (_, _) => changes++;
        h.BeginCoalesce("drag");
        h.UpdateCoalesce(C);
        Assert.True(h.IsDirty);
        h.CancelCoalesce();
        Assert.Same(B, h.Current);
        Assert.False(h.IsDirty);
        Assert.Equal(["b"], h.UndoLabels);
        Assert.Equal(2, changes);
        h.CancelCoalesce(); // nothing in progress: no-op
        Assert.Equal(2, changes);
    }

    [Fact]
    public void PushUndoAndRedoDuringACoalesceCommitItFirst()
    {
        var h = new History<Doc>(A);
        h.BeginCoalesce("drag");
        h.UpdateCoalesce(B);
        h.Push(C, "c");
        Assert.Equal(["c", "drag"], h.UndoLabels);

        h.BeginCoalesce("drag 2");
        h.UpdateCoalesce(D);
        Assert.True(h.Undo());
        Assert.Same(C, h.Current);
        Assert.Equal("drag 2", h.RedoLabel);

        h.BeginCoalesce("drag 3");
        h.UpdateCoalesce(E);
        Assert.False(h.CanRedo);
        Assert.False(h.Redo()); // committing drag 3 discarded the redo branch
        Assert.Same(E, h.Current);
        Assert.Equal(["drag 3", "c", "drag"], h.UndoLabels);
    }

    [Fact]
    public void ChangedIsRaisedOnEveryVisibleChange()
    {
        var h = new History<Doc>(A);
        int changes = 0;
        h.Changed += (_, _) => changes++;
        h.Push(B, "b");          // 1
        h.Undo();                // 2
        h.Redo();                // 3
        h.MarkSaved();           // 4
        h.MarkSaved();           // already saved: none
        h.BeginCoalesce("drag"); // none
        h.UpdateCoalesce(C);     // 5
        h.UpdateCoalesce(C);     // same value: none
        h.CommitCoalesce();      // 6
        h.Reset(D);              // 7
        Assert.Equal(7, changes);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var h = new History<Doc>(A);
        h.Push(B, "b");
        h.Undo();
        h.BeginCoalesce("drag");
        h.UpdateCoalesce(C);
        h.Reset(D);
        Assert.Same(D, h.Current);
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
        Assert.False(h.IsDirty);
        Assert.False(h.IsCoalescing);
        Assert.Empty(h.UndoLabels);
        Assert.Empty(h.RedoLabels);
    }

    [Fact]
    public void UpdateWithoutBeginThrows()
    {
        var h = new History<Doc>(A);
        var ex = Assert.Throws<InvalidOperationException>(() => h.UpdateCoalesce(B));
        Assert.Contains("BeginCoalesce", ex.Message);
    }
}
