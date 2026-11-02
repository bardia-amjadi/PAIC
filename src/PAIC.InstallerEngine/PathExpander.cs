using System.Text.RegularExpressions;

namespace PAIC.InstallerEngine;

/// <summary>Expands {Variable} tokens and %ENVIRONMENT% references in operation parameters.</summary>
public sealed class PathExpander
{
    private static readonly Regex Token = new(@"\{([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private readonly IReadOnlyDictionary<string, string> _variables;
    private readonly Func<string, string> _expandEnvironment;

    public PathExpander(IReadOnlyDictionary<string, string> variables, Func<string, string> expandEnvironment)
    {
        _variables = variables;
        _expandEnvironment = expandEnvironment;
    }

    /// <summary>Project files use Windows separators; convert them when running on a platform that uses another one (tests, tooling).</summary>
    public static string ToLocalPath(string path) => Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', Path.DirectorySeparatorChar);

    public IEnumerable<string> UnknownTokens(string? text) =>
        string.IsNullOrEmpty(text)
            ? Enumerable.Empty<string>()
            : Token.Matches(text).Select(m => m.Groups[1].Value).Where(n => !_variables.ContainsKey(n)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="EngineException">A token has no value.</exception>
    public string Expand(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var unknown = UnknownTokens(text).ToList();
        if (unknown.Count > 0) throw new EngineException($"Unknown variable {{{unknown[0]}}} in '{text}'.");
        var withTokens = Token.Replace(text, m => _variables[m.Groups[1].Value]);
        return _expandEnvironment(withTokens);
    }
}
