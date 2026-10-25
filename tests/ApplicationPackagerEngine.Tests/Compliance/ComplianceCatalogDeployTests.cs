using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.Compliance.StubComplianceSite;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>
/// Catalog deploys: the real snapshot, tag and remove scripts on the stub site, with a builder
/// stand-in that makes on the stub site what the builder makes on a real site.
/// </summary>
public sealed class ComplianceCatalogDeployTests
{
    /// <summary>Makes the entry's objects on the stub site as the builder does, step by step.</summary>
    internal sealed class SiteBuilder(StubComplianceSite site) : ICatalogBuilderRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }
        public int StopAfterItems { get; init; } = int.MaxValue;
        public int? ExitCode { get; init; } = 0;
        public bool Cancel { get; init; }
        public bool Refuse { get; init; }
        public int Runs { get; private set; }

        public Task<CatalogBuilderRun> RunAsync(CatalogEntry entry, byte[] builder, IReadOnlyList<string> arguments, Action<string> line, TimeSpan timeout, CancellationToken ct)
        {
            Runs++;
            if (Refuse) throw new CatalogBuilderRefusedException(CatalogBuilderRunner.HashMessage);
            Arguments = arguments;
            var detectOnly = arguments.Contains("-DetectOnly");
            var skip = arguments.Contains("-SkipDeploy");
            var collections = entry.CollectionParameters.Select(p => arguments.ToList().IndexOf("-" + p)).Where(i => i >= 0).Select(i => arguments[i + 1]).ToList();
            var next = (long)site.State["Next"]!;
            var nextAssignment = (long)site.State["NextAssignment"]!;
            for (var i = 0; i < entry.ProducesConfigurationItems.Count; i++)
            {
                if (i >= StopAfterItems) break;
                line("Creating Configuration Item: " + entry.ProducesConfigurationItems[i]);
                var ci = (next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                site.Items.Add(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Ci(ci, entry.ProducesConfigurationItems[i], 2, RegistrySetting("RegSetting_" + ci, "S", remediate: !detectOnly)))));
                if (i >= StopAfterItems - 1 && StopAfterItems < int.MaxValue) break;
                line("Creating Configuration Baseline: " + entry.ProducesBaselines[i]);
                var cb = (next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                site.Baselines.Add(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(CiBaseline(cb, entry.ProducesBaselines[i], "Builder text.", ci))));
                if (!skip && i < collections.Count)
                {
                    line("Deploying baseline to: " + collections[i]);
                    var id = site.Collections.Select(c => c!.AsObject()).First(c => (string)c["Name"]! == collections[i])["CollectionID"]!.ToString();
                    site.Assignments.Add(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Deployment(nextAssignment++, cb, id, enforcement: !detectOnly, schedule: "0001200000100008"))));
                }
            }
            site.State["Next"] = next;
            site.State["NextAssignment"] = nextAssignment;
            return Task.FromResult(Cancel ? new CatalogBuilderRun(null, false, true, []) : new CatalogBuilderRun(ExitCode, false, false, []));
        }
    }

    internal static StubComplianceSite Site(params object[] collections) => new(NewState(
        items: [Ci("16780001", "Existing CI", 3)],
        baselines: [CiBaseline("16780002", "Existing Baseline", "", "16780001")],
        assignments: [Deployment(16777270, "16780002", "MCM00080")],
        collections: collections.Length > 0 ? collections : null));

    private static ComplianceChange Deploy(string id, Dictionary<string, string> values, bool confirmed = false) =>
        new(ComplianceChangeKind.CatalogDeploy, id, values, [], null, null, null, null, null, null, [], [], confirmed);

    private static async Task<(ComplianceOutcome Outcome, ComplianceDeploymentTests.Recorder<ComplianceOutcome> Progress)> Apply(ComplianceService service, ComplianceChange change)
    {
        var progress = new ComplianceDeploymentTests.Recorder<ComplianceOutcome>();
        var outcomes = await service.ApplyAsync([change], progress, CancellationToken.None);
        return (outcomes.Single(), progress);
    }

    [Fact]
    public async Task ADetectOnlyDeployMakesTheObjectsTagsEachBaselineOnceAndReportsEachStep()
    {
        var site = Site();
        var builder = new SiteBuilder(site);
        var service = ComplianceDeploymentTests.Service(site, builder);
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None);

        Assert.Equal(["MCM00080"], plan.TargetCollectionIds);
        Assert.Contains(plan.Impact, l => l.Contains("CCM Test 1 (MCM00080) (1 member now)", StringComparison.Ordinal));
        Assert.Contains(plan.Impact, l => l.Contains("detect only", StringComparison.Ordinal));
        Assert.Contains(plan.Impact, l => l.Contains("[ccm:llmnr-disable@1.0.0]", StringComparison.Ordinal));

        var (outcome, progress) = await Apply(service, plan);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(["-SiteCode", "MCM", "-SiteServer", "cm01.contoso.com", "-CollectionName", "CCM Test 1", "-DetectOnly"], builder.Arguments);
        var baseline = site.Baselines.Select(b => b!.AsObject()).Single(b => (string)b["Name"]! == "Hardening LLMNR Disable");
        Assert.Equal("Builder text. [ccm:llmnr-disable@1.0.0]", (string)baseline["Description"]!);
        Assert.All(progress.Items.Where(p => p.InProgress), p => Assert.True(p.Succeeded));
        var steps = progress.Items.Where(p => p.InProgress).Select(p => p.Message).ToList();
        Assert.Equal(steps, outcome.Steps);
        Assert.Contains("Creating configuration item Hardening: LLMNR Disable.", steps);
        Assert.Contains("Creating baseline Hardening LLMNR Disable.", steps);
        Assert.Contains("Deploying to collection CCM Test 1.", steps);
        Assert.Contains(steps, s => s.StartsWith("Wrote the drift tag", StringComparison.Ordinal));
        Assert.Equal(3, outcome.UndoChange!.CreatedIds.Count);
    }

    [Fact]
    public async Task UndoRemovesOnlyWhatTheRunMadeInTheRightOrder()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None);
        var (outcome, _) = await Apply(service, plan);

        var (undone, _) = await Apply(service, await service.PrepareAsync(outcome.UndoChange!, CancellationToken.None));

        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal(["Existing CI"], site.Items.Select(i => (string)i!["Name"]!));
        Assert.Equal(["Existing Baseline"], site.Baselines.Select(b => (string)b!["Name"]!));
        Assert.Equal([16777270L], site.Assignments.Select(a => (long)a!["AssignmentID"]!));
        var order = site.Log.Where(l => l.StartsWith("Remove-CM", StringComparison.Ordinal)).Select(l => l.Split(':')[0]).Distinct().ToList();
        Assert.Equal(["Remove-CMBaselineDeployment", "Remove-CMBaseline", "Remove-CMConfigurationItem"], order);
    }

    [Fact]
    public async Task ASecondRunIsRefusedBecauseTheBuilderWouldChangeTheExistingObjects()
    {
        var site = Site();
        var builder = new SiteBuilder(site);
        var service = ComplianceDeploymentTests.Service(site, builder);
        await Apply(service, await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None));

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() =>
            service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None));

        Assert.Contains("second run", error.Message);
        Assert.Equal(1, builder.Runs);
    }

    [Fact]
    public async Task ANameTakenBetweenThePlanAndTheRunStopsTheRun()
    {
        var site = Site();
        var builder = new SiteBuilder(site);
        var service = ComplianceDeploymentTests.Service(site, builder);
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None);
        site.Baselines.Add(JsonNode.Parse("""{"Id":"16780050","Name":"Hardening LLMNR Disable","Description":"","Version":1,"Items":[]}"""));

        var (outcome, _) = await Apply(service, plan);

        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.Equal(0, builder.Runs);
    }

    [Fact]
    public async Task RemediationNeedsItsOwnConfirmationAndThePlanNamesTheRegistryPaths()
    {
        var site = Site();
        var builder = new SiteBuilder(site);
        var service = ComplianceDeploymentTests.Service(site, builder);
        var plan = await service.PrepareAsync(Deploy("tls-legacy-disable", new() { ["CollectionName"] = "CCM Test 2", ["DetectOnly"] = "False" }), CancellationToken.None);

        Assert.Contains(plan.Impact, l => l.Contains("REMEDIATE", StringComparison.Ordinal));
        Assert.Contains(plan.Impact, l => l.Contains(@"HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.0\Server", StringComparison.Ordinal));
        Assert.Contains(plan.Impact, l => l.Contains("restart", StringComparison.Ordinal));
        Assert.Contains(plan.Impact, l => l.Contains("3 members now", StringComparison.Ordinal));

        var (refused, _) = await Apply(service, plan);
        Assert.StartsWith(ComplianceService.SkippedPrefix, refused.Message);
        Assert.Equal(0, builder.Runs);

        var (done, _) = await Apply(service, plan with { RemediationConfirmed = true });
        Assert.True(done.Succeeded, done.Message);
        Assert.DoesNotContain("-DetectOnly", builder.Arguments!);
    }

    [Fact]
    public async Task SpecExecDeploysTwoPairsToTwoCollections()
    {
        var site = Site(
            new { CollectionID = "MCM00080", Name = "Devices: Hyperthreading Enabled", CollectionType = 2, Members = 4 },
            new { CollectionID = "MCM00081", Name = "Devices: Hyperthreading Disabled", CollectionType = 2, Members = 2 });
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));
        var plan = await service.PrepareAsync(Deploy("specexec-mitigations", new()), CancellationToken.None);

        Assert.Equal(["MCM00080", "MCM00081"], plan.TargetCollectionIds);
        var (outcome, _) = await Apply(service, plan);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(6, outcome.UndoChange!.CreatedIds.Count);
        Assert.All(site.Baselines.Select(b => b!.AsObject()).Where(b => ((string)b["Name"]!).StartsWith("CPU", StringComparison.Ordinal)),
            b => Assert.EndsWith("[ccm:specexec-mitigations@1.0.0]", (string)b["Description"]!));
    }

    [Fact]
    public async Task AMissingDependencyBlocksThePlanWithAPlainMessage()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() => service.PrepareAsync(Deploy("specexec-mitigations", new()), CancellationToken.None));

        Assert.Contains("Devices: Hyperthreading Enabled", error.Message);
        Assert.Contains("does not exist", error.Message);
    }

    [Fact]
    public async Task ADependencyIsReplacedByTheCollectionTheOperatorSelects()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));

        var plan = await service.PrepareAsync(Deploy("specexec-mitigations", new() { ["HtEnabledCollectionName"] = "CCM Test 1", ["HtDisabledCollectionName"] = "CCM Test 2" }), CancellationToken.None);

        Assert.Equal(["MCM00080", "MCM00081"], plan.TargetCollectionIds);
    }

    [Theory]
    [InlineData("All Systems", "built-in")]
    [InlineData("Users A", "not a device collection")]
    [InlineData("Nope", "does not exist")]
    [InlineData("CCM*", "wildcards")]
    [InlineData("-CollectionName", "dash")]
    public async Task TargetsThatCannotTakeTheBaselineAreRefused(string collection, string reason)
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() =>
            service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = collection }), CancellationToken.None));

        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public async Task ASiteOtherThanTheConnectionIsRefused()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() =>
            service.PrepareAsync(Deploy("llmnr-disable", new() { ["SiteServer"] = "other.contoso.com" }), CancellationToken.None));

        Assert.Contains("site of the connection", error.Message);
    }

    [Fact]
    public async Task ABuilderThatStopsPartWayIsReconciledByNameAndItsUndoRemovesWhatItMade()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site) { StopAfterItems = 1, ExitCode = 1 });
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None);

        var (outcome, _) = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.Contains("1 configuration item, 0 baselines and 0 deployments", outcome.Message);
        Assert.Equal(["ConfigurationItem:16790000"], outcome.UndoChange!.CreatedIds);
        var (undone, _) = await Apply(service, outcome.UndoChange);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal(["Existing CI"], site.Items.Select(i => (string)i!["Name"]!));
    }

    [Fact]
    public async Task ACancelledBuilderReportsTheRealState()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site) { StopAfterItems = 1, Cancel = true });
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None);

        var (outcome, progress) = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.Contains("was cancelled after it made 1 configuration item", outcome.Message);
        Assert.Contains(progress.Items, p => p.Message.Contains("process tree was ended", StringComparison.Ordinal));
        Assert.NotNull(outcome.UndoChange);
    }

    [Fact]
    public async Task ARefusedBuilderChangesNothing()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site) { Refuse = true });
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None);

        var (outcome, _) = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.Contains("Nothing was changed", outcome.Message);
        Assert.Null(outcome.UndoChange);
    }

    [Fact]
    public async Task AFailedTagLeavesTheObjectsWithTheirUndo()
    {
        var site = Site();
        site.Fail("tag:16790001");
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None);

        var (outcome, _) = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.Contains("drift tag was not written", outcome.Message);
        Assert.Equal(3, outcome.UndoChange!.CreatedIds.Count);
    }

    private static void AssertKeptForForeignDeployment(StubComplianceSite site, ComplianceOutcome undone, long foreign)
    {
        Assert.False(undone.Succeeded);
        Assert.Contains("kept because it is used by something this run did not create", undone.Message);
        Assert.Contains("Hardening LLMNR Disable (16790001)", undone.Message);
        Assert.Contains("Hardening: LLMNR Disable (16790000)", undone.Message);
        Assert.Contains($"deployment {foreign} to collection MCM00081", undone.Message);
        Assert.NotNull(site.Assignment(foreign));
        Assert.Null(site.Assignment(16777300));
        Assert.NotNull(site.Baseline("16790001"));
        Assert.NotNull(site.Item("16790000"));
        Assert.DoesNotContain("Remove-CMBaseline:16790001", site.Log);
        Assert.DoesNotContain("Remove-CMConfigurationItem:16790000", site.Log);
    }

    [Fact]
    public async Task UndoKeepsTheBaselineAndItsItemWhenAnotherSessionDeployedTheBaselineAfterTheRun()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));
        var (outcome, _) = await Apply(service, await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None));
        site.Assignments.Add(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Deployment(16777400, "16790001", "MCM00081"))));

        var (undone, _) = await Apply(service, await service.PrepareAsync(outcome.UndoChange!, CancellationToken.None));

        AssertKeptForForeignDeployment(site, undone, 16777400);
    }

    [Fact]
    public async Task ASecondUndoCountsOnlyTheObjectsThatExistNow()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));
        var (outcome, _) = await Apply(service, await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None));
        site.Assignments.Add(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Deployment(16777400, "16790001", "MCM00081"))));
        var first = await service.PrepareAsync(outcome.UndoChange!, CancellationToken.None);
        Assert.Contains(first.Impact, l => l.StartsWith("Removes 1 deployment, 1 baseline and 1 configuration item ", StringComparison.Ordinal));
        var (kept, _) = await Apply(service, first);
        Assert.False(kept.Succeeded);
        site.Assignments.Remove(site.Assignments.First(a => (long)a!["AssignmentID"]! == 16777400));

        var again = await service.PrepareAsync(outcome.UndoChange!, CancellationToken.None);

        Assert.Contains(again.Impact, l => l.StartsWith("Removes 0 deployments, 1 baseline and 1 configuration item ", StringComparison.Ordinal));
        Assert.DoesNotContain(again.Impact, l => l.Contains("1 deployment", StringComparison.Ordinal));
        var (undone, _) = await Apply(service, again);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Contains("0 deployments, 1 baseline, 1 configuration item", undone.Message);
        Assert.Null(site.Baseline("16790001"));
        Assert.Null(site.Item("16790000"));
    }

    [Fact]
    public async Task UndoKeepsTheBaselineAndItsItemWhenAnotherSessionDeploysTheBaselineDuringTheUndo()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));
        var (outcome, _) = await Apply(service, await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None));
        site.SetOption("DeployOnRemove", "16777300|16790001|MCM00081|16777401");

        var (undone, _) = await Apply(service, await service.PrepareAsync(outcome.UndoChange!, CancellationToken.None));

        AssertKeptForForeignDeployment(site, undone, 16777401);
    }

    [Fact]
    public async Task UndoRemovesTheBaselineButKeepsTheItemThatAnotherBaselineHoldsNow()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));
        var (outcome, _) = await Apply(service, await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None));
        site.Baseline("16780002")!["Items"] = new JsonArray("16780001", "16790000");

        var (undone, _) = await Apply(service, outcome.UndoChange!);

        Assert.False(undone.Succeeded);
        Assert.Contains("kept because it is used by something this run did not create", undone.Message);
        Assert.Contains("Hardening: LLMNR Disable (16790000)", undone.Message);
        Assert.Contains("baseline Existing Baseline (16780002)", undone.Message);
        Assert.Null(site.Baseline("16790001"));
        Assert.Contains(undone.Steps, s => s.StartsWith("Removed baseline", StringComparison.Ordinal));
        Assert.NotNull(site.Item("16790000"));
        Assert.DoesNotContain("Remove-CMConfigurationItem:16790000", site.Log);
    }

    /// <summary>Makes the first CI, starts the first baseline and is cancelled; the baseline is not on the site yet.</summary>
    private sealed class CancelledAtBaseline(StubComplianceSite site) : ICatalogBuilderRunner
    {
        public Task<CatalogBuilderRun> RunAsync(CatalogEntry entry, byte[] builder, IReadOnlyList<string> arguments, Action<string> line, TimeSpan timeout, CancellationToken ct)
        {
            line("Creating Configuration Item: " + entry.ProducesConfigurationItems[0]);
            site.Items.Add(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Ci("16790000", entry.ProducesConfigurationItems[0], 2, RegistrySetting("RegSetting_16790000", "S")))));
            line("Creating Configuration Baseline: " + entry.ProducesBaselines[0]);
            return Task.FromResult(new CatalogBuilderRun(null, false, true, []));
        }
    }

    private static JsonNode LateBaseline(string? createdBy = null)
    {
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(CiBaseline("16790500", "Hardening LLMNR Disable", "Builder text.", "16790000")))!;
        if (createdBy is not null) node["CreatedBy"] = createdBy;
        return node;
    }

    /// <summary>Adds the baseline right before the given Snapshot read from now on (1 is the read before the builder, 3 the first read again after it).</summary>
    private static void AppearAtSnapshot(StubComplianceSite site, int read)
    {
        var reads = 0;
        site.BeforeRun = (s, _, parameters) =>
        {
            if (parameters.GetValueOrDefault("Operation") as string == "Snapshot" && ++reads == read) s.Baselines.Add(LateBaseline());
        };
    }

    private static int SnapshotReads(StubComplianceSite site) => site.Runs.Count(r => r.Parameters.GetValueOrDefault("Operation") as string == "Snapshot");

    [Fact]
    public async Task ABaselineTheProviderFinishesAfterTheCancelIsFoundAndPartOfTheUndo()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new CancelledAtBaseline(site));
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None);
        var before = SnapshotReads(site);
        AppearAtSnapshot(site, 3);

        var (outcome, _) = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.Contains("was cancelled after it made 1 configuration item, 1 baseline and 0 deployments", outcome.Message);
        Assert.DoesNotContain("may still appear", outcome.Message);
        Assert.Equal(["Baseline:16790500", "ConfigurationItem:16790000"], outcome.UndoChange!.CreatedIds);
        Assert.False(ComplianceService.IsReclaim(outcome.UndoChange));
        Assert.Equal(4, SnapshotReads(site) - before);
    }

    [Fact]
    public async Task ABaselineThatIsStillMissingAfterTheWindowIsNamedAndALaterUndoRemovesIt()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new CancelledAtBaseline(site));
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None);
        var before = SnapshotReads(site);

        var (outcome, _) = await Apply(service, plan);

        Assert.Contains("A baseline may still appear: check ConfigMgr.", outcome.Message);
        Assert.Contains("Undo looks for these names again", outcome.Message);
        Assert.Equal(2 + (int)(ComplianceService.ReconcileWindow / ComplianceService.ReconcileInterval), SnapshotReads(site) - before);
        Assert.True(ComplianceService.IsReclaim(outcome.UndoChange!));
        Assert.Equal(["ConfigurationItem:16790000"], outcome.UndoChange!.CreatedIds);

        site.Baselines.Add(LateBaseline());
        var undo = await service.PrepareAsync(outcome.UndoChange, CancellationToken.None);
        Assert.Contains("Baseline:16790500", undo.CreatedIds);
        var (undone, _) = await Apply(service, undo);

        Assert.True(undone.Succeeded, undone.Message);
        Assert.Null(site.Baseline("16790500"));
        Assert.Null(site.Item("16790000"));
    }

    [Fact]
    public async Task ALaterUndoLeavesABaselineWithTheNameThatAnotherAccountMade()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new CancelledAtBaseline(site));
        var (outcome, _) = await Apply(service, await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None));
        site.Baselines.Add(LateBaseline(@"CONTOSO\OtherAdmin"));

        var (undone, _) = await Apply(service, outcome.UndoChange!);

        Assert.NotNull(site.Baseline("16790500"));
        Assert.DoesNotContain("Remove-CMBaseline:16790500", site.Log);
        Assert.NotNull(site.Item("16790000"));
        Assert.Contains("Hardening: LLMNR Disable (16790000) was kept", undone.Message);
    }

    [Fact]
    public async Task AnUndoOfACancelledRunWithNothingFoundYetIsRefusedAndCanBeOfferedAgain()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new CancelledAtBaseline(site));
        var (outcome, _) = await Apply(service, await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), CancellationToken.None));
        site.Items.Clear();
        var undo = outcome.UndoChange! with { CreatedIds = [] };

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() => service.PrepareAsync(undo, CancellationToken.None));
        Assert.Equal(ComplianceService.NothingReclaimedMessage, error.Message);

        site.Baselines.Add(LateBaseline());
        var (undone, _) = await Apply(service, await service.PrepareAsync(undo, CancellationToken.None));
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Null(site.Baseline("16790500"));
    }

    [Fact]
    public async Task CreatingAMissingDependencyIsRefusedWithAPlainMessage()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site, new SiteBuilder(site));

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() =>
            service.PrepareAsync(Deploy("specexec-mitigations", new() { [ComplianceService.CreateDependencyKey] = "Devices: Hyperthreading Enabled" }), CancellationToken.None));

        Assert.Contains("Collections tab", error.Message);
    }
}