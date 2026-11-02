using PAIC.Core.IO;

namespace PAIC.ProjectSystem.Model;

public static class ProjectSchema
{
    /// <summary>Current .paproj schema version. Bump together with a new <c>ISchemaMigration</c>.</summary>
    public const int CurrentVersion = 1;
}

public static class ProjectLayout
{
    public const string FileExtension = ".paproj";
    public const string AssetsFolderName = "assets";

    public static string AssetsDirectory(string projectFilePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!, AssetsFolderName);
}

/// <summary>Base class for errors that are safe to show to the user.</summary>
public class ProjectException : PAIC.Core.UserFacingException
{
    public ProjectException(string message) : base(message) { }
    public ProjectException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ProjectFormatException : ProjectException
{
    public ProjectFormatException(string message) : base(message) { }
    public ProjectFormatException(string message, Exception inner) : base(message, inner) { }
}

public static class ProjectNaming
{
    private static readonly string[] Reserved =
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public static bool IsValidName(string? name, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(name)) { reason = "Enter a project name."; return false; }
        if (name.Length > 100) { reason = "Name must be 100 characters or fewer."; return false; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { reason = "Name contains characters that are not allowed in file names."; return false; }
        if (name != name.Trim() || name.EndsWith('.')) { reason = "Name must not start or end with spaces or end with a dot."; return false; }
        if (Reserved.Contains(name.ToUpperInvariant())) { reason = "Name is reserved by Windows."; return false; }
        return true;
    }

    public static void EnsureValid(string? name)
    {
        if (!IsValidName(name, out var reason)) throw new ProjectException(reason!);
    }
}
