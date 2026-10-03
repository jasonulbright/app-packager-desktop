using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// BYO applications: a dropped installer persisted as a no-script application with
/// numbered, hash-verified source revisions, and staged through the ad-hoc path.
/// </summary>
public sealed class ByoApplicationTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private WorkbenchStore Store => _t.Store;

    public void Dispose() => _t.Dispose();

    private string Installer(string name, string content) => _t.NewFile(Path.Combine(_t.NewFolder(), name), content);

    private string Fixture(string fixture, string asFileName)
    {
        var target = Path.Combine(_t.NewFolder(), asFileName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), target, true);
        return target;
    }

    private static ByoInstallerIdentity Identity(string type = "EXE", string arch = "x64", string? productCode = null,
        string appName = "Tool", string? publisher = null, string? version = null) =>
        new(type, arch, productCode, appName, publisher, version);

    private static DroppedInstallerAnalysisService ExeAnalyzer() => new(new FakeMsiInspector { Throws = true });

    private static StagingService Staging() => new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
        new WrapperGeneratorService(), new ManifestService());

    // ── Item 32: persistence ───────────────────────────────────

    [Fact]
    public void Save_PersistsTheDroppedInstallerAndItsFirstSourceRevision()
    {
        var installer = Installer("internal-tool.exe", "binary-v1");
        var result = Store.SaveByoApplication(installer, null, "Internal Tool", "Contoso", "1.0");

        Assert.Equal(1, result.Revision);
        Assert.Equal(WorkbenchJson.FileSha256(installer), result.Source.Sha256);
        Assert.Matches("^byo:[0-9a-f]{32}$", result.ApplicationId);

        var definition = Store.GetApplicationDefinition(result.ApplicationId);
        Assert.True(definition.Persisted);
        Assert.Equal("Manual", definition.UpdatePolicy);
        Assert.Equal("byo", definition.Origin);
        Assert.Equal("Internal Tool", definition.DisplayName);
        Assert.Equal(ApplicationIds.DefaultProfileId, definition.ActiveProfileId);

        var stored = Store.GetByoInstallerPath(result.ApplicationId, result.Source);
        Assert.Equal(Path.Combine(Store.GetApplicationFolder(result.ApplicationId), "sources", "1", "internal-tool.exe"), stored);
        Assert.Equal("binary-v1", File.ReadAllText(stored));

        var listed = Assert.Single(Store.DiscoverApplications(), a => a.ApplicationId == result.ApplicationId);
        Assert.Equal(ApplicationOrigin.Byo, listed.Origin);
        Assert.Equal("Manual", listed.UpdatePolicy);
    }

    [Fact]
    public void Save_TakesTheNameFromTheAnalysisWhenNoneIsGiven_AndRequiresOne()
    {
        var installer = Installer("tool.exe", "v1");
        var result = Store.SaveByoApplication(installer, Identity(appName: "From Analysis", publisher: "Analysed Co", version: "3.1"));
        var definition = Store.GetApplicationDefinition(result.ApplicationId);
        Assert.Equal("From Analysis", definition.DisplayName);
        Assert.Equal("Analysed Co", definition.Publisher);
        Assert.Equal("3.1", result.Source.SoftwareVersion);

        var error = Assert.Throws<InvalidOperationException>(() => Store.SaveByoApplication(installer, Identity(appName: "")));
        Assert.Contains("requires a display name", error.Message);
    }

    [Fact]
    public void Save_CarriesTheConfirmedStageValues()
    {
        var installer = Installer("tool.exe", "v1");
        var values = new ByoStageValues { InstallArgs = "/S /NORESTART", UninstallCommand = "uninst.exe /S", Confirmed = true };
        var result = Store.SaveByoApplication(installer, Identity(), "Tool", stageValues: values);

        var source = Assert.Single(Store.GetByoSources(result.ApplicationId));
        Assert.Equal("/S /NORESTART", source.StageValues!.InstallArgs);
        Assert.Equal("uninst.exe /S", source.StageValues.UninstallCommand);
        Assert.True(source.StageValues.Confirmed);
    }

    // ── Item 33: source revisions ──────────────────────────────

    [Fact]
    public void AReplacementWithAnotherArchitectureAndInstallerType_IsRevision2_AndFlagsBothAsBlocking()
    {
        var created = Store.SaveByoApplication(Installer("tool.exe", "v1"), Identity("EXE", "x86"), "Tool", softwareVersion: "1.0");
        var updated = Store.UpdateByoSource(created.ApplicationId, Installer("tool.exe", "v2"), Identity("MSI", "x64"), "2.0");

        Assert.Equal(2, updated.Revision);
        Assert.Contains("Architecture", updated.IncompatibleOverrides);
        Assert.Contains("InstallerType", updated.IncompatibleOverrides);
        Assert.All(updated.IdentityChanges, c => Assert.Equal(ByoChangeSeverity.Blocking, c.Severity));
        var architecture = Assert.Single(updated.IdentityChanges, c => c.Field == "Architecture");
        Assert.Equal("x86", architecture.Previous);
        Assert.Equal("x64", architecture.Current);
        Assert.Equal(["Architecture", "InstallerType"], Store.GetByoSources(created.ApplicationId)[1].IncompatibleOverrides.Order());
    }

    [Fact]
    public void ProductCodeAppNameAndPublisherChanges_AreReview_AndNotIncompatible()
    {
        var created = Store.SaveByoApplication(Installer("tool.msi", "v1"),
            Identity("MSI", "x64", "{11111111-1111-1111-1111-111111111111}", "Tool", "Contoso"), "Tool");
        var updated = Store.UpdateByoSource(created.ApplicationId, Installer("tool.msi", "v2"),
            Identity("MSI", "x64", "{22222222-2222-2222-2222-222222222222}", "Tool Pro", "Fabrikam"));

        Assert.Equal(["ProductCode", "AppName", "Publisher"], updated.IdentityChanges.Select(c => c.Field));
        Assert.All(updated.IdentityChanges, c => Assert.Equal(ByoChangeSeverity.Review, c.Severity));
        Assert.Empty(updated.IncompatibleOverrides);
    }

    [Fact]
    public void AnIdentityThatDiffersOnlyInCase_IsNotAChange()
    {
        var created = Store.SaveByoApplication(Installer("tool.msi", "v1"),
            Identity("MSI", "x64", "{ABCDEF00-1111-1111-1111-111111111111}", "Tool"), "Tool");
        var updated = Store.UpdateByoSource(created.ApplicationId, Installer("tool.msi", "v2"),
            Identity("msi", "X64", "{abcdef00-1111-1111-1111-111111111111}", "TOOL"));
        Assert.Empty(updated.IdentityChanges);
    }

    [Fact]
    public void AReplacementNeverOverwritesThePreviousRevision()
    {
        var created = Store.SaveByoApplication(Installer("tool.exe", "v1"), Identity(), "Tool");
        var updated = Store.UpdateByoSource(created.ApplicationId, Installer("tool.exe", "v2"), Identity());

        var sources = Store.GetByoSources(created.ApplicationId);
        Assert.Equal([1, 2], sources.Select(s => s.Revision));
        Assert.Equal("v1", File.ReadAllText(Store.GetByoInstallerPath(created.ApplicationId, sources[0])));
        Assert.Equal("v2", File.ReadAllText(Store.GetByoInstallerPath(created.ApplicationId, sources[1])));
        Assert.NotEqual(sources[0].Sha256, sources[1].Sha256);
        Assert.Equal(sources[1].Sha256, updated.Source.Sha256);
    }

    [Fact]
    public void AnOrphanRevisionFolder_IsNeverOverwritten()
    {
        var created = Store.SaveByoApplication(Installer("tool.exe", "v1"), Identity(), "Tool");
        var orphan = _t.NewFile(Path.Combine(Store.GetApplicationFolder(created.ApplicationId), "sources", "2", "tool.exe"), "orphan");

        var error = Assert.Throws<InvalidOperationException>(() =>
            Store.UpdateByoSource(created.ApplicationId, Installer("tool.exe", "v2"), Identity()));
        Assert.Contains("never overwritten", error.Message);
        Assert.Equal("orphan", File.ReadAllText(orphan));
        Assert.Single(Store.GetByoSources(created.ApplicationId));
    }

    [Fact]
    public void ASourceRevisionOnANonByoApplication_IsRefused()
    {
        var installer = Installer("x.exe", "x");
        var error = Assert.Throws<ArgumentException>(() => Store.UpdateByoSource("catalog:package-7zip", installer, Identity()));
        Assert.Contains("BYO applications only", error.Message);
    }

    [Fact]
    public void Preview_ReportsTheDifferences_AndWritesNothing()
    {
        var created = Store.SaveByoApplication(Installer("tool.exe", "v1"), Identity("EXE", "x86", appName: "Tool"), "Tool");
        var before = File.ReadAllText(Path.Combine(Store.GetApplicationFolder(created.ApplicationId), "application.json"));

        var changes = Store.PreviewByoSource(created.ApplicationId, Identity("MSI", "x86", "{X}", "Tool"));

        Assert.Equal(["InstallerType", "ProductCode"], changes.Select(c => c.Field));
        Assert.Equal([ByoChangeSeverity.Blocking, ByoChangeSeverity.Review], changes.Select(c => c.Severity));
        Assert.Contains("InstallerType changed from 'EXE' to 'MSI'", changes[0].Message);
        Assert.Equal(before, File.ReadAllText(Path.Combine(Store.GetApplicationFolder(created.ApplicationId), "application.json")));
        Assert.False(Directory.Exists(Path.Combine(Store.GetApplicationFolder(created.ApplicationId), "sources", "2")));
    }

    [Fact]
    public void AnInstallerNameWithAStreamColon_IsRefused()
    {
        var folder = _t.NewFolder();
        var host = _t.NewFile(Path.Combine(folder, "tool.exe"), "host");
        File.WriteAllText(host + ":payload", "hidden");
        Assert.ThrowsAny<Exception>(() => Store.SaveByoApplication(host + ":payload", Identity(), "Tool"));
        Assert.DoesNotContain(Store.DiscoverApplications(), a => a.Origin == ApplicationOrigin.Byo);
    }

    // ── Item 40: resolving the revision a build stages ─────────

    [Fact]
    public void ABuildOfAByoApplicationWithNoRevision_ReportsTheByoError()
    {
        var id = ApplicationIds.New(ApplicationOrigin.Byo);
        Store.SaveApplicationDefinition(new WorkbenchApplicationDefinition { ApplicationId = id, DisplayName = "No Source", Origin = "byo" });
        var error = Assert.Throws<InvalidOperationException>(() => Store.ResolveByoSource(id));
        Assert.Contains("has no stored source revision", error.Message);
        Assert.DoesNotContain("packager script", error.Message);
    }

    [Fact]
    public void ABuildOfAnUnstoredByoApplication_ReportsIt()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Store.ResolveByoSource(ApplicationIds.New(ApplicationOrigin.Byo)));
        Assert.Contains("is not stored in this data root", error.Message);
    }

    [Fact]
    public void ARevisionWhoseInstallerIsGone_ReportsThat()
    {
        var created = Store.SaveByoApplication(Installer("byo-tool.exe", "binary"), Identity(appName: "BYO Tool"), "BYO Tool", softwareVersion: "1.0");
        File.Delete(Store.GetByoInstallerPath(created.ApplicationId, created.Source));
        var error = Assert.Throws<FileNotFoundException>(() => Store.ResolveByoSource(created.ApplicationId));
        Assert.Contains("is missing its installer", error.Message);
    }

    [Fact]
    public void ARevisionWhoseInstallerChanged_FailsItsHashCheck()
    {
        var created = Store.SaveByoApplication(Installer("tool.exe", "binary"), Identity(), "Tool");
        File.WriteAllText(Store.GetByoInstallerPath(created.ApplicationId, created.Source), "tampered");
        var error = Assert.Throws<InvalidOperationException>(() => Store.ResolveByoSource(created.ApplicationId));
        Assert.Contains("no longer matches its recorded SHA-256", error.Message);
    }

    [Fact]
    public void Resolve_TakesTheNewestRevision_OrTheNewestOfAVersion()
    {
        var created = Store.SaveByoApplication(Installer("tool.exe", "v1"), Identity(), "Tool", softwareVersion: "1.0");
        Store.UpdateByoSource(created.ApplicationId, Installer("tool.exe", "v2"), Identity(), "2.0");
        Store.UpdateByoSource(created.ApplicationId, Installer("tool.exe", "v1b"), Identity(), "1.0");

        Assert.Equal(3, Store.ResolveByoSource(created.ApplicationId).Source.Revision);
        Assert.Equal(2, Store.ResolveByoSource(created.ApplicationId, "2.0").Source.Revision);
        Assert.Equal(3, Store.ResolveByoSource(created.ApplicationId, "1.0").Source.Revision);
        var error = Assert.Throws<InvalidOperationException>(() => Store.ResolveByoSource(created.ApplicationId, "9.9"));
        Assert.Contains("has no source revision for version '9.9'", error.Message);
    }

    [Fact]
    public void Resolve_ReadsTheRevisionFolder_NotTheStoredPath()
    {
        var created = Store.SaveByoApplication(Installer("tool.exe", "v1"), Identity(), "Tool");
        var definition = Store.GetApplicationDefinition(created.ApplicationId);
        var source = (System.Text.Json.Nodes.JsonObject)definition.Sources[0]!;
        source["Path"] = @"\\elsewhere\share\tool.exe";
        Store.SaveApplicationDefinition(definition);

        var resolved = Store.ResolveByoSource(created.ApplicationId);
        Assert.Equal(Store.GetByoInstallerPath(created.ApplicationId, created.Source), resolved.InstallerPath);
    }

    // ── Item 32/40: staging through the dropped-installer path ─

    [Fact]
    public async Task AByoStageOnTheDefaultProfile_EqualsStagingTheSameDroppedInstallerDirectly()
    {
        var dropped = Fixture("nsis-u-none.bin", "TestApp-Setup-1.2.3.exe");
        var analyzer = ExeAnalyzer();
        var analysis = await analyzer.AnalyzeAsync(dropped);
        var overrides = new DroppedInstallerOverrides
        {
            AppName = analysis.AppName.Value, Publisher = analysis.Publisher.Value, SoftwareVersion = analysis.SoftwareVersion.Value,
            InstallArgs = analysis.InstallArgs.Value, UninstallArgs = analysis.UninstallArgs.Value,
            UninstallCommand = analysis.UninstallCommand.Value
        };

        var directRoot = _t.NewFolder("direct");
        var directManifest = analyzer.BuildStageManifest(analysis, overrides, confirmed: true);
        ByoStaging.ApplyAdHocSource(directManifest, analysis);
        var direct = await Staging().StageDroppedInstallerAsync(directManifest, dropped, directRoot);
        Assert.True(direct.Success, direct.ErrorMessage);

        var saved = Store.SaveByoApplication(dropped, ByoInstallerIdentity.From(analysis), overrides.AppName, overrides.Publisher,
            overrides.SoftwareVersion, ByoStageValues.From(overrides, confirmed: true));
        var byoRoot = _t.NewFolder("byo");
        var plan = await ByoStaging.PlanAsync(Store, analyzer, saved.ApplicationId);
        var byo = await ByoStaging.StageAsync(Staging(), plan, byoRoot);
        Assert.True(byo.Success, byo.ErrorMessage);

        Assert.Equal(Path.GetRelativePath(directRoot, direct.ContentPath), Path.GetRelativePath(byoRoot, byo.ContentPath));
        var directFiles = Tree(direct.ContentPath);
        var byoFiles = Tree(byo.ContentPath);
        Assert.Equal(directFiles.Keys.Order(StringComparer.OrdinalIgnoreCase), byoFiles.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (relative, hash) in directFiles)
            if (relative != "stage-manifest.json") Assert.Equal(hash, byoFiles[relative]);

        // The manifest differs only where the installer was read from, and in the plan digest that covers that path.
        var directJson = WorkbenchTestRoot.Json(File.ReadAllText(direct.ManifestPath));
        var byoJson = WorkbenchTestRoot.Json(File.ReadAllText(byo.ManifestPath));
        Assert.Equal(dropped, WorkbenchJson.AsText(directJson["AdHocSource"]));
        Assert.Equal(plan.InstallerPath, WorkbenchJson.AsText(byoJson["AdHocSource"]));
        foreach (var json in new[] { directJson, byoJson })
        {
            json.Remove("AdHocSource");
            json.Remove("StagedAt");
            json.Remove("PlanDigest");
        }
        Assert.Equal(directJson.ToJsonString(), byoJson.ToJsonString());
    }

    [Fact]
    public async Task AByoStage_UsesTheStoredReviewValues()
    {
        var dropped = Fixture("nsis-u-none.bin", "TestApp-Setup-1.2.3.exe");
        var analyzer = ExeAnalyzer();
        var analysis = await analyzer.AnalyzeAsync(dropped);
        var saved = Store.SaveByoApplication(dropped, ByoInstallerIdentity.From(analysis), "Test App Renamed", "Test Co", "1.2.3",
            new ByoStageValues { InstallArgs = "/S /D=C:\\Apps\\TestApp", Confirmed = true });

        var plan = await ByoStaging.PlanAsync(Store, analyzer, saved.ApplicationId);

        Assert.Equal("Test App Renamed", plan.Manifest.AppName);
        Assert.Equal("/S /D=C:\\Apps\\TestApp", plan.Manifest.InstallArgs);
        Assert.Equal("1.2.3", plan.Manifest.SoftwareVersion);
        Assert.Equal("NSIS", plan.Manifest.DetectedEngine);
    }

    [Fact]
    public async Task APredictedAnalysisThatWasNeverConfirmed_DoesNotStage()
    {
        var installer = Installer("plain.exe", new string('\0', 600));
        var analyzer = ExeAnalyzer();
        var analysis = await analyzer.AnalyzeAsync(installer);
        Assert.True(analysis.RequiresConfirmation);
        var saved = Store.SaveByoApplication(installer, ByoInstallerIdentity.From(analysis), "Plain", "Contoso", "1.0",
            new ByoStageValues { InstallArgs = "/quiet", Confirmed = false });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ByoStaging.PlanAsync(Store, analyzer, saved.ApplicationId));
        Assert.Contains("predicted, not confirmed", error.Message);
    }

    [Fact]
    public async Task AProfiledByoStage_SealsItsBuild_AndPackageWithoutStageReusesThatManifest()
    {
        var dropped = Fixture("nsis-u-none.bin", "TestApp-Setup-1.2.3.exe");
        var analyzer = ExeAnalyzer();
        var analysis = await analyzer.AnalyzeAsync(dropped);
        var saved = Store.SaveByoApplication(dropped, ByoInstallerIdentity.From(analysis), "Test App", "Test Co", "1.2.3",
            new ByoStageValues { InstallArgs = "/S", Confirmed = true });
        var profile = _t.NewProfile(saved.ApplicationId, "Pilot");
        var downloadRoot = _t.NewFolder("downloads");
        var snapshot = Store.CreateRunSnapshot(new RunSnapshotRequest
        {
            ApplicationId = saved.ApplicationId, ProfileId = profile.ProfileId, Target = RunTargets.Mecm, DownloadRoot = downloadRoot
        });

        var plan = await ByoStaging.PlanAsync(Store, analyzer, saved.ApplicationId);
        Assert.Null(ByoStaging.FindPriorStageManifest(plan, downloadRoot, profile.ProfileId));
        var result = await ByoStaging.StageAsync(Staging(), plan, downloadRoot, inputs: new StageInputs { RunSnapshot = snapshot });
        Assert.True(result.Success, result.ErrorMessage);

        Assert.StartsWith(Path.Combine(downloadRoot, "profiles", profile.ProfileId), result.ContentPath);
        Assert.Equal(Path.Combine(ByoStaging.StageFolder(plan.Manifest, downloadRoot, profile.ProfileId), "stage-manifest.json"), result.ManifestPath);
        var record = Assert.Single(BuildRecords.List(Store.DataRoot, saved.ApplicationId, profile.ProfileId));
        Assert.Equal(snapshot.BuildId, WorkbenchJson.AsText(record["BuildId"]));
        Assert.Equal(result.ManifestPath,
            BuildRecords.ResolveStageManifestForBuild(snapshot.BuildId, ByoStaging.ApplicationStageRoot(plan.Manifest, downloadRoot, profile.ProfileId)).ManifestPath);

        Assert.Equal(result.ManifestPath, ByoStaging.FindPriorStageManifest(plan, downloadRoot, profile.ProfileId, RunTargets.Mecm));
        Assert.Null(ByoStaging.FindPriorStageManifest(plan, downloadRoot, profile.ProfileId, RunTargets.WsusOnly));
        Assert.Null(ByoStaging.FindPriorStageManifest(plan, downloadRoot, ApplicationIds.DefaultProfileId));
    }

    private static Dictionary<string, string> Tree(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), WorkbenchJson.FileSha256, StringComparer.OrdinalIgnoreCase);
}
