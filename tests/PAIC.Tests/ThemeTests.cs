using PAIC.Core.Validation;
using PAIC.ThemeEngine;
using Xunit;

namespace PAIC.Tests;

public class ThemeColorTests
{
    [Fact]
    public void ContrastRatio_BlackOnWhite_Is21() =>
        Assert.Equal(21.0, ThemeColor.ContrastRatio("#000000", "#FFFFFF")!.Value, 1);

    [Fact]
    public void ContrastRatio_SameColour_Is1() =>
        Assert.Equal(1.0, ThemeColor.ContrastRatio("#336699", "#336699")!.Value, 3);

    [Theory]
    [InlineData("#FFFFFF", true)]
    [InlineData("#80FFFFFF", true)]
    [InlineData("FFFFFF", false)]
    [InlineData("#FFF", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("", false)]
    public void TryParse(string hex, bool ok) => Assert.Equal(ok, ThemeColor.TryParse(hex, out _, out _, out _, out _));

    [Fact]
    public void ContrastRatio_InvalidColour_IsNull() => Assert.Null(ThemeColor.ContrastRatio("nope", "#FFFFFF"));
}

public class ThemeValidationTests
{
    private static ValidationReport Validate(ThemeDefinition t) => ThemeValidator.CreateDefault().Validate(t);

    [Fact]
    public void BuiltInThemes_HaveNoErrorsOrWarnings()
    {
        foreach (var theme in BuiltInThemes.All())
        {
            var report = Validate(theme);
            Assert.True(report.Issues.Count == 0, $"{theme.Id}: {string.Join("; ", report.Issues)}");
        }
    }

    [Fact]
    public void BuiltInThemeIds_AreUnique() =>
        Assert.Equal(BuiltInThemes.All().Count, BuiltInThemes.All().Select(t => t.Id).Distinct().Count());

    [Fact]
    public void MissingAndInvalidColours_AreErrors()
    {
        var t = BuiltInThemes.FluentLight();
        t.Colors.Remove(ThemeTokens.Accent);
        t.Colors[ThemeTokens.Border] = "red";
        var codes = Validate(t).Issues.Select(i => i.Code).ToList();
        Assert.Contains("PAIC2003", codes);
        Assert.Contains("PAIC2004", codes);
    }

    [Fact]
    public void LowContrast_IsWarningThenError()
    {
        var t = BuiltInThemes.FluentLight();
        t.Colors[ThemeTokens.TextPrimary] = "#8A8A8A"; // ~3.1:1 on #F3F3F3
        Assert.Contains(Validate(t).Issues, i => i.Code == "PAIC2011" && i.Severity == ValidationSeverity.Warning);
        t.Colors[ThemeTokens.TextPrimary] = "#F3F3F3";
        Assert.Contains(Validate(t).Issues, i => i.Code == "PAIC2010" && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void BadIdAndRanges_AreErrors()
    {
        var t = BuiltInThemes.FluentLight();
        t.Id = "Bad Id";
        t.Typography.BodySize = 3;
        t.Shape.CornerRadius = 99;
        var codes = Validate(t).Issues.Select(i => i.Code).ToList();
        Assert.Contains("PAIC2001", codes);
        Assert.Contains("PAIC2021", codes);
        Assert.Contains("PAIC2030", codes);
    }

    [Fact]
    public void UnknownToken_IsWarning()
    {
        var t = BuiltInThemes.FluentLight();
        t.Colors["extra"] = "#000000";
        var report = Validate(t);
        Assert.False(report.HasErrors);
        Assert.Contains(report.Issues, i => i.Code == "PAIC2005");
    }
}

public class ThemeSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesTheme()
    {
        var t = BuiltInThemes.Midnight();
        t.Typography.BodySize = 15;
        var json = ThemeSerializer.Serialize(t);
        Assert.Contains("\"format\": \"paic-theme\"", json);
        var back = ThemeSerializer.Deserialize(json);
        Assert.Equal(t.Id, back.Id);
        Assert.Equal(ThemeMode.Dark, back.Mode);
        Assert.Equal(15, back.Typography.BodySize);
        Assert.Equal(t.Colors[ThemeTokens.Accent], back.Colors[ThemeTokens.Accent]);
    }

    [Fact]
    public void WrongFormat_NewerVersion_AndInvalidTheme_AreRejected()
    {
        Assert.Throws<ThemeException>(() => ThemeSerializer.Deserialize("not json"));
        Assert.Throws<ThemeException>(() => ThemeSerializer.Deserialize("{\"format\":\"other\"}"));
        var json = ThemeSerializer.Serialize(BuiltInThemes.FluentLight()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 9");
        Assert.Throws<ThemeException>(() => ThemeSerializer.Deserialize(json));
        var bad = BuiltInThemes.FluentLight();
        bad.Colors[ThemeTokens.Accent] = "nope";
        Assert.Throws<ThemeException>(() => ThemeSerializer.Deserialize(ThemeSerializer.Serialize(bad)));
    }
}

public class ThemeLibraryTests
{
    private static ThemeDefinition Custom(string id = "my-theme")
    {
        var t = BuiltInThemes.FluentLight();
        t.Id = id; t.Name = "My theme"; t.Colors[ThemeTokens.Accent] = "#AA3300";
        return t;
    }

    [Fact]
    public void Save_List_Delete()
    {
        using var dir = new TempDir();
        var lib = new ThemeLibrary(Path.Combine(dir.Path, "themes"));
        Assert.Empty(lib.LoadUserThemes());
        lib.Save(Custom());
        Assert.Equal("my-theme", Assert.Single(lib.LoadUserThemes()).Id);
        lib.Delete("my-theme");
        Assert.Empty(lib.LoadUserThemes());
    }

    [Fact]
    public void Save_RejectsBuiltInIdsAndInvalidThemes()
    {
        using var dir = new TempDir();
        var lib = new ThemeLibrary(dir.Path);
        Assert.Throws<ThemeException>(() => lib.Save(BuiltInThemes.FluentLight()));
        var bad = Custom(); bad.Colors.Remove(ThemeTokens.Surface);
        Assert.Throws<ThemeException>(() => lib.Save(bad));
    }

    [Fact]
    public void Export_ThenImport_IntoAnotherLibrary()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var file = Path.Combine(a.Path, "x" + ThemeSerializer.FileExtension);
        new ThemeLibrary(a.Path).Export(Custom(), file);

        var other = new ThemeLibrary(Path.Combine(b.Path, "lib"));
        var imported = other.Import(file);
        Assert.Equal("my-theme", imported.Id);
        Assert.Equal("#AA3300", Assert.Single(other.LoadUserThemes()).Colors[ThemeTokens.Accent]);
    }

    [Fact]
    public void Import_OfBuiltInId_IsRenamed()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "light.paictheme");
        File.WriteAllText(file, ThemeSerializer.Serialize(BuiltInThemes.FluentLight()));
        var imported = new ThemeLibrary(Path.Combine(dir.Path, "lib")).Import(file);
        Assert.Equal("fluent-light-imported", imported.Id);
    }

    [Fact]
    public void Delete_RejectsPathTraversalIds()
    {
        using var dir = new TempDir();
        Assert.Throws<ThemeException>(() => new ThemeLibrary(dir.Path).Delete("../evil"));
    }
}
