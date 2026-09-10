using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Applications whose ARP DisplayVersion carries a build suffix the vendor
/// version does not. A Version comparison against such a value is false even
/// when the right release is installed, so the detection compares the string
/// prefix instead.
/// </summary>
public class PrefixVersionDetectionTests
{
    [Theory]
    [InlineData("postgresql13.json", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 13")]
    [InlineData("postgresql14.json", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 14")]
    [InlineData("postgresql15.json", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 15")]
    [InlineData("postgresql16.json", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 16")]
    [InlineData("postgresql17.json", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 17")]
    [InlineData("winrar.json", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WinRAR archiver")]
    public void TheArpDisplayVersionIsComparedAsAStringPrefix(string fileName, string key)
    {
        var detection = DefinitionFixture.Load(fileName).Detection;

        Assert.Equal("RegistryKeyValue", detection.Type);
        Assert.Equal("HKLM", detection.RegistryHive);
        Assert.Equal(key, detection.RegistryKeyTemplate);
        Assert.Equal("DisplayVersion", detection.ValueName);
        Assert.Equal("String", detection.PropertyType);
        Assert.Equal("BeginsWith", detection.Operator);
        Assert.True(detection.Is64Bit);
    }

    /// <summary>
    /// The staged expectation is the short vendor version and the installed ARP value
    /// carries an extra part. A detection compares the installed value against the
    /// expectation, so the ordering comparison reads 7.23.0 against 7.23 and matches.
    /// The prefix rule is upstream's shape for these six apps; it is not a repair for a
    /// WinRAR release the old rule missed.
    /// </summary>
    [Fact]
    public void TheOrderingComparisonWouldAlsoHaveMatchedTheInstalledWinRarRelease()
    {
        var def = DefinitionFixture.Load("winrar.json");
        def.Detection.PropertyType = "Version";
        def.Detection.Operator = "GreaterEquals";

        var staged = StagingService.BuildDetection(def, null, "7.23", "723");

        Assert.Equal("7.23", staged.ExpectedValue);
        Assert.True(Version.Parse("7.23.0") >= Version.Parse(staged.ExpectedValue!));
        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.CompareValue("7.23.0", staged));
    }

    /// <summary>
    /// The EnterpriseDB build suffix is what an ordering comparison cannot survive: the
    /// installed value is not a version at all, so a Version rule has nothing to compare
    /// and the prefix rule is what actually reads it.
    /// </summary>
    [Fact]
    public void ThePostgresArpValueIsNotAVersionAndOnlyThePrefixRuleReadsIt()
    {
        Assert.False(Version.TryParse("17.11-1", out _));

        var def = DefinitionFixture.Load("postgresql17.json");
        var staged = StagingService.BuildDetection(def, null, "17.11");

        Assert.Equal("17.11", staged.ExpectedValue);
        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.CompareValue("17.11-1", staged));

        // The same reading under an ordering comparison cannot be evaluated.
        var ordering = StagingService.BuildDetection(def, null, "17.11");
        ordering.PropertyType = "Version";
        ordering.Operator = "GreaterEquals";
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.CompareValue("17.11-1", ordering));
    }

    [Fact]
    public void ThePrefixIsComparedAgainstTheDisplayedVersion()
    {
        var def = DefinitionFixture.Load("winrar.json");

        var detection = StagingService.BuildDetection(def, null, "7.23.0", "723");

        Assert.Equal("7.23.0", detection.ExpectedValue);
    }

    /// <summary>An equality rule still reads the value the installer wrote.</summary>
    [Fact]
    public void AnEqualityRuleKeepsTheInstallerValue()
    {
        var def = DefinitionFixture.Load("winrar.json");
        def.Detection.Operator = "IsEquals";

        var detection = StagingService.BuildDetection(def, null, "7.23.0", "7.23.0.1");

        Assert.Equal("7.23.0.1", detection.ExpectedValue);
    }

    [Fact]
    public void TheGeneratedClauseCarriesTheStringPrefixComparison()
    {
        var manifest = new StageManifest
        {
            AppName = "PostgreSQL 17",
            Publisher = "PostgreSQL Global Development Group",
            SoftwareVersion = "17.6",
            Detection = new StageDetection
            {
                Type = "RegistryKeyValue",
                RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 17",
                ValueName = "DisplayVersion",
                PropertyType = "String",
                Operator = "BeginsWith",
                ExpectedValue = "17.6",
                Is64Bit = true
            }
        };

        var script = MecmTarget.BuildPackageScript(manifest, manifest.AppName,
            @"\server\share\Apps\PostgreSQL\PostgreSQL 17\17.6", "P01", "comment",
            15, 30, ConditionTemplateService.Defaults());

        Assert.Contains(
            @"New-CMDetectionClauseRegistryKeyValue -Hive LocalMachine -KeyName 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PostgreSQL 17' -ValueName 'DisplayVersion' -PropertyType String -Value -ExpressionOperator BeginsWith -ExpectedValue '17.6' -Is64Bit",
            script);
    }
}
