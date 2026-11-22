using System.Data.SQLite;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// One test per site-scoped store (Q-112d): with a site active the store reads and writes below sites\&lt;FOLDER&gt; of the data root,
/// with none active it keeps the single-site path of earlier versions, and the data of one site is not visible under another site.
/// Site codes are fictional (AB1, CD2). A scope sets the site for the test's own async flow only.
/// </summary>
public sealed class SiteStoreRoutingTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "ap-sitestores-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SQLiteConnection.ClearAllPools();
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static TheoryData<string, string> Stores => new()
    {
        { "check-cm-result", "results" },
        { "history-db", @"results\history.db" },
        { "ring-plans", @"deployments\rings" },
        { "ring-runs", @"deployments\rings\runs" },
        { "deployment-audit", @"deployments\audit.jsonl" },
        { "hygiene", "hygiene" },
        { "hygiene-audit", @"hygiene\" + HygieneAuditLog.FileName },
        { "dpcontent-audit", @"dpcontent\" + DpContentAuditLog.FileName },
        { "dpcontent-reports", @"dpcontent\Reports" },
        { "pivots-sessions", @"pivots\sessions" },
        { "maintenance-reports", @"maintenance\reports" },
        { "collections-reports", @"collections\reports" },
        { "compliance-reports", @"collections\reports" },
    };

    private static string PathOf(string store) => store switch
    {
        "check-cm-result" => CheckConfigMgrResultExport.DefaultFolder,
        "history-db" => SiteHistoryDatabase.DefaultPath(),
        "ring-plans" => RingPlanStore.DefaultFolder(),
        "ring-runs" => RingRunStore.DefaultFolder(),
        "deployment-audit" => DeploymentAuditLog.DefaultPath(),
        "hygiene" => HygieneFolders.Root(),
        "hygiene-audit" => HygieneAuditLog.DefaultPath(),
        "dpcontent-audit" => DpContentAuditLog.DefaultPath(),
        "dpcontent-reports" => DpContentReportWriter.DefaultFolder(),
        "pivots-sessions" => PivotsSessionStore.DefaultFolder(),
        "maintenance-reports" => MaintenanceReportWriter.DefaultFolder(),
        "collections-reports" => CollectionReportWriter.DefaultFolder(),
        "compliance-reports" => ComplianceReportWriter.DefaultFolder(),
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public void AStoreResolvesBelowTheActiveSiteFolder(string store, string relative)
    {
        using (SiteContext.Scope("AB1", _data))
            Assert.Equal(Path.Combine(_data, "sites", "AB1", relative), PathOf(store));
        using (SiteContext.Scope("CD2", _data))
            Assert.Equal(Path.Combine(_data, "sites", "CD2", relative), PathOf(store));
        using (SiteContext.Scope("AB1~2", _data))
            Assert.Equal(Path.Combine(_data, "sites", "AB1~2", relative), PathOf(store));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void WithNoSiteActiveAStoreKeepsTheSingleSitePath(string store, string relative)
    {
        _ = relative;
        using (SiteContext.Scope(null, _data))
            Assert.DoesNotContain(Path.Combine("AppPackagerData", "sites"), PathOf(store));
        using (SiteContext.Scope(null, _data))
            Assert.DoesNotContain(Path.Combine(_data, "sites"), PathOf(store));
    }

    [Fact]
    public void ASiteSwitchMovesTheNextCallWithoutAnyRestart()
    {
        // The path is read on every use: the same store object follows the scope.
        var plans = new RingPlanStore();
        string a, b;
        using (SiteContext.Scope("AB1", _data)) a = plans.Folder;
        using (SiteContext.Scope("CD2", _data)) b = plans.Folder;
        Assert.NotEqual(a, b);
        Assert.EndsWith(Path.Combine("AB1", "deployments", "rings"), a);
        Assert.EndsWith(Path.Combine("CD2", "deployments", "rings"), b);
    }

    [Fact]
    public void TheTemplatesAndTheCveCacheStayGlobal()
    {
        using (SiteContext.Scope("AB1", _data))
        {
            Assert.DoesNotContain("sites", DeploymentTemplateStore.DefaultFolder());
            Assert.DoesNotContain("sites", ApplicationPackagerEngine.Services.Vulnerability.VulnerabilityPaths.DataFolder);
            Assert.DoesNotContain("sites", ApplicationPackagerEngine.Services.Logging.AppLog.DefaultFolder());
            Assert.DoesNotContain("sites", ApplicationPackagerEngine.Services.VendorImport.FeedCredentialStore.DefaultPath);
        }
    }

    [Fact]
    public void TheHistoryOfOneSiteIsInvisibleUnderAnotherAndReturnsOnSwitchBack()
    {
        var history = new SiteOperationHistory();
        var at = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        var counts = new Dictionary<string, int> { ["changed"] = 1 };
        using (SiteContext.Scope("AB1", _data))
            Assert.NotNull(history.Record(new SiteOperationEntry("Maintenance Windows", "Add", "Collection one", SiteOperationOutcome.Succeeded, counts, at)));
        using (SiteContext.Scope("CD2", _data))
        {
            Assert.Empty(history.Recent(null, 10));
            Assert.NotNull(history.Record(new SiteOperationEntry("DP Content Manager", "Redistribute", "Package two", SiteOperationOutcome.Succeeded, counts, at)));
            Assert.Equal(["Redistribute"], history.Recent(null, 10).Select(r => r.Action));
        }
        using (SiteContext.Scope("AB1", _data))
            Assert.Equal(["Add"], history.Recent(null, 10).Select(r => r.Action));
        Assert.True(File.Exists(Path.Combine(_data, "sites", "AB1", "results", "history.db")));
        Assert.True(File.Exists(Path.Combine(_data, "sites", "CD2", "results", "history.db")));
    }

    [Fact]
    public void TheInventoryOfOneSiteIsInvisibleUnderAnotherAndReturnsOnSwitchBack()
    {
        var inventory = new ApplicationInventoryService();
        var identity = new InventoryApplicationIdentity("uid-seven", "catalog:7zip", "profile-a", 1);
        var staged = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        using (SiteContext.Scope("AB1", _data))
            inventory.RecordStagedBuild(new InventoryStagedBuild(identity, "24.09", "build-1", @"C:\b\record.json", @"C:\b\manifest.json", staged));
        using (SiteContext.Scope("CD2", _data))
            Assert.Null(inventory.GetPackageState("catalog:7zip", "profile-a"));
        using (SiteContext.Scope("AB1", _data))
            Assert.Equal("24.09", inventory.GetPackageState("catalog:7zip", "profile-a")?.Version);
    }

    [Fact]
    public void AFixedPathGivenToAStoreIsUsedAsItIs()
    {
        var fixedPath = Path.Combine(_data, "fixed", "history.db");
        var history = new SiteOperationHistory(fixedPath);
        using (SiteContext.Scope("AB1", _data))
            history.Record(new SiteOperationEntry("T", "A", "x", SiteOperationOutcome.Succeeded, new Dictionary<string, int>()));
        Assert.True(File.Exists(fixedPath));
        Assert.False(Directory.Exists(Path.Combine(_data, "sites")));
    }
}
