using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The build record of a PS1 packager stage, sealed from the manifest the child wrote:
/// the same record a JSON stage seals, none for a manifest that does not carry the
/// snapshot's BuildId, and the child's own record left as it is.
/// </summary>
public sealed class PackagerStageSealTests : IDisposable
{
    private const string ApplicationId = "catalog:package-widget";
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private RunSnapshot Snapshot() =>
        _t.Store.CreateRunSnapshot(new RunSnapshotRequest { ApplicationId = ApplicationId, ProfileId = _t.NewProfile(ApplicationId, "Pilot").ProfileId });

    private string StageManifest(string root, RunSnapshot? carries, string version = "1.2.3")
    {
        var folder = Path.Combine(root, "Widget", version);
        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        File.WriteAllText(Path.Combine(folder, "scripts", "detect.ps1"), "exit 0");
        var manifest = new StageManifest
        {
            AppName = "Widget",
            SoftwareVersion = version,
            InstallerFile = "widget.exe",
            InstallerType = "EXE",
            BuildId = carries?.BuildId ?? "",
            ApplicationId = carries?.ApplicationId ?? "",
            ProfileId = carries?.ProfileId ?? "",
            ProfileRevision = carries?.ProfileRevision ?? 0
        };
        var path = Path.Combine(folder, "stage-manifest.json");
        new ManifestService().Write(path, manifest);
        return path;
    }

    [Fact]
    public void TheManifestTheChildLoggedIsSealedInTheJsonStageRecordShape()
    {
        var snapshot = Snapshot();
        var manifestPath = StageManifest(_t.NewFolder("downloads"), snapshot);
        var output = $"Staging...\r\n[INFO] Wrote stage manifest         : {manifestPath}\r\nDone";

        var record = PackagerStageSeal.Seal(snapshot, PackagerStageSeal.ManifestPathFromOutput(output), null, out var message);

        Assert.NotNull(record);
        Assert.StartsWith("Build record sealed", message);
        Assert.Equal(snapshot.BuildId, WorkbenchJson.Get(record, "BuildId").Text);
        Assert.Equal("1.2.3", WorkbenchJson.Get(record, "SoftwareVersion").Text);
        Assert.Equal(Path.GetDirectoryName(manifestPath), WorkbenchJson.Get(record, "StageRoot").Text);
        var listed = Assert.Single(BuildRecords.List(_t.Store.DataRoot, ApplicationId, snapshot.ProfileId));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(WorkbenchJson.Get(listed, "Path").Text)!, "scripts", "detect.ps1")));
    }

    [Fact]
    public void AManifestWithoutTheSnapshotsBuildIdGetsNoRecord()
    {
        var snapshot = Snapshot();
        var manifestPath = StageManifest(_t.NewFolder("downloads"), carries: null);

        Assert.Null(PackagerStageSeal.Seal(snapshot, manifestPath, null, out var message));
        Assert.Contains($"carries no BuildId {snapshot.BuildId}", message);
        Assert.Empty(BuildRecords.List(_t.Store.DataRoot, ApplicationId));
    }

    [Fact]
    public void WithoutALoggedPathTheManifestCarryingTheBuildIdIsFound()
    {
        var snapshot = Snapshot();
        var root = _t.NewFolder("downloads");
        StageManifest(root, carries: null, version: "1.2.2");
        StageManifest(root, snapshot);

        Assert.NotNull(PackagerStageSeal.Seal(snapshot, null, root, out _));
        Assert.Equal("1.2.3", WorkbenchJson.Get(BuildRecords.Latest(_t.Store.DataRoot, ApplicationId), "SoftwareVersion").Text);
    }

    [Fact]
    public void ARecordTheChildSealedIsLeftAsItIs()
    {
        var snapshot = Snapshot();
        var manifestPath = StageManifest(_t.NewFolder("downloads"), snapshot);
        var stageRoot = Path.GetDirectoryName(manifestPath)!;
        var own = BuildRecords.Write(_t.Store.DataRoot, stageRoot, new ManifestService().Read(manifestPath))!;
        var before = File.ReadAllText(WorkbenchJson.Get(own, "Path").Text!);

        PackagerStageSeal.Seal(snapshot, manifestPath, null, out var message);

        Assert.StartsWith("Build record sealed by the packager", message);
        Assert.Equal(before, File.ReadAllText(WorkbenchJson.Get(own, "Path").Text!));
    }

    [Fact]
    public void NoLoggedManifestAndNoSearchRootSealsNothing()
    {
        Assert.Null(PackagerStageSeal.Seal(Snapshot(), null, null, out var message));
        Assert.StartsWith("No build record sealed", message);
        Assert.Null(PackagerStageSeal.ManifestPathFromOutput("Staged without a manifest line"));
    }
}
