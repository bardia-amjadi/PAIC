using PAIC.Controls;
using PAIC.Designer;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Layouts;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Validation;
using Xunit;

namespace PAIC.Tests;

public class PageLayoutTests
{
    private static ValidationContextHolder Validate(PaicProject p) => new(ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(p, null)));
    private sealed record ValidationContextHolder(PAIC.Core.Validation.ValidationReport Report);

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void DefaultLayout_IsValid_AndFitsTheWindow(InstallerPageType type)
    {
        var project = ProjectFactory.CreateNew("Demo");
        project.Pages.Clear();
        project.Pages.Add(new InstallerPageDefinition { Id = "p", Title = "P", Type = type, Elements = PageLayouts.Create(type, 720, 480) });
        var report = Validate(project).Report;
        Assert.False(report.HasErrors, string.Join("; ", report.Issues));
        Assert.DoesNotContain(report.Issues, i => i.Code == "PAIC1607");
        Assert.NotEmpty(project.Pages[0].Elements);
        Assert.All(project.Pages[0].Elements, e => Assert.Equal(e.Id, e.Name));
    }

    public static IEnumerable<object[]> AllTypes() => Enum.GetValues<InstallerPageType>().Select(t => new object[] { t });

    [Fact]
    public void LicensePage_GatesNextButtonOnAcceptance()
    {
        var elements = PageLayouts.Create(InstallerPageType.LicenseAgreement, 720, 480);
        var next = elements.Single(e => e.Type == ControlTypes.Button && e.GetString("text") == "Next");
        Assert.Equal("LicenseAccepted", next.EnabledWhen!.Variable);
        Assert.Contains(elements, e => e.Type == ControlTypes.Checkbox && e.GetString("variable") == "LicenseAccepted");
    }

    [Fact]
    public void Layout_ScalesWithWindowSize()
    {
        var small = PageLayouts.Create(InstallerPageType.Welcome, 640, 400);
        var cancel = small.Single(e => e.GetString("text") == "Cancel");
        Assert.Equal(640 - 40 - 100, cancel.X);
        Assert.Equal(400 - 60, cancel.Y);
    }

    [Fact]
    public void NewProject_PagesHaveLayouts()
    {
        var project = ProjectFactory.CreateNew("Demo");
        Assert.All(project.Pages, p => Assert.NotEmpty(p.Elements));
        var report = Validate(project).Report;
        Assert.False(report.HasErrors, string.Join("; ", report.Issues));
        Assert.DoesNotContain(report.Issues, i => i.Code is "PAIC1624" or "PAIC1623");
    }
}

public class ProjectDesignValidationTests
{
    private static List<string> Codes(PaicProject p) =>
        ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(p, null)).Issues.Select(i => i.Code).ToList();

    private static PaicProject Empty()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Pages.Clear();
        return p;
    }

    [Fact]
    public void BrokenNavigationAndBackgrounds_AreReported()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Pages[0].NextPageId = "ghost";
        p.Pages[1].Background = new PageBackground { Kind = PageBackgroundKind.Token, Value = "nope" };
        p.Pages[2].Background = new PageBackground { Kind = PageBackgroundKind.Image, Value = "missing" };
        p.Pages[3].Background = new PageBackground { Kind = PageBackgroundKind.Color, Value = "red" };
        p.Pages[4].VisibleWhen = new ConditionDefinition { Variable = "NoSuchVariable" };
        var button = ElementFactory.Create(ControlTypes.Button, "go", 10, 10);
        button.Action = new ElementAction { Kind = ElementActionKind.GoToPage, TargetPageId = "ghost" };
        p.Pages[4].Elements.Add(button);

        var codes = Codes(p);
        Assert.Contains("PAIC1620", codes);
        Assert.Equal(3, codes.Count(c => c == "PAIC1621"));
        Assert.Contains("PAIC1622", codes);
        Assert.Contains("PAIC1608", codes);
    }

    [Fact]
    public void ProgressPageWithoutProgressBar_IsWarned()
    {
        var p = Empty();
        p.Pages.Add(new InstallerPageDefinition { Id = "progress", Type = InstallerPageType.InstallationProgress, Title = "Installing" });
        Assert.Contains("PAIC1623", Codes(p));
    }

    [Fact]
    public void UnreachablePages_AreFoundAndFixedByGoToPage()
    {
        var p = Empty();
        p.Pages.Add(new InstallerPageDefinition { Id = "a", Title = "A", NextPageId = "c" });
        p.Pages.Add(new InstallerPageDefinition { Id = "b", Title = "B" });
        p.Pages.Add(new InstallerPageDefinition { Id = "c", Title = "C" });
        var issues = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(p, null)).Issues;
        Assert.Contains(issues, i => i.Code == "PAIC1624" && i.Location == "pages[b]");
        Assert.DoesNotContain(issues, i => i.Code == "PAIC1624" && i.Location == "pages[c]");

        var jump = ElementFactory.Create(ControlTypes.Button, "jump", 0, 0);
        jump.Action = new ElementAction { Kind = ElementActionKind.GoToPage, TargetPageId = "b" };
        p.Pages[2].Elements.Add(jump);
        Assert.DoesNotContain("PAIC1624", Codes(p));
    }

    [Fact]
    public void PagesAfterConditionalPages_StayReachable()
    {
        var p = Empty();
        p.Pages.Add(new InstallerPageDefinition { Id = "a", Title = "A" });
        p.Pages.Add(new InstallerPageDefinition { Id = "b", Title = "B", VisibleWhen = new ConditionDefinition { Variable = "InstallDir", Operator = ConditionOperator.IsNotEmpty } });
        p.Pages.Add(new InstallerPageDefinition { Id = "c", Title = "C" });
        Assert.DoesNotContain("PAIC1624", Codes(p));
    }

    [Fact]
    public void WindowSize_IsRangeChecked()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Installation.WindowWidth = 100;
        Assert.Contains("PAIC1626", Codes(p));
    }

    [Fact]
    public void CustomControls_AreValidatedAndUsageIsChecked()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.CustomControls.Add(new CustomControlDefinition { Id = "badge", Name = "Badge", Width = 100, Height = 30 });
        p.CustomControls.Add(new CustomControlDefinition { Id = "badge", Name = "Again", Width = 100, Height = 30 });
        p.CustomControls.Add(new CustomControlDefinition { Id = "Bad Id", Name = "Bad", Width = 100, Height = 30 });
        p.Pages[0].Elements.Add(new DesignElement { Id = "inst", Type = "custom:ghost", Width = 50, Height = 20, X = 0, Y = 0 });
        var issues = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(p, null)).Issues;
        Assert.Contains(issues, i => i.Code == "PAIC1102" && i.Location == "customControls");
        Assert.Contains(issues, i => i.Code == "PAIC1701");
        Assert.Contains(issues, i => i.Code == "PAIC1602");
    }

    [Fact]
    public void MissingAssetForImageElement_IsAnError()
    {
        var p = ProjectFactory.CreateNew("Demo");
        var image = ElementFactory.Create(ControlTypes.Image, "logo", 10, 10);
        image.Set("asset", "no-such-asset");
        p.Pages[0].Elements.Add(image);
        Assert.Contains("PAIC1606", Codes(p));
    }

    [Fact]
    public void Phase2ProjectFiles_WithoutElements_StillLoad()
    {
        var p = ProjectFactory.CreateNew("Old");
        var json = System.Text.Json.Nodes.JsonNode.Parse(ProjectSerializer.Default.Serialize(p))!.AsObject();
        foreach (var page in json["pages"]!.AsArray()) { page!.AsObject().Remove("elements"); page.AsObject().Remove("background"); }
        json["installation"]!.AsObject().Remove("windowWidth");
        var back = ProjectSerializer.Default.Deserialize(json.ToJsonString());
        Assert.All(back.Pages, pg => Assert.Empty(pg.Elements));
        Assert.Equal(720, back.Installation.WindowWidth);
        Assert.DoesNotContain(ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(back, null)).Issues, i => i.Severity == PAIC.Core.Validation.ValidationSeverity.Error);
    }

    [Fact]
    public void ElementsRoundTripThroughProjectFile()
    {
        var p = ProjectFactory.CreateNew("Demo");
        var b = p.Pages[0].Elements.First(e => e.Type == ControlTypes.Button);
        b.Action = new ElementAction { Kind = ElementActionKind.SetVariable, Variable = "X", Value = "1" };
        b.VisibleWhen = new ConditionDefinition { Variable = "Y", Operator = ConditionOperator.Contains, Value = "z" };
        p.Pages[0].Background = new PageBackground { Kind = PageBackgroundKind.Color, Value = "#112233" };
        var json = ProjectSerializer.Default.Serialize(p);
        Assert.Contains("\"setVariable\"", json);
        Assert.Contains("\"contains\"", json);
        var back = ProjectSerializer.Default.Deserialize(json);
        var bb = back.Pages[0].Elements.First(e => e.Type == ControlTypes.Button);
        Assert.Equal(ElementActionKind.SetVariable, bb.Action!.Kind);
        Assert.Equal(ConditionOperator.Contains, bb.VisibleWhen!.Operator);
        Assert.Equal(PageBackgroundKind.Color, back.Pages[0].Background.Kind);
        Assert.Equal(p.Pages[0].Elements.Count, back.Pages[0].Elements.Count);
    }
}

public class PageServiceTests
{
    [Fact]
    public void AddPage_UsesTypeLayout_UniqueIds_AndPosition()
    {
        var p = ProjectFactory.CreateNew("Demo");
        var added = PageService.AddPage(p, InstallerPageType.Welcome, afterIndex: 0);
        Assert.Equal("welcome-2", added.Id);
        Assert.Equal(1, p.Pages.IndexOf(added));
        Assert.NotEmpty(added.Elements);

        var custom = PageService.AddPage(p, InstallerPageType.Custom, "My Page");
        Assert.Equal("my-page", custom.Id);
        Assert.Equal("My Page", custom.Title);
        Assert.Same(custom, p.Pages[^1]);
    }

    [Fact]
    public void RemovePage_IsBlockedByReferences_UnlessCleared()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Pages[0].NextPageId = "completion";
        var jump = ElementFactory.Create(ControlTypes.Button, "jump", 0, 0);
        jump.Action = new ElementAction { Kind = ElementActionKind.GoToPage, TargetPageId = "completion" };
        p.Pages[1].Elements.Add(jump);

        Assert.Equal(2, PageService.FindReferences(p, "completion").Count);
        Assert.Throws<ProjectException>(() => PageService.RemovePage(p, "completion"));
        Assert.Equal(5, p.Pages.Count);

        PageService.RemovePage(p, "completion", clearReferences: true);
        Assert.Equal(4, p.Pages.Count);
        Assert.Equal("", p.Pages[0].NextPageId);
        Assert.Equal(ElementActionKind.None, jump.Action!.Kind);
    }

    [Fact]
    public void RemovePage_WithoutReferences_Succeeds_AndUnknownPageThrows()
    {
        var p = ProjectFactory.CreateNew("Demo");
        PageService.RemovePage(p, "license");
        Assert.DoesNotContain(p.Pages, x => x.Id == "license");
        Assert.Throws<ProjectException>(() => PageService.RemovePage(p, "license"));
    }

    [Fact]
    public void MovePage_ClampsIndex()
    {
        var p = ProjectFactory.CreateNew("Demo");
        PageService.MovePage(p, "completion", -5);
        Assert.Equal("completion", p.Pages[0].Id);
        PageService.MovePage(p, "completion", 99);
        Assert.Equal("completion", p.Pages[^1].Id);
        Assert.Equal(5, p.Pages.Count);
    }

    [Fact]
    public void DuplicatePage_CopiesElementsIndependently()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Pages[1].NextPageId = "completion";
        var copy = PageService.DuplicatePage(p, "license");
        Assert.Equal("license-copy", copy.Id);
        Assert.Equal(2, p.Pages.IndexOf(copy));
        Assert.Equal("", copy.NextPageId);
        Assert.Equal(p.Pages[1].Elements.Count, copy.Elements.Count);
        var x = p.Pages[1].Elements[0].X;
        copy.Elements[0].X += 100;
        Assert.Equal(x, p.Pages[1].Elements[0].X);
    }

    [Fact]
    public void ResetLayout_RestoresDefaults()
    {
        var p = ProjectFactory.CreateNew("Demo");
        var page = p.Pages[0];
        var expected = PageLayouts.Create(page.Type, p.Installation.WindowWidth, p.Installation.WindowHeight).Count;
        page.Elements.Clear();
        PageService.ResetLayout(p, page.Id);
        Assert.Equal(expected, page.Elements.Count);
    }
}

public class PreviewSessionTests
{
    private static PaicProject Project() => ProjectFactory.CreateNew("Demo");

    private static DesignElement Button(PaicProject p, string pageId, string text) =>
        p.Pages.Single(x => x.Id == pageId).Elements.Single(e => e.Type == ControlTypes.Button && e.GetString("text") == text);

    [Fact]
    public void Starts_WithBuiltInVariables_AndSeededInputs()
    {
        var s = new PreviewSession(Project());
        Assert.Equal("welcome", s.CurrentPageId);
        Assert.Equal("Demo", s.Variables["ProductName"]);
        Assert.Equal("1.0.0", s.Variables["Version"]);
        Assert.Equal(@"%ProgramFiles%\Demo", s.Variables["InstallDir"]);
        Assert.Equal("false", s.Variables["LicenseAccepted"]);
    }

    [Fact]
    public void NextAndBack_WalkTheHistory()
    {
        var p = Project();
        var s = new PreviewSession(p);
        Assert.False(s.CanGoBack);
        Assert.True(s.Next());
        Assert.Equal("license", s.CurrentPageId);
        Assert.True(s.CanGoBack);
        Assert.True(s.Back());
        Assert.Equal("welcome", s.CurrentPageId);
        Assert.False(s.Back());
    }

    [Fact]
    public void LicenseNext_IsDisabledUntilAccepted()
    {
        var p = Project();
        var s = new PreviewSession(p, "license");
        var next = Button(p, "license", "Next");
        Assert.False(s.IsElementEnabled(next));
        s.Variables["LicenseAccepted"] = "true";
        Assert.True(s.IsElementEnabled(next));
        Assert.True(s.IsElementEnabled(Button(p, "license", "Cancel")));
    }

    [Fact]
    public void ConditionalPages_AreSkipped()
    {
        var p = Project();
        p.Pages[1].VisibleWhen = new ConditionDefinition { Variable = "ShowLicense", Operator = ConditionOperator.IsTrue };
        var s = new PreviewSession(p);
        s.Next();
        Assert.Equal("directory", s.CurrentPageId);

        var s2 = new PreviewSession(p);
        s2.Variables["ShowLicense"] = "true";
        s2.Next();
        Assert.Equal("license", s2.CurrentPageId);
    }

    [Fact]
    public void NextAndBackOverrides_AreHonoured()
    {
        var p = Project();
        p.Pages[0].NextPageId = "completion";
        p.Pages[4].BackPageId = "directory";
        var s = new PreviewSession(p);
        s.Next();
        Assert.Equal("completion", s.CurrentPageId);
        s.Back();
        Assert.Equal("directory", s.CurrentPageId);
    }

    [Fact]
    public void EndOfInstaller_Finishes_AndCancelStops()
    {
        var p = Project();
        var s = new PreviewSession(p, "completion");
        Assert.False(s.Next());
        Assert.Equal(PreviewState.Finished, s.State);
        s.Restart();
        Assert.Equal(PreviewState.Running, s.State);
        Assert.Equal("welcome", s.CurrentPageId);

        Assert.Equal(PreviewActionResult.Done, s.Apply(new ElementAction { Kind = ElementActionKind.Cancel }));
        Assert.Equal(PreviewState.Cancelled, s.State);
        Assert.False(s.Next());
        Assert.Equal(PreviewActionResult.Ignored, s.Apply(new ElementAction { Kind = ElementActionKind.Next }));
    }

    [Fact]
    public void Actions_SetVariablesBrowseAndNavigate()
    {
        var p = Project();
        var s = new PreviewSession(p);
        Assert.Equal(PreviewActionResult.NeedsFolderBrowse, s.Apply(new ElementAction { Kind = ElementActionKind.BrowseFolder, Variable = "InstallDir" }));
        s.Apply(new ElementAction { Kind = ElementActionKind.SetVariable, Variable = "Tag", Value = "{ProductName}-x" });
        Assert.Equal("Demo-x", s.Variables["Tag"]);
        s.Apply(new ElementAction { Kind = ElementActionKind.GoToPage, TargetPageId = "directory" });
        Assert.Equal("directory", s.CurrentPageId);
        Assert.Equal(PreviewActionResult.Ignored, s.Apply(new ElementAction { Kind = ElementActionKind.GoToPage, TargetPageId = "ghost" }));
    }

    [Fact]
    public void AllowFlags_DisableBackAndCancel()
    {
        var p = Project();
        p.Pages[1].AllowBack = false;
        p.Pages[1].AllowCancel = false;
        var s = new PreviewSession(p, "license");
        s.Next(); s.Back(); // history exists, but back is not allowed on the page we are on after going forward again
        var s2 = new PreviewSession(p);
        s2.Next();
        Assert.Equal("license", s2.CurrentPageId);
        Assert.False(s2.CanGoBack);
        Assert.False(s2.IsElementEnabled(Button(p, "license", "Cancel")));
        Assert.False(s2.IsElementEnabled(Button(p, "license", "Back")));
    }

    [Fact]
    public void ElementVisibility_FollowsConditionsAndFlag()
    {
        var p = Project();
        var s = new PreviewSession(p);
        var e = new DesignElement { Id = "x", Visible = true, VisibleWhen = new ConditionDefinition { Variable = "Flag", Operator = ConditionOperator.IsTrue } };
        Assert.False(s.IsElementVisible(e));
        s.Variables["Flag"] = "true";
        Assert.True(s.IsElementVisible(e));
        e.Visible = false;
        Assert.False(s.IsElementVisible(e));
    }
}
