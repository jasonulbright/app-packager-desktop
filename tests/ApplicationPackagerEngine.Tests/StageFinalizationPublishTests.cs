using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// What a finalized manifest changes at publish: requirement operations, the setup
/// file, deployment type timing and execution, the Intune script-conversion opt-in,
/// the custom icon, the stored signing policy and the profile-scoped network folder.
/// </summary>
public sealed class StageFinalizationPublishTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apwb-publish-" + Guid.NewGuid().ToString("N"));

    public StageFinalizationPublishTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static JsonNode Ops(string json) => WorkbenchJson.Parse(json)!;

    [Fact]
    public void AddReplaceAndRemoveMatchByRuleIdOrConditionId()
    {
        var manifest = new StageManifest
        {
            Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "x64" }, new StageRequirement { ConditionId = "os-language", Cultures = ["de-DE"] }]
        };
        RequirementOperations.Apply(manifest, Ops("""
            [ { "Op": "Replace", "RuleId": "cpu-arch", "Rule": { "ConditionId": "cpu-arch", "Value": "ARM64" } },
              { "Op": "Remove", "RuleId": "OS-LANGUAGE" },
              { "Op": "Add", "RuleId": "vpn", "Rule": { "ConditionId": "vpn-connected", "Value": true } } ]
            """));

        Assert.Equal(["cpu-arch", "vpn"], manifest.Requirements!.Select(RequirementOperations.RuleIdOf));
        Assert.Equal("ARM64", manifest.Requirements![0].Value!.ToString());
    }

    [Fact]
    public void AStarOperationReachesTheBaseAndEveryEntryAndANamedOneOnlyThatEntry()
    {
        var manifest = new StageManifest
        {
            DeploymentTypes = [new StageDeploymentType { NameSuffix = "ARM64" }, new StageDeploymentType { NameSuffix = "x64" }]
        };
        RequirementOperations.Apply(manifest, Ops("""
            [ { "Op": "Add", "RuleId": "all", "Rule": { "ConditionId": "vpn-connected", "Value": true }, "AppliesTo": ["*"] },
              { "Op": "Add", "RuleId": "arm", "Rule": { "ConditionId": "cpu-arch", "Value": "ARM64" }, "AppliesTo": ["arm64"] } ]
            """));

        Assert.Equal(["all"], manifest.Requirements!.Select(r => r.RuleId));
        Assert.Equal(["all", "arm"], manifest.DeploymentTypes[0].Requirements!.Select(r => r.RuleId));
        Assert.Equal(["all"], manifest.DeploymentTypes[1].Requirements!.Select(r => r.RuleId));
    }

    [Fact]
    public void TheSingleDeploymentTypeTakesTheManifestRequirements()
    {
        var manifest = new StageManifest { Requirements = [new StageRequirement { RuleId = "r", ConditionId = "cpu-arch", Value = "x64" }] };
        var spec = Assert.Single(DeploymentTypeSpecResolver.Resolve(manifest, @"\\s\c", "App"));
        Assert.Equal("cpu-arch", Assert.Single(spec.Requirements).ConditionId);
    }

    [Theory]
    [InlineData(null, new string[0], "install.bat")]
    [InlineData("\"%~dp0Deploy-Application.exe\" -DeploymentType Install", new[] { "Deploy-Application.exe" }, "Deploy-Application.exe")]
    [InlineData(".\\setup.cmd /quiet", new[] { "setup.cmd" }, "setup.cmd")]
    [InlineData("missing.exe /S", new[] { "Deploy-Application.ps1" }, "Deploy-Application.ps1")]
    [InlineData("..\\outside.exe", new string[0], "install.bat")]
    public void TheSetupFileIsTheCommandsFirstExistingEntryThenTheFallbacks(string? command, string[] files, string expected)
    {
        var stage = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "install.bat"), "@echo off");
        File.WriteAllText(Path.Combine(_dir, "outside.exe"), "MZ");
        foreach (var file in files) File.WriteAllText(Path.Combine(stage, file), "x");
        var manifest = new StageManifest { CommandOverrides = command is null ? null : new StageCommandOverrides { Install = command } };

        Assert.Equal(expected, SetupFileResolver.Resolve(stage, manifest));
    }

    [Fact]
    public void AVariantManifestResolvesTheSetupFileInItsFirstEntrysSubfolder()
    {
        var stage = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(stage, "arm64"));
        File.WriteAllText(Path.Combine(stage, "arm64", "go.bat"), "x");
        var manifest = new StageManifest
        {
            DeploymentTypes = [new StageDeploymentType { NameSuffix = "ARM64", ContentSubpath = "arm64", InstallCommandLine = "go.bat" }]
        };
        Assert.Equal(@"arm64\go.bat", SetupFileResolver.Resolve(stage, manifest));
    }

    [Fact]
    public void ManifestTimingOutranksTheCallerDefaults()
    {
        var timing = DeploymentTypeTiming.Resolve(new StageTiming { EstimatedMinutes = 12, MaximumMinutes = 40 }, 15, 30);
        Assert.Equal((12, 40, "manifest"), (timing.EstimatedRuntimeMins, timing.MaximumRuntimeMins, timing.Source));
        Assert.Equal("parameters", DeploymentTypeTiming.Resolve(new StageTiming(), 15, 30).Source);
    }

    [Theory]
    [InlineData(0, 30, "out of range: estimated installation time is 0 minutes")]
    [InlineData(10, 1441, "out of range: maximum allowed run time is 1441 minutes")]
    [InlineData(60, 30, "inconsistent: estimated installation time 60 minutes exceeds the maximum allowed run time 30 minutes")]
    public void TimingOutsideTheBoundsIsRefused(int estimated, int maximum, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeploymentTypeTiming.Resolve(new StageTiming { EstimatedMinutes = estimated, MaximumMinutes = maximum }, context: "deployment type 'App'"));
        Assert.Contains("Timing for the deployment type 'App' is " + message, ex.Message);
    }

    [Fact]
    public void AMaximumAboveTwelveHoursIsReportedNotRefused()
    {
        var log = new List<string>();
        var timing = DeploymentTypeTiming.Resolve(new StageTiming { MaximumMinutes = 721 }, 15, 30, onLog: log.Add);
        Assert.Equal(721, timing.MaximumRuntimeMins);
        Assert.Contains(log, l => l.Contains("exceeds the 12-hour monitoring ceiling"));
        log.Clear();
        DeploymentTypeTiming.Resolve(new StageTiming { MaximumMinutes = 720 }, 15, 30, onLog: log.Add);
        Assert.Empty(log);
    }

    private static StageManifest Registry() => new()
    {
        AppName = "App",
        Publisher = "Vendor",
        SoftwareVersion = "1.0",
        Detection = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.0"
        }
    };

    private static string Script(StageManifest manifest, List<string>? log = null) =>
        MecmTarget.BuildPackageScript(manifest, manifest.AppName, @"\\server\share\Apps\Vendor\App\1.0", "P01", "comment",
            15, 30, ConditionTemplateService.Defaults(), onLog: log is null ? null : log.Add);

    [Fact]
    public void TheDeploymentTypeCarriesTheManifestTimingAndExecution()
    {
        var manifest = Registry();
        manifest.Timing = new StageTiming { EstimatedMinutes = 12, MaximumMinutes = 40 };
        manifest.LogonRequirementType = "OnlyWhenUserLoggedOn";
        manifest.RequireUserInteraction = true;
        var log = new List<string>();

        var script = Script(manifest, log);

        Assert.Contains(" -InstallationBehaviorType InstallForSystem -LogonRequirementType OnlyWhenUserLoggedOn -EstimatedRuntimeMins 12 -MaximumRuntimeMins 40 -RequireUserInteraction:$true", script);
        Assert.Contains(" -UserInteractionMode Hidden", script);
        Assert.DoesNotContain("-RequireUserInteraction $true", script);
        Assert.Contains("Runtime minutes (manifest)   : estimated 12, maximum 40", log);
    }

    [Fact]
    public async Task TheInteractionArgumentsBindToASwitchParameterInWindowsPowerShell()
    {
        var manifest = Registry();
        manifest.RequireUserInteraction = true;
        var script = Script(manifest);
        var start = script.IndexOf(" -RequireUserInteraction", StringComparison.Ordinal);
        var fragment = script[start..].Split(" -")[1];
        var probe = "function Add-Probe { [CmdletBinding()] param([switch]$RequireUserInteraction, [string]$UserInteractionMode) " +
                    "[Console]::Out.Write(('{0}|{1}' -f $RequireUserInteraction.IsPresent, $UserInteractionMode)) }; " +
                    "Add-Probe -" + fragment + " -UserInteractionMode Hidden";

        var run = await BoundedProcessRunner.RunAsync(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(probe))
        }, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal("", run.StdErr.Trim());
        Assert.Equal("True|Hidden", run.StdOut.Trim());
    }

    [Fact]
    public void AnUnprofiledManifestKeepsTheCallerTimingAndTheContextBehavior()
    {
        var system = Script(Registry());
        Assert.Contains(" -InstallationBehaviorType InstallForSystem -LogonRequirementType WhetherOrNotUserLoggedOn -EstimatedRuntimeMins 15 -MaximumRuntimeMins 30 -ContentFallback", system);

        var user = Registry();
        user.InstallContext = "User";
        Assert.Contains(" -InstallationBehaviorType InstallForUser -EstimatedRuntimeMins 15", Script(user));
    }

    [Fact]
    public void AVariantContextOutranksTheManifestContext()
    {
        var manifest = Registry();
        manifest.InstallationBehaviorType = "InstallForSystem";
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "User", InstallationBehaviorType = "InstallForUser" },
            new StageDeploymentType { NameSuffix = "System" }
        ];
        var script = Script(manifest);
        Assert.Contains("-InstallForUser", script.Replace("-InstallationBehaviorType ", "-"));
        Assert.Contains(" -InstallationBehaviorType InstallForSystem -LogonRequirementType WhetherOrNotUserLoggedOn", script);
    }

    [Fact]
    public void AnUnknownBehaviorIsRefusedBeforeItReachesTheScript()
    {
        var manifest = Registry();
        manifest.InstallationBehaviorType = "InstallForSystem'; Remove-Item C:\\ #";
        Assert.Throws<InvalidOperationException>(() => Script(manifest));
    }

    private static StageDetection PrefixDetection(bool? conversion) => new()
    {
        Type = "RegistryKeyValue",
        RegistryKeyRelative = @"SOFTWARE\Contoso",
        ValueName = "DisplayVersion",
        ExpectedValue = "5.",
        Operator = "BeginsWith",
        IntuneScriptConversion = conversion
    };

    [Fact]
    public void APrefixOperatorWithTheOptInBecomesAScriptRule()
    {
        var rule = Assert.Single(IntunePublishService.BuildRules(PrefixDetection(true)))!;
        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", (string?)rule["@odata.type"]);
    }

    [Fact]
    public void APrefixOperatorOnAManifestThatRefusesConversionIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(PrefixDetection(false)));
        Assert.Equal("Detection operator 'BeginsWith' on registry value 'DisplayVersion' has no native Intune rule; converting it to a PowerShell detection script is an explicit profile choice (Detection.IntuneScriptConversion). Set it in the workbench, or publish this application to ConfigMgr only.", ex.Message);
    }

    [Fact]
    public void AManifestStagedBeforeTheFieldExistedStillConverts()
    {
        var detection = WorkbenchJson.Parse("""{ "Type": "RegistryKeyValue", "RegistryKeyRelative": "SOFTWARE\\Contoso", "ValueName": "DisplayVersion", "ExpectedValue": "5.", "Operator": "EndsWith" }""")!
            .Deserialize<StageDetection>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Null(detection.IntuneScriptConversion);
        Assert.Single(IntunePublishService.BuildRules(detection));
    }

    [Fact]
    public void ACompoundClauseFollowsTheTopLevelChoice()
    {
        var compound = new StageDetection
        {
            Type = "Compound",
            Connector = "And",
            IntuneScriptConversion = false,
            Clauses = [PrefixDetection(null), new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Contoso" }]
        };
        Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(compound));
    }

    private static IntunePublishOptions Options() => new()
    {
        TenantId = "tenant-id", ClientId = "client-id", ClientSecret = "secret",
        PollInterval = TimeSpan.Zero, CommitPollInterval = TimeSpan.Zero, PollTimeoutSec = 30
    };

    private static FakeGraphHandler Handler() =>
        new((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/oauth2/v2.0/token")) return FakeGraphHandler.Json("""{"access_token":"fake-token","expires_in":3599}""");
            if (uri.Contains("blob.core.windows.net")) return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") };
            if (uri.Contains("mobileApps?$filter=") || uri.Contains("mobileApps?%24filter=")) return FakeGraphHandler.Json("""{"value":[]}""");
            if (uri.EndsWith("/contentVersions") && request.Method == HttpMethod.Post) return FakeGraphHandler.Json("""{"id":"7"}""");
            if (uri.EndsWith("/files") && request.Method == HttpMethod.Post)
                return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"azureStorageUriRequestPending"}""");
            if (uri.EndsWith("/commit") && request.Method == HttpMethod.Post) return FakeGraphHandler.Empty();
            if (uri.Contains("/files/file-1") && request.Method == HttpMethod.Get)
                return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"commitFileSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1"}""");
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch) return FakeGraphHandler.Json("""{"id":"app-100"}""");
            return FakeGraphHandler.Json("{}");
        });

    private async Task<JsonObject> PublishBody(StageManifest manifest)
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = Handler();
        using var svc = new IntunePublishService(handler);
        await svc.PublishAsync(package, manifest, Options());
        var create = handler.Requests.First(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        return (JsonObject)JsonNode.Parse(create.Body)!;
    }

    [Fact]
    public async Task TheManifestSetupFileOutranksThePackageMetadata()
    {
        var manifest = Registry();
        manifest.SetupFile = @"Deploy-Application.exe";
        Assert.Equal("Deploy-Application.exe", (string?)(await PublishBody(manifest))["setupFilePath"]);
    }

    [Fact]
    public async Task AProfileIconOutranksTheStagedIcon()
    {
        File.WriteAllBytes(Path.Combine(_dir, "brand.png"), [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        File.WriteAllBytes(Path.Combine(_dir, "app-icon.ico"), [0, 0, 1, 0, 1, 0]);
        var manifest = Registry();
        manifest.Icon = "app-icon.ico";
        manifest.CustomAssets = [new StageCustomAsset { Category = "Icon", RelativePath = "brand.png" }];

        var body = await PublishBody(manifest);

        Assert.Equal("image/png", (string?)body["largeIcon"]!["type"]);
    }

    [Fact]
    public void AStoredPolicyWithAMalformedThumbprintIsNotAdopted()
    {
        var stored = new SigningPolicy { SignDeployment = true, CertificateThumbprint = "not-a-thumbprint", StoreLocation = "Elsewhere", TimestampServer = " http://ts " };
        var loaded = SigningPolicy.FromSettings(stored);
        Assert.Equal("", loaded.CertificateThumbprint);
        Assert.Equal(SigningPolicy.CurrentUser, loaded.StoreLocation);
        Assert.Equal("http://ts", loaded.TimestampServer);
        Assert.True(loaded.SignDeployment);

        var thumb = new string('a', 40);
        Assert.Equal(thumb.ToUpperInvariant(), SigningPolicy.FromSettings(new SigningPolicy { CertificateThumbprint = thumb }).CertificateThumbprint);
        Assert.False(SigningPolicy.FromSettings(null).AnySwitch);
    }

    [Fact]
    public void TheSnapshotPolicyOutranksTheGlobalPolicy()
    {
        var global = new SigningPolicy { SignDetection = true };
        Assert.True(StageFinalizer.EffectivePolicy(null, global).SignDetection);
        var snapshot = new Models.Workbench.RunSnapshot { SigningPolicy = new JsonObject { ["SignDeployment"] = true } };
        var effective = StageFinalizer.EffectivePolicy(snapshot, global);
        Assert.True(effective.SignDeployment);
        Assert.False(effective.SignDetection);
    }

    [Fact]
    public void ANetworkFolderTakesTheProfileSuffixRecordedInTheBuildsSnapshot()
    {
        using var t = new WorkbenchTestRoot();
        var profile = t.NewProfile("catalog:package-scope", "Alpha Team");
        var snapshot = t.Store.CreateRunSnapshot(new Models.Workbench.RunSnapshotRequest { ApplicationId = "catalog:package-scope", ProfileId = profile.ProfileId });
        var manifest = new StageManifest { BuildId = snapshot.BuildId, ProfileId = profile.ProfileId };

        var scope = ProfileContentScope.ForManifest(manifest, t.Store.DataRoot);

        Assert.Equal($"AlphaTeam-{profile.ProfileId[..8]}", scope.Suffix);
        Assert.Equal($"1.0-AlphaTeam-{profile.ProfileId[..8]}", scope.ScopedVersionFolder("1.0"));
        Assert.Equal("1.0", ProfileContentScope.ForManifest(new StageManifest(), t.Store.DataRoot).ScopedVersionFolder("1.0"));
        Assert.Equal(Path.Combine(@"C:\dl", "profiles", profile.ProfileId), ProfileContentScope.LocalDownloadRoot(@"C:\dl", profile.ProfileId));
        Assert.Equal(@"C:\dl", ProfileContentScope.LocalDownloadRoot(@"C:\dl", "default"));
    }
}
