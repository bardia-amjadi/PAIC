using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PAIC.ProjectSystem.Recent;

public sealed class RecentProject
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTimeOffset LastOpenedUtc { get; set; }
}

public interface IRecentProjectsStore
{
    /// <summary>Returns recent projects, newest first, omitting files that no longer exist.</summary>
    IReadOnlyList<RecentProject> Load();
    void Add(string path, string name);
    void Remove(string path);
}

public sealed class RecentProjectsStore : IRecentProjectsStore
{
    private const int MaxItems = 15;
    private readonly string _file;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    public RecentProjectsStore(string file, ILogger<RecentProjectsStore>? logger = null)
    {
        _file = file;
        _logger = logger;
    }

    public IReadOnlyList<RecentProject> Load()
    {
        lock (_gate)
        {
            return ReadAll().Where(r => File.Exists(r.Path)).OrderByDescending(r => r.LastOpenedUtc).ToList();
        }
    }

    public void Add(string path, string name)
    {
        lock (_gate)
        {
            var full = System.IO.Path.GetFullPath(path);
            var items = ReadAll().Where(r => !string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)).ToList();
            items.Add(new RecentProject { Path = full, Name = name, LastOpenedUtc = DateTimeOffset.UtcNow });
            WriteAll(items.OrderByDescending(r => r.LastOpenedUtc).Take(MaxItems).ToList());
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            var full = System.IO.Path.GetFullPath(path);
            WriteAll(ReadAll().Where(r => !string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)).ToList());
        }
    }

    private List<RecentProject> ReadAll()
    {
        try
        {
            if (!File.Exists(_file)) return new();
            return JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(_file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not read recent projects from {File}", _file);
            return new();
        }
    }

    private void WriteAll(List<RecentProject> items)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not write recent projects to {File}", _file);
        }
    }
}
