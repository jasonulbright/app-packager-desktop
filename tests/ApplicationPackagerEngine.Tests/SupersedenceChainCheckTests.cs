using System.Security;
using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The chain check: the detection of the old application against the new one. It warns and never blocks.</summary>
public sealed class SupersedenceChainCheckTests
{
    private const string Method = """
        <EnhancedDetectionMethod xmlns="http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/AppMgmtSDK/AppMgmtSDK">
          <Settings><File LogicalName="File_1" Path="C:\Vendor" Filter="app.exe" /></Settings>
          <Rule><Expression><Operator>{OP}</Operator><Operands>
            <SettingReference SettingLogicalName="File_1" DataType="Version" Method="Value" />
            <ConstantValue Value="1.0.0" DataType="Version" />
          </Operands></Expression></Rule>
        </EnhancedDetectionMethod>
        """;

    /// <summary>The XML of a deployment type as the site stores it: the detection method is XML text inside an argument.</summary>
    private static string DeploymentTypeXml(string op) =>
        "<AppMgmtDigest><DeploymentType><Installer><DetectAction><Args><Arg Name=\"MethodBody\" Type=\"String\">"
        + SecurityElement.Escape(Method.Replace("{OP}", op)) + "</Arg></Args></DetectAction></Installer></DeploymentType></AppMgmtDigest>";

    private static StageManifest Manifest(string op, string? propertyType = "Version") => new()
    {
        AppName = "New", SoftwareVersion = "2.0.0",
        Detection = new StageDetection { Type = "File", FilePath = "p", FileName = "f", ExpectedValue = "2.0.0", Operator = op, PropertyType = propertyType }
    };

    private static OldApplicationDetection Old(params string[] operators) =>
        new("Old", operators.Select(o => new OldVersionClause("Old - x64", o)).ToList());

    // ---- reading the site ----

    [Theory]
    [InlineData("GreaterEquals")]
    [InlineData("Equals")]
    public void TheVersionOperatorIsReadFromTheDetectionMethodTextInsideTheDeploymentTypeXml(string op) =>
        Assert.Equal([op], ChainCheck.VersionOperators(DeploymentTypeXml(op)));

    [Fact]
    public void ADocumentThatCannotBeReadOrHasNoVersionClauseGivesNoClause()
    {
        Assert.Empty(ChainCheck.VersionOperators(null));
        Assert.Empty(ChainCheck.VersionOperators("<<<not xml"));
        var stringClause = DeploymentTypeXml("Equals").Replace("DataType=&quot;Version&quot;", "DataType=&quot;String&quot;");
        Assert.Empty(ChainCheck.VersionOperators(stringClause));
    }

    [Fact]
    public void TheDetectionScriptOutputIsParsedPerDeploymentType()
    {
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        var output = $"APDXML|{B64("Old - x64")}|{B64(DeploymentTypeXml("GreaterEquals"))}\nnoise\nAPDXML|{B64("Old - x86")}|{B64(DeploymentTypeXml("Equals"))}\n";

        var detection = ChainCheck.ParseDetection("Old", output);

        Assert.Equal([("Old - x64", "GreaterEquals"), ("Old - x86", "Equals")], detection.Clauses.Select(c => (c.DeploymentType, c.Operator)));
    }

    [Fact]
    public void TheDetectionScriptAsksOneApplicationByNameAndEscapesIt()
    {
        var script = ChainCheck.BuildDetectionScript("MCM", null, "O'Neil \"x\"");

        Assert.Contains("Get-CMDeploymentType -ApplicationName 'O''Neil \"x\"' -DisableWildcardHandling", script);
        Assert.Contains("APDXML|{0}|{1}", script);
    }

    // ---- the warnings ----

    [Fact]
    public void ReplaceAndUninstallWithExactWarnsWhenTheOldApplicationStillHasGreaterEquals()
    {
        var warnings = ChainCheck.Evaluate("New", VersionRule.Exact, SupersedenceMode.ReplaceAndUninstall, Manifest("IsEquals"), Old("GreaterEquals"));

        var warning = Assert.Single(warnings);
        Assert.Contains("'Old'", warning);
        Assert.Contains("Old - x64", warning);
        Assert.Contains("uninstall will still be reported as failed", warning);
    }

    [Fact]
    public void AnOldApplicationThatIsAlreadyExactGivesNoWarningForReplaceAndUninstall() =>
        Assert.Empty(ChainCheck.Evaluate("New", VersionRule.Exact, SupersedenceMode.ReplaceAndUninstall, Manifest("IsEquals"), Old("Equals")));

    [Fact]
    public void AnExactOldClauseAgainstANewMinimumIsInconsistent()
    {
        var warnings = ChainCheck.Evaluate("New", VersionRule.Minimum, SupersedenceMode.Replace, Manifest("GreaterEquals"), Old("Equals"));

        Assert.Contains(warnings, w => w.Contains("inconsistent") && w.Contains("Exact version clause"));
    }

    [Fact]
    public void AMinimumOldClauseAgainstANewExactIsInconsistentWhenNothingIsUninstalled()
    {
        var warnings = ChainCheck.Evaluate("New", VersionRule.Exact, SupersedenceMode.Replace, Manifest("IsEquals"), Old("GreaterEquals"));

        Assert.Contains(warnings, w => w.Contains("inconsistent") && w.Contains("GreaterEquals"));
    }

    [Fact]
    public void ANewApplicationThatKeepsGreaterEqualsWarnsUnderReplaceAndUninstallOnly()
    {
        var kept = Manifest("GreaterEquals");

        var under = ChainCheck.Evaluate("New", VersionRule.Exact, SupersedenceMode.ReplaceAndUninstall, kept, Old("Equals"));
        var replace = ChainCheck.Evaluate("New", VersionRule.Exact, SupersedenceMode.Replace, kept, Old("Equals"));

        Assert.Contains(under, w => w.Contains("keeps a GreaterEquals version clause"));
        Assert.DoesNotContain(replace, w => w.Contains("keeps a GreaterEquals"));
    }

    [Fact]
    public void AnExistenceClauseOrAScriptIsNotAVersionClause()
    {
        var existence = Manifest("GreaterEquals", propertyType: "Existence");

        Assert.Empty(ChainCheck.MinimumClauses(existence));
        Assert.Empty(ChainCheck.Evaluate("New", VersionRule.Exact, SupersedenceMode.ReplaceAndUninstall, existence, Old()));
    }

    [Fact]
    public void ACompoundDetectionIsWalkedAndTheDeploymentTypeDetectionsToo()
    {
        var manifest = Manifest("IsEquals");
        manifest.Detection = new StageDetection
        {
            Type = "Compound", Connector = "And",
            Clauses = [new StageDetection { Type = "File", FilePath = "p", FileName = "a", Operator = "GreaterEquals" }]
        };
        manifest.DeploymentTypes = [new StageDeploymentType { NameSuffix = "x86", Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = "k", ValueName = "DisplayVersion", Operator = "GreaterEquals" } }];

        Assert.Equal(2, ChainCheck.MinimumClauses(manifest).Count);
    }
}
