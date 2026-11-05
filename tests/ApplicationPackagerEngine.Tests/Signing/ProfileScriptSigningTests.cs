using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>
/// A profile can place files under scripts\ beside the generated detect.ps1 and
/// requirements\. Signing must keep them and sign them with the deployment files,
/// and a preserved third-party signature must meet the timestamp rule of the policy
/// (PowerShell commit c97d69f).
/// </summary>
[Collection(SigningCollection.Name)]
public sealed class ProfileScriptSigningTests(SigningCertificateFixture fixture)
{
    private const string DetectText = "if (Test-Path 'C:\\x') { 'Installed' }";

    private string NewStage(string name)
    {
        var stage = fixture.NewDirectory(name);
        Directory.CreateDirectory(Path.Combine(stage, @"scripts\requirements"));
        Directory.CreateDirectory(Path.Combine(stage, @"scripts\profile"));
        File.WriteAllText(Path.Combine(stage, @"scripts\detect.ps1"), "Write-Output 'STALE'", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, @"scripts\requirements\old.ps1"), "Write-Output 'STALE'", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, @"scripts\profile.ps1"), "Write-Output 'profile'", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, @"scripts\profile\tool.psm1"), "function Get-Tool { 'tool' }", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, "install.ps1"), "exit 0", new ASCIIEncoding());
        File.WriteAllText(Path.Combine(stage, "uninstall.ps1"), "exit 0", new ASCIIEncoding());
        return stage;
    }

    private static ScriptSigningInput Input() => new()
    {
        Detection = new StageDetection { Type = "Script", ScriptText = DetectText }
    };

    [Fact]
    public void SigningKeepsAndSignsTheScriptsAProfilePlacedUnderScripts()
    {
        var stage = NewStage("profile-signed");

        var record = ScriptSigningOrchestrator.SignStage(stage, Input(), fixture.Policy(sd: true, sp: true));

        var profile = Path.Combine(stage, @"scripts\profile.ps1");
        var module = Path.Combine(stage, @"scripts\profile\tool.psm1");
        Assert.True(File.Exists(profile));
        Assert.True(File.Exists(module));
        Assert.True(ScriptSignatureVerifier.Verify(profile).SignatureIntact);
        Assert.True(ScriptSignatureVerifier.Verify(module).SignatureIntact);

        var files = record.Deployment.Files.Select(f => f.RelativePath).ToList();
        Assert.Contains(@"scripts\profile.ps1", files);
        Assert.Contains(@"scripts\profile\tool.psm1", files);
        Assert.DoesNotContain(@"scripts\detect.ps1", files);
        Assert.DoesNotContain(files, f => f.StartsWith(@"scripts\requirements\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SigningStillReplacesTheGeneratedDetectionAndRequirementScripts()
    {
        var stage = NewStage("generated-replaced");

        ScriptSigningOrchestrator.SignStage(stage, Input(), fixture.Policy(sd: true, sp: true));

        Assert.DoesNotContain("STALE", File.ReadAllText(Path.Combine(stage, @"scripts\detect.ps1")));
        Assert.False(File.Exists(Path.Combine(stage, @"scripts\requirements\old.ps1")));
    }

    [Fact]
    public void ABuildWithNoSigningSwitchKeepsTheProfileScriptsToo()
    {
        var stage = NewStage("profile-unsigned");

        ScriptSigningOrchestrator.SignStage(stage, Input(), new SigningPolicy());

        Assert.Equal("Write-Output 'profile'", File.ReadAllText(Path.Combine(stage, @"scripts\profile.ps1")));
        Assert.True(File.Exists(Path.Combine(stage, @"scripts\profile\tool.psm1")));
    }

    [Fact]
    public void TheInventoryOmitsOnlyTheGeneratedScripts()
    {
        var stage = NewStage("inventory");
        var (owned, thirdParty) = ScriptSigningOrchestrator.DeploymentInventory(stage, null);
        var names = owned.Concat(thirdParty).Select(f => f.RelativePath).ToList();

        Assert.Contains(@"scripts\profile.ps1", names);
        Assert.Contains(@"scripts\profile\tool.psm1", names);
        Assert.DoesNotContain(@"scripts\detect.ps1", names);
        Assert.DoesNotContain(@"scripts\requirements\old.ps1", names);
    }

    // ---- a preserved signature and TimestampRequired ----------------------

    private string SignedScript(string name)
    {
        var path = Path.Combine(fixture.NewDirectory(name), "third-party.ps1");
        File.WriteAllText(path, "exit 0", new ASCIIEncoding());
        var service = new ScriptSigningService();
        Assert.Equal(SigningStatus.SignedAndVerified, service.SignForCategory(path, fixture.Policy(sp: true), SigningCategory.Deployment).Status);
        return path;
    }

    [Fact]
    public void APreservedSignatureWithoutATimestampFailsWhenTheTimestampIsRequired()
    {
        var path = SignedScript("preserved-untimed");
        var before = File.ReadAllBytes(path);

        var result = new ScriptSigningService().SignForCategory(path,
            fixture.Policy(sp: true, timestampRequired: true), SigningCategory.Deployment, preserveExisting: true);

        Assert.Equal(SigningStatus.Failed, result.Status);
        Assert.Contains("TimestampRequired is set", result.Reason);
        Assert.Contains("Deployment", result.Reason);
        Assert.False(result.Timestamped);
        Assert.Equal(ScriptSignatureVerifier.FileSha256Hex(path), result.Sha256);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ARequiredExistingSignatureWithoutATimestampFailsWhenTheTimestampIsRequired()
    {
        var path = SignedScript("required-untimed");

        var result = new ScriptSigningService().SignForCategory(path,
            fixture.Policy(rp: true, timestampRequired: true), SigningCategory.Deployment);

        Assert.Equal(SigningStatus.Failed, result.Status);
        Assert.Contains("TimestampRequired is set", result.Reason);
    }

    [Fact]
    public void APreservedSignatureStillPassesWhenNoTimestampIsRequired()
    {
        var path = SignedScript("preserved-ok");

        var result = new ScriptSigningService().SignForCategory(path,
            fixture.Policy(sp: true), SigningCategory.Deployment, preserveExisting: true);

        Assert.Equal(SigningStatus.ExistingSignatureValid, result.Status);
    }
}
