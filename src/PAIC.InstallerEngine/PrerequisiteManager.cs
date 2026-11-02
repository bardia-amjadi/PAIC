using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerEngine;

public enum PrerequisiteState { Satisfied, NotApplicable, Missing, VersionTooLow, VersionTooHigh, DependencyMissing, InstallFailed }

public sealed record PrerequisiteStatus(PrerequisiteDefinition Definition, PrerequisiteState State, Version? InstalledVersion, string Message)
{
    public bool IsSatisfied => State is PrerequisiteState.Satisfied or PrerequisiteState.NotApplicable;
}

public static class VersionParser
{
    /// <summary>Parses "8.0.1", "v8.0", "8" and "8.0.1-preview.2"; returns null for text without a leading number.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < t.Length && (char.IsDigit(t[end]) || t[end] == '.')) end++;
        var numeric = t[..end].Trim('.');
        if (numeric.Length == 0) return null;
        var parts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) numeric += ".0";
        else if (parts.Length > 4) numeric = string.Join('.', parts.Take(4));
        return Version.TryParse(numeric, out var v) ? v : null;
    }
}

/// <summary>Detects, orders and installs prerequisites.</summary>
public sealed class PrerequisiteManager
{
    private readonly SystemServices _system;
    private readonly ILogger _logger;

    public PrerequisiteManager(SystemServices system, ILogger logger)
    {
        _system = system;
        _logger = logger;
    }

    /// <summary>Orders prerequisites so every dependency comes first. Keeps the declared order otherwise.</summary>
    public static IReadOnlyList<PrerequisiteDefinition> Order(IEnumerable<PrerequisiteDefinition> definitions)
    {
        var list = definitions.ToList();
        var byId = list.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<PrerequisiteDefinition>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Visit(PrerequisiteDefinition p)
        {
            if (state.TryGetValue(p.Id, out var s))
            {
                if (s == 1) throw new EngineException($"Prerequisite '{p.Id}' is part of a dependency cycle.");
                return;
            }
            state[p.Id] = 1;
            foreach (var dep in p.DependsOn)
            {
                if (!byId.TryGetValue(dep, out var d)) throw new EngineException($"Prerequisite '{p.Id}' depends on unknown prerequisite '{dep}'.");
                Visit(d);
            }
            state[p.Id] = 2;
            result.Add(p);
        }
        foreach (var p in list) Visit(p);
        return result;
    }

    private static bool AppliesTo(PrerequisiteArchitecture required, PrerequisiteArchitecture machine) =>
        required == PrerequisiteArchitecture.Any || required == machine;

    public PrerequisiteStatus Evaluate(PrerequisiteDefinition def, IReadOnlyDictionary<string, PrerequisiteStatus>? known = null)
    {
        if (!AppliesTo(def.Architecture, _system.Detection.Architecture))
            return new(def, PrerequisiteState.NotApplicable, null, $"{def.Name} is not needed on this architecture.");

        foreach (var dep in def.DependsOn)
            if (known is not null && known.TryGetValue(dep, out var depStatus) && !depStatus.IsSatisfied)
                return new(def, PrerequisiteState.DependencyMissing, null, $"{def.Name} needs {depStatus.Definition.Name} first.");

        var installed = _system.Detection.GetVersion(def.Detection, def.DetectionTarget, def.DetectionValue);
        if (installed is null) return new(def, PrerequisiteState.Missing, null, $"{def.Name} was not found.");

        var min = VersionParser.Parse(def.MinVersion);
        var max = VersionParser.Parse(def.MaxVersion);
        if (min is not null && installed < min) return new(def, PrerequisiteState.VersionTooLow, installed, $"{def.Name} {installed} is older than the required {min}.");
        if (max is not null && installed > max) return new(def, PrerequisiteState.VersionTooHigh, installed, $"{def.Name} {installed} is newer than the supported {max}.");
        return new(def, PrerequisiteState.Satisfied, installed, $"{def.Name} {installed} is installed.");
    }

    public Task<IReadOnlyList<PrerequisiteStatus>> EvaluateAsync(IEnumerable<PrerequisiteDefinition> definitions, CancellationToken ct)
    {
        var known = new Dictionary<string, PrerequisiteStatus>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PrerequisiteStatus>();
        foreach (var def in Order(definitions))
        {
            ct.ThrowIfCancellationRequested();
            var status = Evaluate(def, known);
            known[def.Id] = status;
            result.Add(status);
        }
        return Task.FromResult<IReadOnlyList<PrerequisiteStatus>>(result);
    }

    /// <summary>Installs prerequisites that are missing or too old: download, verify SHA-256, run silently, detect again.</summary>
    public async Task<IReadOnlyList<PrerequisiteStatus>> InstallMissingAsync(IEnumerable<PrerequisiteDefinition> definitions, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var known = new Dictionary<string, PrerequisiteStatus>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PrerequisiteStatus>();
        var ordered = Order(definitions);
        for (var i = 0; i < ordered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var def = ordered[i];
            var status = Evaluate(def, known);
            if (status.State is PrerequisiteState.Missing or PrerequisiteState.VersionTooLow)
            {
                progress?.Report(new InstallProgress("Prerequisites", 100d * i / ordered.Count, $"Installing {def.Name}..."));
                status = await InstallAsync(def, status, known, progress, ct);
            }
            known[def.Id] = status;
            result.Add(status);
        }
        return result;
    }

    private async Task<PrerequisiteStatus> InstallAsync(PrerequisiteDefinition def, PrerequisiteStatus before, IReadOnlyDictionary<string, PrerequisiteStatus> known, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(def.DownloadUrl))
            return before with { Message = $"{before.Message} No download source is configured." };
        if (!Uri.TryCreate(def.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new(def, PrerequisiteState.InstallFailed, before.InstalledVersion, $"The download URL for {def.Name} must be an https address.");
        if (string.IsNullOrWhiteSpace(def.Sha256))
            return new(def, PrerequisiteState.InstallFailed, before.InstalledVersion, $"{def.Name} has no SHA-256 checksum, so its download cannot be verified.");

        // unique, per-run temp folder so another user or process cannot swap the file between download and run
        var temp = Path.Combine(Path.GetTempPath(), "paic-prereq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) fileName = "prerequisite.exe";
            var file = Path.Combine(temp, fileName);
            _logger.LogInformation("Downloading {Name} from {Url}", def.Name, uri);
            await _system.Downloader.DownloadAsync(uri, file, new Progress<double>(p => progress?.Report(new InstallProgress("Prerequisites", p * 100, $"Downloading {def.Name}..."))), ct);

            await using (var stream = File.OpenRead(file))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (!actual.Equals(def.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    return new(def, PrerequisiteState.InstallFailed, before.InstalledVersion, $"The download of {def.Name} failed its integrity check and was not run.");
            }

            var result = await _system.Processes.RunAsync(new ProcessSpec(file, def.InstallArguments, temp, TimeSpan.FromMinutes(30)), ct);
            if (result.TimedOut) return new(def, PrerequisiteState.InstallFailed, before.InstalledVersion, $"The installer for {def.Name} did not finish in time.");
            if (result.ExitCode is not (0 or 3010 or 1641))
                return new(def, PrerequisiteState.InstallFailed, before.InstalledVersion, $"The installer for {def.Name} failed with exit code {result.ExitCode}.");

            var after = Evaluate(def, known);
            if (!after.IsSatisfied) return new(def, PrerequisiteState.InstallFailed, after.InstalledVersion, $"{def.Name} was installed but is still not detected: {after.Message}");
            var reboot = result.ExitCode is 3010 or 1641 ? " A restart is required." : "";
            return after with { Message = after.Message + reboot };
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
