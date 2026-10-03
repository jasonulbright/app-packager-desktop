using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Stage finalization: a build without a run snapshot gains only its signing record,
/// and a build with one applies its profile in the fixed order.
/// </summary>
public sealed class StageFinalizationTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private string NewStage(string installerFile = "setup.exe")
    {
        var stage = _t.NewFolder();
        _t.NewFile(Path.Combine(stage, installerFile), "installer");
        _t.NewFile(Path.Combine(stage, "install.ps1"), "exit 0");
        _t.NewFile(Path.Combine(stage, "uninstall.ps1"), "exit 0");
        _t.NewFile(Path.Combine(stage, "install.bat"), "@echo off");
        _t.NewFile(Path.Combine(stage, "uninstall.bat"), "@echo off");
        return stage;
    }

    private static StageManifest NewManifest(string installerFile = "setup.exe", string version = "1.2.3") => new()
    {
        AppName = "Test App",
        Publisher = "Test Publisher",
        SoftwareVersion = version,
        InstallerFile = installerFile,
        InstallerType = "EXE",
        InstallArgs = "/S",
        ProductCode = "{11111111-2222-3333-4444-555555555555}",
        Detection = new StageDetection
        {
            Type = "RegistryKeyValue", Hive = "LocalMachine", RegistryKeyRelative = @"SOFTWARE\Test", ValueName = "DisplayVersion"
        }
    };

    private static StageFinalizer Finalizer(RunSnapshot? snapshot = null, SigningPolicy? policy = null)
    {
        var finalizer = StageFinalizer.Begin(new StageInputs { RunSnapshot = snapshot, SigningPolicy = policy });
        finalizer.ConditionTemplates = ConditionTemplateService.Defaults();
        return finalizer;
    }

    private WorkbenchProfile Update(WorkbenchProfile profile, Action<WorkbenchProfile> change)
    {
        var copy = profile.Clone();
        change(copy);
        return _t.Store.SaveProfile(copy);
    }

    private RunSnapshot Snapshot(string applicationId, string profileId, JsonObject? runOverrides = null) =>
        _t.Store.CreateRunSnapshot(new RunSnapshotRequest { ApplicationId = applicationId, ProfileId = profileId, RunOverrides = runOverrides });

    private static JsonNode? Json(string text) => WorkbenchJson.Parse(text);

    private static Dictionary<string, string> TreeHashes(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), WorkbenchJson.FileSha256, StringComparer.OrdinalIgnoreCase);

    // ── Without a snapshot ──────────────────────────────────────

    [Fact]
    public void WithoutASnapshot_OnlyTheSigningRecordIsAdded()
    {
        var stage = NewStage();
        var manifest = NewManifest();
        var before = JsonSerializer.SerializeToNode(manifest)!.AsObject();

        Finalizer().Apply(stage, manifest);

        var after = JsonSerializer.SerializeToNode(manifest)!.AsObject();
        Assert.NotNull(after["ScriptSigning"]);
        after.Remove("ScriptSigning");
        Assert.Equal(before.ToJsonString(), after.ToJsonString());
        Assert.Equal("default", manifest.ProfileId);
        Assert.Equal(0, manifest.ProfileRevision);
        Assert.Equal("Default", manifest.DetectionSource);
        Assert.Null(manifest.SetupFile);
        Assert.Equal(SigningStatus.NotApplicable, manifest.ScriptSigning!["Detection"]!["Status"]!.GetValue<string>());
        Assert.Equal(new SigningPolicy().PolicyDigest, manifest.ScriptSigning!["PolicyDigest"]!.GetValue<string>());
    }

    [Fact]
    public void WithoutASnapshot_TheStagedFilesAreUntouchedByteForByte()
    {
        var stage = NewStage();
        _t.NewFile(Path.Combine(stage, "scripts", "detect.ps1"), "# a previous build's detector");
        var before = TreeHashes(stage);
        var manifest = NewManifest();
        manifest.Detection = new StageDetection { Type = "Script", ScriptText = "if (Test-Path 'C:\\x') { 'Installed' }" };

        var finalizer = Finalizer();
        finalizer.PrepareStageRoot(stage);
        finalizer.Apply(stage, manifest);

        Assert.Equal(before, TreeHashes(stage));
        Assert.Equal(SigningStatus.NotRequested, manifest.ScriptSigning!["Detection"]!["Status"]!.GetValue<string>());
        Assert.Null(manifest.ScriptSigning!["Detection"]!["File"]);
    }

    [Fact]
    public void WithoutASnapshot_TheFullWriteMatchesAPlainManifestWriteExceptForScriptSigning()
    {
        var plainStage = NewStage();
        var finalizedStage = NewStage();
        new ManifestService().Write(Path.Combine(plainStage, "stage-manifest.json"), NewManifest());
        Finalizer().FinalizeAndWrite(new ManifestService(), Path.Combine(finalizedStage, "stage-manifest.json"), NewManifest());

        var plain = ReadManifestNode(plainStage);
        var finalized = ReadManifestNode(finalizedStage);
        Assert.NotNull(finalized["ScriptSigning"]);
        foreach (var node in new[] { plain, finalized })
        {
            node.Remove("ScriptSigning");
            node.Remove("StagedAt");
            node.Remove("PlanDigest");
        }
        Assert.Equal(plain.ToJsonString(), finalized.ToJsonString());
        Assert.Equal(TreeHashes(plainStage).Where(p => p.Key != "stage-manifest.json").OrderBy(p => p.Key),
            TreeHashes(finalizedStage).Where(p => p.Key != "stage-manifest.json").OrderBy(p => p.Key));
        Assert.False(Directory.Exists(Path.Combine(_t.Store.DataRoot, "builds")));
    }

    private static JsonObject ReadManifestNode(string stage) =>
        WorkbenchJson.Parse(File.ReadAllText(Path.Combine(stage, "stage-manifest.json")))!.AsObject();

    [Fact]
    public void ASigningSwitchWithNoUsableCertificateIsRefusedWhenTheBuildBegins()
    {
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => StageFinalizer.Begin(new StageInputs
        {
            SigningPolicy = new SigningPolicy { SignDeployment = true, CertificateThumbprint = new string('A', 40) }
        }));
        Assert.Contains("SigningCertificateUnavailable", ex.Message);
    }

    // ── With a profile ──────────────────────────────────────────

    [Fact]
    public void AppliesCommandsTimingExecutionDetectionAndRequirements()
    {
        const string id = "catalog:package-finalize";
        var saved = Update(_t.NewProfile(id), p =>
        {
            p.Set("Install", Json("""{ "Command": "{{ContentRoot}}install.bat /extra" }"""));
            p.Set("Detection", Json("""{ "Mode": "Custom", "Rule": { "Type": "File", "FilePath": "C:\\Program Files\\Test", "FileName": "test.exe" } }"""));
            p.Set("Requirements", Json("""{ "Operations": [ { "Op": "Add", "RuleId": "cpu-arch", "Rule": { "ConditionId": "cpu-arch", "Value": "x64" }, "AppliesTo": ["*"] } ] }"""));
            p.Set("Timing", Json("""{ "EstimatedMinutes": 12, "MaximumMinutes": 40 }"""));
            p.Set("Execution", Json("""{ "Context": "InstallForSystem", "ScriptHost": "x64" }"""));
        });
        var snapshot = Snapshot(id, saved.ProfileId);

        var stage = NewStage();
        var manifest = NewManifest();
        Finalizer(snapshot).Apply(stage, manifest);

        Assert.Equal("%~dp0install.bat /extra", manifest.CommandOverrides!.Install);
        Assert.Equal("Custom", manifest.DetectionSource);
        Assert.Equal("File", manifest.Detection.Type);
        Assert.Equal(12, manifest.Timing.EstimatedMinutes);
        Assert.Equal(40, manifest.Timing.MaximumMinutes);
        Assert.False(manifest.Timing.IntuneApplied);
        Assert.Equal("InstallForSystem", manifest.Execution.Context);
        Assert.Equal("InstallForSystem", manifest.InstallationBehaviorType);
        var rule = Assert.Single(manifest.Requirements!);
        Assert.Equal("cpu-arch", rule.RuleId);
        Assert.Equal(snapshot.BuildId, manifest.BuildId);
        Assert.Equal(id, manifest.ApplicationId);
        Assert.Equal(saved.ProfileId, manifest.ProfileId);
        Assert.Equal(saved.Revision, manifest.ProfileRevision);
        Assert.Equal("install.bat", manifest.SetupFile);
        Assert.False(manifest.Detection.IntuneScriptConversion);
    }

    [Fact]
    public void ARunOverrideOutranksTheProfileTimingAndAnEstimateAboveTheMaximumIsRefused()
    {
        const string id = "catalog:package-timing";
        var saved = Update(_t.NewProfile(id), p => p.Set("Timing", Json("""{ "EstimatedMinutes": 10, "MaximumMinutes": 20 }""")));

        var manifest = NewManifest();
        Finalizer(Snapshot(id, saved.ProfileId, (JsonObject)Json("""{ "MaximumMinutes": 90 }""")!)).Apply(NewStage(), manifest);
        Assert.Equal(10, manifest.Timing.EstimatedMinutes);
        Assert.Equal(90, manifest.Timing.MaximumMinutes);

        var tooShort = Snapshot(id, saved.ProfileId, (JsonObject)Json("""{ "MaximumMinutes": 5 }""")!);
        var ex = Assert.Throws<InvalidOperationException>(() => Finalizer(tooShort).Apply(NewStage(), NewManifest()));
        Assert.Equal("Estimated runtime (10) cannot exceed maximum runtime (5).", ex.Message);
    }

    [Fact]
    public void AnUnknownBuildTokenInAProfileCommandIsRefused()
    {
        const string id = "catalog:package-token";
        var saved = Update(_t.NewProfile(id), p => p.Set("Install", Json("""{ "Command": "{{Nope}}" }""")));
        var ex = Assert.Throws<InvalidOperationException>(() => Finalizer(Snapshot(id, saved.ProfileId)).Apply(NewStage(), NewManifest()));
        Assert.StartsWith("Unknown build token '{{Nope}}'", ex.Message);
    }

    [Fact]
    public void CopiesSourceFilesAndRecordsTheirHashes()
    {
        const string id = "catalog:package-sourcefiles";
        var profile = _t.NewProfile(id);
        var file = _t.NewFile(Path.Combine(_t.NewFolder(), "settings.json"), """{"k":"v"}""");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, file);
        var saved = Update(profile, p => p.Set("SourceFiles", Json($$"""[ { "Asset": "{{asset.AssetId}}", "Destination": "config\\settings.json" } ]""")));

        var stage = NewStage();
        var manifest = NewManifest();
        Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, manifest);

        Assert.True(File.Exists(Path.Combine(stage, "config", "settings.json")));
        var entry = Assert.Single(manifest.CustomAssets, a => a.Category == "SourceFile");
        Assert.Equal(asset.Sha256, entry.Sha256);
        Assert.Equal(@"config\settings.json", entry.RelativePath);
        Assert.Equal(asset.Size, entry.Size);
    }

    [Theory]
    [InlineData(@"..\escape.txt", "escapes the content root")]
    [InlineData("INSTALL.BAT", "collides with a file already in the stage root")]
    public void ASourceFileDestinationOutsideTheRootOrOnAGeneratedFileIsRefused(string destination, string message)
    {
        var stage = NewStage();
        var source = _t.NewFile(Path.Combine(_t.NewFolder(), "x.txt"), "x");
        var ex = Assert.Throws<InvalidOperationException>(() => StageFileCopier.Copy(stage, source, destination));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void AMissingSourceFileIsRefused()
    {
        var stage = NewStage();
        var ex = Assert.Throws<InvalidOperationException>(() => StageFileCopier.Copy(stage, Path.Combine(stage, "gone.txt"), "new.txt"));
        Assert.Contains("is missing", ex.Message);
    }

    [Fact]
    public void AReparsePointInTheDestinationChainIsRefused()
    {
        var stage = NewStage();
        var link = Path.Combine(stage, "linked");
        try { Directory.CreateSymbolicLink(link, _t.NewFolder()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!JunctionCreated(link, _t.NewFolder())) return;
        }
        var source = _t.NewFile(Path.Combine(_t.NewFolder(), "y.txt"), "y");
        var refused = Assert.Throws<InvalidOperationException>(() => StageFileCopier.Copy(stage, source, @"linked\y.txt"));
        Assert.Contains("reparse point", refused.Message);
    }

    private static bool JunctionCreated(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(30_000);
        return Directory.Exists(link);
    }

    [Fact]
    public void TwoProfilesProduceSeparateBuildsAndRecords()
    {
        const string id = "catalog:package-twoprofiles";
        var first = _t.NewProfile(id, "One");
        var second = _t.Store.CopyProfile(id, first.ProfileId, "Two");
        var firstStage = NewStage();
        var secondStage = NewStage();
        var firstManifest = NewManifest();
        var secondManifest = NewManifest();
        var firstFinalizer = Finalizer(Snapshot(id, first.ProfileId));
        var secondFinalizer = Finalizer(Snapshot(id, second.ProfileId));

        firstFinalizer.FinalizeAndWrite(new ManifestService(), Path.Combine(firstStage, "stage-manifest.json"), firstManifest);
        secondFinalizer.FinalizeAndWrite(new ManifestService(), Path.Combine(secondStage, "stage-manifest.json"), secondManifest);

        Assert.NotEqual(firstManifest.ProfileId, secondManifest.ProfileId);
        Assert.NotEqual(firstManifest.BuildId, secondManifest.BuildId);
        var firstRecords = BuildRecords.List(_t.Store.DataRoot, id, first.ProfileId);
        var secondRecords = BuildRecords.List(_t.Store.DataRoot, id, second.ProfileId);
        Assert.Equal(firstManifest.BuildId, Assert.Single(firstRecords)["BuildId"]!.GetValue<string>());
        Assert.Equal(secondManifest.BuildId, Assert.Single(secondRecords)["BuildId"]!.GetValue<string>());
    }

    [Fact]
    public void TheProfileIconReplacesTheStagedIcon()
    {
        const string id = "catalog:package-icon";
        var profile = _t.NewProfile(id);
        var icon = _t.NewFile(Path.Combine(_t.NewFolder(), "brand.png"), "PNGDATA");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, icon);
        var saved = Update(profile, p => p.Set("Application", Json($$"""{ "Icon": { "Asset": "{{asset.AssetId}}" } }""")));

        var stage = NewStage();
        _t.NewFile(Path.Combine(stage, "app-icon.ico"), "old");
        var manifest = NewManifest();
        manifest.Icon = "app-icon.ico";
        Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, manifest);

        Assert.Equal("app-icon.png", manifest.Icon);
        Assert.False(File.Exists(Path.Combine(stage, "app-icon.ico")));
        Assert.Equal("PNGDATA", File.ReadAllText(Path.Combine(stage, "app-icon.png")));
        Assert.Contains(manifest.CustomAssets, a => a.Category == "Icon" && a.RelativePath == "app-icon.png");
    }

    [Fact]
    public void AnExplicitNullIconRemovesTheStagedIcon()
    {
        const string id = "catalog:package-noicon";
        var saved = Update(_t.NewProfile(id), p => p.Set("Application", Json("""{ "Icon": null }""")));
        var stage = NewStage();
        _t.NewFile(Path.Combine(stage, "app-icon.png"), "old");
        var manifest = NewManifest();
        manifest.Icon = "app-icon.png";

        Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, manifest);

        Assert.Null(manifest.Icon);
        Assert.False(File.Exists(Path.Combine(stage, "app-icon.png")));
    }

    [Theory]
    [InlineData("IncludeVersion", "IncludeVersion", "App - 1.2.3 (x64)")]
    [InlineData("No version", "NoVersion", "App (x64)")]
    public void TheProfileTitleModeIsRecordedAndApplied(string stored, string recorded, string title)
    {
        const string id = "catalog:package-title";
        var saved = Update(_t.NewProfile(id), p => p.Set("Application.TitleMode", JsonValue.Create(stored)));
        var manifest = NewManifest();
        manifest.AppName = "App - 1.2.3 (x64)";

        Finalizer(Snapshot(id, saved.ProfileId)).Apply(NewStage(), manifest);

        Assert.Equal(recorded, manifest.TitleMode);
        Assert.Equal(title, manifest.AppName);
    }

    [Fact]
    public void VariantOverridesReachOnlyTheNamedDeploymentType()
    {
        const string id = "catalog:package-variants";
        var saved = Update(_t.NewProfile(id), p =>
        {
            p.Set("Variants.Overrides", Json("""{ "ARM64": { "InstallCommand": "{{ContentRoot}}arm.bat {{Version}}", "Context": "InstallForUser" } }"""));
            p.Set("Requirements", Json("""{ "Operations": [ { "Op": "Add", "RuleId": "vpn", "Rule": { "ConditionId": "vpn-connected", "Value": true }, "AppliesTo": ["x64"] } ] }"""));
        });
        var manifest = NewManifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "ARM64", Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "ARM64" }] },
            new StageDeploymentType { NameSuffix = "x64" }
        ];

        Finalizer(Snapshot(id, saved.ProfileId)).Apply(NewStage(), manifest);

        Assert.Equal("%~dp0arm.bat 1.2.3", manifest.DeploymentTypes[0].InstallCommandLine);
        Assert.Equal("InstallForUser", manifest.DeploymentTypes[0].InstallationBehaviorType);
        Assert.Null(manifest.DeploymentTypes[1].InstallCommandLine);
        Assert.Equal("cpu-arch", Assert.Single(manifest.DeploymentTypes[0].Requirements!).ConditionId);
        Assert.Equal("vpn", Assert.Single(manifest.DeploymentTypes[1].Requirements!).RuleId);
        Assert.Null(manifest.Requirements);
    }

    [Fact]
    public void CustomInstallModeReplacesTheGeneratedScriptWithTheBoundAsset()
    {
        const string id = "catalog:package-custom";
        var profile = _t.NewProfile(id);
        var script = _t.NewFile(Path.Combine(_t.NewFolder(), "custom.ps1"), "Start-Process (Join-Path {{ContentRoot}} '{{InstallerFile}}')");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, script);
        var saved = Update(profile, p => p.Set("Install", Json($$"""{ "Mode": "Custom", "Script": "{{asset.AssetId}}" }""")));
        var stage = NewStage();
        var manifest = NewManifest();

        Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, manifest);

        Assert.Equal("Start-Process (Join-Path $PSScriptRoot 'setup.exe')", File.ReadAllText(Path.Combine(stage, "install.ps1")));
        Assert.Contains(manifest.CustomAssets, a => a.Category == "Install" && a.RelativePath == "install.ps1");
    }

    [Fact]
    public void ExtendModeComposesTheOrchestratorFromTheProfile()
    {
        const string id = "catalog:package-extend";
        var profile = _t.NewProfile(id);
        var hook = _t.NewFile(Path.Combine(_t.NewFolder(), "after.ps1"), "exit 0");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, hook);
        var saved = Update(profile, p => p.Set("Install", Json($$"""{ "Mode": "Extend", "After": "{{asset.AssetId}}" }""")));
        var stage = NewStage();

        Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, NewManifest());

        Assert.Equal("exit 0", File.ReadAllText(Path.Combine(stage, "install-generated.ps1")));
        Assert.True(File.Exists(Path.Combine(stage, "install-after.ps1")));
        Assert.Contains("Invoke-Step", File.ReadAllText(Path.Combine(stage, "install.ps1")));
    }

    [Fact]
    public void ExtendModeStagesHookTextStoredInTheProfile()
    {
        const string id = "catalog:package-extend-text";
        var saved = Update(_t.NewProfile(id), p => p.Set("Install", new JsonObject
        {
            ["Mode"] = "Extend",
            ["BeforeText"] = "Set-Content -Path C:\\LabTest\\before.txt -Value '{{InstallerFile}}'",
            ["AfterText"] = "exit 0"
        }));
        var stage = NewStage();
        var manifest = NewManifest();

        Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, manifest);

        Assert.Equal("Set-Content -Path C:\\LabTest\\before.txt -Value 'setup.exe'", File.ReadAllText(Path.Combine(stage, "install-before.ps1")));
        Assert.Equal("exit 0", File.ReadAllText(Path.Combine(stage, "install-after.ps1")));
        var orchestrator = File.ReadAllText(Path.Combine(stage, "install.ps1"));
        var before = orchestrator.IndexOf("Invoke-Step -Name 'install-before.ps1'", StringComparison.Ordinal);
        var generated = orchestrator.IndexOf("Invoke-Step -Name 'install-generated.ps1'", StringComparison.Ordinal);
        var after = orchestrator.IndexOf("Invoke-Step -Name 'install-after.ps1'", StringComparison.Ordinal);
        Assert.True(before >= 0 && before < generated && generated < after);
        Assert.Contains(manifest.CustomAssets, a => a.Category == "InstallBefore" && a.RelativePath == "install-before.ps1" && a.Asset is null);
        Assert.Contains(manifest.CustomAssets, a => a.Category == "InstallAfter" && a.RelativePath == "install-after.ps1" && a.Asset is null);
    }

    [Fact]
    public void AHookAssetWinsOverHookTextForTheSameStep()
    {
        const string id = "catalog:package-extend-both";
        var profile = _t.NewProfile(id);
        var asset = _t.Store.AddAsset(id, profile.ProfileId, _t.NewFile(Path.Combine(_t.NewFolder(), "before.ps1"), "# from asset"));
        var saved = Update(profile, p => p.Set("Uninstall", new JsonObject
        {
            ["Mode"] = "Extend", ["Before"] = asset.AssetId, ["BeforeText"] = "# from text"
        }));
        var stage = NewStage();

        Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, NewManifest());

        Assert.Equal("# from asset", File.ReadAllText(Path.Combine(stage, "uninstall-before.ps1")));
    }

    [Fact]
    public void CustomInstallModeStagesScriptTextStoredInTheProfile()
    {
        const string id = "catalog:package-custom-text";
        var saved = Update(_t.NewProfile(id), p => p.Set("Install", new JsonObject
        {
            ["Mode"] = "Custom", ["ScriptText"] = "Start-Process (Join-Path {{ContentRoot}} '{{InstallerFile}}')"
        }));
        var stage = NewStage();
        var manifest = NewManifest();

        Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, manifest);

        Assert.Equal("Start-Process (Join-Path $PSScriptRoot 'setup.exe')", File.ReadAllText(Path.Combine(stage, "install.ps1")));
        Assert.Contains(manifest.CustomAssets, a => a.Category == "Install" && a.RelativePath == "install.ps1" && a.Asset is null);
    }

    [Fact]
    public void CustomModeWithNeitherAScriptAssetNorScriptTextIsRefused()
    {
        const string id = "catalog:package-custom-empty";
        var saved = Update(_t.NewProfile(id), p => p.Set("Install", new JsonObject { ["Mode"] = "Custom", ["ScriptText"] = "  " }));
        var stage = NewStage();
        var before = TreeHashes(stage);

        var ex = Assert.Throws<InvalidOperationException>(() => Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, NewManifest()));

        Assert.Equal("Install.Mode Custom requires a script.", ex.Message);
        Assert.Equal(before, TreeHashes(stage));
    }

    [Fact]
    public void ExtendModeWithoutAGeneratedScriptIsRefused()
    {
        const string id = "catalog:package-extend-missing";
        var saved = Update(_t.NewProfile(id), p => p.Set("Uninstall", Json("""{ "Mode": "Extend" }""")));
        var stage = NewStage();
        File.Delete(Path.Combine(stage, "uninstall.ps1"));

        var ex = Assert.Throws<InvalidOperationException>(() => Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, NewManifest()));
        Assert.Equal("Uninstall.Mode Extend needs the packager's generated uninstall.ps1, which the stage did not produce.", ex.Message);
    }

    [Fact]
    public void AnExplicitIntuneScriptConversionIsCarriedOntoTheDetection()
    {
        const string id = "catalog:package-conversion";
        var saved = Update(_t.NewProfile(id), p => p.Set("Detection.IntuneScriptConversion", JsonValue.Create(true)));
        var manifest = NewManifest();
        Finalizer(Snapshot(id, saved.ProfileId)).Apply(NewStage(), manifest);
        Assert.True(manifest.Detection.IntuneScriptConversion);
    }

    // ── Stale-artifact pruning ──────────────────────────────────

    [Fact]
    public void ASourceFileTheProfileNoLongerCarriesIsRemovedOnTheNextBuild()
    {
        const string id = "catalog:package-prune";
        var profile = _t.NewProfile(id);
        var file = _t.NewFile(Path.Combine(_t.NewFolder(), "settings.json"), """{"k":"v"}""");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, file);
        var withFile = Update(profile, p => p.Set("SourceFiles", Json($$"""[ { "Asset": "{{asset.AssetId}}", "Destination": "config\\settings.json" } ]""")));

        var stage = NewStage();
        Finalizer(Snapshot(id, withFile.ProfileId)).FinalizeAndWrite(new ManifestService(), Path.Combine(stage, "stage-manifest.json"), NewManifest());
        Assert.True(File.Exists(Path.Combine(stage, "config", "settings.json")));

        var withoutFile = Update(withFile, p => p.Set("SourceFiles", new JsonArray()));
        var second = NewManifest();
        Finalizer(Snapshot(id, withoutFile.ProfileId)).Apply(stage, second);

        Assert.False(File.Exists(Path.Combine(stage, "config", "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(stage, "config")));
        Assert.Empty(second.CustomAssets);
    }

    [Fact]
    public void ASourceFileTheProfileStillCarriesIsKept()
    {
        const string id = "catalog:package-prune-keep";
        var profile = _t.NewProfile(id);
        var file = _t.NewFile(Path.Combine(_t.NewFolder(), "keep.json"), """{"k":1}""");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, file);
        var saved = Update(profile, p => p.Set("SourceFiles", Json($$"""[ { "Asset": "{{asset.AssetId}}", "Destination": "config\\keep.json" } ]""")));

        var stage = NewStage();
        for (var pass = 0; pass < 2; pass++)
            Finalizer(Snapshot(id, saved.ProfileId)).FinalizeAndWrite(new ManifestService(), Path.Combine(stage, "stage-manifest.json"), NewManifest());
        Assert.True(File.Exists(Path.Combine(stage, "config", "keep.json")));
    }

    [Fact]
    public void ASourceFileWhoseContentChangedSinceItWasSealedIsKept()
    {
        const string id = "catalog:package-prune-changed";
        var profile = _t.NewProfile(id);
        var file = _t.NewFile(Path.Combine(_t.NewFolder(), "s.txt"), "sealed");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, file);
        var withFile = Update(profile, p => p.Set("SourceFiles", Json($$"""[ { "Asset": "{{asset.AssetId}}", "Destination": "s.txt" } ]""")));
        var stage = NewStage();
        Finalizer(Snapshot(id, withFile.ProfileId)).FinalizeAndWrite(new ManifestService(), Path.Combine(stage, "stage-manifest.json"), NewManifest());
        File.WriteAllText(Path.Combine(stage, "s.txt"), "a vendor file now");

        var withoutFile = Update(withFile, p => p.Set("SourceFiles", new JsonArray()));
        var log = new List<string>();
        Finalizer(Snapshot(id, withoutFile.ProfileId)).Apply(stage, NewManifest(), log.Add);

        Assert.Equal("a vendor file now", File.ReadAllText(Path.Combine(stage, "s.txt")));
        Assert.Contains(log, l => l.Contains("content differs from the sealed source file"));
    }

    [Fact]
    public void AStaleGeneratedScriptIsRemovedWhenTheProfileLeavesExtendMode()
    {
        const string id = "catalog:package-prune-extend";
        var profile = _t.NewProfile(id);
        var stage = NewStage();
        _t.NewFile(Path.Combine(stage, "install-generated.ps1"), "exit 0");

        Finalizer(Snapshot(id, profile.ProfileId)).Apply(stage, NewManifest());

        Assert.False(File.Exists(Path.Combine(stage, "install-generated.ps1")));
    }

    [Fact]
    public void AProfileReplacesItsOwnIdenticalCopyWhenNoBuildRecordWasWritten()
    {
        const string id = "catalog:package-restage";
        var profile = _t.NewProfile(id);
        var file = _t.NewFile(Path.Combine(_t.NewFolder(), "settings.txt"), "value=1");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, file);
        var saved = Update(profile, p => p.Set("SourceFiles", Json($$"""[ { "Asset": "{{asset.AssetId}}", "Destination": "config\\settings.txt" } ]""")));

        var stage = NewStage();
        for (var pass = 0; pass < 2; pass++)
            Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, NewManifest());
        Assert.Equal("value=1", File.ReadAllText(Path.Combine(stage, "config", "settings.txt")));
    }

    [Fact]
    public void ADifferingFileAlreadyAtTheDestinationIsStillRefused()
    {
        const string id = "catalog:package-restage-collide";
        var profile = _t.NewProfile(id);
        var file = _t.NewFile(Path.Combine(_t.NewFolder(), "settings.txt"), "value=1");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, file);
        var saved = Update(profile, p => p.Set("SourceFiles", Json($$"""[ { "Asset": "{{asset.AssetId}}", "Destination": "config\\settings.txt" } ]""")));
        var stage = NewStage();
        _t.NewFile(Path.Combine(stage, "config", "settings.txt"), "generated by the packager");

        var ex = Assert.Throws<InvalidOperationException>(() => Finalizer(Snapshot(id, saved.ProfileId)).Apply(stage, NewManifest()));
        Assert.Contains("collides with a file already in the stage root", ex.Message);
    }

    // ── Signing hand-off ────────────────────────────────────────

    [Fact]
    public void TheSigningRecordCarriesTheLauncherFindingsArray()
    {
        var stage = NewStage();
        File.WriteAllText(Path.Combine(stage, "install.bat"), "@echo off\r\npowershell.exe -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"\r\n");
        var manifest = NewManifest();

        Finalizer().Apply(stage, manifest);

        var findings = manifest.ScriptSigning!["LauncherFindings"]!.AsArray();
        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("install.bat", f!["File"]!.GetValue<string>()));
        Assert.False(manifest.ScriptSigning!["LaunchersBypassFree"]!.GetValue<bool>());
    }

    // ── Launcher regeneration ───────────────────────────────────

    [Fact]
    public void AStageRootAPreviousSnapshotBuildUsedGetsFreshLaunchers()
    {
        var stage = NewStage();
        File.WriteAllText(Path.Combine(stage, "stage-manifest.json"), """{ "SchemaVersion": 4, "BuildId": "20260101-000000-aaaaaaaa" }""");

        Finalizer().PrepareStageRoot(stage);

        Assert.False(File.Exists(Path.Combine(stage, "install.ps1")));
        Assert.False(File.Exists(Path.Combine(stage, "install.bat")));
        Assert.True(File.Exists(Path.Combine(stage, "setup.exe")));
    }

    [Fact]
    public void AStageRootThatNeverCarriedAProfileOrSigningKeepsItsLaunchers()
    {
        var stage = NewStage();
        File.WriteAllText(Path.Combine(stage, "stage-manifest.json"),
            $$"""{ "SchemaVersion": 4, "BuildId": "", "ScriptSigning": { "PolicyDigest": "{{new SigningPolicy().PolicyDigest}}" } }""");

        Finalizer().PrepareStageRoot(stage);

        Assert.True(File.Exists(Path.Combine(stage, "install.ps1")));
        Assert.True(File.Exists(Path.Combine(stage, "install.bat")));
    }
}
