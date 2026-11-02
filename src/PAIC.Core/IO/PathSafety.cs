namespace PAIC.Core.IO;

/// <summary>Helpers that keep project-relative paths from escaping their root (path traversal prevention).</summary>
public static class PathSafety
{
    /// <summary>True when the path is relative, without drive letters, streams or '..' segments.</summary>
    public static bool IsSafeRelativePath(string? path, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(path)) { reason = "Path is empty."; return false; }
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains(':'))
        { reason = "Path contains invalid characters."; return false; }
        if (path.StartsWith('/') || path.StartsWith('\\') || Path.IsPathRooted(path))
        { reason = "Path must be relative."; return false; }
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment == "..") { reason = "Path must not contain '..' segments."; return false; }
        }
        return true;
    }

    /// <summary>Normalises separators to '/' (the form stored in project files).</summary>
    public static string ToProjectPath(string relative) => relative.Replace('\\', '/');

    /// <summary>Combines root and relative and guarantees the result stays inside root.</summary>
    public static string ResolveUnder(string root, string relative)
    {
        if (!IsSafeRelativePath(relative, out var reason))
            throw new InvalidOperationException($"Unsafe path '{relative}': {reason}");
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path '{relative}' escapes its root directory.");
        return full;
    }
}
