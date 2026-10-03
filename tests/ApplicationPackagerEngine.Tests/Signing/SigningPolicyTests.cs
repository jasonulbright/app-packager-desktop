using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

public sealed class SigningPolicyTests
{
    [Fact]
    public void Defaults_EverySwitchOff_CurrentUserAndSha256()
    {
        var p = SigningPolicy.Normalize(null);
        Assert.False(p.SignDetection || p.SignRequirements || p.SignDeployment);
        Assert.False(p.RequireDetection || p.RequireRequirements || p.RequireDeployment);
        Assert.False(p.TimestampRequired);
        Assert.Equal("SHA256", p.HashAlgorithm);
        Assert.Equal("CurrentUser", p.StoreLocation);
        Assert.Equal("", p.CertificateThumbprint);
        Assert.Equal("", p.TimestampServer);
    }

    [Fact]
    public void Normalizes_StringSwitchesStoreCaseAndThumbprintPunctuation()
    {
        var p = SigningPolicy.FromJson("""{"SignDetection":"true","StoreLocation":"localmachine","CertificateThumbprint":"aa bb:cc","HashAlgorithm":"sha384"}""");
        Assert.True(p.SignDetection);
        Assert.Equal("LocalMachine", p.StoreLocation);
        Assert.Equal("AABBCC", p.CertificateThumbprint);
        Assert.Equal("SHA384", p.HashAlgorithm);
        Assert.Equal(64, p.PolicyDigest.Length);
    }

    [Fact]
    public void IsIdempotent_WhenGivenItsOwnOutput()
    {
        var once = SigningPolicy.Normalize(new SigningPolicy { SignDeployment = true, CertificateThumbprint = "ab:cd" });
        Assert.Equal(once.PolicyDigest, SigningPolicy.Normalize(once).PolicyDigest);
        Assert.Equal(once.CanonicalString(), SigningPolicy.Normalize(once).CanonicalString());
    }

    [Fact]
    public void RefusesAnUnsupportedStoreLocation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SigningPolicy.Normalize(new SigningPolicy { StoreLocation = "Machine" }));
        Assert.Contains("StoreLocation", ex.Message);
    }

    [Fact]
    public void NeverEnablesASwitchFromUnparsableInput()
    {
        Assert.False(SigningPolicy.FromJson("""{"SignDeployment":"maybe","RequireDetection":null}""").SignDeployment);
        Assert.False(SigningPolicy.FromJson("""{"RequireDetection":null}""").RequireDetection);
        Assert.True(SigningPolicy.FromJson("""{"RequireDetection":"on"}""").RequireDetection);
        Assert.True(SigningPolicy.FromJson("""{"RequireDetection":1}""").RequireDetection);
    }

    [Fact]
    public void Digest_ChangesWithEveryField_AndEachSwitchCombinationIsDistinct()
    {
        var digests = new HashSet<string>();
        foreach (var d in new[] { false, true })
        foreach (var r in new[] { false, true })
        foreach (var p in new[] { false, true })
            digests.Add(new SigningPolicy { SignDetection = d, SignRequirements = r, SignDeployment = p }.PolicyDigest);
        Assert.Equal(8, digests.Count);

        var baseline = new SigningPolicy().PolicyDigest;
        Assert.NotEqual(baseline, new SigningPolicy { TimestampServer = "http://ts.example" }.PolicyDigest);
        Assert.NotEqual(baseline, new SigningPolicy { TimestampRequired = true }.PolicyDigest);
        Assert.NotEqual(baseline, new SigningPolicy { RequireRequirements = true }.PolicyDigest);
        Assert.NotEqual(baseline, new SigningPolicy { StoreLocation = "LocalMachine" }.PolicyDigest);
    }

    [Fact]
    public void Digest_EqualsTheValueThePowerShellModuleComputesForTheSamePolicy()
    {
        Assert.Equal("b9456828f3adc8735ef0a45f961d212383801872f18bb841575c58dbb9e13971", new SigningPolicy().PolicyDigest);
        Assert.Equal("7910ef37dd1f5c2cd90109917fff8bbea239f31ae02a4d9d886a7daf1e64ab4e",
            new SigningPolicy
            {
                SignDetection = true, StoreLocation = "localmachine", CertificateThumbprint = "aa bb:cc",
                TimestampServer = " http://ts.example ", HashAlgorithm = "sha384"
            }.PolicyDigest);
    }

    [Fact]
    public void AggregateStatus_FailedOutranksSignedOutranksExistingOutranksNotRequested()
    {
        Assert.Equal(SigningStatus.NotApplicable, SigningStatus.Aggregate([]));
        Assert.Equal(SigningStatus.Failed, SigningStatus.Aggregate(["SignedAndVerified", "Failed", "NotRequested"]));
        Assert.Equal(SigningStatus.SignedAndVerified, SigningStatus.Aggregate(["ExistingSignatureValid", "SignedAndVerified"]));
        Assert.Equal(SigningStatus.ExistingSignatureValid, SigningStatus.Aggregate(["NotRequested", "ExistingSignatureValid"]));
        Assert.Equal(SigningStatus.NotRequested, SigningStatus.Aggregate(["NotRequested", "NotApplicable"]));
        Assert.Equal(SigningStatus.NotApplicable, SigningStatus.Aggregate(["NotApplicable"]));
    }

    [Fact]
    public void Record_RoundTripsThroughJsonWithTheManifestFieldNames()
    {
        var record = new ScriptSigningRecord
        {
            PolicyDigest = "abc",
            Detection = new DetectionSigningRecord { Status = "SignedAndVerified", Thumbprint = "T", Sha256 = "s", Timestamped = true, File = @"scripts\detect.ps1", Reason = "r" },
            Requirements = new RequirementsSigningRecord { Status = "NotRequested", Items = [new RequirementSigningItem { RuleId = "vpn", File = @"scripts\requirements\vpn.ps1", Status = "NotRequested" }] },
            Deployment = new DeploymentSigningRecord { Status = "Failed", LaunchersBypassFree = false, Files = [new DeploymentSigningFile { RelativePath = "install.ps1", Owner = "ThirdParty" }] }
        };
        var node = record.ToJsonNode();
        foreach (var key in new[] { "PolicyDigest", "Detection", "Requirements", "Deployment" })
            Assert.NotNull(node[key]);
        Assert.Equal(@"scripts\detect.ps1", (string?)node["Detection"]!["File"]);
        Assert.False((bool)node["Deployment"]!["LaunchersBypassFree"]!);
        Assert.Equal("vpn", (string?)node["Requirements"]!["Items"]![0]!["RuleId"]);

        var back = ScriptSigningRecord.FromJsonNode(node)!;
        Assert.Equal("SignedAndVerified", back.Detection.Status);
        Assert.Equal("ThirdParty", back.Deployment.Files[0].Owner);
    }
}

public sealed class DeploymentLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apchain-" + Guid.NewGuid().ToString("N")[..8]);

    public DeploymentLauncherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private LauncherChainResult Audit(SigningPolicy? policy = null, IEnumerable<LauncherCommand>? commands = null) =>
        DeploymentLauncher.AuditChain(_root, commands, policy ?? new SigningPolicy());

    private void Write(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_root, name), string.Join("\r\n", lines));

    [Fact]
    public void Unsigned_ReproducesTheHistoricalLauncherString()
    {
        var launcher = DeploymentLauncher.Build("install.ps1", false);
        Assert.Equal("powershell.exe -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"", launcher.BatInvoke);
        Assert.Equal("powershell.exe -NonInteractive -ExecutionPolicy Bypass -File \"install.ps1\"", launcher.CommandLine);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    public void Signed_CarriesNoExecutionPolicyOrEncodedCommand(string host)
    {
        var launcher = DeploymentLauncher.Build("install.ps1", true, host);
        foreach (var text in new[] { launcher.CommandLine, launcher.BatBody, launcher.BatInvoke })
        {
            Assert.DoesNotMatch("(?i)executionpolicy", text);
            Assert.DoesNotMatch(@"(?i)\bbypass\b", text);
            Assert.DoesNotMatch(@"(?i)-ep\b", text);
            Assert.DoesNotMatch("(?i)encodedcommand", text);
        }
        Assert.Contains("-NoProfile -NonInteractive -File", launcher.CommandLine);
    }

    [Fact]
    public void X86_SelectsTheSysWow64HostExplicitly()
    {
        var launcher = DeploymentLauncher.Build("install.ps1", true, "x86");
        Assert.Equal(@"%SystemRoot%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe", launcher.Executable);
        Assert.StartsWith(@"%SystemRoot%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe ", launcher.CommandLine);
    }

    [Fact]
    public void SignedOutput_PassesTheChainAudit()
    {
        File.WriteAllText(Path.Combine(_root, "install.bat"), DeploymentLauncher.Build("install.ps1", true).BatBody);
        var result = Audit(new SigningPolicy { SignDeployment = true });
        Assert.True(result.BypassFree);
        Assert.Empty(result.Findings);
        Assert.Equal(1, result.FilesInspected);
    }

    [Theory]
    [InlineData("-ExecutionPolicy Bypass")]
    [InlineData("-ep Bypass")]
    [InlineData("-ex Bypass")]
    [InlineData("-ExecutionPo Bypass")]
    [InlineData("-executionpolicy bypass")]
    [InlineData("/ExecutionPolicy Bypass")]
    public void CatchesEveryExecutionPolicySpellingAndAbbreviation(string spelling)
    {
        Write("ep.bat", $"PowerShell.exe -NonInteractive {spelling} -File \"%~dp0install.ps1\"");
        Assert.Contains(Audit().Findings, f => f.Code == DeploymentLauncher.CodeExecutionPolicy);
    }

    [Theory]
    [InlineData("-EncodedCommand")]
    [InlineData("-enc")]
    [InlineData("-e")]
    public void CatchesEncodedCommandInvocations(string spelling)
    {
        Write("enc.bat", $"PowerShell.exe {spelling} ZQB4AGkAdAAgADAA");
        Assert.Contains(Audit().Findings, f => f.Code == DeploymentLauncher.CodeEncodedCommand);
    }

    [Fact]
    public void FlagsACustomCommandCarryingBypass_AndInspectsDeploymentTypeCommands()
    {
        var result = Audit(commands:
        [
            new LauncherCommand("InstallCommandLine", "cmd.exe /c setup.exe && powershell -nop -executionpolicy bypass -file extra.ps1"),
            new LauncherCommand("DeploymentTypes[1].InstallCommandLine", "powershell.exe -ExecutionPolicy Bypass -File \"Invoke-AppDeployToolkit.ps1\"")
        ]);
        Assert.Equal(2, result.CommandsInspected);
        Assert.Contains(result.Findings, f => f.Code == DeploymentLauncher.CodeCustomCommandBypass);
        Assert.Contains(result.Findings, f => f.Code == DeploymentLauncher.CodeExecutionPolicy && f.File == "DeploymentTypes[1].InstallCommandLine");
    }

    [Fact]
    public void AcceptsTheSignedChildLaunchShape_AndRejectsOneThatStillPassesAPolicy()
    {
        Write("install.ps1",
            "$steps = @(\"before.ps1\", \"generated.ps1\", \"after.ps1\")",
            "foreach ($step in $steps) {",
            "    $p = Start-Process -FilePath \"powershell.exe\" -ArgumentList @(\"-NoProfile\", \"-NonInteractive\", \"-File\", \"$PSScriptRoot\\$step\") -Wait -PassThru",
            "    if ($p.ExitCode -ne 0) { exit $p.ExitCode }",
            "}",
            "exit 0");
        Assert.True(Audit(new SigningPolicy { SignDeployment = true }).BypassFree);

        Write("install.ps1",
            "$p = Start-Process -FilePath \"powershell.exe\" -ArgumentList @(\"-NoProfile\", \"-NonInteractive\", \"-ExecutionPolicy\", \"Bypass\", \"-File\", \"step.ps1\") -Wait -PassThru",
            "exit $p.ExitCode");
        var ex = Assert.Throws<InvalidOperationException>(() => Audit(new SigningPolicy { SignDeployment = true }));
        Assert.StartsWith("Signed deployment mode refuses this build: ", ex.Message);
    }

    [Theory]
    [InlineData("pwsh.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"")]
    [InlineData("start \"\" \"%PSEXE%\" -ep Bypass -File \"%~dp0install.ps1\"")]
    [InlineData("& $exe -enc ZQB4AGkAdAAgADAA")]
    public void TokenScansALauncherThatNeverNamesPowerShellLiterally(string line)
    {
        Write("indirect.cmd", line);
        Assert.False(Audit().BypassFree);
    }

    [Fact]
    public void CommentsAndStringsThatMerelyContainBypass_AreNotFindings()
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
    public void ABarePolicyNameOnALineThatNamesTheExecutionPolicy_IsAFinding()
    {
        Write("install.ps1", "Set-ExecutionPolicy RemoteSigned -Scope Process", "exit 0");
        var finding = Assert.Single(Audit().Findings);
        Assert.Equal(DeploymentLauncher.CodePolicyRelaxation, finding.Code);
        Assert.Equal("remotesigned", finding.Message[(finding.Message.IndexOf('\'') + 1)..finding.Message.LastIndexOf('\'')]);
    }

    [Fact]
    public void NothingInsideASignatureBlockIsScanned()
    {
        Write("signed.ps1", "exit 0",
            "# SIG # Begin signature block",
            "# -ExecutionPolicy Bypass -enc AAAA",
            "-ep bypass",
            "# SIG # End signature block");
        Assert.Empty(Audit().Findings);
    }

    [Fact]
    public void RequireDeploymentAlone_EnforcesTheFindings()
    {
        File.WriteAllText(Path.Combine(_root, "install.bat"), DeploymentLauncher.Build("install.ps1", false).BatBody);
        var ex = Assert.Throws<InvalidOperationException>(() => Audit(new SigningPolicy { RequireDeployment = true }));
        Assert.Contains("refuses this build", ex.Message);
    }

    [Fact]
    public void UnsignedMode_IsInformational()
    {
        File.WriteAllText(Path.Combine(_root, "install.bat"), DeploymentLauncher.Build("install.ps1", false).BatBody);
        var result = Audit();
        Assert.False(result.BypassFree);
        Assert.False(result.Enforced);
    }

    [Fact]
    public void ReportsTheOffendingFileAndLine()
    {
        var file = Path.Combine(_root, "install.bat");
        File.WriteAllText(file, "@echo off\r\nPowerShell.exe -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"");
        var finding = Audit().Findings.First(f => f.Code == DeploymentLauncher.CodeExecutionPolicy);
        Assert.Equal(file, finding.File);
        Assert.Equal(2, finding.LineNumber);

        var ex = Assert.Throws<InvalidOperationException>(() => Audit(new SigningPolicy { SignDeployment = true }));
        Assert.Contains($"({file}:2)", ex.Message);
    }

    [Fact]
    public void ThirdPartyToolkitFolders_AreSkipped()
    {
        var toolkit = Path.Combine(_root, "Toolkit");
        Directory.CreateDirectory(toolkit);
        File.WriteAllText(Path.Combine(toolkit, "Invoke-AppDeployToolkit.ps1"), "Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process");
        File.WriteAllText(Path.Combine(_root, "vendor.ps1"), "powershell -ep bypass");
        var result = DeploymentLauncher.AuditChain(_root, null, new SigningPolicy { SignDeployment = true }, ["vendor.ps1"]);
        Assert.True(result.BypassFree);
        Assert.Equal(2, result.ThirdPartySkipped);
    }
}
