using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Tests.Signing;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Shared pieces for staging a JSON definition from a loopback server with a profile.</summary>
internal static class ProfileStageFixture
{
    public const string ApplicationId = "catalog:widget";
    public const string WrapperInstall =
        "$exePath = Join-Path $PSScriptRoot 'widget-1.2.3.exe'\r\n" +
        "$proc = Start-Process -FilePath $exePath -ArgumentList '/S' -Wait -PassThru -NoNewWindow\r\n" +
        "if (-not $proc) { exit 1 }\r\n" +
        "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\n" +
        "exit $proc.ExitCode";

    public static LoopbackFileServer Server() =>
        new(new Dictionary<string, byte[]> { ["widget-1.2.3.exe"] = [(byte)'M', (byte)'Z', .. new byte[64]] });

    public static AppDefinition Definition(LoopbackFileServer server) => new()
    {
        Id = "widget",
        Vendor = "Contoso",
        Application = "Widget",
        InstallerType = "EXE",
        InstallerFramework = "NSIS",
        InstallArgs = "/S",
        UninstallArgs = "/S",
        VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = server.Url("page"), Pattern = "(x)" },
        DownloadUrl = server.Url("widget-{version}.exe"),
        Detection = new DetectionConfig
        {
            Type = "RegistryKeyValue",
            RegistryKeyTemplate = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Widget",
            PropertyType = "Version",
            Operator = "GreaterEquals"
        }
    };

    public static StagingService Service() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
            new WrapperGeneratorService(), new ManifestService());

    public static string AddAsset(WorkbenchTestRoot t, string profileId, string name, string content) =>
        t.Store.AddAsset(ApplicationId, profileId, t.NewFile(Path.Combine(t.NewFolder(), name), content)).AssetId;

    public static Dictionary<string, string> Tree(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => File.ReadAllText(f), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A JSON definition staged end to end through StagingService with a profile that
/// sets an Extend install with hooks, a custom detection, a timing override, a source
/// file and a title mode.
/// </summary>
public sealed class ProfileStagePipelineTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private (RunSnapshot Snapshot, WorkbenchProfile Profile) ProfileSnapshot()
    {
        var profile = _t.NewProfile(ProfileStageFixture.ApplicationId, "Pilot Ring");
        var before = ProfileStageFixture.AddAsset(_t, profile.ProfileId, "before.ps1", "Write-Output 'before {{Version}}'\r\nexit 0");
        var after = ProfileStageFixture.AddAsset(_t, profile.ProfileId, "after.ps1", "exit 0");
        var ini = ProfileStageFixture.AddAsset(_t, profile.ProfileId, "settings.ini", "[widget]\r\nmode=pilot");
        var edit = profile.Clone();
        edit.Set("Install", WorkbenchJson.Parse($$"""{ "Mode": "Extend", "Before": "{{before}}", "After": "{{after}}" }"""));
        edit.Set("Detection", WorkbenchJson.Parse("""{ "Mode": "Custom", "Rule": { "Type": "File", "FilePath": "%ProgramFiles%\\Widget", "FileName": "widget.exe", "PropertyType": "Existence", "Is64Bit": true } }"""));
        edit.Set("Timing", WorkbenchJson.Parse("""{ "EstimatedMinutes": 20, "MaximumMinutes": 60 }"""));
        edit.Set("SourceFiles", WorkbenchJson.Parse($$"""[ { "Asset": "{{ini}}", "Destination": "config\\settings.ini" } ]"""));
        edit.Set("Application.TitleMode", JsonValue.Create("IncludeVersion"));
        var saved = _t.Store.SaveProfile(edit);
        var snapshot = _t.Store.CreateRunSnapshot(new RunSnapshotRequest
        {
            ApplicationId = ProfileStageFixture.ApplicationId,
            ProfileId = saved.ProfileId,
            RunOverrides = new JsonObject { ["MaximumMinutes"] = 90 }
        });
        return (snapshot, saved);
    }

    [Fact]
    public async Task AProfileBuildStagesItsTreeManifestPlanDigestAndBuildRecord()
    {
        using var server = ProfileStageFixture.Server();
        var (snapshot, profile) = ProfileSnapshot();
        var root = _t.NewFolder("root");

        var result = await ProfileStageFixture.Service().StageAsync(ProfileStageFixture.Definition(server), root,
            knownVersion: "1.2.3", inputs: new StageInputs { RunSnapshot = snapshot });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Path.Combine(root, "profiles", profile.ProfileId, "Widget-x64", "1.2.3"), result.ContentPath);

        var tree = ProfileStageFixture.Tree(result.ContentPath);
        Assert.Equal(
            [@"config\settings.ini", "install-after.ps1", "install-before.ps1", "install-generated.ps1", "install.bat", "install.ps1",
             "stage-manifest.json", "uninstall.bat", "uninstall.ps1", "widget-1.2.3.exe"],
            tree.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(ProfileStageFixture.WrapperInstall, tree["install-generated.ps1"]);
        Assert.Equal("Write-Output 'before 1.2.3'\r\nexit 0", tree["install-before.ps1"]);
        Assert.Equal(ExtendOrchestrator.Build("install-before.ps1", "install-generated.ps1", "install-after.ps1"), tree["install.ps1"]);
        Assert.Equal(DeploymentLauncher.Build("install.ps1", false).BatBody, tree["install.bat"]);
        Assert.Equal("[widget]\r\nmode=pilot", tree[@"config\settings.ini"]);

        var manifest = new ManifestService().Read(result.ManifestPath);
        Assert.Equal(snapshot.BuildId, manifest.BuildId);
        Assert.Equal(ProfileStageFixture.ApplicationId, manifest.ApplicationId);
        Assert.Equal(profile.ProfileId, manifest.ProfileId);
        Assert.Equal(profile.Revision, manifest.ProfileRevision);
        Assert.Equal("Custom", manifest.DetectionSource);
        Assert.Equal("File", manifest.Detection.Type);
        Assert.Equal("widget.exe", manifest.Detection.FileName);
        Assert.False(manifest.Detection.IntuneScriptConversion);
        Assert.Equal(20, manifest.Timing.EstimatedMinutes);
        Assert.Equal(90, manifest.Timing.MaximumMinutes);
        Assert.Equal(90, manifest.RunOverrides["MaximumMinutes"]!.GetValue<int>());
        Assert.Equal("IncludeVersion", manifest.TitleMode);
        Assert.Equal("Widget - 1.2.3 (x64)", manifest.AppName);
        Assert.Equal("install.bat", manifest.SetupFile);
        Assert.Equal(["InstallBefore", "InstallAfter", "InstallOrchestrator", "SourceFile"], manifest.CustomAssets.Select(a => a.Category));
        var source = manifest.CustomAssets.Single(a => a.Category == "SourceFile");
        Assert.Equal(WorkbenchJson.FileSha256(Path.Combine(result.ContentPath, "config", "settings.ini")), source.Sha256);
        Assert.Contains(manifest.FileHashes!, h => h.RelativePath == @"config\settings.ini");
        Assert.Contains(manifest.FileHashes!, h => h.RelativePath == "install-before.ps1");

        var signing = ScriptSigningRecord.FromJsonNode(manifest.ScriptSigning)!;
        Assert.Equal(new SigningPolicy().PolicyDigest, signing.PolicyDigest);
        Assert.Equal(SigningStatus.NotRequested, signing.Deployment.Status);
        Assert.Contains(signing.Deployment.Files, f => f.RelativePath == "install-generated.ps1");

        var written = WorkbenchJson.Parse(File.ReadAllText(result.ManifestPath))!.AsObject();
        Assert.Equal(StageManifestPlanDigest.Compute(written), manifest.PlanDigest);
        Assert.Matches("^[0-9A-F]{64}$", manifest.PlanDigest!);

        var record = BuildRecords.Latest(_t.Store.DataRoot, ProfileStageFixture.ApplicationId, profile.ProfileId)!;
        Assert.Equal(snapshot.BuildId, record["BuildId"]!.GetValue<string>());
        Assert.Equal(manifest.PlanDigest, record["PlanDigest"]!.GetValue<string>());
        Assert.Equal(result.ContentPath, record["StageRoot"]!.GetValue<string>());
        Assert.Equal(90, record["RunOverrides"]!["MaximumMinutes"]!.GetValue<int>());
        var stored = _t.Store.GetProfile(ProfileStageFixture.ApplicationId, profile.ProfileId);
        Assert.Equal(60, WorkbenchJson.AsInt(stored.Get("Timing.MaximumMinutes").Value));
        Assert.Equal(profile.Revision, stored.Revision);

        Assert.Equal(result.ManifestPath,
            BuildRecords.ResolveStageManifestForBuild(snapshot.BuildId, Path.Combine(root, "profiles")).ManifestPath);

        var mecm = MecmTarget.BuildPackageScript(manifest, manifest.AppName, @"\\server\share\Applications\Contoso\Widget (x64)\1.2.3-PilotRing",
            "P01", "comment", 15, 30, ConditionTemplateService.Defaults(), manifest.CommandOverrides,
            scriptSigning: MecmScriptSigningContext.FromManifest(manifest, new SigningPolicy()));
        Assert.Contains(" -EstimatedRuntimeMins 20 -MaximumRuntimeMins 90", mecm);
        Assert.Equal($"1.2.3-PilotRing-{profile.ProfileId[..8]}", ProfileContentScope.ForManifest(manifest, _t.Store.DataRoot).ScopedVersionFolder(manifest.SoftwareVersion));
    }

    [Fact]
    public async Task ARebuildOfTheSameProfileReplacesItsOwnFilesAndKeepsOneTree()
    {
        using var server = ProfileStageFixture.Server();
        var (snapshot, profile) = ProfileSnapshot();
        var root = _t.NewFolder("root");
        var first = await ProfileStageFixture.Service().StageAsync(ProfileStageFixture.Definition(server), root,
            knownVersion: "1.2.3", inputs: new StageInputs { RunSnapshot = snapshot });
        Assert.True(first.Success, first.ErrorMessage);

        var edit = _t.Store.GetProfile(ProfileStageFixture.ApplicationId, profile.ProfileId);
        edit.Set("Install", WorkbenchJson.Parse("""{ "Mode": "Generated" }"""));
        edit.Set("SourceFiles", new JsonArray());
        var saved = _t.Store.SaveProfile(edit);
        var second = await ProfileStageFixture.Service().StageAsync(ProfileStageFixture.Definition(server), root,
            knownVersion: "1.2.3", inputs: new StageInputs
            {
                RunSnapshot = _t.Store.CreateRunSnapshot(new RunSnapshotRequest { ApplicationId = ProfileStageFixture.ApplicationId, ProfileId = saved.ProfileId })
            });

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(first.ContentPath, second.ContentPath);
        Assert.Equal(["install.bat", "install.ps1", "stage-manifest.json", "uninstall.bat", "uninstall.ps1", "widget-1.2.3.exe"],
            ProfileStageFixture.Tree(second.ContentPath).Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(ProfileStageFixture.WrapperInstall, File.ReadAllText(Path.Combine(second.ContentPath, "install.ps1")));
        Assert.Equal(2, BuildRecords.List(_t.Store.DataRoot, ProfileStageFixture.ApplicationId, profile.ProfileId).Count);
    }

    [Fact]
    public async Task TwoProfilesOfOneApplicationStageIntoDisjointRootsWithTheirOwnBuilds()
    {
        using var server = ProfileStageFixture.Server();
        var root = _t.NewFolder("root");
        var alpha = _t.NewProfile(ProfileStageFixture.ApplicationId, "Alpha");
        var beta = _t.Store.CopyProfile(ProfileStageFixture.ApplicationId, alpha.ProfileId, "Beta");
        var manifests = new List<StageManifest>();
        var roots = new List<string>();
        foreach (var (profile, overrides) in new[] { (alpha, new JsonObject { ["EstimatedMinutes"] = 7, ["MaximumMinutes"] = 21 }), (beta, new JsonObject()) })
        {
            var snapshot = _t.Store.CreateRunSnapshot(new RunSnapshotRequest
            {
                ApplicationId = ProfileStageFixture.ApplicationId, ProfileId = profile.ProfileId, RunOverrides = overrides
            });
            var result = await ProfileStageFixture.Service().StageAsync(ProfileStageFixture.Definition(server), root,
                knownVersion: "1.2.3", inputs: new StageInputs { RunSnapshot = snapshot });
            Assert.True(result.Success, result.ErrorMessage);
            manifests.Add(new ManifestService().Read(result.ManifestPath));
            roots.Add(result.ContentPath);
        }

        Assert.NotEqual(roots[0], roots[1]);
        Assert.Empty(Directory.GetFiles(roots[0], "*", SearchOption.AllDirectories)
            .Intersect(Directory.GetFiles(roots[1], "*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase));
        Assert.NotEqual(manifests[0].BuildId, manifests[1].BuildId);
        Assert.NotEqual(manifests[0].PlanDigest, manifests[1].PlanDigest);
        Assert.All(manifests, m => Assert.Equal(4, m.SchemaVersion));
        Assert.Equal((7, 21), (manifests[0].Timing.EstimatedMinutes!.Value, manifests[0].Timing.MaximumMinutes!.Value));
        for (var i = 0; i < 2; i++)
        {
            var record = Assert.Single(BuildRecords.List(_t.Store.DataRoot, ProfileStageFixture.ApplicationId, manifests[i].ProfileId));
            Assert.Equal(manifests[i].BuildId, record["BuildId"]!.GetValue<string>());
            Assert.Equal(roots[i], BuildRecords.ResolveStageManifestForBuild(manifests[i].BuildId, Path.Combine(root, "profiles")).StageRoot);
        }
        Assert.Equal($"1.2.3-Alpha-{alpha.ProfileId[..8]}", ProfileContentScope.ForManifest(manifests[0], _t.Store.DataRoot).ScopedVersionFolder("1.2.3"));
        Assert.Equal($"1.2.3-Beta-{beta.ProfileId[..8]}", ProfileContentScope.ForManifest(manifests[1], _t.Store.DataRoot).ScopedVersionFolder("1.2.3"));
    }

    [Fact]
    public async Task TheStagedOrchestratorPropagatesExitCodesThroughInstallBat()
    {
        using var server = ProfileStageFixture.Server();
        var (snapshot, _) = ProfileSnapshot();
        var result = await ProfileStageFixture.Service().StageAsync(ProfileStageFixture.Definition(server), _t.NewFolder("root"),
            knownVersion: "1.2.3", inputs: new StageInputs { RunSnapshot = snapshot });
        Assert.True(result.Success, result.ErrorMessage);

        int RunWith(string generated, out bool afterRan)
        {
            var copy = _t.NewFolder();
            foreach (var name in new[] { "install.bat", "install.ps1" })
                File.Copy(Path.Combine(result.ContentPath, name), Path.Combine(copy, name));
            var marker = Path.Combine(copy, "after-ran.txt");
            File.WriteAllText(Path.Combine(copy, "install-before.ps1"), "exit 0", Encoding.ASCII);
            File.WriteAllText(Path.Combine(copy, "install-generated.ps1"), generated, Encoding.ASCII);
            File.WriteAllText(Path.Combine(copy, "install-after.ps1"), $"Set-Content -LiteralPath '{marker}' -Value 'ran'; exit 0", Encoding.ASCII);

            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = copy
            };
            psi.Environment.Remove("PSModulePath");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(Path.Combine(copy, "install.bat"));
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(120_000));
            afterRan = File.Exists(marker);
            return proc.ExitCode;
        }

        Assert.Equal(1603, RunWith("exit 1603", out var afterRanOnFailure));
        Assert.False(afterRanOnFailure);
        Assert.Equal(3010, RunWith("exit 3010", out var afterRanOnReboot));
        Assert.True(afterRanOnReboot);
        Assert.Equal(0, RunWith("exit 0", out var afterRanOnSuccess));
        Assert.True(afterRanOnSuccess);
    }

    [Fact]
    public async Task ARequireSwitchWithNoUsableCertificateRefusesTheBuildBeforeAnyContent()
    {
        using var server = ProfileStageFixture.Server();
        var root = Path.Combine(_t.Root, "never-written");

        var result = await ProfileStageFixture.Service().StageAsync(ProfileStageFixture.Definition(server), root,
            knownVersion: "1.2.3", inputs: new StageInputs
            {
                SigningPolicy = new SigningPolicy { RequireDeployment = true, CertificateThumbprint = new string('B', 40) }
            });

        Assert.False(result.Success);
        Assert.StartsWith("SigningCertificateUnavailable:", result.ErrorMessage);
        Assert.False(Directory.Exists(root));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ADefaultBuildWithSigningOffStagesWhatItStagedBefore()
    {
        using var server = ProfileStageFixture.Server();
        var root = _t.NewFolder("root");
        var result = await ProfileStageFixture.Service().StageAsync(ProfileStageFixture.Definition(server), root,
            knownVersion: "1.2.3", inputs: new StageInputs { SigningPolicy = new SigningPolicy() });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Path.Combine(root, "Widget-x64", "1.2.3"), result.ContentPath);
        Assert.Equal(ProfileStageFixture.WrapperInstall, File.ReadAllText(Path.Combine(result.ContentPath, "install.ps1")));
        var manifest = new ManifestService().Read(result.ManifestPath);
        Assert.Equal("", manifest.BuildId);
        Assert.Null(manifest.SetupFile);
        Assert.Null(manifest.Detection.IntuneScriptConversion);
        Assert.Empty(BuildRecords.List(_t.Store.DataRoot, ProfileStageFixture.ApplicationId));
    }
}

/// <summary>
/// A signed profile build with a throwaway CurrentUser code-signing certificate:
/// the manifest block, signatures that verify, launchers with no execution-policy
/// argument and a launcher audit that passes.
/// </summary>
[Collection(SigningCollection.Name)]
public sealed class SignedProfileStageTests(SigningCertificateFixture fixture) : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task ASignedBuildSignsVerifiesAndShipsBypassFreeLaunchers()
    {
        using var server = ProfileStageFixture.Server();
        var profile = _t.NewProfile(ProfileStageFixture.ApplicationId, "Signed");
        var before = ProfileStageFixture.AddAsset(_t, profile.ProfileId, "before.ps1", "exit 0");
        var edit = profile.Clone();
        edit.Set("Install", WorkbenchJson.Parse($$"""{ "Mode": "Extend", "Before": "{{before}}" }"""));
        edit.Set("Detection", WorkbenchJson.Parse("""{ "Mode": "Custom", "Rule": { "Type": "Script", "ScriptText": "if (Test-Path \"$env:ProgramFiles\\Widget\\widget.exe\") { Write-Output 'Installed' }" } }"""));
        var saved = _t.Store.SaveProfile(edit);
        var policy = fixture.Policy(sd: true, sp: true);
        var snapshot = _t.Store.CreateRunSnapshot(new RunSnapshotRequest
        {
            ApplicationId = ProfileStageFixture.ApplicationId,
            ProfileId = saved.ProfileId,
            SigningPolicy = JsonSerializer.SerializeToNode(policy)
        }, new ScriptSigningCertificateGate());

        var result = await ProfileStageFixture.Service().StageAsync(ProfileStageFixture.Definition(server), _t.NewFolder("root"),
            knownVersion: "1.2.3", inputs: new StageInputs { RunSnapshot = snapshot });

        Assert.True(result.Success, result.ErrorMessage);
        var stage = result.ContentPath;
        var manifest = new ManifestService().Read(result.ManifestPath);
        var record = ScriptSigningRecord.FromJsonNode(manifest.ScriptSigning)!;
        Assert.Equal(SigningPolicy.Normalize(policy).PolicyDigest, record.PolicyDigest);
        Assert.Equal(SigningStatus.SignedAndVerified, record.Detection.Status);
        Assert.Equal(@"scripts\detect.ps1", record.Detection.File);
        Assert.Equal(fixture.Thumbprint, record.Detection.Thumbprint, ignoreCase: true);
        Assert.Equal(SigningStatus.SignedAndVerified, record.Deployment.Status);
        Assert.True(record.Deployment.LaunchersBypassFree);
        Assert.Empty(manifest.ScriptSigning!["LauncherFindings"]!.AsArray());
        Assert.True(manifest.ScriptSigning!["LaunchersBypassFree"]!.GetValue<bool>());

        foreach (var file in new[] { @"scripts\detect.ps1", "install.ps1", "install-generated.ps1", "install-before.ps1", "uninstall.ps1" })
        {
            var verify = ScriptSignatureVerifier.Verify(Path.Combine(stage, file));
            Assert.True(verify.SignatureIntact, $"{file}: {verify.Status}");
            Assert.Equal(fixture.Thumbprint, verify.Thumbprint, ignoreCase: true);
        }
        Assert.Contains(record.Deployment.Files, f => f.RelativePath == "install-generated.ps1" && f.Status == SigningStatus.SignedAndVerified);
        Assert.Equal(ScriptSignatureVerifier.FileSha256Hex(Path.Combine(stage, "scripts", "detect.ps1")), record.Detection.Sha256);

        foreach (var launcher in new[] { "install.bat", "uninstall.bat" })
        {
            var body = File.ReadAllText(Path.Combine(stage, launcher));
            Assert.DoesNotContain("ExecutionPolicy", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("powershell.exe -NoProfile -NonInteractive -File", body);
        }
        Assert.DoesNotContain("ExecutionPolicy", File.ReadAllText(Path.Combine(stage, "install.ps1")).Split("# SIG # Begin")[0]);

        var audit = DeploymentLauncher.AuditChain(stage, ScriptSigningInput.FromManifest(manifest).Commands, SigningPolicy.Normalize(policy));
        Assert.True(audit.BypassFree);
        Assert.True(audit.Enforced);

        var script = MecmTarget.BuildPackageScript(manifest, manifest.AppName, @"\\server\share\Applications\Contoso\Widget\1.2.3-Signed",
            "P01", "comment", 15, 30, ConditionTemplateService.Defaults(),
            scriptSigning: MecmScriptSigningContext.FromManifest(manifest, policy));
        Assert.Contains($"{MecmScriptSigningPs.DetectionFileFunction} -Path '\\\\server\\share\\Applications\\Contoso\\Widget\\1.2.3-Signed\\scripts\\detect.ps1'", script);

        var rule = Assert.Single(IntunePublishService.BuildRules(manifest.Detection, IntuneScriptRuleContext.FromManifest(manifest, policy, stage)))!;
        Assert.True((bool)rule["enforceSignatureCheck"]!);
    }
}
