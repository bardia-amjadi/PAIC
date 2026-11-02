using PAIC.Controls;
using PAIC.Core.Validation;
using Xunit;

namespace PAIC.Tests;

public class ControlRegistryTests
{
    [Fact]
    public void AllSpecifiedControlTypes_AreRegistered()
    {
        var types = ControlRegistry.All.Select(d => d.Type).ToHashSet();
        foreach (var t in new[] { "text", "richtext", "image", "button", "textinput", "passwordinput", "checkbox", "radio", "dropdown", "list", "progress", "card", "stepper", "accordion", "separator", "icon", "container" })
            Assert.Contains(t, types);
    }

    [Fact]
    public void Descriptors_HaveConsistentContracts()
    {
        foreach (var d in ControlRegistry.All)
        {
            Assert.Equal(d.Properties.Count, d.Properties.Select(p => p.Name).Distinct().Count());
            foreach (var p in d.Properties)
            {
                if (p.Kind is PropertyKind.Choice or PropertyKind.Glyph or PropertyKind.ColorToken)
                {
                    Assert.NotNull(p.Choices);
                    Assert.Contains(p.Default, p.Choices!);
                }
                if (p.Kind == PropertyKind.Number) Assert.True(p.Max > p.Min, $"{d.Type}.{p.Name}");
            }
            Assert.True(d.DefaultWidth > 0 && d.DefaultHeight > 0);
        }
    }

    [Fact]
    public void Factory_AppliesDefaults_AndUniqueVariableNames()
    {
        var first = ElementFactory.Create(ControlTypes.TextInput, "textinput-1", 10, 20);
        Assert.Equal(300, first.Width);
        Assert.Equal(56, first.Height);
        Assert.Equal("Textinput", first.GetString("variable"));
        var second = ElementFactory.Create(ControlTypes.TextInput, "textinput-2", 0, 0, new[] { first });
        Assert.Equal("Textinput2", second.GetString("variable"));
        Assert.NotNull(ElementFactory.Create(ControlTypes.Button, "button-1", 0, 0).Action);
    }

    [Fact]
    public void Getters_FallBackToDescriptorDefaults()
    {
        var e = new DesignElement { Id = "t", Type = ControlTypes.Text };
        Assert.Equal("body", e.GetString("style"));
        Assert.True(e.GetBool("wrap"));
        e.Set("style", "title");
        Assert.Equal("title", e.GetString("style"));
        e.Set("x", 1.5);
        Assert.Equal(1.5, e.GetNumber("x"));
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var parent = ElementFactory.Create(ControlTypes.Card, "card-1", 0, 0);
        parent.Children.Add(ElementFactory.Create(ControlTypes.Button, "button-1", 5, 5));
        parent.Children[0].Action = new ElementAction { Kind = ElementActionKind.Next };
        var copy = parent.Clone();
        copy.Children[0].X = 99;
        copy.Children[0].Action!.Kind = ElementActionKind.Cancel;
        copy.Properties["title"] = "changed";
        Assert.Equal(5, parent.Children[0].X);
        Assert.Equal(ElementActionKind.Next, parent.Children[0].Action!.Kind);
        Assert.Equal("Card", parent.GetString("title"));
    }

    [Fact]
    public void ElementIds_AreUniqueAcrossTree()
    {
        var card = ElementFactory.Create(ControlTypes.Card, "card-1", 0, 0);
        card.Children.Add(ElementFactory.Create(ControlTypes.Text, "text-1", 0, 0));
        var roots = new List<DesignElement> { card };
        Assert.Equal("text-2", ElementIds.Next(roots, ControlTypes.Text));
        Assert.Equal("custom-badge-1", ElementIds.Next(roots, "custom:badge"));
    }
}

public class LayoutEngineTests
{
    private static DesignElement Box(string id, double x, double y, double w, double h, string type = ControlTypes.Text) =>
        new() { Id = id, Type = type, X = x, Y = y, Width = w, Height = h };

    [Fact]
    public void VerticalStack_ArrangesWithPaddingSpacingAndStretch()
    {
        var c = Box("c", 0, 0, 300, 200, ControlTypes.Container);
        c.Set("layout", "vertical"); c.Set("padding", 10); c.Set("spacing", 5);
        c.Children.Add(Box("a", 99, 99, 50, 20));
        c.Children.Add(Box("b", 99, 99, 50, 30));
        var r = LayoutEngine.ArrangeChildren(c);
        Assert.Equal(new Rect(10, 10, 280, 20), r[0]);
        Assert.Equal(new Rect(10, 35, 280, 30), r[1]);
    }

    [Fact]
    public void HorizontalStack_WithoutStretch_KeepsChildHeight()
    {
        var c = Box("c", 0, 0, 300, 100, ControlTypes.Container);
        c.Set("layout", "horizontal"); c.Set("spacing", 8); c.Set("stretch", false);
        c.Children.Add(Box("a", 0, 0, 50, 20));
        c.Children.Add(Box("b", 0, 0, 60, 30));
        var r = LayoutEngine.ArrangeChildren(c);
        Assert.Equal(new Rect(0, 0, 50, 20), r[0]);
        Assert.Equal(new Rect(58, 0, 60, 30), r[1]);
    }

    [Fact]
    public void AbsoluteContainer_UsesChildPositions()
    {
        var c = Box("c", 0, 0, 300, 100, ControlTypes.Container);
        c.Children.Add(Box("a", 7, 9, 50, 20));
        Assert.Equal(new Rect(7, 9, 50, 20), LayoutEngine.ArrangeChildren(c)[0]);
    }

    [Fact]
    public void AbsoluteRect_IncludesCardTitleOffset()
    {
        var card = ElementFactory.Create(ControlTypes.Card, "card-1", 100, 50);
        card.Children.Add(Box("child", 10, 10, 40, 20));
        var abs = LayoutEngine.GetAbsoluteRect(new List<DesignElement> { card }, "child")!.Value;
        Assert.Equal(110, abs.X);
        Assert.Equal(50 + LayoutEngine.CardTitleHeight + 10, abs.Y);
    }

    [Fact]
    public void CollapsedAccordion_HidesChildrenFromWalkAndHitTest()
    {
        var acc = ElementFactory.Create(ControlTypes.Accordion, "acc-1", 0, 0);
        acc.Children.Add(Box("child", 0, 0, 50, 20));
        var roots = new List<DesignElement> { acc };
        Assert.NotNull(LayoutEngine.GetAbsoluteRect(roots, "child"));
        acc.Set("expanded", false);
        Assert.Null(LayoutEngine.GetAbsoluteRect(roots, "child"));
        Assert.Equal("acc-1", LayoutEngine.HitTest(roots, 10, 10)!.Id);
    }

    [Fact]
    public void HitTest_ReturnsTopmost_SkipsHidden_AndResolvesGroups()
    {
        var below = Box("below", 0, 0, 100, 100);
        var above = Box("above", 50, 50, 100, 100);
        var roots = new List<DesignElement> { below, above };
        Assert.Equal("above", LayoutEngine.HitTest(roots, 60, 60)!.Id);
        above.Visible = false;
        Assert.Equal("below", LayoutEngine.HitTest(roots, 60, 60)!.Id);
        Assert.Null(LayoutEngine.HitTest(roots, 300, 300));

        var group = Box("grp", 200, 200, 100, 100, ControlTypes.Group);
        group.Children.Add(Box("inner", 10, 10, 20, 20));
        roots.Add(group);
        Assert.Equal("grp", LayoutEngine.HitTest(roots, 215, 215)!.Id);
        Assert.Equal("inner", LayoutEngine.HitTest(roots, 215, 215, enterGroups: true)!.Id);
    }

    [Fact]
    public void DropContainer_IsDeepestContainerUnderPoint_AndSkipsExcluded()
    {
        var outer = Box("outer", 0, 0, 400, 400, ControlTypes.Container);
        var inner = Box("inner", 50, 50, 100, 100, ControlTypes.Card);
        outer.Children.Add(inner);
        var roots = new List<DesignElement> { outer };
        Assert.Equal("inner", LayoutEngine.FindDropContainer(roots, 60, 60)!.Id);
        Assert.Equal("outer", LayoutEngine.FindDropContainer(roots, 300, 300)!.Id);
        Assert.Equal("outer", LayoutEngine.FindDropContainer(roots, 60, 60, c => c.Id == "inner")!.Id);
        Assert.Null(LayoutEngine.FindDropContainer(roots, 500, 500));
    }
}

public class ConditionTests
{
    private static Dictionary<string, string> Vars(params (string, string)[] kv) =>
        new(kv.ToDictionary(x => x.Item1, x => x.Item2), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void NullCondition_IsAlwaysTrue() => Assert.True(ConditionEvaluator.Evaluate(null, Vars()));

    [Theory]
    [InlineData(ConditionOperator.IsTrue, "true", "", true)]
    [InlineData(ConditionOperator.IsTrue, "YES", "", true)]
    [InlineData(ConditionOperator.IsTrue, "0", "", false)]
    [InlineData(ConditionOperator.IsFalse, "false", "", true)]
    [InlineData(ConditionOperator.Equals, "Abc", "abc", true)]
    [InlineData(ConditionOperator.NotEquals, "a", "b", true)]
    [InlineData(ConditionOperator.Contains, "hello world", "WORLD", true)]
    [InlineData(ConditionOperator.IsEmpty, "  ", "", true)]
    [InlineData(ConditionOperator.IsNotEmpty, "x", "", true)]
    public void Operators(ConditionOperator op, string value, string operand, bool expected) =>
        Assert.Equal(expected, ConditionEvaluator.Evaluate(new ConditionDefinition { Variable = "v", Operator = op, Value = operand }, Vars(("v", value))));

    [Fact]
    public void MissingVariable_IsEmptyAndFalse()
    {
        Assert.False(ConditionEvaluator.Evaluate(new ConditionDefinition { Variable = "nope", Operator = ConditionOperator.IsTrue }, Vars()));
        Assert.True(ConditionEvaluator.Evaluate(new ConditionDefinition { Variable = "nope", Operator = ConditionOperator.IsEmpty }, Vars()));
    }

    [Fact]
    public void Templating_ReplacesKnownTokensOnly()
    {
        var vars = Vars(("ProductName", "Contoso"), ("Version", "1.2"));
        Assert.Equal("Contoso 1.2 {Unknown} {param:x}", TextTemplating.Resolve("{ProductName} {Version} {Unknown} {param:x}", vars));
        Assert.Equal(new[] { "A", "B" }, TextTemplating.References("{A} and {B}"));
        Assert.Equal("", TextTemplating.Resolve(null, vars));
    }
}

public class ElementValidatorTests
{
    private static List<ValidationIssue> Check(List<DesignElement> roots, ElementValidationContext? ctx = null) =>
        ElementValidator.Validate(roots, ctx ?? new ElementValidationContext()).ToList();

    [Fact]
    public void ValidElements_HaveNoIssues()
    {
        var roots = new List<DesignElement>
        {
            ElementFactory.Create(ControlTypes.Text, "text-1", 10, 10),
            ElementFactory.Create(ControlTypes.TextInput, "textinput-1", 10, 50)
        };
        Assert.Empty(Check(roots));
    }

    [Fact]
    public void DuplicateIds_UnknownTypes_AndBadSizes_AreErrors()
    {
        var a = ElementFactory.Create(ControlTypes.Text, "dup", 0, 0);
        var b = ElementFactory.Create(ControlTypes.Text, "dup", 0, 100);
        var c = new DesignElement { Id = "c", Type = "hologram", Width = 10, Height = 10 };
        var d = ElementFactory.Create(ControlTypes.Text, "d", 0, 200); d.Width = 0;
        var codes = Check(new List<DesignElement> { a, b, c, d }).Select(i => i.Code).ToList();
        Assert.Contains("PAIC1601", codes);
        Assert.Contains("PAIC1602", codes);
        Assert.Contains("PAIC1603", codes);
    }

    [Fact]
    public void PropertyContract_IsEnforced()
    {
        var progress = ElementFactory.Create(ControlTypes.Progress, "p", 0, 0);
        progress.Set("value", "250");
        var text = ElementFactory.Create(ControlTypes.Text, "t", 0, 40);
        text.Set("style", "gigantic");
        text.Set("mystery", "x");
        var input = ElementFactory.Create(ControlTypes.TextInput, "i", 0, 80);
        input.Set("variable", "1bad name");
        var image = ElementFactory.Create(ControlTypes.Image, "img", 0, 160);
        var issues = Check(new List<DesignElement> { progress, text, input, image });
        Assert.Contains(issues, i => i.Code == "PAIC1604" && i.Message.Contains("between"));
        Assert.Contains(issues, i => i.Code == "PAIC1604" && i.Message.Contains("one of"));
        Assert.Contains(issues, i => i.Code == "PAIC1605" && i.Severity == ValidationSeverity.Warning);
        Assert.Contains(issues, i => i.Code == "PAIC1604" && i.Message.Contains("Variable"));
        Assert.Contains(issues, i => i.Code == "PAIC1604" && i.Message.Contains("required"));
    }

    [Fact]
    public void AssetReferences_AreChecked()
    {
        var image = ElementFactory.Create(ControlTypes.Image, "img", 0, 0);
        image.Set("asset", "logo");
        Assert.Contains(Check(new List<DesignElement> { image }), i => i.Code == "PAIC1606");
        var ok = new ElementValidationContext { AssetIds = new HashSet<string> { "logo" } };
        Assert.DoesNotContain(Check(new List<DesignElement> { image }, ok), i => i.Code == "PAIC1606");
    }

    [Fact]
    public void ActionsAndConditions_AreChecked()
    {
        var b = ElementFactory.Create(ControlTypes.Button, "b", 0, 0);
        b.Action = new ElementAction { Kind = ElementActionKind.GoToPage, TargetPageId = "ghost" };
        b.VisibleWhen = new ConditionDefinition { Variable = "Nope" };
        var t = ElementFactory.Create(ControlTypes.Text, "t", 0, 50);
        t.Action = new ElementAction { Kind = ElementActionKind.Next };
        t.EnabledWhen = new ConditionDefinition { Variable = "" };
        var ctx = new ElementValidationContext { PageIds = new HashSet<string> { "welcome" }, KnownVariables = new HashSet<string> { "Known" } };
        var issues = Check(new List<DesignElement> { b, t }, ctx);
        Assert.Contains(issues, i => i.Code == "PAIC1608" && i.Severity == ValidationSeverity.Error);
        Assert.Contains(issues, i => i.Code == "PAIC1608" && i.Severity == ValidationSeverity.Warning);
        Assert.Contains(issues, i => i.Code == "PAIC1609" && i.Severity == ValidationSeverity.Warning);
        Assert.Contains(issues, i => i.Code == "PAIC1609" && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void OutOfBounds_AndSharedVariables_AreWarnings()
    {
        var a = ElementFactory.Create(ControlTypes.TextInput, "a", 600, 0);
        var b = ElementFactory.Create(ControlTypes.TextInput, "b", 0, 100);
        b.Set("variable", a.GetString("variable"));
        var issues = Check(new List<DesignElement> { a, b });
        Assert.Contains(issues, i => i.Code == "PAIC1607" && i.Severity == ValidationSeverity.Warning);
        Assert.Contains(issues, i => i.Code == "PAIC1610");
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void ChildrenOfStackContainers_AreNotBoundsChecked()
    {
        var c = ElementFactory.Create(ControlTypes.Container, "c", 0, 0);
        c.Set("layout", "vertical");
        var child = ElementFactory.Create(ControlTypes.Text, "t", 5000, 5000);
        c.Children.Add(child);
        Assert.DoesNotContain(Check(new List<DesignElement> { c }), i => i.Code == "PAIC1607");
    }

    [Fact]
    public void CustomInstances_RequireDefinition()
    {
        var inst = new DesignElement { Id = "c1", Type = "custom:badge", Width = 50, Height = 20 };
        Assert.Contains(Check(new List<DesignElement> { inst }), i => i.Code == "PAIC1602");
        var ctx = new ElementValidationContext { Controls = new Dictionary<string, CustomControlDefinition> { ["badge"] = new() { Id = "badge", Name = "Badge" } } };
        Assert.DoesNotContain(Check(new List<DesignElement> { inst }, ctx), i => i.Code == "PAIC1602");
    }
}

public class CustomControlTests
{
    private static CustomControlDefinition Badge()
    {
        var def = new CustomControlDefinition { Id = "badge", Name = "Badge", Width = 200, Height = 40 };
        def.Parameters.Add(new CustomParameter { Name = "Title", Label = "Title", Default = "Hello" });
        def.Parameters.Add(new CustomParameter { Name = "Style", Type = ParameterType.Choice, Choices = { "body", "title" }, Default = "body" });
        var text = ElementFactory.Create(ControlTypes.Text, "text-1", 0, 0);
        text.Width = 200; text.Height = 24;
        text.Set("text", "{param:Title}");
        text.Set("style", "{param:Style}");
        def.Template.Add(text);
        return def;
    }

    [Fact]
    public void ValidDefinition_HasNoErrors() => Assert.False(CustomControlValidator.Validate(Badge()).HasErrors);

    [Fact]
    public void Expand_SubstitutesParameters_AndScopesIds()
    {
        var def = Badge();
        var instance = new DesignElement { Id = "badge-1", Type = "custom:badge", X = 5, Y = 6, Width = 200, Height = 40, Properties = { ["Title"] = "Hi" } };
        var group = CustomControlExpander.Expand(instance, def);
        Assert.Equal(ControlTypes.Group, group.Type);
        Assert.Equal("badge-1", group.Id);
        Assert.Equal(5, group.X);
        var child = Assert.Single(group.Children);
        Assert.Equal("badge-1.text-1", child.Id);
        Assert.Equal("Hi", child.GetString("text"));
        Assert.Equal("body", child.GetString("style")); // default used
        Assert.Equal("{param:Title}", def.Template[0].GetString("text")); // template untouched
    }

    [Fact]
    public void Validator_FindsProblems()
    {
        var def = Badge();
        def.Template[0].Set("text", "{param:Missing}");
        def.Parameters.Add(new CustomParameter { Name = "Title" });
        def.Parameters.Add(new CustomParameter { Name = "Bad name" });
        def.Parameters.Add(new CustomParameter { Name = "Pick", Type = ParameterType.Choice, Default = "x" });
        def.Template.Add(new DesignElement { Id = "nested", Type = "custom:other", Width = 10, Height = 10 });
        var codes = CustomControlValidator.Validate(def).Issues.Select(i => i.Code).ToList();
        Assert.Contains("PAIC1704", codes);
        Assert.Contains("PAIC1705", codes);
        Assert.Contains("PAIC1706", codes);
        Assert.Contains("PAIC1708", codes);

        var bad = Badge(); bad.Id = "Bad Id"; bad.Width = 5;
        var badCodes = CustomControlValidator.Validate(bad).Issues.Select(i => i.Code).ToList();
        Assert.Contains("PAIC1701", badCodes);
        Assert.Contains("PAIC1703", badCodes);
    }

    [Fact]
    public void Serializer_RoundTrips_AndRejectsInvalid()
    {
        var json = CustomControlSerializer.Serialize(Badge());
        Assert.Contains("\"format\": \"paic-control\"", json);
        var back = CustomControlSerializer.Deserialize(json);
        Assert.Equal("badge", back.Id);
        Assert.Equal(2, back.Parameters.Count);
        Assert.Equal(ParameterType.Choice, back.Parameters[1].Type);
        Assert.Equal("{param:Title}", back.Template[0].GetString("text"));

        Assert.Throws<CustomControlException>(() => CustomControlSerializer.Deserialize("nope"));
        Assert.Throws<CustomControlException>(() => CustomControlSerializer.Deserialize("{\"format\":\"x\"}"));
        var bad = Badge(); bad.Template[0].Set("text", "{param:Ghost}");
        Assert.Throws<CustomControlException>(() => CustomControlSerializer.Deserialize(CustomControlSerializer.Serialize(bad)));
    }

    [Fact]
    public void Library_SavesAcrossProjects_ImportsAndDeletes()
    {
        using var dir = new TempDir();
        var lib = new ControlLibrary(Path.Combine(dir.Path, "controls"));
        Assert.Empty(lib.Load());
        lib.Save(Badge());
        Assert.Equal("badge", Assert.Single(lib.Load()).Id);

        var file = Path.Combine(dir.Path, "badge" + CustomControlSerializer.FileExtension);
        lib.Export(Badge(), file);
        var other = new ControlLibrary(Path.Combine(dir.Path, "other"));
        Assert.Equal("badge", other.Import(file).Id);
        Assert.Single(other.Load());

        lib.Delete("badge");
        Assert.Empty(lib.Load());
        Assert.Throws<CustomControlException>(() => lib.Delete("../evil"));
        var invalid = Badge(); invalid.Template[0].Set("text", "{param:Nope}");
        Assert.Throws<CustomControlException>(() => lib.Save(invalid));
    }
}
