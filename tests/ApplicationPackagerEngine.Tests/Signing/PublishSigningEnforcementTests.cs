using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>
/// The Require switches refuse a publish before the first site write or
/// Graph call: ConfigMgr detection and deployment at script build, the
/// deployment files before the content copy, existing and created script
/// conditions in the generated read-back, and the Intune scriptContent bytes.
/// </summary>
[Collection(SigningCollection.Name)]
public sealed class PublishSigningEnforcementTests(SigningCertificateFixture fixture)
{
    private const string Content = @"\\server\share\Apps\Vendor\App\1.0";

    private static StageManifest ScriptManifest() => new()
    {
        AppName = "App", Publisher = "Vendor", SoftwareVersion = "1.0",
        Detection = new StageDetection { Type = "Script", ScriptText = "Write-Output 'Installed'\r\nexit 0" }
    };

    private static string Build(StageManifest manifest, MecmScriptSigningContext? signing) =>
        MecmTarget.BuildPackageScript(manifest, manifest.AppName, Content, "P01", "comment", 15, 30,
            ConditionTemplateService.Defaults(), null, null, null, null, OnExistingBehavior.Skip, signing);

    // ── ConfigMgr detection ────────────────────────────────────

    [Theory]
    [InlineData("NotRequested")]
    [InlineData("Failed")]
    public void RequireDetection_RefusesAnUnsignedDetectionWhenTheScriptIsBuilt(string status)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(ScriptManifest(), new MecmScriptSigningContext
        {
            Policy = new SigningPolicy { RequireDetection = true },
            Record = new ScriptSigningRecord { Detection = new DetectionSigningRecord { Status = status, File = @"scripts\detect.ps1" } }
        }));
        Assert.Equal($"RequireDetection is set but the Detection category status is '{status}'; refusing to publish.", ex.Message);
    }

    [Fact]
    public void RequireDetection_RefusesAStageWithoutASigningRecord()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(ScriptManifest(), new MecmScriptSigningContext { Policy = new SigningPolicy { RequireDetection = true } }));
        Assert.Equal("RequireDetection is set but the Detection category status is 'none'; refusing to publish.", ex.Message);
    }

    [Fact]
    public void RequireDetection_RefusesADeploymentTypeDetectionCarriedAsText()
    {
        var manifest = ScriptManifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "x64", Detection = new StageDetection { Type = "Script", ScriptText = "exit 0" } }
        ];
        Assert.Throws<InvalidOperationException>(() => Build(manifest, new MecmScriptSigningContext
        {
            Policy = new SigningPolicy { RequireDetection = true },
            Record = new ScriptSigningRecord { Detection = new DetectionSigningRecord { Status = "NotRequested" } }
        }));
    }

    // ── ConfigMgr deployment ───────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("Failed")]
    [InlineData("NotRequested")]
    [InlineData("NotApplicable")]
    public void RequireDeployment_RefusesARecordThatIsMissingOrNotSigned(string? status)
    {
        var record = status is null ? null : new ScriptSigningRecord { Deployment = new DeploymentSigningRecord { Status = status } };
        var ex = Assert.Throws<InvalidOperationException>(() => Build(ScriptManifest(), new MecmScriptSigningContext
        {
            Policy = new SigningPolicy { RequireDeployment = true },
            Record = record
        }));
        Assert.Equal($"RequireDeployment is set but the Deployment category status is '{status ?? "none"}'; refusing to publish.", ex.Message);
    }

    private (string Stage, ScriptSigningRecord Record) SignedDeploymentStage()
    {
        var stage = fixture.NewDirectory("deploy");
        File.WriteAllText(Path.Combine(stage, "install.ps1"), "exit 0", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, "uninstall.ps1"), "exit 0", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, "install.bat"), DeploymentLauncher.Build("install.ps1", true).BatBody, new ASCIIEncoding());
        var record = ScriptSigningOrchestrator.SignStage(stage, new ScriptSigningInput(), fixture.Policy(sp: true, rp: true));
        Assert.Equal(SigningStatus.SignedAndVerified, record.Deployment.Status);
        return (stage, record);
    }

    [Fact]
    public void RequireDeployment_AcceptsIntactFilesWhoseHashesMatchTheRecord()
    {
        var (stage, record) = SignedDeploymentStage();
        MecmTarget.VerifyRequiredDeploymentSigning(new SigningPolicy { RequireDeployment = true }, record, stage);
        Assert.Contains("Add-CMScriptDeploymentType", Build(ScriptManifest(), new MecmScriptSigningContext
        {
            Policy = new SigningPolicy { RequireDeployment = true }, Record = record
        }));
    }

    [Fact]
    public void RequireDeployment_RefusesAFileAlteredAfterSigning()
    {
        var (stage, record) = SignedDeploymentStage();
        File.AppendAllText(Path.Combine(stage, "install.ps1"), "\r\nWrite-Output 'added'");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MecmTarget.VerifyRequiredDeploymentSigning(new SigningPolicy { RequireDeployment = true }, record, stage));
        Assert.StartsWith("RequireDeployment is set but deployment script 'install.ps1' ", ex.Message);
        Assert.EndsWith("; refusing to publish.", ex.Message);
    }

    [Fact]
    public void RequireDeployment_RefusesAnIntactFileWhoseHashDiffersFromTheRecord()
    {
        var (stage, record) = SignedDeploymentStage();
        var other = Path.Combine(fixture.NewDirectory("other"), "install.ps1");
        File.WriteAllText(other, "exit 1", new ASCIIEncoding());
        ScriptSigningService.SignFile(other, fixture.Signer, "SHA256", null);
        File.Copy(other, Path.Combine(stage, "install.ps1"), overwrite: true);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MecmTarget.VerifyRequiredDeploymentSigning(new SigningPolicy { RequireDeployment = true }, record, stage));
        Assert.Contains("hash", ex.Message);
    }

    [Fact]
    public void RequireDeployment_RefusesAMissingRecordedFile()
    {
        var (stage, record) = SignedDeploymentStage();
        File.Delete(Path.Combine(stage, "uninstall.ps1"));
        Assert.Throws<InvalidOperationException>(() =>
            MecmTarget.VerifyRequiredDeploymentSigning(new SigningPolicy { RequireDeployment = true }, record, stage));
    }

    [Fact]
    public void WithoutRequireDeployment_NothingIsVerified()
    {
        var (stage, record) = SignedDeploymentStage();
        File.AppendAllText(Path.Combine(stage, "install.ps1"), "\r\nWrite-Output 'added'");
        MecmTarget.VerifyRequiredDeploymentSigning(new SigningPolicy(), record, stage);
        MecmTarget.VerifyRequiredDeploymentSigning(new SigningPolicy { SignDeployment = true }, null, stage);
    }

    // ── ConfigMgr script conditions ────────────────────────────

    private static string EncodedBody(byte[] bytes) =>
        "preview&#xD;\n# ENCODEDSCRIPT # Begin Configuration Manager encoded script block #" + Convert.ToBase64String(bytes) +
        "# ENCODEDSCRIPT # End Configuration Manager encoded script block #";

    /// <summary>
    /// Runs Get-ApkgScriptGlobalCondition against stand-in site cmdlets: the
    /// named conditions exist with the given stored bodies, and a created
    /// condition stores <paramref name="createdBody"/>.
    /// </summary>
    private string RunResolver(Dictionary<string, string?> existing, string? createdBody, string scriptText, byte[]? signedBytes,
        string contentHash, string expectedThumbprint)
    {
        var dir = fixture.NewDirectory("gc");
        var helpers = Path.Combine(dir, "helpers.ps1");
        File.WriteAllText(helpers, MecmScriptSigningPs.ReadBackHelpers() + "\r\n" + MecmScriptSigningPs.ScriptGlobalConditionFunctions(), new UTF8Encoding(true));
        var sb = new StringBuilder();
        sb.Append($". {SigningCertificateFixture.PsLiteral(helpers)}; ");
        sb.Append("$script:site = @{}; ");
        foreach (var (name, body) in existing)
        {
            var bodyFile = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(bodyFile, body ?? "");
            sb.Append($"$script:site[{SigningCertificateFixture.PsLiteral(name)}] = {(body is null ? "''" : $"[IO.File]::ReadAllText({SigningCertificateFixture.PsLiteral(bodyFile)})")}; ");
        }
        var createdFile = Path.Combine(dir, "created.txt");
        File.WriteAllText(createdFile, createdBody ?? "");
        sb.Append($"$script:created = {(createdBody is null ? "''" : $"[IO.File]::ReadAllText({SigningCertificateFixture.PsLiteral(createdFile)})")}; ");
        sb.Append("function New-ApkgSdm([string]$b) { if ([string]::IsNullOrEmpty($b)) { return '' }; '<Root><ScriptBody>' + [Security.SecurityElement]::Escape($b) + '</ScriptBody></Root>' }; ");
        sb.Append("function Get-CMGlobalCondition { param($Name, $Id) if ($Id) { return }; if ($script:site.ContainsKey($Name)) { [pscustomobject]@{ CI_ID = 1; PlatformType = 1; SDMPackageXML = (New-ApkgSdm $script:site[$Name]) } } }; ");
        sb.Append("function New-CMGlobalConditionScript { param($Name, $DataType, $ScriptLanguage, $FilePath, $ScriptText, $Description) $script:site[$Name] = $script:created }; ");
        var b64 = signedBytes is null ? "" : Convert.ToBase64String(signedBytes);
        sb.Append("try { $r = Get-ApkgScriptGlobalCondition -Name 'Cond' -PlatformType 0 -DataType 'Boolean' -Description 'd' ");
        sb.Append($"-ScriptText {SigningCertificateFixture.PsLiteral(scriptText)} -SignedBase64 '{b64}' -ContentHash '{contentHash}' -ExpectedThumbprint '{expectedThumbprint}'; 'RESULT=ACCEPTED' }} ");
        sb.Append("catch { 'RESULT=REFUSED ' + $_.Exception.Message }");
        return SigningCertificateFixture.RunWindowsPowerShell(sb.ToString()).Last(l => l.StartsWith("RESULT=", StringComparison.Ordinal));
    }

    private (byte[] Bytes, string Hash8) SignedCondition(string body)
    {
        var path = Path.Combine(fixture.NewDirectory("cond"), "c.ps1");
        File.WriteAllText(path, body, new UTF8Encoding(false));
        ScriptSigningService.SignFile(path, fixture.Signer, "SHA256", null);
        var bytes = File.ReadAllBytes(path);
        return (bytes, ScriptSignatureVerifier.Sha256Hex(bytes)[..8]);
    }

    [Fact]
    public void AnExistingVersionedSignedCondition_WithTheExpectedBytesAndSigner_IsAttached()
    {
        var (bytes, hash) = SignedCondition("$true");
        Assert.Equal("RESULT=ACCEPTED", RunResolver(new() { [$"Cond ({hash})"] = EncodedBody(bytes) }, null,
            Encoding.UTF8.GetString(bytes), bytes, hash, fixture.Thumbprint));
    }

    [Fact]
    public void AnExistingVersionedSignedCondition_WithOtherBytes_IsRefused()
    {
        var (bytes, hash) = SignedCondition("$true");
        var (other, _) = SignedCondition("$false");
        var result = RunResolver(new() { [$"Cond ({hash})"] = EncodedBody(other) }, null, Encoding.UTF8.GetString(bytes), bytes, hash, fixture.Thumbprint);
        Assert.StartsWith("RESULT=REFUSED", result);
    }

    [Fact]
    public void AnExistingVersionedSignedCondition_SignedByAnotherCertificate_IsRefused()
    {
        var (bytes, hash) = SignedCondition("$true");
        var result = RunResolver(new() { [$"Cond ({hash})"] = EncodedBody(bytes) }, null, Encoding.UTF8.GetString(bytes), bytes, hash, new string('B', 40));
        Assert.StartsWith("RESULT=REFUSED", result);
    }

    [Fact]
    public void AnExistingVersionedSignedCondition_WithNoReadableBody_IsRefused()
    {
        var (bytes, hash) = SignedCondition("$true");
        Assert.StartsWith("RESULT=REFUSED",
            RunResolver(new() { [$"Cond ({hash})"] = null }, null, Encoding.UTF8.GetString(bytes), bytes, hash, fixture.Thumbprint));
    }

    [Fact]
    public void AnExistingVersionedUnsignedCondition_WithOtherText_IsRefused()
    {
        var result = RunResolver(new() { ["Cond"] = "$false", ["Cond (aaaaaaaa)"] = "$maybe" }, null, "$true", null, "aaaaaaaa", "");
        Assert.StartsWith("RESULT=REFUSED", result);
    }

    [Fact]
    public void ACreatedSignedCondition_WhoseReadBackHasNoBody_IsRefused()
    {
        var (bytes, hash) = SignedCondition("$true");
        Assert.StartsWith("RESULT=REFUSED", RunResolver(new(), null, Encoding.UTF8.GetString(bytes), bytes, hash, fixture.Thumbprint));
    }

    [Fact]
    public void ACreatedSignedCondition_WhoseReadBackMatches_IsAccepted()
    {
        var (bytes, hash) = SignedCondition("$true");
        Assert.Equal("RESULT=ACCEPTED", RunResolver(new(), EncodedBody(bytes), Encoding.UTF8.GetString(bytes), bytes, hash, fixture.Thumbprint));
    }

    [Fact]
    public void ACreatedUnsignedCondition_WhoseReadBackHasNoBody_StaysAWarning() =>
        Assert.Equal("RESULT=ACCEPTED", RunResolver(new(), null, "$true", null, "aaaaaaaa", ""));

    // ── Intune ─────────────────────────────────────────────────

    private (string Stage, ScriptSigningRecord Record) SignedDetectionStage()
    {
        var stage = fixture.NewDirectory("intune");
        var input = new ScriptSigningInput { Detection = new StageDetection { Type = "Script", ScriptText = "Write-Output 'Installed'\r\nexit 0" } };
        var record = ScriptSigningOrchestrator.SignStage(stage, input, fixture.Policy(sd: true));
        Assert.Equal(SigningStatus.SignedAndVerified, record.Detection.Status);
        return (stage, record);
    }

    [Fact]
    public void Intune_ASignedDetectionWhoseBytesMatchTheRecord_IsPublished()
    {
        var (stage, record) = SignedDetectionStage();
        var rule = IntunePublishService.BuildRules(ScriptManifest().Detection,
            IntuneScriptRuleContext.From(record, new SigningPolicy { RequireDetection = true }, stage))[0]!;
        Assert.True((bool)rule["enforceSignatureCheck"]!);
        Assert.Equal(File.ReadAllBytes(Path.Combine(stage, record.Detection.File!)), Convert.FromBase64String((string)rule["scriptContent"]!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Intune_ADetectionFileAlteredAfterSigning_IsRefused(bool require)
    {
        var (stage, record) = SignedDetectionStage();
        File.AppendAllText(Path.Combine(stage, record.Detection.File!), "\r\n# edited");
        var ex = Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(ScriptManifest().Detection,
            IntuneScriptRuleContext.From(record, new SigningPolicy { RequireDetection = require }, stage)));
        Assert.EndsWith("refusing to publish.", ex.Message);
    }

    [Fact]
    public void Intune_AnIntactDetectionWhoseHashDiffersFromTheRecord_IsRefused()
    {
        var (stage, record) = SignedDetectionStage();
        record.Detection.Sha256 = new string('0', 64);
        var ex = Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(ScriptManifest().Detection,
            IntuneScriptRuleContext.From(record, new SigningPolicy(), stage)));
        Assert.Contains("hash", ex.Message);
    }

    [Fact]
    public void Intune_ADetectionSignedByAnotherCertificate_IsRefused()
    {
        var (stage, record) = SignedDetectionStage();
        record.Detection.Thumbprint = fixture.SecondThumbprint;
        var ex = Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(ScriptManifest().Detection,
            IntuneScriptRuleContext.From(record, new SigningPolicy(), stage)));
        Assert.Contains(fixture.SecondThumbprint, ex.Message);
    }

    [Fact]
    public void Intune_ARecordedSignatureWithoutAFile_IsVerifiedAgainstTheTextBytes()
    {
        var (_, record) = SignedDetectionStage();
        var context = new IntuneScriptRuleContext
        {
            DetectionStatus = record.Detection.Status, DetectionSha256 = record.Detection.Sha256, DetectionThumbprint = record.Detection.Thumbprint
        };
        Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(ScriptManifest().Detection, context));
    }
}
