using PAIC.Controls;
using PAIC.Designer;
using Xunit;

namespace PAIC.Tests;

public class ResizeAndSnapTests
{
    [Fact]
    public void Resize_HandlesKeepOppositeEdgeFixed()
    {
        var r = new Rect(10, 10, 100, 50);
        Assert.Equal(new Rect(10, 10, 120, 60), ResizeMath.Resize(r, ResizeHandle.BottomRight, 20, 10));
        Assert.Equal(new Rect(30, 20, 80, 40), ResizeMath.Resize(r, ResizeHandle.TopLeft, 20, 10));
        Assert.Equal(new Rect(10, 10, 130, 50), ResizeMath.Resize(r, ResizeHandle.Right, 30, 99));
        Assert.Equal(new Rect(10, 5, 100, 55), ResizeMath.Resize(r, ResizeHandle.Top, 99, -5));
    }

    [Fact]
    public void Resize_ClampsToMinimumSize()
    {
        var r = ResizeMath.Resize(new Rect(10, 10, 100, 50), ResizeHandle.Right, -500, 0);
        Assert.Equal(10, r.X);
        Assert.Equal(8, r.Width);
        var l = ResizeMath.Resize(new Rect(10, 10, 100, 50), ResizeHandle.Left, 500, 0);
        Assert.Equal(110, l.Right);
        Assert.Equal(8, l.Width);
    }

    [Fact]
    public void Resize_KeepAspectOnCorners()
    {
        var r = ResizeMath.Resize(new Rect(0, 0, 100, 50), ResizeHandle.BottomRight, 100, 0, keepAspect: true);
        Assert.Equal(200, r.Width);
        Assert.Equal(100, r.Height);
    }

    [Fact]
    public void Snap_AlignsToNeighbourEdges_AndCanvasCentre()
    {
        var other = new Rect(100, 100, 50, 50);
        var near = SnapEngine.Snap(new Rect(147, 300, 40, 20), new[] { other }, 0, 6, 720, 480);
        Assert.Equal(3, near.Dx); // left edge 147 -> other's right edge 150
        Assert.Contains(150.0, near.VerticalGuides);

        var centred = SnapEngine.Snap(new Rect(338, 10, 40, 20), Array.Empty<Rect>(), 0, 6, 720, 480);
        Assert.Equal(2, centred.Dx); // centre 358 -> 360
    }

    [Fact]
    public void Snap_FallsBackToGrid_AndIgnoresFarEdges()
    {
        var snap = SnapEngine.Snap(new Rect(203, 117, 40, 20), new[] { new Rect(600, 400, 10, 10) }, 8, 6, 720, 480);
        Assert.Equal(-3, snap.Dx); // 203 -> 200
        Assert.Equal(3, snap.Dy);  // 117 -> 120
        Assert.Empty(snap.VerticalGuides);
    }
}

public class DesignerDocumentTests
{
    private static (DesignerDocument Doc, List<DesignElement> Roots) New(double w = 720, double h = 480)
    {
        var roots = new List<DesignElement>();
        return (new DesignerDocument(roots, w, h), roots);
    }

    private static DesignElement AddBox(DesignerDocument doc, double x, double y, double w = 20, double h = 20)
    {
        var e = doc.AddAt(ControlTypes.Text, x, y);
        doc.SetBounds(e.Id, x, y, w, h);
        return e;
    }

    [Fact]
    public void AddAt_CreatesSelectedElement_AndUndoRedoKeepTheSameListInstance()
    {
        var (doc, roots) = New();
        var e = doc.AddAt(ControlTypes.Text, 100, 50);
        Assert.Equal("text-1", e.Id);
        Assert.Equal(100, e.X); Assert.Equal(50, e.Y);
        Assert.Equal(new[] { "text-1" }, doc.SelectedIds);
        Assert.True(doc.CanUndo);
        Assert.Equal("Add text", doc.UndoLabel);

        Assert.True(doc.Undo());
        Assert.Empty(roots);
        Assert.Empty(doc.SelectedIds);
        Assert.True(doc.CanRedo);

        Assert.True(doc.Redo());
        Assert.Single(roots);
        Assert.Same(roots, doc.Roots);
    }

    [Fact]
    public void NewHistoryEntry_ClearsRedo_AndNoOpsAreNotRecorded()
    {
        var (doc, _) = New();
        var a = doc.AddAt(ControlTypes.Text, 0, 0);
        doc.Undo();
        Assert.True(doc.CanRedo);
        var b = doc.AddAt(ControlTypes.Button, 0, 0);
        Assert.False(doc.CanRedo);

        var label = doc.UndoLabel;
        doc.SetProperty(b.Id, "text", b.GetString("text")); // same value
        Assert.Equal(label, doc.UndoLabel);
        doc.SetProperty(b.Id, "text", "OK");
        Assert.Equal("Set text", doc.UndoLabel);
        doc.Undo();
        Assert.Equal("Button", doc.Find(b.Id)!.GetString("text"));
        Assert.NotNull(a);
    }

    [Fact]
    public void MoveInteraction_IsOneUndoStep_AndLiveUpdatesDoNotAddHistory()
    {
        var (doc, _) = New();
        var e = AddBox(doc, 100, 50);
        doc.Select(e.Id);
        var before = doc.UndoLabel;

        doc.BeginInteraction();
        doc.UpdateMove(10, 10);
        doc.UpdateMove(30, 40);
        Assert.Equal(before, doc.UndoLabel);
        Assert.True(doc.CommitInteraction("Move"));
        Assert.Equal(130, e.X); Assert.Equal(90, e.Y);
        Assert.Equal("Move", doc.UndoLabel);

        doc.Undo();
        Assert.Equal(100, doc.Find(e.Id)!.X);
        Assert.Equal(50, doc.Find(e.Id)!.Y);
    }

    [Fact]
    public void CancelInteraction_RestoresOriginalState_AndEmptyDragIsNotRecorded()
    {
        var (doc, _) = New();
        var e = AddBox(doc, 100, 50);
        doc.Select(e.Id);
        doc.BeginInteraction();
        doc.UpdateMove(25, 25);
        doc.CancelInteraction();
        Assert.Equal(100, doc.Find(e.Id)!.X);
        Assert.False(doc.InteractionActive);

        var label = doc.UndoLabel;
        doc.BeginInteraction();
        Assert.False(doc.CommitInteraction("Move"));
        Assert.Equal(label, doc.UndoLabel);
    }

    [Fact]
    public void ResizeInteraction_UsesHandle()
    {
        var (doc, _) = New();
        var e = AddBox(doc, 10, 10, 100, 50);
        doc.Select(e.Id);
        doc.BeginInteraction();
        doc.UpdateResize(ResizeHandle.BottomRight, 20, 10);
        doc.CommitInteraction("Resize");
        Assert.Equal(120, e.Width); Assert.Equal(60, e.Height);
        doc.BeginInteraction();
        doc.UpdateResize(ResizeHandle.TopLeft, 10, 10);
        doc.CommitInteraction("Resize");
        Assert.Equal(20, doc.Find(e.Id)!.X);
        Assert.Equal(110, doc.Find(e.Id)!.Width);
    }

    [Fact]
    public void LockedElements_DoNotMoveResizeOrDelete()
    {
        var (doc, roots) = New();
        var e = AddBox(doc, 10, 10);
        doc.SetLocked(e.Id, true);
        doc.Select(e.Id);
        doc.BeginInteraction();
        doc.UpdateMove(50, 50);
        doc.UpdateResize(ResizeHandle.Right, 50, 0);
        Assert.False(doc.CommitInteraction("Move"));
        Assert.Equal(10, e.X);
        Assert.Equal(0, doc.DeleteSelection());
        Assert.Single(roots);
        Assert.False(doc.Nudge(5, 5));
    }

    [Fact]
    public void Delete_Duplicate_Nudge()
    {
        var (doc, roots) = New();
        var e = AddBox(doc, 10, 10);
        doc.Select(e.Id);
        Assert.Equal(1, doc.Duplicate());
        Assert.Equal(2, roots.Count);
        var copy = roots[1];
        Assert.NotEqual(e.Id, copy.Id);
        Assert.Equal(26, copy.X); Assert.Equal(26, copy.Y);
        Assert.Equal(new[] { copy.Id }, doc.SelectedIds);

        Assert.True(doc.Nudge(5, -2));
        Assert.Equal(31, copy.X); Assert.Equal(24, copy.Y);

        Assert.Equal(1, doc.DeleteSelection());
        Assert.Single(roots);
        Assert.Empty(doc.SelectedIds);
        doc.Undo();
        Assert.Equal(2, roots.Count);
    }

    [Fact]
    public void CopyPaste_WorksAcrossDocuments_AndIntoContainers()
    {
        var (a, _) = New();
        var src = AddBox(a, 10, 10);
        a.Select(src.Id);
        a.Copy();

        var (b, rootsB) = New();
        Assert.Equal(1, b.Paste());
        Assert.Single(rootsB);
        Assert.Equal(26, rootsB[0].X);

        var card = b.AddAt(ControlTypes.Card, 200, 200);
        b.Select(card.Id);
        b.Paste();
        Assert.Single(card.Children);
        Assert.NotEqual(rootsB[0].Id, card.Children[0].Id);
    }

    [Fact]
    public void PastedAndDuplicatedTrees_GetFreshIdsForEveryDescendant()
    {
        var (doc, roots) = New();
        var card = doc.AddAt(ControlTypes.Card, 10, 10);
        var inner = doc.AddAt(ControlTypes.Text, 20, 60); // lands inside the card
        Assert.Contains(inner, card.Children);
        doc.Select(card.Id);
        doc.Duplicate();
        var all = ElementIds.Flatten(roots).Select(e => e.Id).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void Align_ToSelectionBounds_AndToCanvasForSingleElement()
    {
        var (doc, _) = New();
        var a = AddBox(doc, 10, 10); var b = AddBox(doc, 50, 30); var c = AddBox(doc, 90, 60);
        var all = new[] { a, b, c };
        void Reset()
        {
            doc.SetBounds(a.Id, 10, 10, 20, 20); doc.SetBounds(b.Id, 50, 30, 20, 20); doc.SetBounds(c.Id, 90, 60, 20, 20);
            doc.SelectMany(all.Select(e => e.Id));
        }

        // selection bounds: x 10..110, y 10..80
        Reset(); doc.Align(AlignKind.Left);
        Assert.All(all, e => Assert.Equal(10, e.X));
        Reset(); doc.Align(AlignKind.Right);
        Assert.All(all, e => Assert.Equal(90, e.X));
        Reset(); doc.Align(AlignKind.CenterHorizontal);
        Assert.All(all, e => Assert.Equal(50, e.X));
        Reset(); doc.Align(AlignKind.Top);
        Assert.All(all, e => Assert.Equal(10, e.Y));
        Reset(); doc.Align(AlignKind.CenterVertical);
        Assert.All(all, e => Assert.Equal(35, e.Y));
        Reset(); doc.Align(AlignKind.Bottom);
        Assert.All(all, e => Assert.Equal(60, e.Y));

        doc.SetBounds(a.Id, 10, 10, 100, 40);
        doc.Select(a.Id);
        doc.Align(AlignKind.CenterHorizontal);
        doc.Align(AlignKind.CenterVertical);
        Assert.Equal(310, a.X);
        Assert.Equal(220, a.Y);
    }

    [Fact]
    public void Distribute_EqualisesGaps_KeepingOutermostFixed()
    {
        var (doc, _) = New();
        var a = AddBox(doc, 0, 0); var b = AddBox(doc, 50, 0); var c = AddBox(doc, 200, 0);
        doc.SelectMany(new[] { a.Id, b.Id, c.Id });
        Assert.True(doc.Distribute(DistributeAxis.Horizontal));
        Assert.Equal(0, a.X); Assert.Equal(100, b.X); Assert.Equal(200, c.X);

        doc.Select(a.Id);
        Assert.False(doc.Distribute(DistributeAxis.Horizontal)); // needs three
    }

    [Fact]
    public void SetSpacing_PlacesElementsWithExactGap()
    {
        var (doc, _) = New();
        var a = AddBox(doc, 0, 0); var b = AddBox(doc, 100, 0); var c = AddBox(doc, 200, 0);
        doc.SelectMany(new[] { a.Id, b.Id, c.Id });
        Assert.True(doc.SetSpacing(DistributeAxis.Horizontal, 10));
        Assert.Equal(new[] { 0.0, 30.0, 60.0 }, new[] { a.X, b.X, c.X });
        Assert.True(doc.SetSpacing(DistributeAxis.Vertical, 5));
        Assert.Equal(new[] { 0.0, 25.0, 50.0 }, new[] { a.Y, b.Y, c.Y });
    }

    [Fact]
    public void MatchSize_UsesFirstElementInZOrder()
    {
        var (doc, _) = New();
        var a = AddBox(doc, 0, 0, 50, 10); var b = AddBox(doc, 0, 40, 20, 30);
        doc.SelectMany(new[] { a.Id, b.Id });
        Assert.True(doc.MatchSize(true, false));
        Assert.Equal(50, b.Width); Assert.Equal(30, b.Height);
        Assert.True(doc.MatchSize(false, true));
        Assert.Equal(10, b.Height);
    }

    [Fact]
    public void Group_And_Ungroup_PreserveAbsolutePositions()
    {
        var (doc, roots) = New();
        var a = AddBox(doc, 10, 10, 20, 20); var b = AddBox(doc, 50, 40, 30, 30);
        doc.SelectMany(new[] { a.Id, b.Id });
        Assert.True(doc.Group());

        var group = Assert.Single(roots);
        Assert.Equal(ControlTypes.Group, group.Type);
        Assert.Equal(10, group.X); Assert.Equal(10, group.Y);
        Assert.Equal(70, group.Width); Assert.Equal(60, group.Height);
        Assert.Equal(new[] { 0.0, 40.0 }, group.Children.Select(c => c.X).ToArray());
        Assert.Equal(new[] { 0.0, 30.0 }, group.Children.Select(c => c.Y).ToArray());
        Assert.Equal(new[] { group.Id }, doc.SelectedIds);

        Assert.True(doc.Ungroup());
        Assert.Equal(2, roots.Count);
        Assert.Equal(new[] { 10.0, 50.0 }, roots.Select(r => r.X).ToArray());
        Assert.Equal(new[] { 10.0, 40.0 }, roots.Select(r => r.Y).ToArray());
        Assert.Equal(2, doc.SelectedIds.Count);

        doc.Undo(); // back to grouped
        Assert.Equal(ControlTypes.Group, Assert.Single(roots).Type);
    }

    [Fact]
    public void Group_NeedsTwoUnlockedElements()
    {
        var (doc, _) = New();
        var a = AddBox(doc, 0, 0);
        doc.Select(a.Id);
        Assert.False(doc.Group());
        Assert.False(doc.Ungroup());
    }

    [Fact]
    public void MoveLayer_ChangesZOrder()
    {
        var (doc, roots) = New();
        var a = AddBox(doc, 0, 0); var b = AddBox(doc, 0, 30); var c = AddBox(doc, 0, 60);
        string Order() => string.Join(",", roots.Select(r => r.Id[^1]));
        var ids = new[] { a.Id, b.Id, c.Id };

        doc.Select(a.Id); doc.MoveLayer(LayerMove.ToFront);
        Assert.Equal(new[] { b.Id, c.Id, a.Id }, roots.Select(r => r.Id));
        doc.Select(c.Id); doc.MoveLayer(LayerMove.ToBack);
        Assert.Equal(new[] { c.Id, b.Id, a.Id }, roots.Select(r => r.Id));
        doc.Select(c.Id); doc.MoveLayer(LayerMove.Forward);
        Assert.Equal(new[] { b.Id, c.Id, a.Id }, roots.Select(r => r.Id));
        doc.Select(a.Id); doc.MoveLayer(LayerMove.Backward);
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, roots.Select(r => r.Id));
        Assert.NotEmpty(Order());
        Assert.Equal(3, ids.Length);
    }

    [Fact]
    public void AddAt_InsideContainer_UsesLocalCoordinates()
    {
        var (doc, roots) = New();
        var container = doc.AddAt(ControlTypes.Card, 100, 100);
        var inner = doc.AddAt(ControlTypes.Button, 120, 150);
        Assert.Same(container, roots.Single());
        Assert.Contains(inner, container.Children);
        Assert.Equal(20, inner.X);
        Assert.Equal(150 - 100 - LayoutEngine.CardTitleHeight, inner.Y);
    }

    [Fact]
    public void StackChildren_CannotBeDraggedButCanBeResized()
    {
        var (doc, roots) = New();
        var stack = ElementFactory.Create(ControlTypes.Container, "stack", 0, 0);
        stack.Set("layout", "vertical");
        roots.Add(stack);
        var child = doc.AddAt(ControlTypes.Text, 20, 20);
        Assert.Contains(child, stack.Children);

        doc.Select(child.Id);
        doc.BeginInteraction();
        doc.UpdateMove(50, 50);
        doc.UpdateResize(ResizeHandle.BottomRight, 10, 10);
        doc.CommitInteraction("Resize");
        Assert.Equal(20, child.X);
        Assert.Equal(34, child.Height); // 24 + 10
    }

    [Fact]
    public void DroppingOntoAContainer_ReparentsAndKeepsAbsolutePosition()
    {
        var (doc, roots) = New();
        var container = ElementFactory.Create(ControlTypes.Container, "box", 0, 0);
        container.Width = 300; container.Height = 200;
        roots.Add(container);
        var e = AddBox(doc, 350, 250, 50, 50);
        doc.Select(e.Id);
        doc.BeginInteraction();
        doc.UpdateMove(-250, -200);
        doc.CommitInteraction("Move", (110, 60));
        Assert.Contains(e, container.Children);
        Assert.DoesNotContain(e, roots);
        Assert.Equal(100, e.X); Assert.Equal(50, e.Y);

        // and back out to the page
        doc.BeginInteraction();
        doc.UpdateMove(400, 0);
        doc.CommitInteraction("Move", (550, 60));
        Assert.Contains(e, roots);
        Assert.Equal(500, e.X);
    }

    [Fact]
    public void Property_Name_Visibility_Action_Condition_Edits_AreUndoable()
    {
        var (doc, _) = New();
        var b = doc.AddAt(ControlTypes.Button, 0, 0);
        doc.SetName(b.Id, "Install");
        doc.SetVisible(b.Id, false);
        doc.SetAction(b.Id, new ElementAction { Kind = ElementActionKind.GoToPage, TargetPageId = "x" });
        doc.SetEnabledWhen(b.Id, new ConditionDefinition { Variable = "Ok" });
        var e = doc.Find(b.Id)!;
        Assert.Equal("Install", e.Name); Assert.False(e.Visible);
        Assert.Equal(ElementActionKind.GoToPage, e.Action!.Kind);
        Assert.Equal("Ok", e.EnabledWhen!.Variable);
        doc.Undo(); doc.Undo(); doc.Undo(); doc.Undo();
        e = doc.Find(b.Id)!;
        Assert.NotEqual("Install", e.Name); Assert.True(e.Visible);
        Assert.Equal(ElementActionKind.None, e.Action!.Kind);
        Assert.Null(e.EnabledWhen);
    }

    [Fact]
    public void Changed_And_SelectionChanged_Events_Fire()
    {
        var (doc, _) = New();
        int changed = 0, selection = 0;
        doc.Changed += () => changed++;
        doc.SelectionChanged += () => selection++;
        var e = doc.AddAt(ControlTypes.Text, 0, 0);
        Assert.True(changed >= 1);
        Assert.True(selection >= 1);
        var before = changed;
        doc.SetProperty(e.Id, "text", "x");
        Assert.True(changed > before);
    }

    [Fact]
    public void AddAt_UnknownCustomControl_Throws()
    {
        var (doc, _) = New();
        Assert.Throws<ArgumentException>(() => doc.AddAt("custom:nope", 0, 0));
    }

    [Fact]
    public void AddAt_CustomControl_UsesDefinitionSizeAndParameterDefaults()
    {
        var def = new CustomControlDefinition { Id = "badge", Name = "Badge", Width = 150, Height = 30 };
        def.Parameters.Add(new CustomParameter { Name = "Title", Default = "Hello" });
        var roots = new List<DesignElement>();
        var doc = new DesignerDocument(roots, 720, 480, id => id == "badge" ? def : null);
        var e = doc.AddAt("custom:badge", 10, 10);
        Assert.Equal(150, e.Width); Assert.Equal(30, e.Height);
        Assert.Equal("Hello", e.Properties["Title"]);
        Assert.Equal("custom-badge-1", e.Id);
    }
}
