using System.Text;
using PAIC.Core.IO;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;

namespace PAIC.TemplateEngine;

public interface ITemplateService
{
    /// <summary>Built-in templates followed by user templates.</summary>
    IReadOnlyList<TemplateEntry> List();
    /// <summary>Validates a .paictemplate file and adds it to the user library.</summary>
    TemplateEntry Import(string file);
    void Export(TemplateEntry template, string file);
    /// <summary>Captures an open project (settings, pages, themes, assets) as a new user template.</summary>
    TemplateEntry SaveFromProject(PaicProject project, string projectDirectory, string name, string description, string category);
    void Delete(string id);
    /// <summary>Returns a fresh copy of the template's project, personalised for <paramref name="projectName"/>, plus its asset files.</summary>
    (PaicProject Project, IReadOnlyDictionary<string, byte[]> Files) Prepare(TemplateEntry template, string projectName);
}

public sealed class TemplateService : ITemplateService
{
    public const string ProductNameToken = "{ProductName}";
    private readonly string _directory;

    public TemplateService(string userTemplateDirectory) => _directory = userTemplateDirectory;

    public IReadOnlyList<TemplateEntry> List()
    {
        var list = new List<TemplateEntry>(BuiltInTemplates.All());
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*" + TemplateSerializer.FileExtension).OrderBy(f => f))
            {
                try { list.Add(TemplateSerializer.Deserialize(File.ReadAllText(file))); }
                catch (Exception ex) when (ex is TemplateException or IOException or UnauthorizedAccessException) { /* skip unreadable or invalid */ }
            }
        }
        return list;
    }

    public TemplateEntry Import(string file)
    {
        string json;
        try { json = File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TemplateException($"Could not read the template file: {ex.Message}", ex);
        }
        var template = TemplateSerializer.Deserialize(json);
        var id = template.Info.Id;
        if (BuiltInTemplates.All().Any(t => t.Info.Id == id) || File.Exists(PathFor(id)))
        {
            template.Info.Id = UniqueId(id + "-imported");
            template.Info.Name += " (imported)";
        }
        Write(template);
        return template;
    }

    public void Export(TemplateEntry template, string file)
    {
        try { WriteAtomic(file, TemplateSerializer.Serialize(template)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TemplateException($"Could not export the template: {ex.Message}", ex);
        }
    }

    public TemplateEntry SaveFromProject(PaicProject project, string projectDirectory, string name, string description, string category)
    {
        var id = Slug(name);
        var info = new TemplateInfo { Id = id, Name = name.Trim(), Description = description.Trim(), Category = string.IsNullOrWhiteSpace(category) ? "Custom" : category.Trim() };
        TemplateSerializer.ValidateInfo(info);
        if (BuiltInTemplates.All().Any(t => t.Info.Id == id) || File.Exists(PathFor(id)))
            throw new TemplateException($"A template named '{name}' already exists. Choose a different name.");

        var copy = ProjectSerializer.Default.Deserialize(ProjectSerializer.Default.Serialize(project));
        var originalName = copy.Application.ProductName;
        if (!string.IsNullOrWhiteSpace(originalName))
        {
            copy.Installation.DefaultInstallDirectory = copy.Installation.DefaultInstallDirectory.Replace(originalName, ProductNameToken);
            copy.Application.ProductName = ProductNameToken;
        }

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var asset in copy.Assets)
        {
            var path = PathFor(projectDirectory, asset.Path);
            if (!File.Exists(path)) throw new TemplateException($"Asset '{asset.Path}' is missing on disk; fix the project before saving it as a template.");
            var data = File.ReadAllBytes(path);
            total += data.Length;
            if (total > TemplateSerializer.MaxTotalAssetBytes) throw new TemplateException("The project's assets are larger than 50 MB, which is the limit for templates.");
            files[PathSafety.ToProjectPath(asset.Path)] = data;
        }

        var template = new TemplateEntry { Info = info, Project = copy, Files = files, IsBuiltIn = false };
        Write(template);
        return template;
    }

    public void Delete(string id)
    {
        if (BuiltInTemplates.All().Any(t => t.Info.Id == id)) throw new TemplateException("Built-in templates cannot be deleted.");
        try { var path = PathFor(id); if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TemplateException($"Could not delete the template: {ex.Message}", ex);
        }
    }

    public (PaicProject Project, IReadOnlyDictionary<string, byte[]> Files) Prepare(TemplateEntry template, string projectName)
    {
        ProjectNaming.EnsureValid(projectName);
        var project = ProjectSerializer.Default.Deserialize(ProjectSerializer.Default.Serialize(template.Project));
        project.Metadata = new ProjectMetadata { Name = projectName };
        project.Metadata.GeneratorVersion = typeof(TemplateService).Assembly.GetName().Version?.ToString() ?? "";
        project.Application.ProductName = projectName;
        project.Installation.DefaultInstallDirectory = project.Installation.DefaultInstallDirectory.Replace(ProductNameToken, projectName);
        foreach (var op in project.Operations)
            foreach (var key in op.Parameters.Keys.ToList())
                op.Parameters[key] = op.Parameters[key].Replace(ProductNameToken, projectName);

        var files = template.Files.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        return (project, files);
    }

    // ---------- helpers ----------

    private string PathFor(string id)
    {
        if (!PAIC.ThemeEngine.ThemeValidator.IdPattern.IsMatch(id)) throw new TemplateException("Invalid template id.");
        return Path.Combine(_directory, id + TemplateSerializer.FileExtension);
    }

    private static string PathFor(string projectDirectory, string relative) => PathSafety.ResolveUnder(projectDirectory, relative);

    private string UniqueId(string baseId)
    {
        var id = baseId;
        for (var i = 2; BuiltInTemplates.All().Any(t => t.Info.Id == id) || File.Exists(PathFor(id)); i++) id = $"{baseId}-{i}";
        return id;
    }

    private void Write(TemplateEntry template)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            WriteAtomic(PathFor(template.Info.Id), TemplateSerializer.Serialize(template));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TemplateException($"Could not save the template: {ex.Message}", ex);
        }
    }

    private static void WriteAtomic(string file, string content)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
    }

    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
