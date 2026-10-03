using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Sealed build records, build selection by exact BuildId, and publication records.</summary>
public sealed class BuildRecordTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private StageManifest SealedManifest(string applicationId, string profileId, string buildId) => new()
    {
        AppName = "Test App",
        SoftwareVersion = "1.2.3",
        BuildId = buildId,
        ApplicationId = applicationId,
        ProfileId = profileId,
        ProfileRevision = 2,
        PlanDigest = "ABC123",
        SetupFile = "install.bat",
        RunOverrides = new JsonObject { ["EstimatedMinutes"] = 7 },
        ScriptSigning = new JsonObject { ["PolicyDigest"] = "digest" },
        CustomAssets = [new StageCustomAsset { Category = "SourceFile", RelativePath = "a.txt", Sha256 = "00" }]
    };

    [Fact]
    public void ASealedRecordRoundTripsAndCopiesTheScriptsFolder()
    {
        var stage = _t.NewFolder();
        _t.NewFile(Path.Combine(stage, "scripts", "detect.ps1"), "# signed detector");
        var manifest = SealedManifest("catalog:package-build", "p1", "20260101-010101-aaaaaaaa");

        var record = BuildRecords.Write(_t.Store.DataRoot, stage, manifest)!;

        Assert.Equal("20260101-010101-aaaaaaaa", record["BuildId"]!.GetValue<string>());
        var latest = BuildRecords.Latest(_t.Store.DataRoot, "catalog:package-build")!;
        Assert.Equal("20260101-010101-aaaaaaaa", latest["BuildId"]!.GetValue<string>());
        Assert.Equal(2, latest["ProfileRevision"]!.GetValue<int>());
        Assert.Equal("digest", latest["PolicyDigest"]!.GetValue<string>());
        Assert.Equal(7, latest["RunOverrides"]!["EstimatedMinutes"]!.GetValue<int>());
        Assert.Equal(stage, latest["StageRoot"]!.GetValue<string>());
        var folder = Path.GetDirectoryName(latest["Path"]!.GetValue<string>())!;
        Assert.Equal("# signed detector", File.ReadAllText(Path.Combine(folder, "scripts", "detect.ps1")));
    }

    [Fact]
    public void ABuildWithoutARunSnapshotWritesNoRecord()
    {
        Assert.Null(BuildRecords.Write(_t.Store.DataRoot, _t.NewFolder(), new StageManifest { AppName = "x" }));
        Assert.False(Directory.Exists(Path.Combine(_t.Store.DataRoot, "builds")));
    }

    [Fact]
    public void RecordsListNewestBuildFirstAndFilterByProfile()
    {
        const string id = "catalog:package-list";
        BuildRecords.Write(_t.Store.DataRoot, _t.NewFolder(), SealedManifest(id, "p1", "20260101-000000-aaaaaaaa"));
        BuildRecords.Write(_t.Store.DataRoot, _t.NewFolder(), SealedManifest(id, "p1", "20260102-000000-bbbbbbbb"));
        BuildRecords.Write(_t.Store.DataRoot, _t.NewFolder(), SealedManifest(id, "p2", "20260103-000000-cccccccc"));

        Assert.Equal(["20260102-000000-bbbbbbbb", "20260101-000000-aaaaaaaa"],
            BuildRecords.List(_t.Store.DataRoot, id, "p1").Select(r => r["BuildId"]!.GetValue<string>()));
        Assert.Equal("20260103-000000-cccccccc", BuildRecords.Latest(_t.Store.DataRoot, id)!["BuildId"]!.GetValue<string>());
        Assert.Empty(BuildRecords.List(_t.Store.DataRoot, "catalog:never-built"));
    }

    [Fact]
    public void AStageManifestResolvesByExactBuildId()
    {
        var searchRoot = _t.NewFolder();
        var stage = Path.Combine(searchRoot, "1.0.0");
        _t.NewFile(Path.Combine(stage, "stage-manifest.json"), """{"SchemaVersion":4,"BuildId":"20260101-010101-aaaaaaaa"}""");

        var resolved = BuildRecords.ResolveStageManifestForBuild("20260101-010101-aaaaaaaa", searchRoot);

        Assert.Equal(stage, resolved.StageRoot);
        Assert.Equal("20260101-010101-aaaaaaaa", resolved.Manifest.BuildId);
    }

    [Fact]
    public void AStaleBuildIsRefusedEvenWithANewerManifestUnderTheSameRoot()
    {
        var searchRoot = _t.NewFolder();
        _t.NewFile(Path.Combine(searchRoot, "2.0.0", "stage-manifest.json"), """{"SchemaVersion":4,"BuildId":"20260301-000000-ffffffff"}""");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildRecords.ResolveStageManifestForBuild("20200101-000000-deadbeef", searchRoot));
        Assert.Equal($"stale build: no staged manifest under '{searchRoot}' carries BuildId '20200101-000000-deadbeef'.", ex.Message);
    }

    [Fact]
    public void AMissingSearchRootIsRefused()
    {
        var missing = Path.Combine(_t.Root, "missing");
        var ex = Assert.Throws<DirectoryNotFoundException>(() => BuildRecords.ResolveStageManifestForBuild("x", missing));
        Assert.Equal($"Stage search root not found: {missing}", ex.Message);
    }

    [Fact]
    public void PublicationRecordsAppendOneJsonLinePerPublication()
    {
        const string id = "catalog:package-publish";
        PublicationRecords.Write(_t.Store.DataRoot, id, "b1", "MECM", result: "Succeeded");
        var (path, record) = PublicationRecords.Write(_t.Store.DataRoot, id, "b2", "Intune", result: "Failed", message: "no tenant");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        var last = WorkbenchJson.Parse(lines[1])!;
        Assert.Equal("Intune", last["Target"]!.GetValue<string>());
        Assert.Equal("Failed", last["Result"]!.GetValue<string>());
        Assert.Equal("no tenant", last["Message"]!.GetValue<string>());
        Assert.Equal("default", record["ProfileId"]!.GetValue<string>());
        Assert.Equal(["Timestamp", "ApplicationId", "ProfileId", "BuildId", "Target", "RemoteAppId", "SiteOrTenant", "Result", "Message"],
            last.AsObject().Select(p => p.Key));
        Assert.Matches(@"\\publications\\catalog_package-publish\\\d{6}\.jsonl$", path);
    }

    [Theory]
    [InlineData("WSUS", "Succeeded")]
    [InlineData("MECM", "Partial")]
    public void APublicationRecordOutsideTheTargetAndResultSetsIsRefused(string target, string result)
    {
        Assert.Throws<ArgumentException>(() => PublicationRecords.Write(_t.Store.DataRoot, "catalog:x", "b", target, result: result));
    }

    [Fact]
    public void ABuildWithoutARunSnapshotRecordsNoPublication()
    {
        Assert.Null(PublicationRecords.TryAppend(new StageManifest(), PublicationRecords.Mecm, PublicationRecords.Succeeded,
            dataRoot: _t.Store.DataRoot));
        Assert.False(Directory.Exists(Path.Combine(_t.Store.DataRoot, "publications")));
    }

    [Fact]
    public void ASnapshotBuildRecordsItsPublication()
    {
        var manifest = new StageManifest { ApplicationId = "catalog:package-pub", BuildId = "b9", ProfileId = "p1" };
        var path = PublicationRecords.TryAppend(manifest, PublicationRecords.Mecm, PublicationRecords.Skipped, "App", "P01",
            dataRoot: _t.Store.DataRoot)!;
        var line = WorkbenchJson.Parse(Assert.Single(File.ReadAllLines(path)))!;
        Assert.Equal("p1", line["ProfileId"]!.GetValue<string>());
        Assert.Equal("Skipped", line["Result"]!.GetValue<string>());
        Assert.Equal("P01", line["SiteOrTenant"]!.GetValue<string>());
    }
}
