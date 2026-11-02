using PAIC.Core.IO;
using PAIC.Core.Validation;
using Xunit;

namespace PAIC.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData("assets/a.png", true)]
    [InlineData("assets\\sub\\a.png", true)]
    [InlineData("../evil.txt", false)]
    [InlineData("assets/../../evil.txt", false)]
    [InlineData("C:\\Windows\\x.dll", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("assets/a.txt:stream", false)]
    [InlineData("", false)]
    public void IsSafeRelativePath(string path, bool expected) =>
        Assert.Equal(expected, PathSafety.IsSafeRelativePath(path, out _));

    [Fact]
    public void ResolveUnder_KeepsResultInsideRoot()
    {
        using var t = new TempDir();
        var full = PathSafety.ResolveUnder(t.Path, "assets/a.txt");
        Assert.StartsWith(Path.GetFullPath(t.Path), full);
        Assert.Throws<InvalidOperationException>(() => PathSafety.ResolveUnder(t.Path, "../x"));
    }
}

public class ValidatorTests
{
    private sealed class ThrowingRule : IValidationRule<int>
    {
        public string Id => "throws";
        public IEnumerable<ValidationIssue> Evaluate(int target) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void ThrowingRule_IsReportedAsError()
    {
        var report = new Validator<int>(new[] { new ThrowingRule() }).Validate(1);
        Assert.True(report.HasErrors);
        Assert.Contains("throws", report.Issues[0].Message);
    }
}

public class HashingTests
{
    [Fact]
    public async Task Sha256_MatchesKnownValue()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "a.txt");
        await File.WriteAllTextAsync(file, "abc");
        const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.Equal(expected, FileHashing.ComputeSha256(file));
        Assert.Equal(expected, await FileHashing.ComputeSha256Async(file));
    }
}
