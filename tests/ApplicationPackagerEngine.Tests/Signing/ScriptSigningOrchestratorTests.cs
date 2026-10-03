using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

[Collection(SigningCollection.Name)]
public sealed class ScriptSigningOrchestratorTests(SigningCertificateFixture fixture)
{
    private const string DetectText = "if (Test-Path 'C:\\Program Files\\Fixture\\app.exe') { Write-Output 'Detected' }\r\nexit 0";

    private sealed record Fixture(string StageRoot, ScriptSigningInput Input, StageDetection Detection);

    /// <summary>
    /// A stage carrying every category: a detection, one requirement script,
    /// install/uninstall wrappers and an owned helper module, under a folder
    /// whose path contains a space.
    /// </summary>
    private Fixture NewFixture(string name, bool signedLaunchers, string detectText = DetectText)
    {
        var stage = fixture.NewDirectory(name);
        Directory.CreateDirectory(Path.Combine(stage, @"scripts\requirements"));
        File.WriteAllText(Path.Combine(stage, @"scripts\detect.ps1"), detectText, new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, @"scripts\requirements\r1.ps1"), "Write-Output 'true'\r\nexit 0", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, "install.ps1"), "Import-Module \"$PSScriptRoot\\helper.psm1\"\r\nexit 0", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, "uninstall.ps1"), "exit 0", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, "helper.psm1"), "function Get-FixtureState { 'ok' }", new ASCIIEncoding());
        foreach (var entry in new[] { "install", "uninstall" })
            File.WriteAllText(Path.Combine(stage, entry + ".bat"), DeploymentLauncher.Build(entry + ".ps1", signedLaunchers).BatBody, new ASCIIEncoding());

        var detection = new StageDetection { Type = "Script", ScriptText = detectText };
        var input = new ScriptSigningInput
        {
            Detection = detection,
            Requirements = [new RequirementScriptSource("r1", "Write-Output 'true'\r\nexit 0")],
            Commands =
            [
                new LauncherCommand("InstallCommandLine", DeploymentLauncher.Build("install.bat", signedLaunchers).CommandLine),
                new LauncherCommand("UninstallCommandLine", DeploymentLauncher.Build("uninstall.bat", signedLaunchers).CommandLine)
            ]
        };
        return new Fixture(stage, input, detection);
    }

    public static TheoryData<bool, bool, bool> Combinations()
    {
        var data = new TheoryData<bool, bool, bool>();
        foreach (var d in new[] { false, true })
        foreach (var r in new[] { false, true })
        foreach (var p in new[] { false, true })
            data.Add(d, r, p);
        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void SignsOnlyTheSelectedCategories(bool d, bool r, bool p)
    {
        var f = NewFixture($"combo-{d}{r}{p}", p);
        var record = ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sd: d, sr: r, sp: p));

        Assert.Equal(d ? "SignedAndVerified" : "NotRequested", record.Detection.Status);
        Assert.Equal(r ? "SignedAndVerified" : "NotRequested", record.Requirements.Status);
        Assert.Equal(p ? "SignedAndVerified" : "NotRequested", record.Deployment.Status);
        Assert.Equal(p, record.Deployment.LaunchersBypassFree);
        Assert.Equal(3, record.Deployment.Files.Count(x => x.Owner == "AppPackager"));
        Assert.Single(record.Requirements.Items);
        Assert.Equal(@"scripts\detect.ps1", record.Detection.File);
        Assert.Equal(64, record.PolicyDigest.Length);
        Assert.Equal(fixture.Policy(sd: d, sr: r, sp: p).PolicyDigest, record.PolicyDigest);

        Assert.Equal(d, ScriptSignatureVerifier.Verify(Path.Combine(f.StageRoot, @"scripts\detect.ps1")).SignatureIntact);
        Assert.Equal(r, ScriptSignatureVerifier.Verify(Path.Combine(f.StageRoot, @"scripts\requirements\r1.ps1")).SignatureIntact);
        foreach (var name in new[] { "install.ps1", "uninstall.ps1", "helper.psm1" })
            Assert.Equal(p, ScriptSignatureVerifier.Verify(Path.Combine(f.StageRoot, name)).SignatureIntact);
        if (d) Assert.Equal(fixture.Thumbprint, record.Detection.Thumbprint);
    }

    [Fact]
    public void ReplacesTheManifestDetectionTextWithTheExactSignedText()
    {
        var f = NewFixture("signed-text", true);
        ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sd: true));

        Assert.Contains(ScriptSignatureVerifier.BeginMarker, f.Detection.ScriptText);
        Assert.Equal(ScriptSignatureVerifier.Represent(Path.Combine(f.StageRoot, @"scripts\detect.ps1")).Text, f.Detection.ScriptText);
        Assert.True(ScriptSignatureVerifier.VerifyBytes(Encoding.UTF8.GetBytes(f.Detection.ScriptText!)).SignatureIntact);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RewritesAStaleDetectionScriptFromTheCurrentManifest(bool signed)
    {
        var f = NewFixture("stale-" + signed, signed);
        const string stale = "Write-Output 'STALE'\r\nexit 0";
        File.WriteAllText(Path.Combine(f.StageRoot, @"scripts\detect.ps1"), stale);
        File.WriteAllText(Path.Combine(f.StageRoot, @"scripts\requirements\r1.ps1"), stale);

        ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, signed ? fixture.Policy(sd: true, sr: true) : new SigningPolicy());

        var detect = File.ReadAllText(Path.Combine(f.StageRoot, @"scripts\detect.ps1"));
        Assert.DoesNotContain("STALE", detect);
        Assert.Contains(@"Program Files\Fixture", detect);
        Assert.DoesNotContain("STALE", f.Detection.ScriptText);
        Assert.DoesNotContain("STALE", File.ReadAllText(Path.Combine(f.StageRoot, @"scripts\requirements\r1.ps1")));
    }

    [Theory]
    [InlineData("UTF8BOM")]
    [InlineData("UTF16LE")]
    [InlineData("UTF8NoBOM")]
    public void MaterializesPreSignedManifestText_InTheEncodingThatKeepsItsSignature(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "UTF8BOM" => new UTF8Encoding(true),
            "UTF16LE" => new UnicodeEncoding(false, true),
            _ => new UTF8Encoding(false)
        };
        var source = Path.Combine(fixture.NewDirectory("presigned-src"), "src.ps1");
        File.WriteAllText(source, "Write-Output 'Detected caf\u00e9'\r\nexit 0", encoding);
        ScriptSigningService.SignFile(source, fixture.Signer, "SHA256", null);

        var f = NewFixture("presigned-" + encodingName, false, ScriptSignatureVerifier.Represent(source).Text);
        var record = ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(rd: true));

        var staged = Path.Combine(f.StageRoot, @"scripts\detect.ps1");
        Assert.Equal("ExistingSignatureValid", record.Detection.Status);
        Assert.True(ScriptSignatureVerifier.Verify(staged).SignatureIntact);
        // The PowerShell subject interface package hashes the script text as its
        // byte-order mark decodes it, so a UTF-16LE original written back with a
        // UTF-8 byte-order mark (the earlier candidate) verifies as well.
        if (encodingName != "UTF16LE")
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(staged));
    }

    [Fact]
    public void RequireDeploymentAlone_RefusesALauncherThatCarriesBypass()
    {
        var f = NewFixture("require-only-launcher", false);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(rp: true)));
        Assert.Contains("refuses this build", ex.Message);
    }

    [Fact]
    public void RequireDetection_RefusesAnUnsignedDetection()
    {
        var f = NewFixture("strict-unsigned", false);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(rd: true)));
        Assert.Equal("RequireDetection is set but the Detection category status is 'Failed'; refusing to publish.", ex.Message);
    }

    [Fact]
    public void RequireRequirements_RefusesAStageWithNoRequirementScripts()
    {
        var f = NewFixture("strict-no-requirements", false);
        var input = new ScriptSigningInput { Detection = f.Input.Detection, Commands = f.Input.Commands };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScriptSigningOrchestrator.SignStage(f.StageRoot, input, fixture.Policy(sr: true, rr: true)));
        Assert.Equal("RequireRequirements is set but the Requirements category status is 'NotApplicable'; refusing to publish.", ex.Message);
    }

    [Fact]
    public void PreservesAnUnchangedThirdPartyToolkitSignatureByteForByte()
    {
        var f = NewFixture("thirdparty", true);
        var vendor = Path.Combine(f.StageRoot, "Toolkit");
        Directory.CreateDirectory(vendor);
        File.WriteAllText(Path.Combine(vendor, "Invoke-AppDeployToolkit.ps1"), "exit 0");
        var helper = Path.Combine(vendor, "AppDeployToolkitMain.ps1");
        File.WriteAllText(helper, "function Get-Vendor { 'v' }");
        ScriptSigningService.SignFile(helper, fixture.Signer, "SHA256", null);
        var before = File.ReadAllBytes(helper);

        var record = ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sp: true));

        var entry = Assert.Single(record.Deployment.Files, x => x.RelativePath.EndsWith("AppDeployToolkitMain.ps1", StringComparison.Ordinal));
        Assert.Equal("ExistingSignatureValid", entry.Status);
        Assert.Equal("ThirdParty", entry.Owner);
        Assert.Equal(before, File.ReadAllBytes(helper));
        // The unsigned toolkit entry script is signed under the deployment switch.
        Assert.Equal("SignedAndVerified", Assert.Single(record.Deployment.Files, x => x.RelativePath.EndsWith("Invoke-AppDeployToolkit.ps1", StringComparison.Ordinal)).Status);
    }

    [Fact]
    public void ADeclaredThirdPartyScript_IsPreserved()
    {
        var f = NewFixture("declared", true);
        var vendor = Path.Combine(f.StageRoot, "vendor.ps1");
        File.WriteAllText(vendor, "exit 0");
        ScriptSigningService.SignFile(vendor, fixture.Signer, "SHA256", null);
        var input = new ScriptSigningInput { Detection = f.Input.Detection, Commands = f.Input.Commands, ThirdPartyScripts = ["vendor.ps1"] };

        var record = ScriptSigningOrchestrator.SignStage(f.StageRoot, input, fixture.Policy(sp: true));
        Assert.Equal("ThirdParty", Assert.Single(record.Deployment.Files, x => x.RelativePath == "vendor.ps1").Owner);
    }

    [Fact]
    public void ASignedBuildWhoseLauncherStillCarriesBypass_IsRefused()
    {
        var f = NewFixture("bypass-launcher", false);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sp: true)));
        Assert.StartsWith("Signed deployment mode refuses this build", ex.Message);
    }

    [Fact]
    public void AHandAuthoredRelaxedLauncher_IsRefusedEvenWhenTheGeneratedWrappersAreClean()
    {
        var f = NewFixture("relaxed-launcher", true);
        File.WriteAllText(Path.Combine(f.StageRoot, "legacy-launch.bat"),
            "@echo off\r\npowershell.exe -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"\r\nexit /b %ERRORLEVEL%\r\n");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sp: true)));
        Assert.Contains("legacy-launch.bat:2", ex.Message);
    }

    [Fact]
    public void ARequiredTimestampThatWasNotProduced_RefusesTheBuild()
    {
        var f = NewFixture("timestamp", true);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sd: true, timestampRequired: true)));
        Assert.Contains("timestamp", ex.Message);
    }

    [Fact]
    public void ADetectionScriptAboveTheConfigMgrLimitAfterSigning_IsRefused()
    {
        var pad = "#" + new string('x', 200);
        var text = string.Join("\r\n", Enumerable.Repeat(pad, 160)) + "\r\nWrite-Output 'Detected'\r\nexit 0";
        Assert.True(Encoding.UTF8.GetByteCount(text) < 32768);
        var f = NewFixture("oversize", true, text);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sd: true)));
        Assert.Matches(@"^Detection script is \d+ bytes after signing; ConfigMgr refuses a script larger than 32768 bytes\.$", ex.Message);
    }

    [Fact]
    public void TheDeploymentSwitch_DoesNotSignDetectionOrRequirementScripts()
    {
        var f = NewFixture("category-isolation", true);
        ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sp: true));
        Assert.Equal("NotSigned", ScriptSignatureVerifier.Verify(Path.Combine(f.StageRoot, @"scripts\detect.ps1")).Status);
        Assert.Equal("NotSigned", ScriptSignatureVerifier.Verify(Path.Combine(f.StageRoot, @"scripts\requirements\r1.ps1")).Status);
        Assert.True(ScriptSignatureVerifier.Verify(Path.Combine(f.StageRoot, "install.ps1")).SignatureIntact);
    }

    [Fact]
    public void EverySwitchOff_ResolvesNoCertificate()
    {
        var f = NewFixture("no-cert", false);
        var resolutions = 0;
        var service = new ScriptSigningService(new SigningCertificateService(_ => { resolutions++; return []; }));
        var record = ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, new SigningPolicy(), service);
        Assert.Equal(0, resolutions);
        Assert.Equal("NotRequested", record.Detection.Status);
        Assert.False(record.Deployment.LaunchersBypassFree);
    }

    [Fact]
    public void ARequiredCategoryWithNoCertificate_RefusesBeforeWritingAnyScript()
    {
        var stage = fixture.NewDirectory("require-no-cert");
        var input = new ScriptSigningInput { Detection = new StageDetection { Type = "Script", ScriptText = DetectText } };
        var ex = Assert.Throws<SigningCertificateUnavailableException>(() =>
            ScriptSigningOrchestrator.SignStage(stage, input, fixture.Policy(sd: true, rd: true, thumbprint: "")));
        Assert.StartsWith("SigningCertificateUnavailable: no certificate selected", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(stage, "scripts")));
    }

    [Fact]
    public void AThumbprintAbsentFromTheStore_IsRefused()
    {
        var f = NewFixture("missing-cert", true);
        var ex = Assert.Throws<SigningCertificateUnavailableException>(() =>
            ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sd: true, thumbprint: new string('A', 40))));
        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public void ANonAsciiHelper_SignsIntactAndKeepsItsEncoding()
    {
        var f = NewFixture("non-ascii", true);
        var helper = Path.Combine(f.StageRoot, "helper.ps1");
        File.WriteAllText(helper, "function Write-GFixtureState { param([string]$Message) Write-Output $Message }\r\n# caption: \u00e9\u00fc\u2014\r\n", new UTF8Encoding(false));

        ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sp: true));

        Assert.Contains(File.ReadAllBytes(helper), b => b > 0x7F);
        Assert.True(ScriptSignatureVerifier.Verify(helper).SignatureIntact);
        Assert.Equal("UTF8NoBOM", ScriptSignatureVerifier.Represent(helper).Encoding);
    }

    [Fact]
    public void StageFileHashes_CoverTheSignedScripts()
    {
        var f = NewFixture("hash-coverage", true);
        ScriptSigningOrchestrator.SignStage(f.StageRoot, f.Input, fixture.Policy(sd: true, sr: true, sp: true));
        var hashes = ManifestService.ComputeFileHashes(f.StageRoot, "stage-manifest.json");
        var paths = hashes.Select(h => h.RelativePath).ToList();
        Assert.Contains("install.ps1", paths);
        Assert.Contains("helper.psm1", paths);
        Assert.Contains(@"scripts\detect.ps1", paths);
        Assert.Contains(@"scripts\requirements\r1.ps1", paths);
        Assert.Empty(ManifestService.VerifyFileHashes(f.StageRoot, hashes, "stage-manifest.json"));
    }

    [Fact]
    public void GeneratedSignedWrappers_AreBypassFree_AndUnsignedOnesAreRefusedUnderRequireDeployment()
    {
        var signedStage = fixture.NewDirectory("gen-signed");
        new WrapperGeneratorService { SignedLaunchers = true }.GenerateExeWrappers(signedStage, "setup.exe", "/S", "/S");
        var record = ScriptSigningOrchestrator.SignStage(signedStage, new ScriptSigningInput(), fixture.Policy(sp: true, rp: true));
        Assert.True(record.Deployment.LaunchersBypassFree);
        Assert.Equal("SignedAndVerified", record.Deployment.Status);
        Assert.Contains("powershell.exe -NoProfile -NonInteractive -File \"%~dp0install.ps1\"", File.ReadAllText(Path.Combine(signedStage, "install.bat")));

        var unsignedStage = fixture.NewDirectory("gen-unsigned");
        new WrapperGeneratorService().GenerateExeWrappers(unsignedStage, "setup.exe", "/S", "/S");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScriptSigningOrchestrator.SignStage(unsignedStage, new ScriptSigningInput(), fixture.Policy(rp: true)));
        Assert.Contains("refuses this build", ex.Message);
    }

    [Fact]
    public void FromManifest_CollectsDetectionCommandsAndScriptConditionTemplates()
    {
        var manifest = new StageManifest
        {
            Detection = new StageDetection { Type = "Script", ScriptText = DetectText },
            CommandOverrides = new StageCommandOverrides { Install = "powershell -ep bypass -file x.ps1" },
            DeploymentTypes =
            [
                new StageDeploymentType
                {
                    NameSuffix = "x64", UninstallCommandLine = "uninstall.bat",
                    Requirements = [new StageRequirement { ConditionId = "vpn-connected", Value = true }, new StageRequirement { ConditionId = "cpu-arch", Value = "x64" }]
                }
            ]
        };
        var input = ScriptSigningInput.FromManifest(manifest, ConditionTemplateService.Defaults());
        Assert.Same(manifest.Detection, input.Detection);
        Assert.Equal(["InstallCommandLine", "DeploymentTypes[1].UninstallCommandLine"], input.Commands.Select(c => c.Source));
        var requirement = Assert.Single(input.Requirements);
        Assert.Equal("vpn-connected", requirement.RuleId);
    }

    [Fact]
    public void ARuleIdIsMadeFileSafe_AndAMissingOneIsNumbered()
    {
        var scripts = ScriptSigningOrchestrator.RequirementScripts(
        [
            new RequirementScriptSource("a b/c", "x"),
            new RequirementScriptSource(null, ""),
            new RequirementScriptSource(null, "y")
        ]);
        Assert.Equal(["a-b-c", "rule3"], scripts.Select(s => s.RuleId));
    }

    [Fact]
    public void AVbScriptDetection_IsNotMaterializedAsAPowerShellScript()
    {
        var stage = fixture.NewDirectory("vbscript");
        var input = new ScriptSigningInput { Detection = new StageDetection { Type = "Script", ScriptLanguage = "VBScript", ScriptText = "WScript.Echo \"x\"" } };
        var record = ScriptSigningOrchestrator.SignStage(stage, input, new SigningPolicy());
        Assert.Equal("NotApplicable", record.Detection.Status);
        Assert.Null(record.Detection.File);
        Assert.False(File.Exists(Path.Combine(stage, @"scripts\detect.ps1")));
    }
}
