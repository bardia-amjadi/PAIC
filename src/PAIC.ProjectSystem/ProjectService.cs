using Microsoft.Extensions.Logging;
using PAIC.Core.IO;
using PAIC.Core.Validation;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Recent;
using PAIC.ProjectSystem.Validation;

namespace PAIC.ProjectSystem;

/// <summary>Owns the currently open project: create, open, save, save-as, validate.</summary>
public interface IProjectService
{
    PaicProject? Current { get; }
    string? FilePath { get; }
    bool IsDirty { get; }
    event EventHandler? Changed;

    /// <summary>
    /// Creates a project directory. When <paramref name="basis"/> is given (e.g. prepared from a template) it is used instead of
    /// the default project, and <paramref name="files"/> (project-relative path -> content, under assets/) are written first.
    /// </summary>
    Task<PaicProject> CreateAsync(string parentDirectory, string projectName, PaicProject? basis = null,
        IReadOnlyDictionary<string, byte[]>? files = null, CancellationToken ct = default);
    Task<PaicProject> OpenAsync(string filePath, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
    Task SaveAsAsync(string newFilePath, CancellationToken ct = default);
    void MarkDirty();
    void Close();
    Task<ValidationReport> ValidateAsync(CancellationToken ct = default);
}

public sealed class ProjectService : IProjectService
{
    private readonly IRecentProjectsStore _recent;
    private readonly ILogger<ProjectService> _logger;
    private readonly ProjectSerializer _serializer;
    private readonly Validator<ProjectValidationContext> _validator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectService(IRecentProjectsStore recent, ILogger<ProjectService> logger,
        ProjectSerializer? serializer = null, Validator<ProjectValidationContext>? validator = null)
    {
        _recent = recent;
        _logger = logger;
        _serializer = serializer ?? ProjectSerializer.Default;
        _validator = validator ?? ProjectValidator.CreateDefault();
    }

    public PaicProject? Current { get; private set; }
    public string? FilePath { get; private set; }
    public bool IsDirty { get; private set; }
    public event EventHandler? Changed;

    public async Task<PaicProject> CreateAsync(string parentDirectory, string projectName, PaicProject? basis = null,
        IReadOnlyDictionary<string, byte[]>? files = null, CancellationToken ct = default)
    {
        ProjectNaming.EnsureValid(projectName);
        if (!Path.IsPathRooted(parentDirectory)) throw new ProjectException("Choose an absolute location for the project.");

        var dir = Path.Combine(parentDirectory, projectName);
        var file = Path.Combine(dir, projectName + ProjectLayout.FileExtension);
        if (File.Exists(file)) throw new ProjectException($"A project already exists at '{file}'.");

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ProjectLayout.AssetsFolderName));
            var project = basis ?? ProjectFactory.CreateNew(projectName);
            project.Metadata.Name = projectName;
            if (files is not null)
            {
                foreach (var (relative, data) in files)
                {
                    var normalized = PathSafety.ToProjectPath(relative);
                    if (!normalized.StartsWith(ProjectLayout.AssetsFolderName + "/", StringComparison.OrdinalIgnoreCase))
                        throw new ProjectException($"File '{relative}' is not inside the assets folder.");
                    var target = PathSafety.ResolveUnder(dir, normalized);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target, data, ct);
                }
            }
            await WriteAtomicAsync(file, project, ct);
            SetCurrent(project, file, dirty: false);
            _recent.Add(file, project.Metadata.Name);
            _logger.LogInformation("Created project {Name} at {Path}", projectName, file);
            return project;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ProjectException($"Could not create the project: {ex.Message}", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task<PaicProject> OpenAsync(string filePath, CancellationToken ct = default)
    {
        if (!string.Equals(Path.GetExtension(filePath), ProjectLayout.FileExtension, StringComparison.OrdinalIgnoreCase))
            throw new ProjectException($"'{Path.GetFileName(filePath)}' is not a {ProjectLayout.FileExtension} file.");
        if (!File.Exists(filePath)) throw new ProjectException($"The file '{filePath}' does not exist.");

        await _gate.WaitAsync(ct);
        try
        {
            var full = Path.GetFullPath(filePath);
            var json = await File.ReadAllTextAsync(full, ct);
            var project = _serializer.Deserialize(json);
            SetCurrent(project, full, dirty: false);
            _recent.Add(full, project.Metadata.Name);
            _logger.LogInformation("Opened project {Name} from {Path} (schema {Schema})", project.Metadata.Name, full, project.SchemaVersion);
            return project;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectException($"Could not read the project: {ex.Message}", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var (project, file) = RequireOpen();
        await _gate.WaitAsync(ct);
        try
        {
            project.Metadata.ModifiedUtc = DateTimeOffset.UtcNow;
            await WriteAtomicAsync(file, project, ct);
            IsDirty = false;
            _logger.LogInformation("Saved project to {Path}", file);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectException($"Could not save the project: {ex.Message}", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsAsync(string newFilePath, CancellationToken ct = default)
    {
        var (project, oldFile) = RequireOpen();
        if (!string.Equals(Path.GetExtension(newFilePath), ProjectLayout.FileExtension, StringComparison.OrdinalIgnoreCase))
            newFilePath += ProjectLayout.FileExtension;
        var newFull = Path.GetFullPath(newFilePath);

        await _gate.WaitAsync(ct);
        try
        {
            var oldAssets = ProjectLayout.AssetsDirectory(oldFile);
            var newAssets = ProjectLayout.AssetsDirectory(newFull);
            Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);

            if (!string.Equals(oldAssets, newAssets, StringComparison.OrdinalIgnoreCase))
            {
                if (IsInside(oldAssets, newAssets))
                    throw new ProjectException("The new location must not be inside the current project's assets folder.");
                CopyDirectory(oldAssets, newAssets, ct);
            }

            project.Metadata.ModifiedUtc = DateTimeOffset.UtcNow;
            await WriteAtomicAsync(newFull, project, ct);
            SetCurrent(project, newFull, dirty: false);
            _recent.Add(newFull, project.Metadata.Name);
            _logger.LogInformation("Saved project as {Path}", newFull);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectException($"Could not save the project: {ex.Message}", ex);
        }
        finally { _gate.Release(); }
    }

    public void MarkDirty()
    {
        if (Current is null || IsDirty) return;
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        if (Current is null) return;
        _logger.LogInformation("Closed project {Name}", Current.Metadata.Name);
        Current = null; FilePath = null; IsDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<ValidationReport> ValidateAsync(CancellationToken ct = default)
    {
        var (project, file) = RequireOpen();
        // Validate a snapshot so edits on the UI thread cannot race with rules running on a worker thread.
        var snapshot = _serializer.Deserialize(_serializer.Serialize(project));
        var ctx = new ProjectValidationContext(snapshot, Path.GetDirectoryName(file));
        return Task.Run(() =>
        {
            var report = _validator.Validate(ctx);
            _logger.LogInformation("Validated project: {Errors} error(s), {Warnings} warning(s)", report.ErrorCount, report.WarningCount);
            return report;
        }, ct);
    }

    private (PaicProject project, string file) RequireOpen() =>
        Current is not null && FilePath is not null ? (Current, FilePath) : throw new ProjectException("No project is open.");

    private void SetCurrent(PaicProject project, string file, bool dirty)
    {
        Current = project; FilePath = file; IsDirty = dirty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task WriteAtomicAsync(string file, PaicProject project, CancellationToken ct)
    {
        var tmp = file + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, _serializer.Serialize(project), new System.Text.UTF8Encoding(false), ct);
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    private static bool IsInside(string parent, string candidate)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        if (!Directory.Exists(source)) { Directory.CreateDirectory(destination); return; }
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReparsePoint) != 0) continue; // never follow links out of the project
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
