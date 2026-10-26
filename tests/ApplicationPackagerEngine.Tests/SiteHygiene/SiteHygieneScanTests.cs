using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The whole scan: every area through the real script and the stub site, compared with the source scan of CM01, and the work it does at scale.</summary>
public sealed class SiteHygieneScanTests
{
    [Fact]
    public void AReadSetWithFewerRowsThanShownIsFailed()
    {
        using var document = JsonDocument.Parse("""[{"Kind":"Rows","Set":"Applications","Rows":[["100"]]},{"Kind":"Set","Set":"Applications","Columns":["CI_ID"],"Shown":2,"Total":2,"State":"Read"}]""");
        var read = HygieneSiteRead.Parse(document.RootElement.EnumerateArray().ToArray());

        var table = read.Table("Applications");
        Assert.Equal(HygieneSetStates.Failed, table.State);
        Assert.Equal("row-count", table.Failure);
        Assert.Single(table.Rows);
    }

    /// <summary>
    /// The differences from the source scan of CM01, each with its reason: MNT-02 finds the disabled
    /// "Backup SMS Site Server" that the source misses (S-69); APP-04 does not run with the probe off (the
    /// source probes always).
    /// </summary>
    private static readonly (string CheckId, string Name, bool NativeOnly)[] KnownDifferences =
    [
        ("MNT-02", "Backup SMS Site Server", true),
        ("APP-04", "CMCO-CANARY-App / CMCO-CANARY-App-Script", false)
    ];

    [Fact]
    public async Task AFullScanFindsWhatTheSourceFindsExceptTheListedDifferences()
    {
        using var h = new SiteHygieneHarness();
        var result = await h.ScanAsync();

        Assert.Equal(57, result.Checks.Count);
        Assert.DoesNotContain(result.Checks, c => c.Reason == "The check is not built.");
        Assert.Equal(["APP-04"], result.Checks.Where(c => c.State != HygieneCheckStates.Ran).Select(c => c.CheckId));
        Assert.Equal(HygieneAreas.All.Count, result.Areas.Count);

        var native = result.Findings.Select(f => f.CheckId + "|" + f.ObjectName).ToHashSet(StringComparer.Ordinal);
        var source = SiteHygieneFixtures.SourceFindings().Select(f => f.CheckId + "|" + f.ObjectName).ToHashSet(StringComparer.Ordinal);
        var nativeOnly = native.Except(source).Order(StringComparer.Ordinal).ToList();
        var sourceOnly = source.Except(native).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(KnownDifferences.Where(d => d.NativeOnly).Select(d => d.CheckId + "|" + d.Name), nativeOnly);
        Assert.Equal(KnownDifferences.Where(d => !d.NativeOnly).Select(d => d.CheckId + "|" + d.Name), sourceOnly);
    }

    [Fact]
    public async Task EachReadIsOneQueryAndThePerObjectReadsAreBoundedByTheObjectsTheyNeed()
    {
        using var h = new SiteHygieneHarness();
        var result = await h.ScanAsync();
        var sets = HygieneChecks.RequiredSets(SiteHygieneService.ResolveAreas(new HashSet<string>()));
        Assert.Equal(sets.Count, h.Site.Runs.Count);
        // Per-object sets: one list query plus one read per object (7 collections with a full schedule, 4 collections with settings, 1 application a task sequence installs, 1 task sequence).
        Assert.Equal(sets.Count + 7 + 4 + 1 + 1, result.QueryCount);
        Assert.Equal(7, h.Site.Log.Count(l => l.StartsWith("Get-CMCollection:", StringComparison.Ordinal)));
        Assert.Equal(1, h.Site.Log.Count(l => l.StartsWith("Get-CMApplication:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ThePerObjectCapMakesItsChecksNotRunWithTheCountsInTheReason()
    {
        using var h = new SiteHygieneHarness();
        h.With(maxObjects: 3);
        var result = await h.ScanAsync("CollectionSchedules", "MaintenanceWindows");
        var col5 = result.Checks.Single(c => c.CheckId == "COL-05");
        Assert.Equal(HygieneCheckStates.NotRun, col5.State);
        Assert.Contains("were read for 3 of 7 objects", col5.Reason);
        var col10 = result.Checks.Single(c => c.CheckId == "COL-10");
        Assert.Equal(HygieneCheckStates.NotRun, col10.State);
        Assert.Contains("3 of 4 objects", col10.Reason);
        Assert.True(result.Truncated);
        Assert.Equal(3, h.Site.Log.Count(l => l.StartsWith("Get-CMCollection:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheQueryCountDoesNotGrowWithTheObjectCount()
    {
        var state = SiteHygieneFixtures.StubState();
        state["Generate"] = new JsonObject { ["Devices"] = 3000, ["Applications"] = 3000, ["Collections"] = 3000 };
        using var h = new SiteHygieneHarness(state);
        var result = await h.ScanAsync("Devices", "Applications", "Collections");
        using var small = new SiteHygieneHarness();
        var baseline = await small.ScanAsync("Devices", "Applications", "Collections");
        Assert.Equal(baseline.QueryCount, result.QueryCount);
        Assert.Equal(300, result.Findings.Count(f => f.CheckId == "DEV-01"));
        Assert.True(result.Findings.Count(f => f.CheckId == "APP-01") >= 3000);
    }

    [Fact]
    public void FiftyThousandObjectsPerSetAreJudgedInLinearWork()
    {
        const int n = 50_000;
        var read = SiteHygieneParity.EmptySite()
            .Put("Devices", ["ResourceID", "Name", "IsClient", "ClientVersion", "LastActiveTime", "SMBIOSGUID"],
                Enumerable.Range(0, n).Select(i => new[] { (20000000 + i).ToString(), $"PC{i:D6}", "True", i % 2 == 0 ? "5.00.9146.1009" : "5.00.9141.1011", i % 10 == 0 ? "2026-01-01T00:00:00" : "2026-10-20T00:00:00", Guid.NewGuid().ToString() }).ToArray())
            .Put("Applications", ["CI_ID", "ModelID", "ModelName", "LocalizedDisplayName", "IsDeployed", "IsExpired", "IsSuperseded", "IsSuperseding", "IsEnabled", "HasContent", "Manufacturer", "SoftwareVersion", "DateCreated", "NumberOfDeploymentTypes", "CIVersion"],
                Enumerable.Range(0, n).Select(i => new[] { (30000000 + i).ToString(), (40000000 + i).ToString(), $"ScopeId_G/Application_{i}", $"App {i}", "False", "False", "False", "False", "True", "True", "G", "1", "2026-01-01T00:00:00", "1", "2" }).ToArray())
            .Put("AppRevisions", ["CI_ID", "ModelName", "CIVersion"], Enumerable.Range(0, n).Select(i => new[] { (60000000 + i).ToString(), $"ScopeId_G/Application_{i}", "1" }).ToArray())
            .Put("Collections", ["CollectionID", "Name", "MemberCount", "RefreshType", "LimitToCollectionID"],
                Enumerable.Range(0, n).Select(i => new[] { $"G{i:D7}", $"Collection {i}", (i % 2).ToString(), "6", "SMS00001" }).ToArray())
            .Put("Boundaries", ["BoundaryID", "DisplayName", "Value", "BoundaryType", "GroupCount"],
                Enumerable.Range(0, n).Select(i => new[] { i.ToString(), $"R{i}", $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}-10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}", "3", "1" }).ToArray());
        var context = new HygieneContext(read, new HygieneThresholds(), SiteHygieneFixtures.CaptureLocal, "MCM");
        var (findings, checks) = HygieneEngine.Run(context, SiteHygieneService.ResolveAreas(new HashSet<string>()), false);

        Assert.Equal(n / 10, findings.Count(f => f.CheckId == "DEV-01"));
        Assert.Equal(n, findings.Count(f => f.CheckId == "APP-01"));
        Assert.Equal(n, findings.Count(f => f.CheckId == "APP-05"));
        Assert.Equal(n / 2, findings.Count(f => f.CheckId == "COL-01"));
        Assert.Equal(1, findings.Count(f => f.CheckId == "COL-03"));
        Assert.DoesNotContain(findings, f => f.CheckId == "BND-03");
        Assert.True(context.RowReads <= 60L * n, $"The checks enumerated {context.RowReads} rows for {n} objects per set.");
    }
}
