using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>COL-01 to COL-10 and DPL-01 to DPL-05: parity with the source on the CM01 rows, and synthetic cases the lab cannot hold.</summary>
public sealed class SiteHygieneCollectionTests
{
    private static readonly string[] AllCollectionAreas = ["Collections", "CollectionSchedules", "MaintenanceWindows", "Deployments"];

    private static readonly string[] ColColumns = ["CollectionID", "Name", "MemberCount", "RefreshType", "LimitToCollectionID"];
    private static readonly string[] DepColumns = ["DependentCollectionID", "SourceCollectionID", "RelationshipType"];
    private static readonly string[] DetailColumns = ["CollectionID", "DirectRules", "QueryRules", "DaySpan", "HourSpan", "MinuteSpan", "StartTime"];
    private static readonly string[] DeploymentColumns = ["DeploymentID", "SoftwareName", "ProgramName", "PackageID", "ModelName", "CollectionID", "CollectionName", "DeploymentIntent", "FeatureType", "NumberTargeted", "NumberSuccess", "NumberInProgress", "NumberErrors", "EnforcementDeadline", "CreationTime"];

    [Theory]
    [InlineData("COL-01")]
    [InlineData("COL-05")]
    [InlineData("COL-07")]
    public void TheNativeCheckFindsWhatTheSourceFindsByCollectionId(string checkId)
    {
        var (findings, checks) = SiteHygieneParity.Native(AllCollectionAreas);
        Assert.Equal(HygieneCheckStates.Ran, checks.Single(c => c.CheckId == checkId).State);
        SiteHygieneParity.SameAsSource(findings, checkId);
        Assert.NotEmpty(findings.Where(f => f.CheckId == checkId));
    }

    [Theory]
    [InlineData("COL-02")]
    [InlineData("COL-10")]
    [InlineData("DPL-01")]
    [InlineData("DPL-03")]
    [InlineData("DPL-05")]
    public void TheNativeCheckFindsWhatTheSourceFindsByName(string checkId)
    {
        var (findings, _) = SiteHygieneParity.Native(AllCollectionAreas);
        SiteHygieneParity.SameAsSource(findings, checkId, s => s.ObjectName, f => f.ObjectName);
        Assert.NotEmpty(findings.Where(f => f.CheckId == checkId));
    }

    [Theory]
    [InlineData("COL-03")]
    [InlineData("COL-04")]
    [InlineData("COL-06")]
    [InlineData("COL-08")]
    [InlineData("COL-09")]
    [InlineData("DPL-02")]
    [InlineData("DPL-04")]
    public void AChecksWithNoCaseOnCm01FindsNothingThereAsTheSourceDoes(string checkId)
    {
        var (findings, checks) = SiteHygieneParity.Native(AllCollectionAreas);
        Assert.Equal(HygieneCheckStates.Ran, checks.Single(c => c.CheckId == checkId).State);
        Assert.DoesNotContain(findings, f => f.CheckId == checkId);
        Assert.DoesNotContain(SiteHygieneFixtures.SourceFindings(), f => f.CheckId == checkId);
    }

    [Fact]
    public void TwoExpiredWindowsOfOneCollectionAreTwoFindingsWithExactRemovals()
    {
        var (findings, _) = SiteHygieneParity.Native(AllCollectionAreas);
        var windows = findings.Where(f => f.CheckId == "COL-10").ToList();
        Assert.Equal(2, windows.Count);
        Assert.Equal(2, windows.Select(f => f.Key).Distinct().Count());
        var one = windows.Single(f => f.ObjectName == "SHL-MW-Expired on SHL-Col-MW");
        Assert.Equal("MCM00219|{51634A25-AF2B-400B-A492-CAF093FF542C}", one.ObjectKey);
        Assert.Contains("Where-Object { $_.ServiceWindowID -ne '{51634A25-AF2B-400B-A492-CAF093FF542C}' }", one.FixText);
        Assert.DoesNotContain("Remove-CMMaintenanceWindow", one.FixText);
    }

    [Fact]
    public void TheExpiredDeploymentIsKeyedByItsAssignmentAndRemovedByItsUniqueId()
    {
        var (findings, _) = SiteHygieneParity.Native(AllCollectionAreas);
        var expired = findings.Single(f => f.CheckId == "DPL-01");
        Assert.Equal("16777469", expired.ObjectKey);
        Assert.Equal("Remove-CMApplicationDeployment -DeploymentId '{BD4982F4-5CF9-46A4-94E8-D2F6FF883480}' -Force", expired.FixText);
    }

    [Fact]
    public void ScheduleChecksDoNotRunWithoutTheScheduleArea()
    {
        var (findings, checks) = SiteHygieneParity.Native("Collections");
        Assert.Equal(HygieneCheckStates.NotSelected, checks.Single(c => c.CheckId == "COL-05").State);
        Assert.Contains("Collection evaluation schedules", checks.Single(c => c.CheckId == "COL-05").Reason);
        Assert.Equal(HygieneCheckStates.Ran, checks.Single(c => c.CheckId == "COL-07").State);
        Assert.DoesNotContain(findings, f => f.CheckId == "COL-05");
    }

    [Fact]
    public void ALoopOfCollectionReferencesIsFoundOncePerCollection()
    {
        var read = SiteHygieneParity.EmptySite()
            .Put("Collections", ColColumns, ["MCM00001", "Loop A", "0", "1", "SMS00001"], ["MCM00002", "Loop B", "0", "1", "SMS00001"], ["MCM00003", "Outside", "0", "1", "SMS00001"])
            .Put("CollectionDependencies", DepColumns, ["MCM00001", "MCM00002", "2"], ["MCM00002", "MCM00001", "3"], ["MCM00003", "MCM00001", "2"], ["MCM00001", "SMS00001", "1"]);
        var (findings, _) = SiteHygieneParity.Native(read, "Collections");
        var loops = findings.Where(f => f.CheckId == "COL-08").ToList();
        Assert.Equal(["MCM00001", "MCM00002"], loops.Select(f => f.ObjectKey).Order());
        Assert.Contains("include reference from 'Loop A' to 'Loop B'", loops.Single(f => f.ObjectKey == "MCM00001").Evidence);
    }

    [Fact]
    public void ADeepChainIsMeasuredWithoutRecursionAndInLinearWork()
    {
        const int n = 50_000;
        var cols = Enumerable.Range(0, n).Select(i => new[] { $"C{i:D7}", $"Chain {i}", "1", "1", "SMS00001" }).ToArray();
        var deps = Enumerable.Range(0, n - 1).Select(i => new[] { $"C{i:D7}", $"C{i + 1:D7}", "2" }).ToArray();
        var read = SiteHygieneParity.EmptySite().Put("Collections", ColColumns, cols).Put("CollectionDependencies", DepColumns, deps);
        var context = new HygieneContext(read, new HygieneThresholds(), SiteHygieneFixtures.CaptureLocal, "MCM");
        var (findings, _) = HygieneEngine.Run(context, new HashSet<string> { "Collections" }, false);
        var chains = findings.Where(f => f.CheckId == "COL-07").ToList();
        Assert.Equal(n - 4, chains.Count);
        Assert.Contains($"chain {n - 1} levels deep", chains.Single(f => f.ObjectKey == "C0000000").Evidence, StringComparison.Ordinal);
        Assert.True(context.RowReads <= 20L * n, $"The checks enumerated {context.RowReads} rows for {n} collections.");
    }

    [Fact]
    public void ADirectRuleCollectionOnAScheduleWithANonIncrementalLimitIsCol06()
    {
        var read = SiteHygieneParity.EmptySite()
            .Put("Collections", ColColumns, ["MCM00010", "Direct", "3", "2", "MCM00011"], ["MCM00011", "Limit", "9", "2", "SMS00001"], ["MCM00012", "Direct under incremental", "3", "2", "SMS00001"], ["SMS00001", "All Systems", "9", "6", ""])
            .Put("CollectionDetails", DetailColumns, ["MCM00010", "3", "0", "1", "0", "0", "2026-10-01T02:00:00"], ["MCM00012", "3", "0", "1", "0", "0", "2026-10-01T02:00:00"]);
        var (findings, _) = SiteHygieneParity.Native(read, "CollectionSchedules");
        var f = Assert.Single(findings, x => x.CheckId == "COL-06");
        Assert.Equal("MCM00010", f.ObjectKey);
        Assert.Equal("Set-CMCollection -CollectionId 'MCM00010' -RefreshType 'Manual'", f.FixText);
    }

    [Fact]
    public void TenFullUpdatesInOneHourAreAHotSpot()
    {
        var cols = Enumerable.Range(0, 10).Select(i => new[] { $"MCM100{i:D2}", $"Hot {i}", "1", "2", "SMS00001" }).ToArray();
        var details = Enumerable.Range(0, 10).Select(i => new[] { $"MCM100{i:D2}", "0", "1", "1", "0", "0", "2026-10-01T02:15:00" }).ToArray();
        var read = SiteHygieneParity.EmptySite().Put("Collections", ColColumns, cols).Put("CollectionDetails", DetailColumns, details);
        var (findings, _) = SiteHygieneParity.Native(read, "CollectionSchedules");
        var f = Assert.Single(findings, x => x.CheckId == "COL-09");
        Assert.Equal("10 full updates starting at 02:00", f.ObjectName);
        Assert.Equal(HygieneFixKind.None, f.FixKind);
        Assert.Null(f.FixText);
        Assert.NotEmpty(f.Guidance);
    }

    [Fact]
    public void RequiredDeploymentsPastDeadlineAndToBroadCollectionsAreFound()
    {
        var read = SiteHygieneParity.EmptySite()
            .Put("Deployments", DeploymentColumns,
                ["{A}", "Failing App", "", "MCM00100", "", "MCM00200", "Pilot", "1", "1", "10", "5", "0", "3", "2026-10-01T00:00:00", "2026-09-01T00:00:00"],
                ["{B}", "Broad App", "", "MCM00101", "", "SMS00001", "All Systems", "1", "1", "10", "10", "0", "0", "2026-10-30T00:00:00", "2026-09-01T00:00:00"],
                ["{C}", "Named All Systems", "", "MCM00102", "", "MCM00300", "All Systems", "1", "1", "10", "10", "0", "0", "2026-10-30T00:00:00", "2026-09-01T00:00:00"]);
        var (findings, _) = SiteHygieneParity.Native(read, "Deployments");
        var failing = Assert.Single(findings, f => f.CheckId == "DPL-02");
        Assert.Equal("{A}", failing.ObjectKey);
        Assert.Contains("3 of 10 targeted clients in error (30%)", failing.Evidence);
        var broad = Assert.Single(findings, f => f.CheckId == "DPL-04");
        Assert.Equal("{B}", broad.ObjectKey);
    }
}
