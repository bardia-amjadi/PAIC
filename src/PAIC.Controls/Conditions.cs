using System.Text.RegularExpressions;

namespace PAIC.Controls;

public static class ConditionEvaluator
{
    private static readonly string[] Truthy = { "true", "1", "yes", "on" };

    public static bool IsTruthy(string? value) => value is not null && Truthy.Contains(value.Trim().ToLowerInvariant());

    /// <summary>A null condition is always satisfied.</summary>
    public static bool Evaluate(ConditionDefinition? c, IReadOnlyDictionary<string, string> variables)
    {
        if (c is null) return true;
        variables.TryGetValue(c.Variable ?? "", out var raw);
        var v = raw ?? "";
        return c.Operator switch
        {
            ConditionOperator.IsTrue => IsTruthy(v),
            ConditionOperator.IsFalse => !IsTruthy(v),
            ConditionOperator.Equals => string.Equals(v, c.Value, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => !string.Equals(v, c.Value, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Contains => v.Contains(c.Value ?? "", StringComparison.OrdinalIgnoreCase),
            ConditionOperator.IsEmpty => string.IsNullOrWhiteSpace(v),
            ConditionOperator.IsNotEmpty => !string.IsNullOrWhiteSpace(v),
            _ => true
        };
    }
}

/// <summary>Replaces {Name} tokens with variable values. Unknown tokens are left untouched.</summary>
public static class TextTemplating
{
    private static readonly Regex Token = new(@"\{([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public static string Resolve(string? text, IReadOnlyDictionary<string, string> variables) =>
        string.IsNullOrEmpty(text) ? "" : Token.Replace(text, m => variables.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    /// <summary>Names referenced as {Name} in the text.</summary>
    public static IEnumerable<string> References(string? text) =>
        string.IsNullOrEmpty(text) ? Enumerable.Empty<string>() : Token.Matches(text).Select(m => m.Groups[1].Value);
}
