using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>
/// Launcher spellings Windows PowerShell 5.1 accepts, each confirmed against
/// powershell.exe: -ec runs an encoded command; a quoted "-ex" reaches
/// powershell.exe unquoted through cmd; '-ep bypass' inside an -ArgumentList
/// string sets the child's policy; an en dash, em dash or horizontal bar
/// works as the parameter dash; cmd removes carets before powershell.exe
/// parses the line. The PSADT layout cases follow the toolkit's own shape:
/// the toolkit at the stage root beside the generated launchers.
/// </summary>
public sealed class LauncherAuditSpellingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apchain2-" + Guid.NewGuid().ToString("N")[..8]);

    public LauncherAuditSpellingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Write(string name, params string[] lines)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\r\n", lines));
    }

    private LauncherChainResult Audit(SigningPolicy? policy = null) =>
        DeploymentLauncher.AuditChain(_root, null, policy ?? new SigningPolicy());

    /// <summary>
    /// An installer switch inside an argument array ('/e', '-en') has the shape of a
    /// PowerShell parameter prefix. No generated script of a shipped definition may
    /// carry one, or a signing policy refuses its stage.
    /// </summary>
    [Fact]
    public void TheGeneratedScriptsOfEveryShippedDefinitionCarryNoParameterFinding()
    {
        var findings = new List<string>();
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json"))
        {
            var def = DefinitionFixture.Load(Path.GetFileName(path));
            foreach (var (name, text) in Wsus.DefaultBuildWsusCheck.Scripts(def))
            {
                // An unsigned .bat launcher names the execution policy by design.
                if (name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var line in text.Split('\n'))
                {
                    if (line.TrimStart().StartsWith('#')) continue;
                    foreach (var (code, token) in DeploymentLauncher.TokenFindings(line))
                        findings.Add($"{Path.GetFileName(path)} {name}: {code} {token}");
                }
            }
        }
        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("launch.bat", "powershell.exe -NoProfile -ec ZQB4AGkAdAAgADAA", DeploymentLauncher.CodeEncodedCommand)]
    [InlineData("launch.bat", "powershell.exe -NoProfile \"-ex\" Bypass -File \"%~dp0install.ps1\"", DeploymentLauncher.CodeExecutionPolicy)]
    [InlineData("launch.ps1", "Start-Process powershell.exe -ArgumentList '-ep bypass -File step.ps1' -Wait", DeploymentLauncher.CodeExecutionPolicy)]
    [InlineData("launch.bat", "powershell.exe -NoProfile –ExecutionPolicy Bypass -File \"%~dp0install.ps1\"", DeploymentLauncher.CodeExecutionPolicy)]
    [InlineData("launch.bat", "powershell.exe -NoProfile —ExecutionPolicy Bypass -File \"%~dp0install.ps1\"", DeploymentLauncher.CodeExecutionPolicy)]
    [InlineData("launch.bat", "powershell.exe -NoProfile ―ExecutionPolicy Bypass -File \"%~dp0install.ps1\"", DeploymentLauncher.CodeExecutionPolicy)]
    [InlineData("launch.bat", "powershell.exe -NoProfile -^ex^ecutionpolicy b^ypass -File \"%~dp0install.ps1\"", DeploymentLauncher.CodeExecutionPolicy)]
    [InlineData("launch.ps1", "Start-Process powershell.exe -ArgumentList '-ep','bypass','-File','step.ps1' -Wait", DeploymentLauncher.CodeExecutionPolicy)]
    [InlineData("launch.ps1", "Start-Process powershell.exe -ArgumentList @('-ex', 'bypass', '-File', 'step.ps1') -Wait", DeploymentLauncher.CodeExecutionPolicy)]
    [InlineData("launch.ps1", "Start-Process powershell.exe -ArgumentList @('-NoProfile';'-ec';'ZQB4AGkAdAAgADAA') -Wait", DeploymentLauncher.CodeEncodedCommand)]
    [InlineData("launch.ps1", "& powershell.exe `-ep bypass -File step.ps1", DeploymentLauncher.CodeExecutionPolicy)]
    public void ASpellingWindowsPowerShellAccepts_IsAFinding(string file, string line, string code)
    {
        Write(file, line);
        Assert.Contains(Audit().Findings, f => f.Code == code);
        Assert.Throws<InvalidOperationException>(() => Audit(new SigningPolicy { SignDeployment = true }));
    }

    [Fact]
    public void CaretsAreRemovedBeforeTheRelaxationCheck()
    {
        Write("launch.bat", "set p=-^Execution^Policy", "powershell.exe %p% b^ypass -File x.ps1");
        Assert.Contains(Audit().Findings, f => f.Code == DeploymentLauncher.CodeExecutionPolicy);
    }

    [Fact]
    public void CommentsAndUnrelatedStrings_StayNonFindings()
    {
        Write("install.ps1",
            "# Uses -ExecutionPolicy Bypass in the legacy wrapper",
            "Write-Output 'bypass the cache'",
            "$mode = 'Unrestricted access'",
            "exit 0");
        Write("install.bat", "@echo off", "rem powershell -ExecutionPolicy Bypass", ":: -ep Bypass", "exit /b 0");
        Assert.True(Audit(new SigningPolicy { SignDeployment = true }).BypassFree);
    }

    [Fact]
    public void AnEmptyToolkitEntryScriptAtTheStageRoot_DoesNotHideTheGeneratedLaunchers()
    {
        Write("install.bat", "@echo off", "powershell.exe -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"");
        Write("Deploy-Application.ps1", "");
        var ex = Assert.Throws<InvalidOperationException>(() => Audit(new SigningPolicy { RequireDeployment = true }));
        Assert.Contains("install.bat", ex.Message);
    }

    [Fact]
    public void APsadtLayoutAtTheStageRoot_KeepsTheToolkitThirdPartyAndTheLaunchersOwned()
    {
        Write("Invoke-AppDeployToolkit.ps1", "Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process");
        Write(@"PSAppDeployToolkit\PSAppDeployToolkit.psm1", "powershell -ep bypass");
        Write(@"SupportFiles\helper.ps1", "powershell -ep bypass");
        Write("install.bat", "@echo off", "powershell.exe -NoProfile -NonInteractive -File \"%~dp0install.ps1\"");
        Write("install.ps1", "exit 0");
        Write("uninstall.ps1", "exit 0");
        Write("uninstall.bat", "@echo off", "exit /b 0");
        Write(@"scripts\detect.ps1", "exit 0");

        var result = Audit(new SigningPolicy { SignDeployment = true });
        Assert.True(result.BypassFree);
        Assert.Equal(3, result.ThirdPartySkipped);
        Assert.Equal(5, result.FilesInspected);

        var (owned, thirdParty) = ScriptSigningOrchestrator.DeploymentInventory(_root, null);
        Assert.Equal(["install.ps1", "uninstall.ps1"], owned.Select(o => o.RelativePath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["Invoke-AppDeployToolkit.ps1", @"PSAppDeployToolkit\PSAppDeployToolkit.psm1", @"SupportFiles\helper.ps1"],
            thirdParty.Select(o => o.RelativePath).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AGeneratedLauncherIsOwned_EvenWhenDeclaredThirdParty()
    {
        Write("install.ps1", "powershell -ep bypass -File x.ps1");
        Assert.Throws<InvalidOperationException>(() =>
            DeploymentLauncher.AuditChain(_root, null, new SigningPolicy { SignDeployment = true }, ["install.ps1"]));
        var (owned, _) = ScriptSigningOrchestrator.DeploymentInventory(_root, ["install.ps1"]);
        Assert.Single(owned, o => o.RelativePath == "install.ps1");
    }

    [Fact]
    public void ANestedToolkitFolder_StaysThirdPartyAsAWhole()
    {
        Write(@"Toolkit\Deploy-Application.ps1", "Set-ExecutionPolicy Bypass -Scope Process");
        Write(@"Toolkit\AppDeployToolkit\AppDeployToolkitMain.ps1", "powershell -ep bypass");
        Write(@"Toolkit\install.ps1", "powershell -ep bypass");
        var result = Audit(new SigningPolicy { SignDeployment = true });
        Assert.True(result.BypassFree);
        Assert.Equal(3, result.ThirdPartySkipped);
    }
}
