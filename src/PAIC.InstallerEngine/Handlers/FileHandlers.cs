using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PAIC.Core.IO;
using PAIC.Core.Validation;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerEngine.Handlers;

public sealed class CreateDirectoryHandler : OperationHandler
{
    public override string Kind => OperationKinds.CreateDirectory;

    public override string Describe(OperationContext c, InstallOperationDefinition op) => $"Create folder {Safe(c, op.Str("path"))}";

    private static string Safe(OperationContext c, string raw) { try { return c.Expand(raw); } catch (EngineException) { return raw; } }

    public override IEnumerable<ValidationIssue> Preflight(OperationContext c, InstallOperationDefinition op) => CheckPath(c, op, "path", op.Flag("allowOutside"));

    public override Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct)
    {
        var records = new List<UndoRecord>();
        EnsureDirectory(c.ResolvePath(op.Str("path"), op.Flag("allowOutside")), op, records);
        return Task.FromResult<IReadOnlyList<UndoRecord>>(records);
    }

    /// <summary>Creates a folder and every missing parent, recording each folder that did not exist before.</summary>
    public static void EnsureDirectory(string path, InstallOperationDefinition op, List<UndoRecord> records)
    {
        var missing = new Stack<string>();
        var current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) break;
            current = parent;
        }
        foreach (var dir in missing)
        {
            Directory.CreateDirectory(dir);
            records.Add(new UndoRecord { Kind = OperationKinds.CreateDirectory, OperationId = op.Id, ComponentId = op.ComponentId ?? "", Key = dir, Data = { ["path"] = dir } });
        }
    }

    public override Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct)
    {
        var path = record.Get("path");
        // only ever remove an empty folder: anything the user put there is left alone
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            try { Directory.Delete(path); }
            catch (IOException ex) { c.Warn($"Could not remove folder '{path}': {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { c.Warn($"Could not remove folder '{path}': {ex.Message}"); }
        }
        return Task.CompletedTask;
    }
}

public sealed class CopyFilesHandler : OperationHandler
{
    public override string Kind => OperationKinds.CopyFiles;

    private static IReadOnlyList<PayloadFile> Matches(OperationContext c, string source) =>
        c.Request.Payload.Files.Where(f => Glob.IsMatch(source, f.Path)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();

    public override string Describe(OperationContext c, InstallOperationDefinition op)
    {
        var count = Matches(c, op.Str("source")).Count;
        string dest; try { dest = c.Expand(op.Str("destination", "{InstallDir}")); } catch (EngineException) { dest = op.Str("destination"); }
        return $"Copy {count} file(s) from {op.Str("source")} to {dest}";
    }

    public override IEnumerable<ValidationIssue> Preflight(OperationContext c, InstallOperationDefinition op)
    {
        var source = op.Str("source");
        if (!PathSafety.IsSafeRelativePath(source, out var why)) { yield return Error("PAIC3102", op, $"Source '{source}' is not allowed: {why}"); yield break; }
        if (Matches(c, source).Count == 0 && !op.Flag("optional"))
            yield return Error("PAIC3103", op, $"No payload file matches '{source}'.");
        foreach (var i in CheckPath(c, op, "destination", op.Flag("allowOutside"))) yield return i;
    }

    public override IEnumerable<string> ElevationReasons(OperationContext c, InstallOperationDefinition op)
    {
        string? dest = null;
        try { dest = c.ResolvePath(op.Str("destination", "{InstallDir}"), op.Flag("allowOutside")); } catch (EngineException) { }
        if (dest is not null && c.System.Paths.RequiresElevation(dest)) yield return $"Writing to '{dest}' needs administrator rights.";
    }

    public override async Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct)
    {
        var source = op.Str("source");
        if (!PathSafety.IsSafeRelativePath(source, out var why)) throw new EngineException($"Source '{source}' is not allowed: {why}");
        var files = Matches(c, source);
        var records = new List<UndoRecord>();
        if (files.Count == 0)
        {
            if (op.Flag("optional")) { c.Logger.LogInformation("Nothing matches {Source}; skipped (optional).", source); return records; }
            throw new EngineException($"No payload file matches '{source}'.");
        }

        var baseDir = Glob.BaseDirectory(source);
        var destination = c.ResolvePath(op.Str("destination", "{InstallDir}"), op.Flag("allowOutside"));
        var overwrite = op.Str("overwrite", "always");
        var userData = op.Flag("userData");
        CreateDirectoryHandler.EnsureDirectory(destination, op, records);

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var relative = baseDir.Length == 0 ? file.Path : file.Path[(baseDir.Length + 1)..];
            var target = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!OperationContext.IsInside(destination, target)) throw new EngineException($"'{file.Path}' would be written outside its destination.");
            CreateDirectoryHandler.EnsureDirectory(Path.GetDirectoryName(target)!, op, records);

            string? backup = null;
            var adopted = false;
            if (File.Exists(target))
            {
                var existingHash = await FileHashing.ComputeSha256Async(target, ct);
                var owned = c.FindExisting(Kind, target) is not null;
                if (existingHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) && owned) adopted = true;
                else if (overwrite == "never") { c.Logger.LogInformation("Kept existing {Path} (overwrite=never).", target); c.SubProgress?.Invoke((i + 1d) / files.Count); continue; }
                else if (overwrite == "ifDifferent" && existingHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    c.Logger.LogInformation("{Path} is already up to date; not claimed because it was not installed by this product.", target);
                    c.SubProgress?.Invoke((i + 1d) / files.Count);
                    continue;
                }
            }

            if (!adopted) backup = await WriteFileAsync(c, file, target, ct);
            var rec = NewRecord(op, Kind, target,
                ("path", target), ("sha256", file.Sha256.ToLowerInvariant()), ("size", file.Size.ToString()),
                ("userData", userData ? "true" : "false"), ("adopted", adopted ? "true" : "false"));
            if (backup is not null) rec.Data["backup"] = backup;
            records.Add(rec);
            c.SubProgress?.Invoke((i + 1d) / files.Count);
        }
        return records;
    }

    /// <summary>Copies the payload file next to the target, verifies its hash, backs up the existing file, and moves it into place.</summary>
    private static async Task<string?> WriteFileAsync(OperationContext c, PayloadFile file, string target, CancellationToken ct)
    {
        var temp = target + ".paic-tmp";
        string actual;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = c.Request.Payload.Open(file.Path))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new EngineException($"Integrity check failed for '{file.Path}' (expected {file.Sha256}, got {actual}).");

            string? backup = null;
            if (File.Exists(target))
            {
                backup = c.NewBackupPath();
                File.Move(target, backup);
            }
            try { File.Move(temp, target); }
            catch
            {
                if (backup is not null && !File.Exists(target)) File.Move(backup, target);
                throw;
            }
            return backup;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    public override async Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct)
    {
        var path = record.Get("path");
        var backup = record.Get("backup");
        if (c.Purpose == UndoPurpose.Rollback)
        {
            if (record.Flag("adopted")) return; // the file was already ours and untouched
            if (File.Exists(path)) File.Delete(path);
            if (backup.Length > 0 && File.Exists(backup)) File.Move(backup, path);
            return;
        }

        if (!File.Exists(path)) return;
        if (c.CurrentExecutable is not null && string.Equals(Path.GetFullPath(c.CurrentExecutable), path, StringComparison.OrdinalIgnoreCase))
        {
            c.DeferredDeletes.Add(path);
            return;
        }
        var protectedData = record.Flag("userData") && !c.RemoveUserData;
        var recorded = record.Get("sha256");
        var modified = recorded.Length > 0 && !(await FileHashing.ComputeSha256Async(path, ct)).Equals(recorded, StringComparison.OrdinalIgnoreCase);
        if (protectedData) { c.Warn($"Kept user data file '{path}'."); return; }
        if (modified && !c.RemoveUserData) { c.Warn($"Kept '{path}' because it was modified after installation."); return; }
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }
}

public sealed class DeleteFilesHandler : OperationHandler
{
    public override string Kind => OperationKinds.DeleteFiles;

    private static (string Directory, string Pattern) Split(OperationContext c, InstallOperationDefinition op)
    {
        var expanded = PathExpander.ToLocalPath(c.Expand(op.Str("path")));
        var index = expanded.LastIndexOfAny(new[] { '\\', '/' });
        if (index <= 0) throw new EngineException($"'{expanded}' is not an absolute path.");
        var pattern = expanded[(index + 1)..];
        if (pattern.Length == 0) throw new EngineException("The path must end with a file name or pattern.");
        // always confined to the install folder
        var directory = c.ResolvePath(expanded[..index], allowOutside: false);
        return (directory, pattern);
    }

    public override IEnumerable<ValidationIssue> Preflight(OperationContext c, InstallOperationDefinition op)
    {
        string? problem = null;
        try { Split(c, op); } catch (EngineException ex) { problem = ex.Message; }
        if (problem is not null) yield return Error("PAIC3101", op, problem);
    }

    public override string Describe(OperationContext c, InstallOperationDefinition op) => $"Delete {op.Str("path")}";

    public override Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct)
    {
        var (directory, pattern) = Split(c, op);
        var records = new List<UndoRecord>();
        if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<UndoRecord>>(records);
        var option = op.Flag("recursive") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(directory, pattern, option).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (file.StartsWith(c.StateDir, StringComparison.OrdinalIgnoreCase)) continue; // never touch installer bookkeeping
            var backup = c.NewBackupPath();
            File.SetAttributes(file, FileAttributes.Normal);
            File.Move(file, backup);
            records.Add(NewRecord(op, Kind, file, ("path", file), ("backup", backup)));
        }
        return Task.FromResult<IReadOnlyList<UndoRecord>>(records);
    }

    public override Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct)
    {
        // Deleted files are gone for good; only a rollback brings them back.
        if (c.Purpose != UndoPurpose.Rollback) return Task.CompletedTask;
        var backup = record.Get("backup");
        var path = record.Get("path");
        if (backup.Length > 0 && File.Exists(backup) && !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(backup, path);
        }
        return Task.CompletedTask;
    }
}
