using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAIC.App.Services;
using PAIC.Core;
using PAIC.Diagnostics;
using PAIC.InstallerEngine;
using PAIC.InstallerEngine.Virtual;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Operations;

namespace PAIC.App.Pages;

/// <summary>A row in one of the lists.</summary>
public sealed class ListItem
{
    public object Tag { get; init; } = new();
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
}

public sealed partial class InstallationPage : Page
{
    private readonly IProjectService _projects = App.Services.GetRequiredService<IProjectService>();
    private readonly IErrorReporter _errors = App.Services.GetRequiredService<IErrorReporter>();
    private PaicProject? _shown;
    private const string None = "(none)";

    public ObservableCollection<ListItem> Components { get; } = new();
    public ObservableCollection<ListItem> Operations { get; } = new();
    public ObservableCollection<ListItem> Prerequisites { get; } = new();

    private PaicProject? Project => _projects.Current;

    public InstallationPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { _projects.Changed += OnProjectChanged; Load(); };
        Unloaded += (_, _) => _projects.Changed -= OnProjectChanged;
    }

    private void OnProjectChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => { if (!ReferenceEquals(_shown, Project)) Load(); });

    private void Load()
    {
        _shown = Project;
        NoProjectBar.IsOpen = Project is null;
        Tabs.Visibility = Project is null ? Visibility.Collapsed : Visibility.Visible;
        if (Project is null) return;
        RefreshComponents(null); RefreshOperations(null); RefreshPrerequisites(null);
    }

    private void Touch() => _projects.MarkDirty();

    private async void Report(string title, Exception ex) => await _errors.ReportAsync(title, ex);

    private static string UniqueId(IEnumerable<string> existing, string baseId)
    {
        var used = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chars = baseId.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = System.Text.RegularExpressions.Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        if (slug.Length == 0) slug = "item";
        var id = slug;
        for (var i = 2; used.Contains(id); i++) id = $"{slug}-{i}";
        return id;
    }

    // ================================================================= components

    private ComponentDefinition? SelectedComponent => (ComponentList.SelectedItem as ListItem)?.Tag as ComponentDefinition;

    private void RefreshComponents(ComponentDefinition? select)
    {
        Components.Clear();
        foreach (var c in Project!.Components)
            Components.Add(new ListItem { Tag = c, Title = c.Name.Length > 0 ? c.Name : c.Id, Subtitle = $"{c.Id}{(c.Required ? " \u2022 required" : c.DefaultSelected ? " \u2022 selected by default" : "")}" });
        ComponentList.SelectedItem = Components.FirstOrDefault(i => ReferenceEquals(i.Tag, select)) ?? Components.FirstOrDefault();
        BuildComponentForm();
    }

    private void ComponentList_SelectionChanged(object sender, SelectionChangedEventArgs e) => BuildComponentForm();

    private void BuildComponentForm()
    {
        var form = new FormBuilder(ComponentForm);
        var c = SelectedComponent;
        if (c is null) { form.Note("Components let users choose which parts of the product to install. Operations can belong to a component."); return; }
        form.Section("Component");
        form.Note($"Id: {c.Id}");
        form.Text("Name", c.Name, v => { c.Name = v; Touch(); RefreshComponents(c); });
        form.Text("Description", c.Description, v => { c.Description = v; Touch(); }, multiline: true);
        form.Bool("Required (always installed)", c.Required, v => { c.Required = v; Touch(); RefreshComponents(c); });
        form.Bool("Selected by default", c.DefaultSelected, v => { c.DefaultSelected = v; Touch(); RefreshComponents(c); });
    }

    private async void AddComponent_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null) return;
        var name = await UiDialogs.PromptTextAsync(XamlRoot, "Add component", "Name", "New component", "Add");
        if (string.IsNullOrWhiteSpace(name)) return;
        var c = new ComponentDefinition { Id = UniqueId(Project.Components.Select(x => x.Id), name), Name = name.Trim() };
        Project.Components.Add(c);
        Touch();
        RefreshComponents(c); RefreshOperations(null);
    }

    private async void RemoveComponent_Click(object sender, RoutedEventArgs e)
    {
        var c = SelectedComponent;
        if (c is null || Project is null) return;
        var used = Project.Operations.Where(o => string.Equals(o.ComponentId, c.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var message = used.Count == 0 ? $"Remove the component '{c.Name}'?" : $"Remove '{c.Name}'? {used.Count} operation(s) belong to it and will become part of every install.";
        if (!await UiDialogs.ConfirmAsync(XamlRoot, "Remove component", message, "Remove")) return;
        foreach (var o in used) o.ComponentId = "";
        Project.Components.Remove(c);
        Touch();
        RefreshComponents(null); RefreshOperations(null);
    }

    // ================================================================= operations

    private InstallOperationDefinition? SelectedOperation => (OperationList.SelectedItem as ListItem)?.Tag as InstallOperationDefinition;

    private void RefreshOperations(InstallOperationDefinition? select)
    {
        Operations.Clear();
        foreach (var o in Project!.Operations)
        {
            var name = OperationCatalog.TryGet(o.Kind, out var d) ? d.DisplayName : $"{o.Kind} (unknown kind)";
            var where = string.IsNullOrEmpty(o.ComponentId) ? "always" : $"component {o.ComponentId}";
            Operations.Add(new ListItem { Tag = o, Title = $"{Operations.Count + 1}. {name}", Subtitle = $"{o.Id} \u2022 {where}" });
        }
        OperationList.SelectedItem = Operations.FirstOrDefault(i => ReferenceEquals(i.Tag, select)) ?? Operations.FirstOrDefault();
        BuildOperationForm();
    }

    private void OperationList_SelectionChanged(object sender, SelectionChangedEventArgs e) => BuildOperationForm();

    private void BuildOperationForm()
    {
        var form = new FormBuilder(OperationForm);
        var o = SelectedOperation;
        if (o is null)
        {
            form.Note("Operations run in this order when the installer installs. Each can belong to a component and have a condition. Everything they change is recorded so rollback and uninstall can undo it.");
            return;
        }
        var known = OperationCatalog.TryGet(o.Kind, out var d);
        form.Section(known ? d!.DisplayName : "Unknown operation kind");
        if (known)
        {
            form.Note(d!.Description);
            if (!d.Reversible) form.Note("This operation cannot be undone automatically; add an uninstall command if it has side effects.");
            if (!d.MsixCompatible) form.Note("Not available in MSIX packages.");
        }
        else form.Note($"The kind '{o.Kind}' is not built in (it may come from a plug-in). Its parameters are shown as text.");

        form.Text("Id", o.Id, v => { o.Id = v.Trim(); Touch(); RefreshOperations(o); });
        var componentChoices = new[] { None }.Concat(Project!.Components.Select(c => c.Id)).ToList();
        form.Choice("Component", componentChoices, string.IsNullOrEmpty(o.ComponentId) ? None : o.ComponentId, v => { o.ComponentId = v == None ? "" : v; Touch(); RefreshOperations(o); });
        form.Condition("Run only when...", o.Condition, c => { o.Condition = c; Touch(); });

        form.Section("Settings");
        if (known)
        {
            foreach (var p in d!.Parameters)
            {
                var label = p.Required ? p.Label + " *" : p.Label;
                var value = o.Parameters.TryGetValue(p.Name, out var v) ? v : "";
                void Set(string s) { if (string.IsNullOrEmpty(s)) o.Parameters.Remove(p.Name); else o.Parameters[p.Name] = s; Touch(); }
                switch (p.Kind)
                {
                    case OperationParameterKind.Bool:
                        form.Bool(label, bool.TryParse(string.IsNullOrEmpty(value) ? p.Default : value, out var b) && b, x => Set(x ? "true" : "false")); break;
                    case OperationParameterKind.Choice:
                        form.Choice(label, p.Choices ?? Array.Empty<string>(), string.IsNullOrEmpty(value) ? p.Default : value, Set); break;
                    default:
                        form.Text(label, value, Set, placeholder: p.Default); break;
                }
                if (p.Help.Length > 0) form.Note(p.Help);
            }
            form.Note("Use {InstallDir}, {ProductName}, {Version}, {Publisher} and wizard variables like {Name} in paths, and %ProgramFiles%-style folders.");
        }
        else
        {
            form.Text("Kind", o.Kind, x => { o.Kind = x.Trim(); Touch(); RefreshOperations(o); });
            form.Text("Parameters (Name=Value per line)", string.Join(Environment.NewLine, o.Parameters.Select(kv => $"{kv.Key}={kv.Value}")), text =>
            {
                o.Parameters.Clear();
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var at = line.IndexOf('=');
                    if (at > 0) o.Parameters[line[..at].Trim()] = line[(at + 1)..];
                }
                Touch();
            }, multiline: true);
        }
    }

    private async void AddOperation_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null) return;
        var names = OperationCatalog.All.Select(d => d.DisplayName).ToList();
        var index = await UiDialogs.ChooseAsync(XamlRoot, "Add operation", "What should the installer do?", names, "Add");
        if (index < 0) return;
        var d = OperationCatalog.All[index];
        var op = new InstallOperationDefinition { Id = UniqueId(Project.Operations.Select(x => x.Id), d.Kind), Kind = d.Kind };
        foreach (var p in d.Parameters.Where(p => p.Required && p.Default.Length > 0)) op.Parameters[p.Name] = p.Default;
        var after = SelectedOperation is { } sel ? Project.Operations.IndexOf(sel) + 1 : Project.Operations.Count;
        Project.Operations.Insert(after, op);
        Touch();
        RefreshOperations(op);
    }

    private void DuplicateOperation_Click(object sender, RoutedEventArgs e)
    {
        var o = SelectedOperation;
        if (o is null || Project is null) return;
        var copy = new InstallOperationDefinition
        {
            Id = UniqueId(Project.Operations.Select(x => x.Id), o.Id), Kind = o.Kind, ComponentId = o.ComponentId,
            Parameters = new Dictionary<string, string>(o.Parameters), Condition = o.Condition?.Clone()
        };
        Project.Operations.Insert(Project.Operations.IndexOf(o) + 1, copy);
        Touch(); RefreshOperations(copy);
    }

    private async void RemoveOperation_Click(object sender, RoutedEventArgs e)
    {
        var o = SelectedOperation;
        if (o is null || Project is null) return;
        if (!await UiDialogs.ConfirmAsync(XamlRoot, "Remove operation", $"Remove '{o.Id}'?", "Remove")) return;
        Project.Operations.Remove(o);
        Touch(); RefreshOperations(null);
    }

    private void OperationUp_Click(object sender, RoutedEventArgs e) => MoveOperation(-1);
    private void OperationDown_Click(object sender, RoutedEventArgs e) => MoveOperation(1);

    private void MoveOperation(int delta)
    {
        var o = SelectedOperation;
        if (o is null || Project is null) return;
        var i = Project.Operations.IndexOf(o);
        var j = i + delta;
        if (j < 0 || j >= Project.Operations.Count) return;
        Project.Operations.RemoveAt(i);
        Project.Operations.Insert(j, o);
        Touch(); RefreshOperations(o);
    }

    // ================================================================= prerequisites

    private PrerequisiteDefinition? SelectedPrerequisite => (PrerequisiteList.SelectedItem as ListItem)?.Tag as PrerequisiteDefinition;

    private void RefreshPrerequisites(PrerequisiteDefinition? select)
    {
        Prerequisites.Clear();
        foreach (var p in Project!.Prerequisites)
            Prerequisites.Add(new ListItem { Tag = p, Title = p.Name.Length > 0 ? p.Name : p.Id, Subtitle = $"{p.Id} \u2022 {p.Detection}{(p.Optional ? " \u2022 optional" : "")}" });
        PrerequisiteList.SelectedItem = Prerequisites.FirstOrDefault(i => ReferenceEquals(i.Tag, select)) ?? Prerequisites.FirstOrDefault();
        BuildPrerequisiteForm();
    }

    private void PrerequisiteList_SelectionChanged(object sender, SelectionChangedEventArgs e) => BuildPrerequisiteForm();

    private void BuildPrerequisiteForm()
    {
        var form = new FormBuilder(PrerequisiteForm);
        var p = SelectedPrerequisite;
        if (p is null)
        {
            form.Note("Prerequisites are checked before anything is installed. Missing ones are downloaded, verified with their SHA-256 checksum and installed silently, in dependency order.");
            return;
        }
        form.Section("Prerequisite");
        form.Text("Id", p.Id, v => { p.Id = v.Trim(); Touch(); RefreshPrerequisites(p); });
        form.Text("Name", p.Name, v => { p.Name = v; Touch(); RefreshPrerequisites(p); });
        form.Bool("Optional (a missing one only warns)", p.Optional, v => { p.Optional = v; Touch(); RefreshPrerequisites(p); });
        form.Text("Minimum version", p.MinVersion, v => { p.MinVersion = v.Trim(); Touch(); }, placeholder: "e.g. 8.0");
        form.Text("Maximum version", p.MaxVersion, v => { p.MaxVersion = v.Trim(); Touch(); }, placeholder: "leave empty for no limit");
        form.Choice("Applies to architecture", Enum.GetNames<PrerequisiteArchitecture>(), p.Architecture.ToString(), v => { p.Architecture = Enum.Parse<PrerequisiteArchitecture>(v); Touch(); });

        form.Section("Detection");
        form.Choice("How is it detected?", Enum.GetNames<DetectionKind>(), p.Detection.ToString(), v => { p.Detection = Enum.Parse<DetectionKind>(v); Touch(); RefreshPrerequisites(p); });
        switch (p.Detection)
        {
            case DetectionKind.RegistryValue:
                form.Text("Registry key", p.DetectionTarget, v => { p.DetectionTarget = v.Trim(); Touch(); }, placeholder: @"HKLM\SOFTWARE\Vendor\Product");
                form.Text("Value name", p.DetectionValue, v => { p.DetectionValue = v.Trim(); Touch(); }); break;
            case DetectionKind.FileVersion:
                form.Text("File path", p.DetectionTarget, v => { p.DetectionTarget = v.Trim(); Touch(); }, placeholder: @"%ProgramFiles%\Vendor\tool.dll"); break;
            case DetectionKind.DotnetRuntime:
                form.Text("Shared runtime name", p.DetectionValue, v => { p.DetectionValue = v.Trim(); Touch(); }, placeholder: "Microsoft.WindowsDesktop.App"); break;
            case DetectionKind.WindowsVersion:
                form.Note("Compares the Windows version with the minimum and maximum above."); break;
            default: form.Note("Choose a detection method; without one the prerequisite cannot be checked."); break;
        }

        form.Section("Download and install");
        form.Text("Download URL (https)", p.DownloadUrl, v => { p.DownloadUrl = v.Trim(); Touch(); });
        form.Text("SHA-256 checksum (required with a URL)", p.Sha256, v => { p.Sha256 = v.Trim(); Touch(); });
        form.Text("Silent install arguments", p.InstallArguments, v => { p.InstallArguments = v; Touch(); }, placeholder: "/quiet /norestart");
        form.Text("Depends on (ids, comma separated)", string.Join(", ", p.DependsOn), v =>
        {
            p.DependsOn = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Touch();
        });
    }

    private async void AddPrerequisite_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null) return;
        var name = await UiDialogs.PromptTextAsync(XamlRoot, "Add prerequisite", "Name", ".NET Desktop Runtime", "Add");
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = new PrerequisiteDefinition { Id = UniqueId(Project.Prerequisites.Select(x => x.Id), name), Name = name.Trim() };
        Project.Prerequisites.Add(p);
        Touch(); RefreshPrerequisites(p);
    }

    private async void RemovePrerequisite_Click(object sender, RoutedEventArgs e)
    {
        var p = SelectedPrerequisite;
        if (p is null || Project is null) return;
        if (!await UiDialogs.ConfirmAsync(XamlRoot, "Remove prerequisite", $"Remove '{p.Name}'?", "Remove")) return;
        Project.Prerequisites.Remove(p);
        foreach (var other in Project.Prerequisites) other.DependsOn.RemoveAll(d => string.Equals(d, p.Id, StringComparison.OrdinalIgnoreCase));
        Touch(); RefreshPrerequisites(null);
    }

    // ================================================================= test

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = await _projects.ValidateAsync();
            var relevant = report.Issues.Where(i => i.Location is not null && (i.Location.StartsWith("operations[") || i.Location.StartsWith("prerequisites[")) || i.Code.StartsWith("PAIC13")).ToList();
            TestOutput.Text = relevant.Count == 0 ? "No problems found in operations and prerequisites." : string.Join(Environment.NewLine, relevant.Select(i => $"{i.Severity} {i.Code}: {i.Message}  ({i.Location})"));
        }
        catch (Exception ex) { Report("Validation failed", ex); }
    }

    /// <summary>Builds a virtual machine that pretends every prerequisite is already installed, so the test focuses on the operations.</summary>
    private (VirtualSystem Vm, string Root, InstallEngine Engine, InMemoryLogSink Log, ILoggerFactory Factory) NewSandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "paic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vm = new VirtualSystem(root);
        foreach (var p in Project!.Prerequisites)
        {
            var version = VersionParser.Parse(p.MinVersion) ?? new Version(1, 0);
            vm.SetDetected(p.Detection, p.DetectionTarget, p.DetectionValue, p.MaxVersion.Length > 0 && VersionParser.Parse(p.MaxVersion) is { } max && max < version ? max : version);
        }
        var log = new InMemoryLogSink();
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new InMemoryLoggerProvider(log)));
        return (vm, root, new InstallEngine(vm.Services, factory.CreateLogger("PAIC.Test")), log, factory);
    }

    private InstallRequest? Request(InstallMode mode)
    {
        if (Project is null || _projects.FilePath is null) return null;
        return new InstallRequest
        {
            Project = Project, Payload = new DirectoryPayloadProvider(Project, Path.GetDirectoryName(_projects.FilePath)!), Mode = mode,
            Components = Project.Components.Select(c => c.Id).ToList()
        };
    }

    private async void Plan_Click(object sender, RoutedEventArgs e)
    {
        Busy.IsActive = true;
        var (vm, root, engine, _, factory) = NewSandbox();
        try
        {
            var request = Request(InstallMode.Install);
            if (request is null) return;
            var plan = await engine.PlanAsync(request);
            var sb = new StringBuilder();
            sb.AppendLine($"Install folder: {plan.InstallDir}");
            sb.AppendLine("Components: " + (plan.Components.Count == 0 ? "(none defined)" : string.Join(", ", plan.Components)));
            var n = 1;
            foreach (var s in plan.Steps) sb.AppendLine($"{n++,3}. {s.Description}");
            foreach (var i in plan.Issues) sb.AppendLine($"{i.Severity}: {i.Message}");
            sb.AppendLine(plan.RequiresElevation ? "Needs administrator rights: " + string.Join(" ", plan.ElevationReasons) : "Does not need administrator rights.");
            TestOutput.Text = sb.ToString();
        }
        catch (Exception ex) { TestOutput.Text = ex is UserFacingException ? ex.Message : ex.ToString(); }
        finally { Busy.IsActive = false; factory.Dispose(); TryDelete(root); }
    }

    private async void Cycle_Click(object sender, RoutedEventArgs e)
    {
        Busy.IsActive = true;
        var (vm, root, engine, log, factory) = NewSandbox();
        var sb = new StringBuilder();
        void Step(string title, InstallResult r)
        {
            sb.AppendLine($"{(r.Success ? "PASS" : "FAIL")}  {title}{(r.Success ? "" : ": " + r.Error)}");
            foreach (var w in r.Warnings) sb.AppendLine("      warning: " + w);
        }
        try
        {
            var install = Request(InstallMode.Install);
            if (install is null) return;
            var installed = await engine.InstallAsync(install);
            Step("Install", installed);
            if (installed.Success && installed.InstallDir is { } dir)
            {
                var verify = await engine.VerifyAsync(dir);
                sb.AppendLine($"{(verify.IsHealthy ? "PASS" : "FAIL")}  Verify: {verify.Checked} item(s) checked, {verify.Missing.Count} missing, {verify.Modified.Count} modified");

                var victim = engine.LoadState(dir)?.Items.FirstOrDefault(i => i.Kind == "copyFiles" && i.Get("internal") != "true");
                if (victim is not null && File.Exists(victim.Get("path")))
                {
                    File.Delete(victim.Get("path"));
                    Step($"Repair (after deleting {Path.GetFileName(victim.Get("path"))})", await engine.InstallAsync(Request(InstallMode.Repair)!));
                    var again = await engine.VerifyAsync(dir);
                    sb.AppendLine($"{(again.IsHealthy ? "PASS" : "FAIL")}  Verify after repair");
                }
                var uninstalled = await engine.UninstallAsync(new UninstallRequest { InstallDir = dir });
                Step("Uninstall", uninstalled);
                var leftovers = Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Count() : 0;
                sb.AppendLine($"{(leftovers == 0 ? "PASS" : "INFO")}  Cleanup: {leftovers} file(s) or folder(s) left in the install folder");
            }
            sb.AppendLine();
            sb.AppendLine("Log");
            foreach (var entry in log.Snapshot()) sb.AppendLine("  " + entry.Text.Replace(Environment.NewLine, Environment.NewLine + "    "));
            TestOutput.Text = sb.ToString();
        }
        catch (Exception ex) { TestOutput.Text = sb + Environment.NewLine + (ex is UserFacingException ? ex.Message : ex.ToString()); }
        finally { Busy.IsActive = false; factory.Dispose(); TryDelete(root); }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
