using PAIC.Controls;
using PAIC.ProjectSystem.Model;

namespace PAIC.ProjectSystem.Layouts;

/// <summary>Variables that always exist at installer run time.</summary>
public static class BuiltInVariables
{
    public static IReadOnlyList<string> Names { get; } = new[] { "ProductName", "Version", "Publisher", "InstallDir", "ErrorMessage", "Status" };
}

/// <summary>Generates the default element layout for each built-in page type.</summary>
public static class PageLayouts
{
    private const double Margin = 40;

    public static List<DesignElement> Create(InstallerPageType type, double width, double height)
    {
        var list = new List<DesignElement>();
        var contentW = width - 2 * Margin;

        DesignElement Add(string ctype, double x, double y, double w, double h, Action<DesignElement>? configure = null)
        {
            var el = ElementFactory.Create(ctype, ElementIds.Next(list, ctype), x, y, list);
            el.Width = w; el.Height = h;
            configure?.Invoke(el);
            list.Add(el);
            return el;
        }
        DesignElement Title(string text) => Add(ControlTypes.Text, Margin, 32, contentW, 40, e => { e.Set("text", text); e.Set("style", "title"); });
        DesignElement Body(string text, double y = 84, double h = 48) => Add(ControlTypes.Text, Margin, y, contentW, h, e => { e.Set("text", text); e.Set("color", "textSecondary"); });
        DesignElement Button(string text, double rightOffset, ElementActionKind kind, bool accent, Action<DesignElement>? cfg = null) =>
            Add(ControlTypes.Button, width - Margin - 100 - rightOffset, height - 60, 100, 32, e =>
            {
                e.Set("text", text); e.Set("style", accent ? "accent" : "standard");
                e.Action = new ElementAction { Kind = kind };
                cfg?.Invoke(e);
            });
        void Nav(string next, bool back = true, bool cancel = true, bool nextAccent = true)
        {
            if (cancel) Button("Cancel", 0, ElementActionKind.Cancel, false);
            Button(next, cancel ? 108 : 0, next == "Finish" ? ElementActionKind.Finish : ElementActionKind.Next, nextAccent);
            if (back) Button("Back", (cancel ? 108 : 0) + 108, ElementActionKind.Back, false);
        }

        switch (type)
        {
            case InstallerPageType.Welcome:
                Title("Welcome to {ProductName} Setup");
                Body("This wizard will install {ProductName} {Version} on your computer.\nClick Next to continue.", 84, 72);
                Nav("Next", back: false);
                break;
            case InstallerPageType.LicenseAgreement:
                Title("License Agreement");
                Body("Please read the license agreement below.", 76, 24);
                Add(ControlTypes.RichText, Margin, 108, contentW, height - 108 - 120, e => e.Set("text", "Enter your license text here, or choose a text asset in the inspector."));
                Add(ControlTypes.Checkbox, Margin, height - 108, contentW, 32, e => { e.Set("label", "I accept the terms of the license agreement"); e.Set("variable", "LicenseAccepted"); });
                Nav("Next");
                var nextButton = list.Last(x => x.Type == ControlTypes.Button && x.GetString("text") == "Next");
                nextButton.EnabledWhen = new ConditionDefinition { Variable = "LicenseAccepted", Operator = ConditionOperator.IsTrue };
                break;
            case InstallerPageType.InstallationDirectory:
                Title("Choose Install Location");
                Body("{ProductName} will be installed in the folder below.", 76, 24);
                Add(ControlTypes.TextInput, Margin, 120, contentW - 108, 56, e => { e.Set("label", "Destination folder"); e.Set("variable", "InstallDir"); e.Set("required", true); });
                Add(ControlTypes.Button, width - Margin - 100, 142, 100, 32, e =>
                {
                    e.Set("text", "Browse...");
                    e.Action = new ElementAction { Kind = ElementActionKind.BrowseFolder, Variable = "InstallDir" };
                });
                Nav("Next");
                break;
            case InstallerPageType.ComponentSelection:
                Title("Select Components");
                Body("Choose the components you want to install.", 76, 24);
                Add(ControlTypes.List, Margin, 112, contentW, height - 112 - 80, e => e.Set("source", "components"));
                Nav("Next");
                break;
            case InstallerPageType.Prerequisites:
                Title("Prerequisites");
                Body("Setup checks that the following requirements are met.", 76, 24);
                Add(ControlTypes.List, Margin, 112, contentW, height - 112 - 80, e => e.Set("source", "prerequisites"));
                Nav("Next");
                break;
            case InstallerPageType.UserConfiguration:
                Title("Configuration");
                Body("Tell us how you want {ProductName} to be set up.", 76, 24);
                Add(ControlTypes.TextInput, Margin, 120, 420, 56, e => { e.Set("label", "Organization"); e.Set("variable", "OrganizationName"); });
                Add(ControlTypes.Checkbox, Margin, 196, 420, 32, e => { e.Set("label", "Create a desktop shortcut"); e.Set("variable", "CreateDesktopShortcut"); e.Set("defaultChecked", true); });
                Nav("Next");
                break;
            case InstallerPageType.InstallationSummary:
                Title("Ready to Install");
                Add(ControlTypes.RichText, Margin, 92, contentW, 160, e => e.Set("text", "**{ProductName}** {Version}\nInstall location: {InstallDir}"));
                Nav("Install");
                break;
            case InstallerPageType.InstallationProgress:
                Title("Installing {ProductName}");
                Add(ControlTypes.Text, Margin, 100, contentW, 24, e => { e.Set("text", "{Status}"); e.Set("color", "textSecondary"); });
                Add(ControlTypes.Progress, Margin, 136, contentW, 16, e => e.Set("source", "install"));
                Button("Cancel", 0, ElementActionKind.Cancel, false);
                break;
            case InstallerPageType.Completion:
                Add(ControlTypes.Icon, Margin, 32, 40, 40, e => { e.Set("glyph", "success"); e.Set("color", "success"); });
                Add(ControlTypes.Text, Margin + 56, 32, contentW - 56, 40, e => { e.Set("text", "Setup completed"); e.Set("style", "title"); });
                Body("{ProductName} was installed successfully.", 92, 24);
                Add(ControlTypes.Checkbox, Margin, 132, contentW, 32, e => { e.Set("label", "Launch {ProductName}"); e.Set("variable", "LaunchApp"); e.Set("defaultChecked", true); });
                Button("Finish", 0, ElementActionKind.Finish, true);
                break;
            case InstallerPageType.Error:
                Add(ControlTypes.Icon, Margin, 32, 40, 40, e => { e.Set("glyph", "error"); e.Set("color", "error"); });
                Add(ControlTypes.Text, Margin + 56, 32, contentW - 56, 40, e => { e.Set("text", "Setup failed"); e.Set("style", "title"); });
                Body("{ErrorMessage}", 92, 72);
                Button("Close", 0, ElementActionKind.Finish, true);
                break;
            case InstallerPageType.Uninstallation:
                Title("Uninstall {ProductName}");
                Body("This will remove {ProductName} from your computer.", 84, 48);
                Button("Cancel", 0, ElementActionKind.Cancel, false);
                Button("Uninstall", 108, ElementActionKind.Next, true);
                break;
            default:
                Title("New page");
                Nav("Next");
                break;
        }
        foreach (var e in list) e.Name = e.Id;
        return list;
    }
}
