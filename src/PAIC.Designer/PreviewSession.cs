using PAIC.Controls;
using PAIC.ProjectSystem.Model;

namespace PAIC.Designer;

public enum PreviewState { Running, Cancelled, Finished }
public enum PreviewActionResult { Done, NeedsFolderBrowse, Ignored }

/// <summary>
/// Interactive walk through the installer's pages with simulated variables. This is a visual preview only:
/// no installation operation runs and no files are touched.
/// </summary>
public sealed class PreviewSession
{
    private readonly PaicProject _project;
    private readonly List<string> _history = new();

    public PreviewSession(PaicProject project, string? startPageId = null)
    {
        _project = project;
        Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProductName"] = project.Application.ProductName,
            ["Version"] = project.Application.Version,
            ["Publisher"] = project.Application.Publisher,
            ["InstallDir"] = project.Installation.DefaultInstallDirectory,
            ["ErrorMessage"] = "An example error message.",
            ["Status"] = "Copying files..."
        };
        SeedDefaults();
        var start = startPageId is not null ? project.Pages.FirstOrDefault(p => p.Id == startPageId) : null;
        start ??= project.Pages.FirstOrDefault(IsPageVisible) ?? project.Pages.FirstOrDefault();
        CurrentPageId = start?.Id;
    }

    public Dictionary<string, string> Variables { get; }
    public string? CurrentPageId { get; private set; }
    public PreviewState State { get; private set; } = PreviewState.Running;
    /// <summary>Value shown by progress bars whose source is "install" (a simulated value).</summary>
    public double ProgressValue { get; set; } = 40;

    public InstallerPageDefinition? CurrentPage => _project.Pages.FirstOrDefault(p => p.Id == CurrentPageId);
    public bool CanGoBack =>
        CurrentPage is { AllowBack: true } p && (_history.Count > 0 || !string.IsNullOrWhiteSpace(p.BackPageId));

    private void SeedDefaults()
    {
        foreach (var e in _project.Pages.SelectMany(p => ElementIds.Flatten(p.Elements)))
        {
            var name = e.GetString("variable");
            if (string.IsNullOrWhiteSpace(name) || Variables.ContainsKey(name)) continue;
            switch (e.Type)
            {
                case ControlTypes.TextInput: Variables[name] = e.GetString("defaultValue"); break;
                case ControlTypes.Checkbox: Variables[name] = e.GetBool("defaultChecked") ? "true" : "false"; break;
                case ControlTypes.Radio or ControlTypes.Dropdown:
                    var def = e.GetString("defaultValue");
                    var first = e.GetString("options").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
                    Variables[name] = string.IsNullOrEmpty(def) ? first : def;
                    break;
                default: Variables[name] = ""; break;
            }
        }
    }

    public bool IsPageVisible(InstallerPageDefinition page) => ConditionEvaluator.Evaluate(page.VisibleWhen, Variables);
    public bool IsElementVisible(DesignElement e) => e.Visible && ConditionEvaluator.Evaluate(e.VisibleWhen, Variables);

    public bool IsElementEnabled(DesignElement e)
    {
        if (!ConditionEvaluator.Evaluate(e.EnabledWhen, Variables)) return false;
        if (e.Type != ControlTypes.Button || e.Action is null) return true;
        return e.Action.Kind switch
        {
            ElementActionKind.Back => CanGoBack,
            ElementActionKind.Cancel => CurrentPage?.AllowCancel ?? true,
            _ => true
        };
    }

    private InstallerPageDefinition? NextVisibleAfter(int index)
    {
        for (var i = index + 1; i < _project.Pages.Count; i++)
            if (IsPageVisible(_project.Pages[i])) return _project.Pages[i];
        return null;
    }

    /// <summary>Moves to the page's next-page override, or the next visible page in order. At the end of the list the preview finishes.</summary>
    public bool Next()
    {
        var page = CurrentPage;
        if (page is null || State != PreviewState.Running) return false;
        InstallerPageDefinition? target = null;
        if (!string.IsNullOrWhiteSpace(page.NextPageId))
            target = _project.Pages.FirstOrDefault(p => string.Equals(p.Id, page.NextPageId, StringComparison.OrdinalIgnoreCase));
        target ??= NextVisibleAfter(_project.Pages.IndexOf(page));
        if (target is null) { State = PreviewState.Finished; return false; }
        Visit(target);
        return true;
    }

    /// <summary>Goes to the page's back-page override, or to the previously visited page.</summary>
    public bool Back()
    {
        var page = CurrentPage;
        if (page is null || !page.AllowBack) return false;
        var target = string.IsNullOrWhiteSpace(page.BackPageId)
            ? null
            : _project.Pages.FirstOrDefault(p => string.Equals(p.Id, page.BackPageId, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            var at = _history.LastIndexOf(target.Id);
            if (at >= 0) _history.RemoveRange(at, _history.Count - at);
            CurrentPageId = target.Id;
            return true;
        }
        if (_history.Count == 0) return false;
        CurrentPageId = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        return true;
    }

    public bool GoTo(string pageId)
    {
        var target = _project.Pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (target is null || State != PreviewState.Running) return false;
        Visit(target);
        return true;
    }

    private void Visit(InstallerPageDefinition target)
    {
        if (CurrentPageId is not null) _history.Add(CurrentPageId);
        CurrentPageId = target.Id;
    }

    public void Restart()
    {
        _history.Clear();
        State = PreviewState.Running;
        CurrentPageId = _project.Pages.FirstOrDefault(IsPageVisible)?.Id ?? _project.Pages.FirstOrDefault()?.Id;
    }

    public PreviewActionResult Apply(ElementAction? action)
    {
        if (action is null || State != PreviewState.Running) return PreviewActionResult.Ignored;
        switch (action.Kind)
        {
            case ElementActionKind.Next: Next(); return PreviewActionResult.Done;
            case ElementActionKind.Back: return Back() ? PreviewActionResult.Done : PreviewActionResult.Ignored;
            case ElementActionKind.Cancel: State = PreviewState.Cancelled; return PreviewActionResult.Done;
            case ElementActionKind.Finish: State = PreviewState.Finished; return PreviewActionResult.Done;
            case ElementActionKind.GoToPage: return GoTo(action.TargetPageId) ? PreviewActionResult.Done : PreviewActionResult.Ignored;
            case ElementActionKind.SetVariable:
                if (string.IsNullOrWhiteSpace(action.Variable)) return PreviewActionResult.Ignored;
                Variables[action.Variable] = TextTemplating.Resolve(action.Value, Variables);
                return PreviewActionResult.Done;
            case ElementActionKind.BrowseFolder: return PreviewActionResult.NeedsFolderBrowse;
            default: return PreviewActionResult.Ignored;
        }
    }
}
