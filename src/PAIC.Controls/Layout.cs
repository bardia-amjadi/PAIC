namespace PAIC.Controls;

public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
    public Rect Union(Rect o)
    {
        var l = Math.Min(X, o.X); var t = Math.Min(Y, o.Y);
        return new Rect(l, t, Math.Max(Right, o.Right) - l, Math.Max(Bottom, o.Bottom) - t);
    }
}

public static class ElementIds
{
    public static string BaseFor(string type) =>
        ControlTypes.IsCustom(type) ? "custom-" + ControlTypes.CustomId(type).ToLowerInvariant() : type;

    /// <summary>Returns the first unused "type-n" id across the whole element tree.</summary>
    public static string Next(IEnumerable<DesignElement> roots, string type)
    {
        var used = AllIds(roots);
        var baseName = BaseFor(type);
        for (var i = 1; ; i++)
        {
            var id = $"{baseName}-{i}";
            if (!used.Contains(id)) return id;
        }
    }

    public static HashSet<string> AllIds(IEnumerable<DesignElement> roots)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots) { set.Add(r.Id); foreach (var d in r.Descendants()) set.Add(d.Id); }
        return set;
    }

    public static IEnumerable<DesignElement> Flatten(IEnumerable<DesignElement> roots)
    {
        foreach (var r in roots) { yield return r; foreach (var d in r.Descendants()) yield return d; }
    }

    public static HashSet<string> VariableNames(IEnumerable<DesignElement> roots) =>
        Flatten(roots).Select(e => e.GetString("variable")).Where(v => !string.IsNullOrWhiteSpace(v)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public static class ElementFactory
{
    /// <summary>Creates an element with descriptor defaults. Required variable properties get a unique name.</summary>
    public static DesignElement Create(string type, string id, double x, double y, IEnumerable<DesignElement>? existing = null,
        CustomControlDefinition? custom = null)
    {
        var el = new DesignElement { Id = id, Type = type, Name = id, X = x, Y = y };
        if (ControlTypes.IsCustom(type))
        {
            if (custom is null) throw new ArgumentException("A custom control definition is required.", nameof(custom));
            el.Width = custom.Width; el.Height = custom.Height;
            foreach (var p in custom.Parameters) el.Properties[p.Name] = p.Default;
            return el;
        }
        var d = ControlRegistry.Get(type);
        el.Width = d.DefaultWidth; el.Height = d.DefaultHeight;
        var usedVars = existing is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : ElementIds.VariableNames(existing);
        foreach (var p in d.Properties)
        {
            if (p.Kind == PropertyKind.Variable && p.Required)
            {
                var baseName = char.ToUpperInvariant(type[0]) + type[1..];
                var name = baseName; var i = 1;
                while (usedVars.Contains(name)) name = baseName + (++i);
                el.Properties[p.Name] = name;
            }
            else if (!string.IsNullOrEmpty(p.Default)) el.Properties[p.Name] = p.Default;
        }
        if (type == ControlTypes.Button) el.Action = new ElementAction();
        return el;
    }
}

/// <summary>Computes child rectangles and absolute positions, including stack layouts of containers.</summary>
public static class LayoutEngine
{
    public const double AccordionHeaderHeight = 36;
    public const double CardTitleHeight = 32;

    public static bool IsStack(DesignElement e) =>
        e.Type == ControlTypes.Container && e.GetString("layout") is "vertical" or "horizontal";

    public static bool ChildrenVisible(DesignElement e) => e.Type != ControlTypes.Accordion || e.GetBool("expanded", true);

    /// <summary>Offset of the children's coordinate origin inside the element.</summary>
    public static (double X, double Y) ContentOffset(DesignElement e) => e.Type switch
    {
        ControlTypes.Accordion => (0, AccordionHeaderHeight),
        ControlTypes.Card => (0, string.IsNullOrWhiteSpace(e.GetString("title")) ? 0 : CardTitleHeight),
        _ => (0, 0)
    };

    /// <summary>Rectangle of each child in the parent's content coordinate space.</summary>
    public static IReadOnlyList<Rect> ArrangeChildren(DesignElement parent)
    {
        var rects = new List<Rect>(parent.Children.Count);
        if (!IsStack(parent))
        {
            foreach (var c in parent.Children) rects.Add(new Rect(c.X, c.Y, c.Width, c.Height));
            return rects;
        }
        var pad = parent.GetNumber("padding", 0);
        var spacing = parent.GetNumber("spacing", 8);
        var stretch = parent.GetBool("stretch", true);
        var vertical = parent.GetString("layout") == "vertical";
        var pos = pad;
        foreach (var c in parent.Children)
        {
            if (vertical)
            {
                rects.Add(new Rect(pad, pos, stretch ? Math.Max(0, parent.Width - 2 * pad) : c.Width, c.Height));
                pos += c.Height + spacing;
            }
            else
            {
                rects.Add(new Rect(pos, pad, c.Width, stretch ? Math.Max(0, parent.Height - 2 * pad) : c.Height));
                pos += c.Width + spacing;
            }
        }
        return rects;
    }

    /// <summary>Rectangle of a root-level element (roots are always absolutely positioned).</summary>
    public static Rect RootRect(DesignElement e) => new(e.X, e.Y, e.Width, e.Height);

    /// <summary>Visits every element in z-order (parents before children, later siblings after earlier ones) with its absolute rectangle.</summary>
    public static void Walk(IReadOnlyList<DesignElement> roots, Action<DesignElement, Rect, IReadOnlyList<DesignElement>> visit)
    {
        var ancestors = new List<DesignElement>();
        void Visit(DesignElement e, Rect abs)
        {
            visit(e, abs, ancestors);
            if (e.Children.Count == 0 || !ChildrenVisible(e)) return;
            var (ox, oy) = ContentOffset(e);
            var rects = ArrangeChildren(e);
            ancestors.Add(e);
            for (var i = 0; i < e.Children.Count; i++)
            {
                var r = rects[i];
                Visit(e.Children[i], new Rect(abs.X + ox + r.X, abs.Y + oy + r.Y, r.Width, r.Height));
            }
            ancestors.RemoveAt(ancestors.Count - 1);
        }
        foreach (var r in roots) Visit(r, RootRect(r));
    }

    public static Rect? GetAbsoluteRect(IReadOnlyList<DesignElement> roots, string id)
    {
        Rect? found = null;
        Walk(roots, (e, r, _) => { if (found is null && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) found = r; });
        return found;
    }

    /// <summary>Topmost visible element at the point. Members of a group resolve to the outermost group unless enterGroups is set.</summary>
    public static DesignElement? HitTest(IReadOnlyList<DesignElement> roots, double x, double y, bool enterGroups = false)
    {
        DesignElement? hit = null;
        var hiddenDepthParents = new HashSet<DesignElement>();
        Walk(roots, (e, r, anc) =>
        {
            if (!e.Visible || anc.Any(a => !a.Visible)) return;
            if (!r.Contains(x, y)) return;
            hit = enterGroups ? e : (anc.FirstOrDefault(a => a.Type == ControlTypes.Group) ?? e);
        });
        return hit;
    }

    /// <summary>Finds the deepest container whose content area contains the point (used as a drop target).</summary>
    public static DesignElement? FindDropContainer(IReadOnlyList<DesignElement> roots, double x, double y, Func<DesignElement, bool>? exclude = null)
    {
        DesignElement? target = null;
        Walk(roots, (e, r, anc) =>
        {
            if (!e.Visible || !ControlRegistry.TryGet(e.Type, out var d) || !d.IsContainer) return;
            if (exclude is not null && (exclude(e) || anc.Any(exclude))) return;
            if (e.Type == ControlTypes.Group) return; // groups are not drop targets
            if (r.Contains(x, y)) target = e;
        });
        return target;
    }
}
