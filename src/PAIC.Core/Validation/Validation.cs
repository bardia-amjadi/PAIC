namespace PAIC.Core.Validation;

/// <summary>Severity of a validation finding.</summary>
public enum ValidationSeverity { Info, Warning, Error }

/// <summary>A single validation finding.</summary>
public sealed record ValidationIssue(string Code, ValidationSeverity Severity, string Message, string? Location = null)
{
    public override string ToString() =>
        $"{Severity} {Code}: {Message}" + (Location is null ? "" : $" ({Location})");
}

/// <summary>The collected result of a validation run.</summary>
public sealed class ValidationReport
{
    private readonly List<ValidationIssue> _issues = new();

    public IReadOnlyList<ValidationIssue> Issues => _issues;
    public bool HasErrors => _issues.Any(i => i.Severity == ValidationSeverity.Error);
    public int ErrorCount => _issues.Count(i => i.Severity == ValidationSeverity.Error);
    public int WarningCount => _issues.Count(i => i.Severity == ValidationSeverity.Warning);

    public void Add(ValidationIssue issue) => _issues.Add(issue);
    public void AddRange(IEnumerable<ValidationIssue> issues) => _issues.AddRange(issues);
}

/// <summary>A single, independently testable validation rule.</summary>
public interface IValidationRule<in T>
{
    string Id { get; }
    IEnumerable<ValidationIssue> Evaluate(T target);
}

/// <summary>Runs a set of rules; a rule that throws is reported as an error instead of aborting validation.</summary>
public sealed class Validator<T>
{
    private readonly IReadOnlyList<IValidationRule<T>> _rules;

    public Validator(IEnumerable<IValidationRule<T>> rules) => _rules = rules.ToList();

    public ValidationReport Validate(T target)
    {
        var report = new ValidationReport();
        foreach (var rule in _rules)
        {
            try
            {
                report.AddRange(rule.Evaluate(target).ToList());
            }
            catch (Exception ex)
            {
                report.Add(new ValidationIssue("PAIC0000", ValidationSeverity.Error,
                    $"Validation rule '{rule.Id}' failed: {ex.Message}"));
            }
        }
        return report;
    }
}
