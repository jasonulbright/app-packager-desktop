using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>APP-01, APP-02, APP-03, APP-05 and PKG-01: parity with the source on the CM01 rows and the real script through the stub site.</summary>
public sealed class SiteHygieneApplicationTests
{
    [Theory]
    [InlineData("APP-01")]
    [InlineData("APP-02")]
    [InlineData("APP-03")]
    [InlineData("APP-05")]
    [InlineData("PKG-01")]
    public void TheNativeCheckFindsWhatTheSourceFindsOnCm01(string checkId)
    {
        var (findings, checks) = SiteHygieneParity.Native("Applications", "Packages");
        Assert.Equal(HygieneCheckStates.Ran, checks.Single(c => c.CheckId == checkId).State);
        SiteHygieneParity.SameAsSource(findings, checkId);
        Assert.Contains(findings, f => f.CheckId == checkId);
    }

    [Fact]
    public void EachFindingCarriesTheTypedFixAndItsExactIdentifiers()
    {
        var (findings, _) = SiteHygieneParity.Native("Applications", "Packages");
        var retired = findings.Single(f => f.CheckId == "APP-02");
        Assert.Equal("SHL-App-Retired", retired.ObjectName);
        Assert.Equal(HygieneFixKind.RemoveApplicationDeployment, retired.FixKind);
        Assert.Equal("{CF6650C6-4DD1-4395-A6E1-FD265169E165}", retired.FixParameters[HygieneFixParameters.DeploymentIds]);
        Assert.Equal("Remove-CMApplicationDeployment -DeploymentId '{CF6650C6-4DD1-4395-A6E1-FD265169E165}' -Force", retired.FixText);

        var unused = findings.First(f => f.CheckId == "APP-01");
        Assert.Equal(HygieneFixKind.RemoveApplication, unused.FixKind);
        Assert.Matches("^Remove-CMApplication -Id [0-9]+ -Force$", unused.FixText);
        Assert.Equal("30", unused.FixParameters[HygieneFixParameters.Days]);

        var revisions = findings.Single(f => f.CheckId == "APP-05" && f.ObjectName == "SHL-App-Unused");
        Assert.Equal(HygieneFixKind.RemoveApplicationRevisionHistory, revisions.FixKind);
        Assert.All(revisions.FixText!.Split("\r\n"), l => Assert.Matches("^Remove-CMApplicationRevisionHistory -Id [0-9]+ -Revision [0-9]+ -Force$", l));

        var pkg = findings.Single(f => f.CheckId == "PKG-01" && f.ObjectKey == "MCM0010E");
        Assert.Equal("Remove-CMPackage -Id 'MCM0010E' -Force", pkg.FixText);
    }

    [Fact]
    public void TheKeyIsTheModelNameNeverTheDisplayName()
    {
        var (findings, _) = SiteHygieneParity.Native("Applications");
        Assert.All(findings.Where(f => f.CheckId.StartsWith("APP-", StringComparison.Ordinal)), f =>
        {
            Assert.StartsWith("ScopeId_", f.ObjectKey);
            Assert.Equal(f.CheckId.ToUpperInvariant() + "|" + f.ObjectKey, f.Key);
        });
    }

    [Fact]
    public void AnApplicationThatIsTheDependencyTargetOfAnOlderRevisionIsNotUnused()
    {
        var read = SiteHygieneFixtures.NativeRead();
        var (before, _) = SiteHygieneParity.Native(read, "Applications");
        var revisions = read.Table("AppRevisions").Records.ToList();
        var unused = before.First(f => f.CheckId == "APP-01" && revisions.Any(r => r.S("ModelName") == f.ObjectKey));
        var oldCi = revisions.First(r => r.S("ModelName") == unused.ObjectKey).S("CI_ID");
        read.Table("AppDependencies").Rows.Add(["99999999", "99999998", oldCi, "99999997", "2"]);
        var (after, _) = SiteHygieneParity.Native(read, "Applications");
        Assert.DoesNotContain(after, f => f.CheckId == "APP-01" && f.ObjectKey == unused.ObjectKey);
    }
    [Fact]
    public async Task TheRealScriptThroughTheStubSiteGivesTheSameFindings()
    {
        using var h = new SiteHygieneHarness();
        var result = await h.ScanAsync("Applications", "Packages");
        var (expected, _) = SiteHygieneParity.Native("Applications", "Packages");

        Assert.Equal(expected.Select(f => f.Key).Order(StringComparer.Ordinal), result.Findings.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.False(result.Truncated);
        Assert.Null(result.Note);
        Assert.Equal("MCM", result.SiteCode);
        Assert.Equal(8, h.Site.Runs.Count);
        Assert.Equal(8, result.QueryCount);
        Assert.Equal(57, result.Checks.Count);
        Assert.Contains(h.Progress, p => p.StartsWith("Reading applications (1 of 8)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedReadMakesItsChecksNotRunAndNeverNoFindings()
    {
        using var h = new SiteHygieneHarness();
        h.Site.Fail("query:SMS_Program");
        var result = await h.ScanAsync("Applications", "Packages");

        var pkg = result.Checks.Single(c => c.CheckId == "PKG-01");
        Assert.Equal(HygieneCheckStates.NotRun, pkg.State);
        Assert.Contains("the read of programs failed", pkg.Reason);
        Assert.DoesNotContain(result.Findings, f => f.CheckId == "PKG-01");
        Assert.Equal(HygieneCheckStates.Ran, result.Checks.Single(c => c.CheckId == "APP-01").State);
        Assert.Contains("Reads that failed: programs", result.Note);
        Assert.Equal(["PKG-01"], result.Checks.Where(c => c.CheckId is "APP-01" or "APP-02" or "APP-03" or "APP-05" or "PKG-01" && c.State == HygieneCheckStates.NotRun).Select(c => c.CheckId));
    }

    [Fact]
    public async Task ACutReadMakesItsChecksNotRunAndTheResultTruncated()
    {
        using var h = new SiteHygieneHarness();
        h.With(maxRows: 5);
        var result = await h.ScanAsync("Applications");

        Assert.True(result.Truncated);
        Assert.All(result.Checks.Where(c => c.CheckId is "APP-01" or "APP-02" or "APP-03" or "APP-05"), c =>
        {
            Assert.Equal(HygieneCheckStates.NotRun, c.State);
            Assert.Contains("were cut at 5 of", c.Reason);
            Assert.Equal(0, c.Findings);
        });
        Assert.Empty(result.Findings);
        Assert.Contains("Reads cut at their cap", result.Note);
    }

    [Fact]
    public async Task CancelBetweenReadsStopsTheScan()
    {
        using var h = new SiteHygieneHarness();
        using var cts = new CancellationTokenSource();
        h.Site.BeforeRun = (_, site) => { if (site.Runs.Count == 2) cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Service.ScanAsync(new HygieneScanOptions(new HashSet<string> { "Applications" }, TimeSpan.Zero, false), new Progress<string>(), cts.Token));
        Assert.Equal(2, h.Site.Runs.Count);
    }

    [Fact]
    public async Task ThePauseRunsBetweenReadsAndReachesTheScript()
    {
        using var h = new SiteHygieneHarness();
        await h.Service.ScanAsync(new HygieneScanOptions(new HashSet<string> { "Packages" }, TimeSpan.FromMilliseconds(250), false), new Progress<string>(), CancellationToken.None);
        Assert.Equal(3, h.Delays.Count);
        Assert.All(h.Delays, d => Assert.Equal(TimeSpan.FromMilliseconds(250), d));
        Assert.All(h.Site.Runs, r => Assert.Equal(250, Convert.ToInt32(r["PacingMs"])));
    }

    [Fact]
    public async Task AnUnknownAreaIsRefusedBeforeAnySiteCall()
    {
        using var h = new SiteHygieneHarness();
        var ex = await Assert.ThrowsAsync<HygieneServiceException>(() => h.ScanAsync("Applications", "Nope"));
        Assert.Contains("Nope", ex.Message);
        Assert.Empty(h.Site.Runs);
    }
}
