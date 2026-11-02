namespace PAIC.InstallerEngine.Cli;

public enum InstallerCommandKind { Help, Install, Uninstall, Repair, Modify, Version, Status, Plan, Prerequisites }

/// <summary>Process exit codes of the installer command line.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int InvalidArguments = 2;
    public const int Cancelled = 3;
    public const int PrerequisitesFailed = 4;
    public const int NotInstalled = 5;
    public const int ElevationRequired = 740;
}

public sealed class InstallerCommand
{
    public InstallerCommandKind Kind { get; set; } = InstallerCommandKind.Help;
    public bool Silent { get; set; }
    public string? InstallDir { get; set; }
    public bool AllComponents { get; set; }
    public List<string>? Components { get; set; }
    public string? ProjectPath { get; set; }
    public string? LogDirectory { get; set; }
    public bool RemoveUserData { get; set; }
    public bool Elevate { get; set; }
    public bool DryRun { get; set; }
    public string? Sandbox { get; set; }
    public bool NoPrerequisites { get; set; }
    public bool AcceptLicense { get; set; }
    public bool Json { get; set; }
    public bool AllowDowngrade { get; set; }
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; set; }
}

/// <summary>Parses the installer's command line. Options accept "--name value" and "--name=value".</summary>
public static class InstallerCommandLine
{
    public const string Usage = """
        Usage: <installer> <command> [options]

        Commands
          install          Install, or upgrade an existing installation
          uninstall        Remove the product
          repair           Restore missing or damaged files and settings
          modify           Change the selected components
          version          Show product and installer version information
          status           Show whether the product is installed and whether it is healthy
          plan             Show what install would do, without changing anything
          prerequisites    Check (and with install, set up) prerequisites

        Options
          -s, --silent               No questions and no interface
          -d, --install-dir <path>   Install folder
          -c, --components <a,b|all> Components to install (required ones are always included)
              --project <file.paproj> Use a project folder as the payload (development and testing)
              --set <Name=Value>     Set an installer variable (repeatable)
              --accept-license       Required for silent installs of products with a license page
              --no-prerequisites     Do not install missing prerequisites
              --remove-user-data     On uninstall, also remove user data and modified files
              --allow-downgrade      Allow installing an older version over a newer one
              --elevate              Ask for administrator rights when they are needed
              --dry-run              Same as the plan command for install/repair/modify
              --sandbox <folder>     Run against a virtual machine rooted in the folder; nothing outside it is changed
              --log <folder>         Write the install log to this folder
              --json                 Machine-readable output for version and status
          -h, --help                 Show this help

        Exit codes
          0 success   1 failure   2 invalid arguments   3 cancelled (rolled back)
          4 prerequisites not satisfied   5 not installed   740 administrator rights required
        """;

    public static InstallerCommand Parse(IReadOnlyList<string> args)
    {
        var cmd = new InstallerCommand();
        if (args.Count == 0) return cmd;

        var first = args[0].ToLowerInvariant();
        if (first is "-h" or "--help" or "/?" or "help" or "-?") return cmd;
        var kind = first switch
        {
            "install" => InstallerCommandKind.Install, "uninstall" => InstallerCommandKind.Uninstall, "repair" => InstallerCommandKind.Repair,
            "modify" => InstallerCommandKind.Modify, "version" => InstallerCommandKind.Version, "status" => InstallerCommandKind.Status,
            "plan" => InstallerCommandKind.Plan, "prerequisites" => InstallerCommandKind.Prerequisites, _ => (InstallerCommandKind?)null
        };
        if (kind is null) { cmd.Error = $"Unknown command '{args[0]}'."; return cmd; }
        cmd.Kind = kind.Value;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string? inline = null;
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--") && eq > 0) { inline = arg[(eq + 1)..]; arg = arg[..eq]; }

            string? Value()
            {
                if (inline is not null) return inline;
                if (i + 1 < args.Count) return args[++i];
                cmd.Error = $"Option '{arg}' needs a value.";
                return null;
            }

            switch (arg.ToLowerInvariant())
            {
                case "-s": case "--silent": case "/s": case "/quiet": case "--quiet": cmd.Silent = true; break;
                case "-d": case "--install-dir": cmd.InstallDir = Value(); break;
                case "-c": case "--components":
                    var list = Value();
                    if (list is null) break;
                    if (list.Equals("all", StringComparison.OrdinalIgnoreCase)) cmd.AllComponents = true;
                    else cmd.Components = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                case "--project": case "-p": cmd.ProjectPath = Value(); break;
                case "--log": cmd.LogDirectory = Value(); break;
                case "--sandbox": cmd.Sandbox = Value(); break;
                case "--set":
                    var pair = Value();
                    if (pair is null) break;
                    var at = pair.IndexOf('=');
                    if (at <= 0) { cmd.Error = $"'{pair}' is not Name=Value."; break; }
                    cmd.Variables[pair[..at].Trim()] = pair[(at + 1)..];
                    break;
                case "--remove-user-data": cmd.RemoveUserData = true; break;
                case "--elevate": cmd.Elevate = true; break;
                case "--dry-run": cmd.DryRun = true; break;
                case "--no-prerequisites": cmd.NoPrerequisites = true; break;
                case "--accept-license": cmd.AcceptLicense = true; break;
                case "--json": cmd.Json = true; break;
                case "--allow-downgrade": cmd.AllowDowngrade = true; break;
                case "-h": case "--help": cmd.Kind = InstallerCommandKind.Help; return cmd;
                default: cmd.Error = $"Unknown option '{args[i]}'."; break;
            }
            if (cmd.Error is not null) return cmd;
        }
        return cmd;
    }
}
