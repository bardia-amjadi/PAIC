namespace PAIC.ThemeEngine;

/// <summary>User-level theme library (reusable across projects) plus the built-in themes.</summary>
public interface IThemeLibrary
{
    IReadOnlyList<ThemeDefinition> BuiltIn { get; }
    IReadOnlyList<ThemeDefinition> LoadUserThemes();
    void Save(ThemeDefinition theme);
    void Delete(string id);
    /// <summary>Reads and validates a .paictheme file and stores it in the library.</summary>
    ThemeDefinition Import(string file);
    void Export(ThemeDefinition theme, string file);
}

public sealed class ThemeLibrary : IThemeLibrary
{
    private readonly string _directory;

    public ThemeLibrary(string directory) => _directory = directory;

    public IReadOnlyList<ThemeDefinition> BuiltIn { get; } = BuiltInThemes.All();

    public IReadOnlyList<ThemeDefinition> LoadUserThemes()
    {
        var result = new List<ThemeDefinition>();
        if (!Directory.Exists(_directory)) return result;
        foreach (var file in Directory.EnumerateFiles(_directory, "*" + ThemeSerializer.FileExtension))
        {
            try { result.Add(ThemeSerializer.Deserialize(File.ReadAllText(file))); }
            catch (Exception ex) when (ex is ThemeException or IOException or UnauthorizedAccessException) { /* skip unreadable or invalid files */ }
        }
        return result.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Save(ThemeDefinition theme)
    {
        var report = ThemeValidator.CreateDefault().Validate(theme);
        if (report.HasErrors) throw new ThemeException("The theme has validation errors and cannot be saved to the library.");
        if (BuiltInThemes.IsBuiltInId(theme.Id)) throw new ThemeException($"'{theme.Id}' is a built-in theme id; duplicate the theme with another name first.");
        try
        {
            Directory.CreateDirectory(_directory);
            WriteAtomic(PathFor(theme.Id), ThemeSerializer.Serialize(theme));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ThemeException($"Could not save the theme: {ex.Message}", ex);
        }
    }

    public void Delete(string id)
    {
        try { var path = PathFor(id); if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ThemeException($"Could not delete the theme: {ex.Message}", ex);
        }
    }

    public ThemeDefinition Import(string file)
    {
        string json;
        try { json = File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ThemeException($"Could not read the theme file: {ex.Message}", ex);
        }
        var theme = ThemeSerializer.Deserialize(json);
        if (BuiltInThemes.IsBuiltInId(theme.Id))
        {
            theme.Id += "-imported";
            theme.Name += " (imported)";
        }
        Save(theme);
        return theme;
    }

    public void Export(ThemeDefinition theme, string file)
    {
        try { WriteAtomic(file, ThemeSerializer.Serialize(theme)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ThemeException($"Could not export the theme: {ex.Message}", ex);
        }
    }

    private string PathFor(string id)
    {
        if (!ThemeValidator.IdPattern.IsMatch(id)) throw new ThemeException("Invalid theme id.");
        return Path.Combine(_directory, id + ThemeSerializer.FileExtension);
    }

    private static void WriteAtomic(string file, string content)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
    }
}
