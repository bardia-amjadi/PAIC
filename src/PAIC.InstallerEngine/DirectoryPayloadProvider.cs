using PAIC.Core.IO;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerEngine;

/// <summary>Payload read straight from a project folder: every asset listed in the project. Used for development and sandbox test installs.</summary>
public sealed class DirectoryPayloadProvider : IPayloadProvider
{
    private readonly string _root;
    private readonly Dictionary<string, PayloadFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public DirectoryPayloadProvider(PaicProject project, string projectDirectory)
    {
        _root = projectDirectory;
        foreach (var asset in project.Assets)
        {
            var path = PathSafety.ToProjectPath(asset.Path);
            var full = PathSafety.ResolveUnder(projectDirectory, path);
            if (!File.Exists(full)) throw new EngineException($"Payload file '{path}' is missing from the project folder.");
            var hash = string.IsNullOrWhiteSpace(asset.Sha256) ? FileHashing.ComputeSha256(full) : asset.Sha256.ToLowerInvariant();
            _files[path] = new PayloadFile(path, new FileInfo(full).Length, hash);
        }
    }

    public IReadOnlyList<PayloadFile> Files => _files.Values.ToList();

    public Stream Open(string path)
    {
        if (!_files.ContainsKey(PathSafety.ToProjectPath(path))) throw new EngineException($"'{path}' is not part of the payload.");
        return File.OpenRead(PathSafety.ResolveUnder(_root, PathSafety.ToProjectPath(path)));
    }
}

/// <summary>In-memory payload (used by tests and to wrap packaged content).</summary>
public sealed class MemoryPayloadProvider : IPayloadProvider
{
    private readonly Dictionary<string, byte[]> _data = new(StringComparer.OrdinalIgnoreCase);

    public MemoryPayloadProvider(IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (path, data) in files) _data[PathSafety.ToProjectPath(path)] = data;
    }

    public IReadOnlyList<PayloadFile> Files =>
        _data.Select(kv => new PayloadFile(kv.Key, kv.Value.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(kv.Value)).ToLowerInvariant())).ToList();

    public Stream Open(string path) =>
        _data.TryGetValue(PathSafety.ToProjectPath(path), out var d) ? new MemoryStream(d, writable: false) : throw new EngineException($"'{path}' is not part of the payload.");
}
