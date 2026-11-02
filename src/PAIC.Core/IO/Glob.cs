using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace PAIC.Core.IO;

/// <summary>Case-insensitive glob matching on '/' separated paths: '*' (no separators), '**' (anything), '?' (one character).</summary>
public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public static bool HasWildcards(string pattern) => pattern.IndexOfAny(new[] { '*', '?' }) >= 0;

    public static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    public static bool IsMatch(string pattern, string path) =>
        Cache.GetOrAdd(Normalize(pattern), Build).IsMatch(Normalize(path));

    /// <summary>The directory part before the first wildcard segment (for a pattern without wildcards: the parent of the file).</summary>
    public static string BaseDirectory(string pattern)
    {
        var segments = Normalize(pattern).Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var firstWildcard = segments.FindIndex(s => HasWildcards(s));
        var count = firstWildcard >= 0 ? firstWildcard : Math.Max(0, segments.Count - 1);
        return string.Join('/', segments.Take(count));
    }

    private static Regex Build(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                if (i + 2 < pattern.Length && pattern[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 2; }
                else { sb.Append(".*"); i += 1; }
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
