using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

[Collection(SigningCollection.Name)]
public sealed class SignStageRulesTests(SigningCertificateFixture fixture)
{
    [Fact]
    public void TheGeneratedScripts_AreReplacedBeforeSigning_AndOtherFilesStay()
    {
        var stage = fixture.NewDirectory("stale");
        Directory.CreateDirectory(Path.Combine(stage, @"scripts\requirements"));
        File.WriteAllText(Path.Combine(stage, @"scripts\detect.ps1"), "Write-Output 'STALE'");
        File.WriteAllText(Path.Combine(stage, @"scripts\profile-file.ps1"), "exit 0");
        File.WriteAllText(Path.Combine(stage, @"scripts\requirements\old-rule.ps1"), "exit 0");
        var input = new ScriptSigningInput
        {
            Detection = new StageDetection { Type = "Script", ScriptText = "exit 0" }
        };

        ScriptSigningOrchestrator.SignStage(stage, input, fixture.Policy(sd: true));

        Assert.Equal([Path.Combine(stage, @"scripts\detect.ps1"), Path.Combine(stage, @"scripts\profile-file.ps1")],
            Directory.GetFiles(Path.Combine(stage, "scripts"), "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("STALE", File.ReadAllText(Path.Combine(stage, @"scripts\detect.ps1")));
    }

    [Fact]
    public void AFailedDeploymentCategory_UnderSignDeployment_Throws()
    {
        var stage = fixture.NewDirectory("deployfail");
        File.WriteAllText(Path.Combine(stage, "install.ps1"), "exit 0", new ASCIIEncoding());
        var locked = Path.Combine(stage, "helper.ps1");
        File.WriteAllText(locked, "exit 0", new ASCIIEncoding());
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ScriptSigningOrchestrator.SignStage(stage, new ScriptSigningInput(), fixture.Policy(sp: true)));
            Assert.StartsWith("Deployment script signing failed and the launchers carry no execution-policy relaxation: ", ex.Message);
            Assert.Contains("helper.ps1", ex.Message);
        }
        finally { File.SetAttributes(locked, FileAttributes.Normal); }
    }
}
