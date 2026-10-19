using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using static ApplicationPackagerEngine.Tests.Compliance.StubComplianceSite;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>Reads and deployment changes through the real embedded scripts on the stub site.</summary>
public sealed class ComplianceDeploymentTests
{
    internal sealed class Recorder<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) { lock (Items) Items.Add(value); }
    }

    internal static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    internal static ComplianceService Service(IConfigMgrSession site, ICatalogBuilderRunner? runner = null, ICatalogService? catalog = null) =>
        new(site, catalog ?? new CatalogService(), runner ?? new CatalogBuilderRunner(Path.Combine(Path.GetTempPath(), "apkg-unused")), () => new ConfigMgrConnection("MCM", "cm01.contoso.com"),
            null, () => Now, () => "t1");

    private static StubComplianceSite Site() => new(NewState(
        items: [Ci("16780001", "Lab CI", 4, RegistrySetting("RegSetting_a", "EnableMulticast"))],
        baselines: [CiBaseline("16780002", "Lab Baseline", "Disables LLMNR. [ccm:llmnr-disable@1.0.0]", "16780001"), CiBaseline("16780003", "Hand made", "No tag here")],
        assignments:
        [
            Deployment(16777270, "16780002", "MCM00080", summary: new { Success = 3, Other = 1, Errors = 1, Unknown = 2, InProgress = 1, Targeted = 8, Time = "2026-10-12T11:59:59Z" }),
            Deployment(16777271, "16780003", "MCM00081", enforcement: true, summary: new { Success = 1, Time = "2026-10-12T12:00:00Z" })
        ]));

    private static async Task<ComplianceOutcome> Apply(ComplianceService service, ComplianceChange change) =>
        (await service.ApplyAsync([change], new Recorder<ComplianceOutcome>(), CancellationToken.None)).Single();

    private static ComplianceChange Change(ComplianceChangeKind kind, BaselineDeploymentRow? deployment = null, bool? remediate = null, string? to = null, bool confirmed = false) =>
        new(kind, null, new Dictionary<string, string>(), [], deployment, remediate, to, null, null, null, [], [], confirmed);

    [Fact]
    public async Task TheListsComeFromFewReadsAndCarryCountsTagsAndCaptures()
    {
        var site = Site();
        var service = Service(site);

        int Queries() { var n = site.Log.Count(l => l == "Query"); site.Log.Clear(); return n; }
        var items = await service.ListConfigurationItemsAsync(CancellationToken.None);
        Assert.Equal(3, Queries());
        var baselines = await service.ListBaselinesAsync(CancellationToken.None);
        Assert.Equal(3, Queries());
        var deployments = await service.ListDeploymentsAsync(null, CancellationToken.None);
        Assert.Equal(5, Queries());
        var one = await service.ListDeploymentsAsync("16780002", CancellationToken.None);

        Assert.Equal(("16780001", "Lab CI", 4, -1, 1), (items.Single().CiId, items.Single().Name, items.Single().Revision, items.Single().SettingCount, items.Single().BaselineCount));
        var tagged = baselines.Single(b => b.BaselineId == "16780002");
        Assert.Equal(("llmnr-disable", "1.0.0", 1, 1), (tagged.CatalogId, tagged.CatalogVersion, tagged.ItemCount, tagged.DeploymentCount));
        Assert.Null(baselines.Single(b => b.BaselineId == "16780003").CatalogId);
        var d = deployments.Single(x => x.DeploymentId == "16777270");
        Assert.Equal(("Lab Baseline", "MCM00080", "CCM Test 1", false, 3, 1, 1, 3), (d.BaselineName, d.CollectionId, d.CollectionName, d.RemediateNoncompliant, d.Compliant, d.NonCompliant, d.Error, d.Unknown));
        Assert.Equal("Every day", d.Schedule);
        Assert.Equal("0001200000100008", d.Settings!.ScheduleToken);
        Assert.Equal(["16777270"], one.Select(x => x.DeploymentId));
        Assert.Equal(1, site.Runs.Count(r => r.Parameters["Operation"] as string == "Items"));
    }

    [Fact]
    public async Task TheStaleFlagFollowsTheSevenDayRuleAtTheBoundary()
    {
        var rows = await Service(Site()).GetComplianceAsync(CancellationToken.None);

        Assert.True(rows.Single(r => r.DeploymentId == "16777270").Stale);
        Assert.False(rows.Single(r => r.DeploymentId == "16777271").Stale);
        Assert.False(ComplianceService.IsStale(null, Now));
        Assert.False(ComplianceService.IsStale(Now.AddDays(-7), Now));
        Assert.True(ComplianceService.IsStale(Now.AddDays(-7).AddSeconds(-1), Now));
    }

    [Fact]
    public async Task DriftShowsCurrentUntaggedAndUnknown()
    {
        var rows = await Service(Site()).GetDriftAsync(CancellationToken.None);

        Assert.Equal("Current", rows.Single(r => r.BaselineId == "16780002").State);
        Assert.Equal("1.0.0", rows.Single(r => r.BaselineId == "16780002").CatalogVersion);
        Assert.Equal("Untagged", rows.Single(r => r.BaselineId == "16780003").State);
    }

    [Fact]
    public async Task RemediationOnNeedsTheSecondConfirmationAndUndoSetsTheOldModeBack()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.SetRemediation, current, true), CancellationToken.None);
        Assert.Contains(plan.Impact, l => l.Contains("1 member now", StringComparison.Ordinal));

        var refused = await Apply(service, plan);
        Assert.StartsWith(ComplianceService.SkippedPrefix, refused.Message);
        Assert.False((bool)site.Assignment(16777270)!["Enforcement"]!);

        var done = await Apply(service, plan with { RemediationConfirmed = true });
        Assert.True(done.Succeeded, done.Message);
        Assert.True((bool)site.Assignment(16777270)!["Enforcement"]!);

        var undone = await Apply(service, done.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.False((bool)site.Assignment(16777270)!["Enforcement"]!);
    }

    [Fact]
    public async Task AChangeOfModeByAnotherAdminSkipsTheWrite()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.SetRemediation, current, true), CancellationToken.None);
        site.BeforeRun = (s, script, values) => { if (script == "WriteCompliance") s.Assignment(16777270)!["Schedule"] = "0001200000100010"; };

        var outcome = await Apply(service, plan with { RemediationConfirmed = true });

        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.DoesNotContain("Set-CMBaselineDeployment", site.Log);
    }

    [Fact]
    public async Task AModeConfigMgrDoesNotKeepFailsTheOutcome()
    {
        var site = Site();
        site.SetOption("IgnoreEnforcement", true);
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780003", CancellationToken.None)).Single();
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.SetRemediation, current, false), CancellationToken.None);

        var outcome = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not keep", outcome.Message);
    }

    [Fact]
    public async Task RedeployCreatesTheNewDeploymentFirstThenRemovesTheOldOne()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780003", CancellationToken.None)).Single();
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.Redeploy, current), CancellationToken.None);

        Assert.StartsWith(ComplianceService.SkippedPrefix, (await Apply(service, plan)).Message);
        var outcome = await Apply(service, plan with { RemediationConfirmed = true });

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Null(site.Assignment(16777271));
        var made = site.Assignment(16777300)!;
        Assert.Equal(("16780003", "MCM00081", true, "0001200000100008"), ((string)made["BaselineId"]!, (string)made["CollectionID"]!, (bool)made["Enforcement"]!, (string)made["Schedule"]!));
        Assert.True(site.Log.IndexOf("New-CMBaselineDeployment:MCM00081") < site.Log.IndexOf("Remove-CMBaselineDeployment:16777271"));
        Assert.Contains("16777300", outcome.Message);
    }

    [Fact]
    public async Task ARedeployWhoseCreateFailsLeavesTheOldDeployment()
    {
        var site = Site();
        site.Fail("create:16780002");
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();

        var outcome = await Apply(service, await service.PrepareAsync(Change(ComplianceChangeKind.Redeploy, current), CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.Contains("was not changed", outcome.Message);
        Assert.NotNull(site.Assignment(16777270));
        Assert.DoesNotContain("SECRET-DETAIL", outcome.Message);
    }

    [Fact]
    public async Task ARedeployWhoseRemoveFailsKeepsBothAndUndoRemovesTheNewOne()
    {
        var site = Site();
        site.Fail("remove:16777270");
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();

        var outcome = await Apply(service, await service.PrepareAsync(Change(ComplianceChangeKind.Redeploy, current), CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.Contains("Both exist", outcome.Message);
        Assert.Equal(ComplianceChangeKind.DeleteDeployment, outcome.UndoChange!.Kind);
        var undone = await Apply(service, outcome.UndoChange);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.NotNull(site.Assignment(16777270));
        Assert.Null(site.Assignment(16777300));
    }

    [Fact]
    public async Task ReassignMovesTheDeploymentAndUndoMovesItBack()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.Reassign, current, to: "mcm00081"), CancellationToken.None);
        Assert.Equal("MCM00081", plan.ToCollectionId);

        var outcome = await Apply(service, plan);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Null(site.Assignment(16777270));
        Assert.Equal("MCM00081", (string)site.Assignment(16777300)!["CollectionID"]!);
        var undone = await Apply(service, outcome.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal("MCM00080", (string)site.Assignment(16777301)!["CollectionID"]!);
        Assert.Null(site.Assignment(16777300));
    }

    [Theory]
    [InlineData("SMS00001", "built-in")]
    [InlineData("MCM00082", "not a device collection")]
    [InlineData("MCM00080", "another collection")]
    [InlineData("MCM00099", "does not exist")]
    public async Task ReassignRefusesTargetsThatCannotTakeTheDeployment(string to, string reason)
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() => service.PrepareAsync(Change(ComplianceChangeKind.Reassign, current, to: to), CancellationToken.None));

        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public async Task ReassignWhoseCreateFailsLeavesTheOldDeployment()
    {
        var site = Site();
        site.Fail("create:16780002");
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();

        var outcome = await Apply(service, await service.PrepareAsync(Change(ComplianceChangeKind.Reassign, current, to: "MCM00081"), CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.NotNull(site.Assignment(16777270));
        Assert.Null(outcome.UndoChange);
    }

    [Fact]
    public async Task DeleteNamesTheCountsAndUndoCreatesTheDeploymentAgainWithItsSettings()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.DeleteDeployment, current), CancellationToken.None);
        Assert.Contains(plan.Impact, l => l.Contains("3 compliant, 1 non-compliant, 1 error, 3 unknown", StringComparison.Ordinal));

        var outcome = await Apply(service, plan);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Null(site.Assignment(16777270));

        var undone = await Apply(service, outcome.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        var made = site.Assignment(16777300)!;
        Assert.Equal(("16780002", "MCM00080", false, "0001200000100008"), ((string)made["BaselineId"]!, (string)made["CollectionID"]!, (bool)made["Enforcement"]!, (string)made["Schedule"]!));
        Assert.Contains("new ID", outcome.Message);
    }

    [Fact]
    public async Task ADeleteThatConfigMgrReportsAsFailedAfterTheRemoveIsReconciled()
    {
        var site = Site();
        site.Fail("after-remove:16777270");
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();

        var outcome = await Apply(service, await service.PrepareAsync(Change(ComplianceChangeKind.DeleteDeployment, current), CancellationToken.None));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.NotNull(outcome.UndoChange);
        Assert.Null(site.Assignment(16777270));
    }

    [Fact]
    public async Task ADeploymentGoneSinceThePlanIsSkipped()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.DeleteDeployment, current), CancellationToken.None);
        site.Assignments.RemoveAt(0);

        var outcome = await Apply(service, plan);

        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.Contains("does not exist", outcome.Message);
    }

    [Fact]
    public async Task CancelBeforeAChangeSkipsTheRest()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.ListDeploymentsAsync("16780002", CancellationToken.None)).Single();
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.DeleteDeployment, current), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcomes = await service.ApplyAsync([plan, plan], new Recorder<ComplianceOutcome>(), cts.Token);

        Assert.All(outcomes, o => Assert.StartsWith(ComplianceService.SkippedPrefix, o.Message));
        Assert.NotNull(site.Assignment(16777270));
    }
}
