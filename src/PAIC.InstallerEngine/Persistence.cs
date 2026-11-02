using System.Text.Json;
using System.Text.Json.Nodes;

namespace PAIC.InstallerEngine;

public sealed record JournalHeader(string TransactionId, string Mode, string Version, DateTimeOffset StartedUtc, string InstallDir, string? PreviousVersion);

public sealed class JournalContents
{
    public JournalHeader? Header { get; set; }
    public List<UndoRecord> Records { get; } = new();
    public InstallState? CommittedState { get; set; }
    public bool RolledBack { get; set; }
    public bool Committed => CommittedState is not null;
    /// <summary>True when the transaction neither committed nor rolled back (the setup was interrupted).</summary>
    public bool IsOpen => !Committed && !RolledBack;
}

/// <summary>
/// Append-only transaction log (JSON lines, flushed to disk after every line). A transaction is open until a commit line
/// (which carries the final install state) or a rollback line is written.
/// </summary>
public sealed class TransactionJournal
{
    private static readonly JsonSerializerOptions Options = new();
    private readonly string _path;

    public TransactionJournal(string path) => _path = path;

    public static string PathFor(string stateDir) => Path.Combine(stateDir, "journal.jsonl");

    public void Begin(JournalHeader header) => Write(new JsonObject { ["type"] = "begin", ["header"] = JsonSerializer.SerializeToNode(header, Options) }, truncate: true);
    public void Append(UndoRecord record) => Write(new JsonObject { ["type"] = "undo", ["record"] = JsonSerializer.SerializeToNode(record, Options) });
    public void Commit(InstallState state) => Write(new JsonObject { ["type"] = "commit", ["state"] = JsonSerializer.SerializeToNode(state, Options) });
    public void MarkRolledBack() => Write(new JsonObject { ["type"] = "rolledback" });

    private void Write(JsonObject line, bool truncate = false)
    {
        using var stream = new FileStream(_path, truncate ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.WriteLine(line.ToJsonString());
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Reads a journal; returns null when the file does not exist. A truncated last line (crash while writing) is ignored.</summary>
    public static JournalContents? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var result = new JournalContents();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch (JsonException) { continue; }
            switch (node?["type"]?.GetValue<string>())
            {
                case "begin": result.Header = node["header"].Deserialize<JournalHeader>(Options); break;
                case "undo": if (node["record"].Deserialize<UndoRecord>(Options) is { } r) result.Records.Add(r); break;
                case "commit": result.CommittedState = node["state"].Deserialize<InstallState>(Options); break;
                case "rolledback": result.RolledBack = true; break;
            }
        }
        return result;
    }
}

/// <summary>Reads and writes the install state in the .paic folder.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public StateStore(string stateDir) => StateDir = stateDir;

    public string StateDir { get; }
    public string StatePath => Path.Combine(StateDir, "state.json");
    public string BackupDir => Path.Combine(StateDir, "backup");

    public static string StateDirFor(string installDir) => Path.Combine(installDir, ".paic");

    public InstallState? Load()
    {
        if (!File.Exists(StatePath)) return null;
        try { return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(StatePath), Options); }
        catch (JsonException ex) { throw new EngineException($"The install state at '{StatePath}' is damaged: {ex.Message}", ex); }
    }

    public void Save(InstallState state)
    {
        Directory.CreateDirectory(StateDir);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options), new System.Text.UTF8Encoding(false));
        File.Move(tmp, StatePath, overwrite: true);
    }
}

/// <summary>Install history kept outside the install folder so it survives uninstall.</summary>
public static class HistoryLog
{
    private static readonly JsonSerializerOptions Options = new();

    public static void Append(string directory, HistoryEntry entry)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "history.jsonl"), JsonSerializer.Serialize(entry, Options) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* history is best effort */ }
    }

    public static IReadOnlyList<HistoryEntry> Read(string directory)
    {
        var path = Path.Combine(directory, "history.jsonl");
        if (!File.Exists(path)) return Array.Empty<HistoryEntry>();
        var list = new List<HistoryEntry>();
        foreach (var line in File.ReadLines(path))
        {
            try { if (JsonSerializer.Deserialize<HistoryEntry>(line, Options) is { } e) list.Add(e); } catch (JsonException) { }
        }
        return list;
    }
}
