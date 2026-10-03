using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Parse diagnostics through Windows PowerShell 5.1: line, column and message as the
/// editors list them, the STDOUT verdict for a detection script, and no execution of
/// anything in the script.
/// </summary>
public sealed class PowerShellScriptInspectorTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task AParseErrorIsReportedWithLineColumnAndMessage()
    {
        var inspection = await PowerShellScriptInspector.InspectAsync("$a = 1\r\nif ($a -eq 1 {\r\n  Write-Output 'x'\r\n}");

        Assert.Null(inspection.Failure);
        Assert.NotEmpty(inspection.Errors);
        Assert.Matches(@"^Line \d+, column \d+: .+", inspection.Diagnostics[0]);
        Assert.Equal(2, inspection.Errors[0].Line);
    }

    [Fact]
    public async Task ACleanScriptHasNoDiagnostics()
    {
        var inspection = await PowerShellScriptInspector.InspectAsync("$x = 'a'\r\nWrite-Output $x\r\nexit 0");

        Assert.Empty(inspection.Diagnostics);
        Assert.True(inspection.WritesOutput);
    }

    [Theory]
    [InlineData("'Installed'\r\nexit 0", true)]
    [InlineData("if (Test-Path 'C:\\x') { 'Installed' }\r\nexit 0", false)]
    [InlineData("Write-Host 'Installed'", true)]
    [InlineData("echo 'Installed'", true)]
    [InlineData("if (Test-Path 'C:\\x') { exit 0 }\r\nexit 1", false)]
    [InlineData("$found = Test-Path 'C:\\x'\r\nexit 0", false)]
    public async Task TheStdoutVerdictFollowsTheWritersAndBareExpressions(string script, bool writes)
    {
        Assert.Equal(writes, (await PowerShellScriptInspector.InspectAsync(script)).WritesOutput);
    }

    [Fact]
    public async Task ATopLevelSideEffectIsNeverExecuted()
    {
        var marker = Path.Combine(_t.NewFolder("side-effect"), "ran.txt");
        var script = $"Set-Content -LiteralPath '{marker}' -Value 'ran'\r\n" +
                     $"[System.IO.File]::WriteAllText('{marker}', 'ran')\r\n" +
                     $"$(New-Item -ItemType File -Path '{marker}' -Force)\r\n" +
                     "Write-Output 'Installed'";

        var inspection = await PowerShellScriptInspector.InspectAsync(script);

        Assert.Empty(inspection.Diagnostics);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void TheScriptTextNeverReachesTheCommandLine()
    {
        var command = PowerShellScriptInspector.InspectorCommand(@"C:\Temp\it's.ps1");

        Assert.Contains(@"ReadAllText('C:\Temp\it''s.ps1'", command);
        Assert.Contains("[System.Management.Automation.Language.Parser]::ParseInput", command);
        Assert.DoesNotContain("Invoke-Expression", command);
        Assert.DoesNotContain("ScriptBlock]::Create", command);
    }

    [Fact]
    public async Task TheInspectorRunsFromATempFolderWhosePathCarriesATypographicApostrophe()
    {
        var folder = _t.NewFolder("O’Neil");

        var inspection = await PowerShellScriptInspector.InspectAsync("$a = 1\r\nif ($a -eq 1 {\r\n}", folder, CancellationToken.None);

        Assert.Null(inspection.Failure);
        Assert.Equal(2, inspection.Errors[0].Line);
        Assert.Contains("O’’Neil", PowerShellScriptInspector.InspectorCommand(Path.Combine(folder, "x.ps1")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"Errors":[{"Line":1,"Column":1,"Message":5}]}""")]
    public void OutputThatIsNotTheInspectorJsonIsAFailureNotAnException(string stdout)
    {
        var inspection = PowerShellScriptInspector.ReadOutput(stdout);

        Assert.NotNull(inspection.Failure);
        Assert.StartsWith("Parser failure: Windows PowerShell returned output the inspector cannot read", inspection.Diagnostics[0]);
    }

    [Fact]
    public void ASingleErrorSerializedAsAnObjectIsRead()
    {
        var inspection = PowerShellScriptInspector.Parse("""{"Errors":{"Line":3,"Column":7,"Message":"Missing closing '}'."},"WritesOutput":false,"Failure":""}""");

        Assert.Equal(["Line 3, column 7: Missing closing '}'."], inspection.Diagnostics);
    }

    [Fact]
    public async Task AnEmptyScriptListsNothing()
    {
        Assert.Same(ScriptInspection.Empty, await PowerShellScriptInspector.InspectAsync("  "));
        Assert.Empty(WorkbenchScriptDiagnostics.ForScript("", ScriptInspection.Empty));
        Assert.Empty(WorkbenchScriptDiagnostics.ForDetection("", ScriptInspection.Empty));
    }

    [Fact]
    public void TheEditorListsUseTheWorkbenchWording()
    {
        Assert.Equal([WorkbenchScriptDiagnostics.ScriptClean], WorkbenchScriptDiagnostics.ForScript("exit 0", new ScriptInspection([], false)));
        Assert.Equal(["No parse errors; the script writes to STDOUT."], WorkbenchScriptDiagnostics.ForDetection("'x'", new ScriptInspection([], true)));
        Assert.Equal(["No parse errors, but nothing is written to STDOUT: Intune would read this as not installed."],
            WorkbenchScriptDiagnostics.ForDetection("exit 0", new ScriptInspection([], false)));
        Assert.Equal(["Parser failure: boom"], WorkbenchScriptDiagnostics.ForDetection("x", new ScriptInspection([], false, "boom")));
    }
}

/// <summary>DETECT-NO-OUTPUT and DETECT-PARSE, the signing plan line and the Intune findings of a sealed build.</summary>
public sealed class WorkbenchValidationFindingsTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private static readonly WorkbenchInheritedSettings Inherited = WorkbenchInheritedSettings.Build("Widget", "Contoso", "", 15, 60);

    public void Dispose() => _t.Dispose();

    private static WorkbenchProfile ScriptDetection(string text)
    {
        var profile = WorkbenchProfile.CreateNew("catalog:widget", name: "Pilot");
        profile.Set("Detection", WorkbenchJson.Parse($$"""{ "Mode": "Custom", "Rule": { "Type": "Script", "ScriptText": {{System.Text.Json.JsonSerializer.Serialize(text)}} } }"""));
        return profile;
    }

    [Fact]
    public void ASilentDetectionScriptIsBlockedWithDetectNoOutput()
    {
        var findings = WorkbenchFindings.Local(ScriptDetection("exit 0"), Inherited, new ScriptInspection([], false));

        var finding = Assert.Single(findings, f => f.Code == "DETECT-NO-OUTPUT");
        Assert.Equal(WorkbenchFinding.Blocking, finding.Severity);
        Assert.DoesNotContain(findings, f => f.Code == "DETECT-PARSE");
    }

    [Fact]
    public void AnUnparseableDetectionScriptIsBlockedWithItsFirstDiagnostic()
    {
        var inspection = new ScriptInspection([new ScriptParseError(2, 14, "Missing closing ')'."), new ScriptParseError(4, 1, "Unexpected token.")], true);

        var findings = WorkbenchFindings.Local(ScriptDetection("if ($a"), Inherited, inspection);

        var finding = Assert.Single(findings, f => f.Code == "DETECT-PARSE");
        Assert.Equal("The detection script does not parse: Line 2, column 14: Missing closing ')'.", finding.Message);
        Assert.Single(WorkbenchFindings.BlockingFor(findings, RunTargets.Mecm), f => f.Code == "DETECT-PARSE");
    }

    [Fact]
    public void ANativeCustomRuleProducesNoScriptFindings()
    {
        var profile = WorkbenchProfile.CreateNew("catalog:widget", name: "Pilot");
        profile.Set("Detection", WorkbenchJson.Parse("""{ "Mode": "Custom", "Rule": { "Type": "File" } }"""));

        Assert.False(WorkbenchFindings.HasCustomDetectionScript(profile));
        Assert.DoesNotContain(WorkbenchFindings.Local(profile, Inherited, new ScriptInspection([], false)), f => f.Code.StartsWith("DETECT-P") || f.Code == "DETECT-NO-OUTPUT");
    }

    [Fact]
    public void TheSigningLineFollowsTheEffectivePolicy()
    {
        Assert.Equal("off; no script is signed in this build", WorkbenchReview.SigningLine(new SigningPolicy()));
        Assert.Equal(
            "signs detection, install and uninstall scripts; requires valid install and uninstall signatures; certificate ABCDEF0123456789ABCDEF0123456789ABCDEF01 (CurrentUser); timestamp http://ts.example (required); SHA256; applied at build time",
            WorkbenchReview.SigningLine(new SigningPolicy
            {
                SignDetection = true, SignDeployment = true, RequireDeployment = true,
                CertificateThumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01",
                TimestampServer = "http://ts.example", TimestampRequired = true
            }));
    }

    [Fact]
    public void TheIntuneFindingsOfASealedBuildCountAgainstTheIntuneChipOnly()
    {
        var stageRoot = _t.NewFolder("stage");
        new ManifestService().Write(Path.Combine(stageRoot, "stage-manifest.json"), new StageManifest
        {
            AppName = "Widget",
            SoftwareVersion = "1.0",
            Architecture = "x64",
            RequireUserInteraction = true,
            Detection = new StageDetection { Type = "File", FilePath = @"C:\Program Files\Widget", FileName = "widget.exe", PropertyType = "Existence" }
        });
        var record = new JsonObject { ["BuildId"] = "20261003-120000-abcdef01", ["StageRoot"] = stageRoot };

        var findings = WorkbenchIntuneFindings.ForBuild(record);

        Assert.Contains(findings, f => f.Code == "INTUNE-InteractiveInstall" && f.Severity == WorkbenchFinding.Blocking);
        Assert.DoesNotContain(findings, f => f.Code == "INTUNE-ADAPTER");
        var chips = WorkbenchFindings.Chips(findings, validated: true);
        Assert.Equal(new WorkbenchChipState("Ready", "Ready", "Unsupported"), chips);
        Assert.Empty(WorkbenchFindings.BlockingFor(findings, RunTargets.Mecm));
    }

    [Fact]
    public void WithoutASealedBuildTheIntuneCheckSaysSo()
    {
        var finding = Assert.Single(WorkbenchIntuneFindings.ForBuild(null));
        Assert.Equal((WorkbenchFinding.Info, WorkbenchIntuneFindings.NoBuildCode), (finding.Severity, finding.Code));
    }
}
