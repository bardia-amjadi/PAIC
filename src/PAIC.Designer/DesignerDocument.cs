using System.Text.Json;
using PAIC.Controls;

namespace PAIC.Designer;

/// <summary>Shared clipboard so elements can be copied between pages.</summary>
public static class DesignerClipboard
{
    public static List<DesignElement> Items { get; private set; } = new();
    public static void Set(IEnumerable<DesignElement> items) => Items = items.Select(i => i.Clone()).ToList();
    public static void Clear() => Items = new();
}

/// <summary>
/// An editing session over one list of root elements (a page, or a custom control's template). All edits go through the document so
/// they are undoable. Undo/redo restores snapshots into the same list instance, so the owner of the list never needs to re-attach it.
/// </summary>
public sealed class DesignerDocument
{
    private sealed record HistoryEntry(string Label, List<DesignElement> Before, List<DesignElement> After);

    private static readonly JsonSerializerOptions FingerprintOptions = new();
    private const int MaxHistory = 200;

    private readonly List<HistoryEntry> _undo = new();
    private readonly List<HistoryEntry> _redo = new();
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CustomControlDefinition?>? _controlLookup;

    private List<DesignElement>? _interactionBefore;
    private Dictionary<string, Rect>? _interactionStart;
    private bool _interactionTouched;

    public DesignerDocument(List<DesignElement> roots, double width, double height, Func<string, CustomControlDefinition?>? controlLookup = null)
    {
        Roots = roots;
        Width = width;
        Height = height;
        _controlLookup = controlLookup;
    }

    public List<DesignElement> Roots { get; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>Raised after any content change (including undo/redo and live drag updates).</summary>
    public event Action? Changed;
    public event Action? SelectionChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    // ---------------------------------------------------------------- lookup

    public DesignElement? Find(string id) => ElementIds.Flatten(Roots).FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    private (List<DesignElement> List, DesignElement? Parent)? Locate(string id) => Locate(Roots, null, id);

    private static (List<DesignElement>, DesignElement?)? Locate(List<DesignElement> list, DesignElement? parent, string id)
    {
        foreach (var e in list)
        {
            if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return (list, parent);
            var inner = Locate(e.Children, e, id);
            if (inner is not null) return inner;
        }
        return null;
    }

    public DesignElement? ParentOf(string id) => Locate(id)?.Parent;

    // ---------------------------------------------------------------- selection

    public IReadOnlyCollection<string> SelectedIds => _selection;

    public IReadOnlyList<DesignElement> SelectedElements =>
        _selection.Select(Find).Where(e => e is not null).Cast<DesignElement>().ToList();

    /// <summary>Selected elements whose ancestors are not also selected, in z-order.</summary>
    public IReadOnlyList<DesignElement> SelectionRoots()
    {
        var result = new List<DesignElement>();
        void Visit(IEnumerable<DesignElement> list)
        {
            foreach (var e in list)
            {
                if (_selection.Contains(e.Id)) result.Add(e); else Visit(e.Children);
            }
        }
        Visit(Roots);
        return result;
    }

    public void Select(string? id, bool additive = false)
    {
        if (!additive) _selection.Clear();
        if (id is not null && Find(id) is not null)
        {
            if (additive && !_selection.Add(id)) _selection.Remove(id);
            else _selection.Add(id);
        }
        SelectionChanged?.Invoke();
    }

    public void SelectMany(IEnumerable<string> ids)
    {
        _selection.Clear();
        foreach (var id in ids) if (Find(id) is not null) _selection.Add(id);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection() { _selection.Clear(); SelectionChanged?.Invoke(); }

    public void SelectAll() => SelectMany(Roots.Select(r => r.Id));

    private void PruneSelection()
    {
        var existing = ElementIds.AllIds(Roots);
        _selection.RemoveWhere(id => !existing.Contains(id));
    }

    // ---------------------------------------------------------------- history

    private static List<DesignElement> CloneAll(IEnumerable<DesignElement> src) => src.Select(e => e.Clone()).ToList();
    private static string Fingerprint(IEnumerable<DesignElement> src) => JsonSerializer.Serialize(src, FingerprintOptions);

    private void Replace(List<DesignElement> content)
    {
        Roots.Clear();
        Roots.AddRange(CloneAll(content));
        PruneSelection();
    }

    private bool Execute(string label, Action action)
    {
        if (_interactionBefore is not null) throw new InvalidOperationException("An interaction is in progress.");
        var before = CloneAll(Roots);
        action();
        if (Fingerprint(before) == Fingerprint(Roots)) return false;
        Push(new HistoryEntry(label, before, CloneAll(Roots)));
        PruneSelection();
        Changed?.Invoke();
        SelectionChanged?.Invoke();
        return true;
    }

    private void Push(HistoryEntry entry)
    {
        _undo.Add(entry);
        if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var e = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        Replace(e.Before); _redo.Add(e);
        Changed?.Invoke(); SelectionChanged?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var e = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        Replace(e.After); _undo.Add(e);
        Changed?.Invoke(); SelectionChanged?.Invoke();
        return true;
    }

    // ---------------------------------------------------------------- interactions (drag move / resize)

    public bool InteractionActive => _interactionBefore is not null;

    public void BeginInteraction()
    {
        if (_interactionBefore is not null) CancelInteraction();
        _interactionBefore = CloneAll(Roots);
        _interactionTouched = false;
        _interactionStart = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in SelectionRoots()) _interactionStart[e.Id] = new Rect(e.X, e.Y, e.Width, e.Height);
    }

    private bool CanMovePosition(DesignElement e)
    {
        if (e.Locked) return false;
        var parent = ParentOf(e.Id);
        return parent is null || !LayoutEngine.IsStack(parent);
    }

    /// <summary>Moves the selection to (start position + total delta). Locked elements and children of stack containers stay put.</summary>
    public void UpdateMove(double totalDx, double totalDy)
    {
        RequireInteraction();
        foreach (var e in SelectionRoots())
        {
            if (!CanMovePosition(e) || !_interactionStart!.TryGetValue(e.Id, out var s)) continue;
            e.X = s.X + totalDx; e.Y = s.Y + totalDy;
            _interactionTouched = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Resizes the single selected element with a handle drag measured from the interaction start.</summary>
    public void UpdateResize(ResizeHandle handle, double totalDx, double totalDy, bool keepAspect = false)
    {
        RequireInteraction();
        var roots = SelectionRoots();
        if (roots.Count != 1) return;
        var e = roots[0];
        if (e.Locked || !_interactionStart!.TryGetValue(e.Id, out var s)) return;
        var parent = ParentOf(e.Id);
        var stack = parent is not null && LayoutEngine.IsStack(parent);
        var r = ResizeMath.Resize(s, handle, totalDx, totalDy, 8, 8, keepAspect);
        if (!stack) { e.X = r.X; e.Y = r.Y; }
        e.Width = r.Width; e.Height = r.Height;
        _interactionTouched = true;
        Changed?.Invoke();
    }

    /// <summary>Finishes the interaction. With a drop point, moved elements are re-parented into the container under it (or to the root).</summary>
    public bool CommitInteraction(string label, (double X, double Y)? dropPoint = null)
    {
        RequireInteraction();
        var before = _interactionBefore!;
        var moved = SelectionRoots().Where(e => _interactionStart!.ContainsKey(e.Id) && CanMovePosition(e)).ToList();
        _interactionBefore = null; _interactionStart = null;

        if (dropPoint is { } p && _interactionTouched && moved.Count > 0) Reparent(moved, p.X, p.Y);

        if (Fingerprint(before) == Fingerprint(Roots)) { _interactionTouched = false; return false; }
        Push(new HistoryEntry(label, before, CloneAll(Roots)));
        Changed?.Invoke();
        return true;
    }

    public void CancelInteraction()
    {
        if (_interactionBefore is null) return;
        var before = _interactionBefore;
        _interactionBefore = null; _interactionStart = null;
        Replace(before);
        Changed?.Invoke(); SelectionChanged?.Invoke();
    }

    private void RequireInteraction()
    {
        if (_interactionBefore is null) throw new InvalidOperationException("Call BeginInteraction first.");
    }

    private void Reparent(List<DesignElement> moved, double dropX, double dropY)
    {
        var movedIds = moved.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var target = LayoutEngine.FindDropContainer(Roots, dropX, dropY, c => movedIds.Contains(c.Id));
        foreach (var e in moved)
        {
            var loc = Locate(e.Id);
            if (loc is null) continue;
            var currentParent = loc.Value.Parent;
            if (ReferenceEquals(currentParent, target)) continue;

            var abs = LayoutEngine.GetAbsoluteRect(Roots, e.Id);
            if (abs is null) continue;
            loc.Value.List.Remove(e);
            double ox = 0, oy = 0;
            if (target is not null)
            {
                var tAbs = LayoutEngine.GetAbsoluteRect(Roots, target.Id)!.Value;
                var (cx, cy) = LayoutEngine.ContentOffset(target);
                ox = tAbs.X + cx; oy = tAbs.Y + cy;
            }
            e.X = abs.Value.X - ox; e.Y = abs.Value.Y - oy;
            (target?.Children ?? Roots).Add(e);
        }
    }

    // ---------------------------------------------------------------- add / delete / clipboard

    private CustomControlDefinition? ControlFor(string type) =>
        ControlTypes.IsCustom(type) ? _controlLookup?.Invoke(ControlTypes.CustomId(type)) : null;

    /// <summary>Adds a new element with its top-left at the given absolute canvas position; containers under the point receive it as a child.</summary>
    public DesignElement AddAt(string type, double x, double y)
    {
        DesignElement? created = null;
        var custom = ControlFor(type);
        if (ControlTypes.IsCustom(type) && custom is null) throw new ArgumentException($"Unknown custom control '{type}'.", nameof(type));
        Execute($"Add {type}", () =>
        {
            var container = LayoutEngine.FindDropContainer(Roots, x, y);
            var el = ElementFactory.Create(type, ElementIds.Next(Roots, type), 0, 0, ElementIds.Flatten(Roots), custom);
            double ox = 0, oy = 0;
            if (container is not null)
            {
                var abs = LayoutEngine.GetAbsoluteRect(Roots, container.Id)!.Value;
                var (cx, cy) = LayoutEngine.ContentOffset(container);
                ox = abs.X + cx; oy = abs.Y + cy;
            }
            el.X = Math.Max(0, Math.Round(x - ox)); el.Y = Math.Max(0, Math.Round(y - oy));
            (container?.Children ?? Roots).Add(el);
            created = el;
        });
        _selection.Clear();
        if (created is not null) _selection.Add(created.Id);
        SelectionChanged?.Invoke();
        return created!;
    }

    /// <summary>Adds a new element at a cascading default position.</summary>
    public DesignElement AddDefault(string type)
    {
        var n = Roots.Count % 10;
        return AddAt(type, 40 + n * 16, 40 + n * 16);
    }

    public int DeleteSelection()
    {
        var targets = SelectionRoots().Where(e => !e.Locked).Select(e => e.Id).ToList();
        if (targets.Count == 0) return 0;
        Execute("Delete", () => { foreach (var id in targets) Locate(id)?.List.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)); });
        _selection.Clear();
        SelectionChanged?.Invoke();
        return targets.Count;
    }

    public void Copy() => DesignerClipboard.Set(SelectionRoots());

    public int Cut()
    {
        Copy();
        return DeleteSelection();
    }

    public int Duplicate()
    {
        var sources = SelectionRoots();
        if (sources.Count == 0) return 0;
        var ids = new List<string>();
        Execute("Duplicate", () =>
        {
            foreach (var src in sources)
            {
                var loc = Locate(src.Id)!.Value;
                var copy = src.Clone();
                Reid(copy);
                copy.X += 16; copy.Y += 16;
                var index = loc.List.IndexOf(src);
                loc.List.Insert(index + 1, copy);
                ids.Add(copy.Id);
            }
        });
        SelectMany(ids);
        return ids.Count;
    }

    /// <summary>Pastes the clipboard into the selected container, or the root.</summary>
    public int Paste()
    {
        var items = DesignerClipboard.Items;
        if (items.Count == 0) return 0;
        var ids = new List<string>();
        var target = SelectionRoots().FirstOrDefault(e => ControlRegistry.TryGet(e.Type, out var d) && d.IsContainer && e.Type != ControlTypes.Group);
        Execute("Paste", () =>
        {
            foreach (var src in items)
            {
                var copy = src.Clone();
                Reid(copy);
                if (target is null) { copy.X += 16; copy.Y += 16; }
                (target?.Children ?? Roots).Add(copy);
                ids.Add(copy.Id);
            }
        });
        SelectMany(ids);
        return ids.Count;
    }

    /// <summary>Gives the element and all its descendants fresh ids that do not collide with anything in the document.</summary>
    private void Reid(DesignElement e) => AssignId(e, ElementIds.AllIds(Roots));

    private static void AssignId(DesignElement e, HashSet<string> used)
    {
        var oldId = e.Id;
        var baseName = ElementIds.BaseFor(e.Type);
        var i = 1;
        string id;
        do { id = $"{baseName}-{i++}"; } while (used.Contains(id));
        used.Add(id);
        e.Id = id;
        if (string.IsNullOrEmpty(e.Name) || e.Name == oldId) e.Name = id;
        foreach (var c in e.Children) AssignId(c, used);
    }

    // ---------------------------------------------------------------- property edits

    private DesignElement Require(string id) => Find(id) ?? throw new ArgumentException($"No element '{id}'.", nameof(id));

    public void SetProperty(string id, string name, string value) => Execute($"Set {name}", () => Require(id).Properties[name] = value);
    public void SetName(string id, string name) => Execute("Rename", () => Require(id).Name = name);
    public void SetVisible(string id, bool visible) => Execute(visible ? "Show" : "Hide", () => Require(id).Visible = visible);
    public void SetLocked(string id, bool locked) => Execute(locked ? "Lock" : "Unlock", () => Require(id).Locked = locked);
    public void SetAction(string id, ElementAction? action) => Execute("Set action", () => Require(id).Action = action?.Clone());
    public void SetVisibleWhen(string id, ConditionDefinition? c) => Execute("Set visibility condition", () => Require(id).VisibleWhen = c?.Clone());
    public void SetEnabledWhen(string id, ConditionDefinition? c) => Execute("Set enabled condition", () => Require(id).EnabledWhen = c?.Clone());

    public void SetBounds(string id, double x, double y, double width, double height) =>
        Execute("Set bounds", () =>
        {
            var e = Require(id);
            if (e.Locked) return;
            if (width <= 0 || height <= 0) throw new ArgumentException("Width and height must be positive.");
            e.X = x; e.Y = y; e.Width = width; e.Height = height;
        });

    /// <summary>Moves the selection by a fixed amount as one undoable step (keyboard nudging).</summary>
    public bool Nudge(double dx, double dy) =>
        Execute("Nudge", () =>
        {
            foreach (var e in SelectionRoots().Where(CanMovePosition)) { e.X += dx; e.Y += dy; }
        });

    // ---------------------------------------------------------------- alignment & spacing

    /// <summary>Selected, unlocked, absolutely positioned elements that share the first one's parent.</summary>
    private List<DesignElement> Siblings()
    {
        var roots = SelectionRoots().Where(CanMovePosition).ToList();
        if (roots.Count == 0) return roots;
        var parent = ParentOf(roots[0].Id);
        return roots.Where(r => ReferenceEquals(ParentOf(r.Id), parent)).ToList();
    }

    public bool Align(AlignKind kind)
    {
        var els = Siblings();
        if (els.Count == 0) return false;
        var parent = ParentOf(els[0].Id);
        Rect bounds;
        if (els.Count == 1) bounds = new Rect(0, 0, parent?.Width ?? Width, parent?.Height ?? Height);
        else bounds = els.Select(e => new Rect(e.X, e.Y, e.Width, e.Height)).Aggregate((a, b) => a.Union(b));
        return Execute("Align " + kind, () =>
        {
            foreach (var e in els)
            {
                switch (kind)
                {
                    case AlignKind.Left: e.X = bounds.X; break;
                    case AlignKind.CenterHorizontal: e.X = bounds.X + (bounds.Width - e.Width) / 2; break;
                    case AlignKind.Right: e.X = bounds.Right - e.Width; break;
                    case AlignKind.Top: e.Y = bounds.Y; break;
                    case AlignKind.CenterVertical: e.Y = bounds.Y + (bounds.Height - e.Height) / 2; break;
                    case AlignKind.Bottom: e.Y = bounds.Bottom - e.Height; break;
                }
                e.X = Math.Round(e.X, 2); e.Y = Math.Round(e.Y, 2);
            }
        });
    }

    /// <summary>Equalises the gaps between three or more elements, keeping the outermost ones fixed.</summary>
    public bool Distribute(DistributeAxis axis)
    {
        var els = Siblings();
        if (els.Count < 3) return false;
        var sorted = axis == DistributeAxis.Horizontal ? els.OrderBy(e => e.X).ToList() : els.OrderBy(e => e.Y).ToList();
        return Execute("Distribute " + axis, () =>
        {
            if (axis == DistributeAxis.Horizontal)
            {
                var total = sorted[^1].X + sorted[^1].Width - sorted[0].X;
                var gap = (total - sorted.Sum(e => e.Width)) / (sorted.Count - 1);
                var pos = sorted[0].X;
                foreach (var e in sorted) { e.X = Math.Round(pos, 2); pos += e.Width + gap; }
            }
            else
            {
                var total = sorted[^1].Y + sorted[^1].Height - sorted[0].Y;
                var gap = (total - sorted.Sum(e => e.Height)) / (sorted.Count - 1);
                var pos = sorted[0].Y;
                foreach (var e in sorted) { e.Y = Math.Round(pos, 2); pos += e.Height + gap; }
            }
        });
    }

    /// <summary>Sets an exact gap between consecutive elements (sorted by position), keeping the first fixed.</summary>
    public bool SetSpacing(DistributeAxis axis, double gap)
    {
        var els = Siblings();
        if (els.Count < 2) return false;
        var sorted = axis == DistributeAxis.Horizontal ? els.OrderBy(e => e.X).ToList() : els.OrderBy(e => e.Y).ToList();
        return Execute("Set spacing", () =>
        {
            var pos = axis == DistributeAxis.Horizontal ? sorted[0].X : sorted[0].Y;
            foreach (var e in sorted)
            {
                if (axis == DistributeAxis.Horizontal) { e.X = pos; pos += e.Width + gap; }
                else { e.Y = pos; pos += e.Height + gap; }
            }
        });
    }

    /// <summary>Gives all selected elements the width and/or height of the first selected element.</summary>
    public bool MatchSize(bool width, bool height)
    {
        var roots = SelectionRoots().Where(e => !e.Locked).ToList();
        if (roots.Count < 2) return false;
        var reference = roots[0];
        return Execute("Match size", () =>
        {
            foreach (var e in roots.Skip(1)) { if (width) e.Width = reference.Width; if (height) e.Height = reference.Height; }
        });
    }

    // ---------------------------------------------------------------- group / layers

    public bool Group()
    {
        var els = SelectionRoots().Where(e => !e.Locked).ToList();
        if (els.Count < 2) return false;
        var loc0 = Locate(els[0].Id)!.Value;
        if (loc0.Parent is not null && LayoutEngine.IsStack(loc0.Parent)) return false;
        if (els.Any(e => !ReferenceEquals(Locate(e.Id)!.Value.List, loc0.List))) return false;

        string? groupId = null;
        Execute("Group", () =>
        {
            var bounds = els.Select(e => new Rect(e.X, e.Y, e.Width, e.Height)).Aggregate((a, b) => a.Union(b));
            var group = ElementFactory.Create(ControlTypes.Group, ElementIds.Next(Roots, ControlTypes.Group), bounds.X, bounds.Y, ElementIds.Flatten(Roots));
            group.Width = bounds.Width; group.Height = bounds.Height;
            var topIndex = els.Max(e => loc0.List.IndexOf(e));
            foreach (var e in els) { loc0.List.Remove(e); e.X -= bounds.X; e.Y -= bounds.Y; group.Children.Add(e); }
            loc0.List.Insert(Math.Min(topIndex - els.Count + 1, loc0.List.Count), group);
            groupId = group.Id;
        });
        if (groupId is not null) Select(groupId);
        return groupId is not null;
    }

    public bool Ungroup()
    {
        var groups = SelectionRoots().Where(e => e.Type == ControlTypes.Group && !e.Locked).ToList();
        if (groups.Count == 0) return false;
        var released = new List<string>();
        Execute("Ungroup", () =>
        {
            foreach (var g in groups)
            {
                var loc = Locate(g.Id)!.Value;
                var index = loc.List.IndexOf(g);
                loc.List.RemoveAt(index);
                foreach (var child in g.Children)
                {
                    child.X += g.X; child.Y += g.Y;
                    loc.List.Insert(index++, child);
                    released.Add(child.Id);
                }
            }
        });
        SelectMany(released);
        return true;
    }

    public bool MoveLayer(LayerMove move)
    {
        var els = SelectionRoots().ToList();
        if (els.Count == 0) return false;
        return Execute("Arrange " + move, () =>
        {
            var ordered = move is LayerMove.ToFront or LayerMove.Forward ? els.AsEnumerable().Reverse() : els;
            foreach (var e in ordered)
            {
                var list = Locate(e.Id)!.Value.List;
                var i = list.IndexOf(e);
                switch (move)
                {
                    case LayerMove.ToFront: list.RemoveAt(i); list.Add(e); break;
                    case LayerMove.ToBack: list.RemoveAt(i); list.Insert(0, e); break;
                    case LayerMove.Forward when i < list.Count - 1 && !_selection.Contains(list[i + 1].Id): list.RemoveAt(i); list.Insert(i + 1, e); break;
                    case LayerMove.Backward when i > 0 && !_selection.Contains(list[i - 1].Id): list.RemoveAt(i); list.Insert(i - 1, e); break;
                }
            }
        });
    }
}
