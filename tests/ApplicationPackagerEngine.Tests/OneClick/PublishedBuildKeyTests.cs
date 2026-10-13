using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests.OneClick;

/// <summary>
/// The build a destination's last publish came from is read from that one sealed build
/// record. The other records of the application are not opened for it.
/// </summary>
public sealed class PublishedBuildKeyTests : IDisposable
{
    private const string ApplicationId = "catalog:widget";
    private const string Published = "20261003-120000-aaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-buildkey-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;

    public PublishedBuildKeyTests()
    {
        _dataRoot = Path.Combine(_root, "workbench");
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Seal(string buildId, string profileId, int revision)
    {
        var stage = Directory.CreateDirectory(Path.Combine(_root, "stage-" + buildId)).FullName;
        var record = BuildRecords.Write(_dataRoot, stage, new StageManifest
        {
            ApplicationId = ApplicationId, BuildId = buildId, ProfileId = profileId, ProfileRevision = revision,
            SoftwareVersion = "2.0", AppName = "Widget"
        });
        return record!["Path"]!.GetValue<string>();
    }

    [Fact]
    public void ARecordIsFoundByItsBuildIdInAnyProfile()
    {
        Seal(Published, "default", 3);
        Seal("20261004-120000-bbbbbbbb", "pilot001", 1);

        var found = BuildRecords.Find(_dataRoot, ApplicationId, "20261004-120000-BBBBBBBB");
        Assert.Equal("pilot001", WorkbenchJson.Get(found!, "ProfileId").Text);
        Assert.EndsWith(BuildRecords.FileName, WorkbenchJson.Get(found!, "Path").Text);
        Assert.Null(BuildRecords.Find(_dataRoot, ApplicationId, "20261005-120000-cccccccc"));
        Assert.Null(BuildRecords.Find(_dataRoot, "catalog:other", Published));
        Assert.Null(BuildRecords.Find(_dataRoot, ApplicationId, @"..\default"));
        Assert.Null(BuildRecords.Find(_dataRoot, ApplicationId, ""));
    }

    [Fact]
    public void TheHistoryOfARowReadsOnlyTheRecordOfThePublishedBuild()
    {
        Seal(Published, "default", 3);
        // A record that cannot be read fails every read of the whole record set.
        File.WriteAllText(Seal("20261004-120000-bbbbbbbb", "default", 4), "{ not json");
        File.WriteAllText(Seal("20261002-120000-cccccccc", "pilot001", 1), "{ not json");

        var inventory = new ApplicationInventoryService(Path.Combine(_root, "history.db"));
        var settings = new AppSettings { SiteCode = "MCM", ProviderMachine = "cm01" };
        var row = new PackagerInfo { Uid = "widget", Application = "Widget", Script = "widget.json" };
        var plan = new PackagerExecutionService.OneClickBuildPlan(_dataRoot, ApplicationId, "default", 4);
        inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("widget", ApplicationInventoryProjection.IdentityFor(row, plan),
            InventoryEnvironmentIdentity.ConfigMgr(settings.SiteCode, settings.ProviderMachine), "Publish", "2.0",
            InventoryOutcome.Succeeded, "Widget 2.0", DateTimeOffset.UtcNow, Published));

        var entry = new ApplicationInventoryProjection(inventory).Entry(row, settings, plan);

        Assert.Equal("2.0", entry.Published[OneClickDestinations.ConfigMgr].Version);
        Assert.Equal(OneClickHistory.BuildKey("default", 3, ""), entry.PublishedBuild[OneClickDestinations.ConfigMgr]);
    }
}
