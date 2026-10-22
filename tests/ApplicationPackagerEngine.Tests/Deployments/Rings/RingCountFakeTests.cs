using System.Collections.Concurrent;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>
/// Plans of 100 and 101 rings through the real RingService, run store and audit log over an
/// in-process deployment service. A 100-ring run through the stub site takes several hundred child
/// PowerShell runs, so the ring flows at this size use this fake; 1, 2, 4 and 13 rings run through
/// the stub site in <see cref="RingCountScriptTests"/>.
/// </summary>
public sealed class RingCountFakeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-ringcount-" + Guid.NewGuid().ToString("N"));
    private DateTime _utc = DeploymentHarness.UtcNow;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private (RingService Service, FakeSite Site, RingRunStore Runs) Make(int collections)
    {
        var audit = new DeploymentAuditLog(() => Path.Combine(_folder, "audit.jsonl"), clock: () => DeploymentHarness.LocalNow, user: () => RingHarness.Account);
        var site = new FakeSite(audit, RingHarness.Waves(collections));
        var runs = new RingRunStore(() => Path.Combine(_folder, "runs"), utcNow: () => _utc, local: DeploymentHarness.Local, user: () => RingHarness.Account);
        return (new RingService(site, site, audit, new RingPlanStore(() => Path.Combine(_folder, "plans")), runs, utcNow: () => _utc, local: DeploymentHarness.Local), site, runs);
    }

    private static readonly DeployableObject Package = DeploymentHarness.Package;

    [Fact]
    public async Task AHundredRingsExpandValidateCreatePromoteCountReconcileAndUndo()
    {
        var (service, site, runs) = Make(100);
        var plan = RingHarness.WavePlan(100, hold: true, purpose: "Required");
        Assert.Empty(RingPlanRules.Validate(plan));
        var rows = await service.ExpandAsync(plan, Package, RingHarness.Start, CancellationToken.None);
        Assert.Equal(100, rows.Count);
        Assert.Equal(1, site.CollectionLists);
        var prepared = await service.PrepareAsync(RingHarness.CreateRun(plan, Package, rows, required: true), CancellationToken.None);
        Assert.False(prepared.Blocked, string.Join(" | ", prepared.Checks.Where(c => c.Result != "Pass").Select(c => c.Message)));
        Assert.Equal(100, site.Prepares);
        var outcome = (await service.ApplyAsync([prepared], new Progress<RingOutcome>(), CancellationToken.None)).Single();
        Assert.True(outcome.Succeeded, outcome.Message);
        var runId = outcome.Run!.RunId;
        for (var index = 2; index <= 100; index++)
        {
            var promote = await service.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, runId, index, required: true), CancellationToken.None);
            Assert.False(promote.Blocked, $"ring {index}: " + string.Join(" | ", promote.Checks.Where(c => c.Result != "Pass").Select(c => c.Message)));
            Assert.True((await service.ApplyAsync([promote], new Progress<RingOutcome>(), CancellationToken.None)).Single().Succeeded, $"ring {index}");
        }
        var run = runs.Find(runId)!;
        Assert.Equal(100, run.Rings.Count(r => r.Status == RingStatuses.Created));
        Assert.Equal(100, site.Deployments.Count);

        site.LiveCalls = 0;
        var live = await service.LiveCountsAsync(run, CancellationToken.None);
        Assert.Equal((100, 1), (live.Count, site.LiveCalls));
        var reconciled = await service.ReconcileAsync(runId, CancellationToken.None);
        Assert.True(reconciled.IsClosed);
        Assert.Equal(2, site.LiveCalls);

        var undo = await service.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, runId), CancellationToken.None);
        Assert.Equal(100, undo.RingChanges.Count);
        Assert.True((await service.ApplyAsync([undo], new Progress<RingOutcome>(), CancellationToken.None)).Single().Succeeded);
        Assert.Empty(site.Deployments);
    }

    [Fact]
    public async Task AHundredRingsCreateAllInOneRunWithOneRunFile()
    {
        var (service, site, runs) = Make(100);
        var plan = RingHarness.WavePlan(100);
        var rows = await service.ExpandAsync(plan, Package, RingHarness.Start, CancellationToken.None);
        var progress = new List<RingOutcome>();
        var outcome = (await service.ApplyAsync([await service.PrepareAsync(RingHarness.CreateRun(plan, Package, rows), CancellationToken.None)],
            new SyncProgress(progress), CancellationToken.None)).Single();
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(100, outcome.RingOutcomes.Count);
        Assert.Equal(100, progress.Count(p => !p.Final));
        Assert.Single(runs.List(false, out _));
        Assert.Equal(100, site.Deployments.Count);
    }

    [Fact]
    public async Task APlanOfOneHundredAndOneRingsIsRefusedWithTheCap()
    {
        var (service, _, _) = Make(101);
        var plan = new RingPlan("Waves-101", "", false, "LocalTime", RingPlanBuilder.FromCollections(RingHarness.Waves(100), "Available", 0, 1, 1)
            .Append(new RingDefinition("Extra", "MCMW0101", "", "Available", 0, null, "DisplayAll", false, false, false, "", null, null, null, null)).ToList(), false);
        var expand = await Assert.ThrowsAsync<DeploymentServiceException>(() => service.ExpandAsync(plan, Package, RingHarness.Start, CancellationToken.None));
        Assert.Contains("The plan has 101 rings. A plan can have at most 100 rings.", expand.Message);
        var rows = RingPlanRules.Expand(plan, RingHarness.Start, DeploymentHarness.Local);
        var prepared = await service.PrepareAsync(RingHarness.CreateRun(plan, Package, rows), CancellationToken.None);
        Assert.True(prepared.Blocked);
        Assert.Contains(prepared.Checks, c => c.Message.Contains("at most 100 rings", StringComparison.Ordinal));
        Assert.Contains("at most 100 rings", Assert.Throws<DeploymentServiceException>(() => service.SavePlan(plan, false)).Message);
    }

    private sealed class SyncProgress(List<RingOutcome> sink) : IProgress<RingOutcome>
    {
        public void Report(RingOutcome value) => sink.Add(value);
    }

    /// <summary>A deployment service that keeps deployments in memory and writes real audit records of its creates.</summary>
    private sealed class FakeSite(DeploymentAuditLog audit, IReadOnlyList<DeploymentCollection> collections) : IDeploymentService, IDeploymentSiteReader
    {
        public ConcurrentDictionary<string, DeploymentRecord> Deployments { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int CollectionLists;
        public int Prepares;
        public int LiveCalls;
        private int _next = 0x100;

        private static PreflightCheck Pass(string name) => new(name, DeploymentCheckResults.Pass, "ok");

        public Task<IReadOnlyList<DeployableObject>> ListObjectsAsync(DeploymentObjectType type, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListProgramsAsync(string packageId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DpGroupInfo>> ListDpGroupsAsync(DeployableObject obj, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(DeployableObject obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeploymentCollection>> ListCollectionsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref CollectionLists);
            return Task.FromResult(collections);
        }

        public Task<DeploymentChange> PrepareAsync(DeploymentChange request, CancellationToken ct)
        {
            Interlocked.Increment(ref Prepares);
            if (request.Kind == DeploymentChangeKind.RemoveDeployment) return Task.FromResult(request with { Checks = [Pass(DeploymentService.CreatedHere)], Impact = ["Removes it."] });
            var members = collections.FirstOrDefault(c => c.CollectionId == request.Collection!.CollectionId)?.MemberCount ?? 0;
            return Task.FromResult(request with
            {
                Checks = [Pass(DeploymentCheckNames.TargetExists), Pass(DeploymentCheckNames.CollectionValid)],
                Impact = ["Creates it."],
                Prepared = new DeploymentPreparedState(request.Object!.Id, "1", [request.Object.Id], members),
                Collection = request.Collection! with { MemberCount = members }
            });
        }

        public Task<IReadOnlyList<DeploymentOutcome>> ApplyAsync(IReadOnlyList<DeploymentChange> changes, IProgress<DeploymentOutcome> progress, CancellationToken ct)
        {
            var outcomes = new List<DeploymentOutcome>();
            foreach (var change in changes)
            {
                if (change.Kind == DeploymentChangeKind.RemoveDeployment)
                {
                    outcomes.Add(new DeploymentOutcome(change, Deployments.TryRemove(change.Existing!.DeploymentId, out _), "Removed.", null, []));
                    continue;
                }
                if (change.NeedsRequiredConfirmation && !change.RequiredConfirmed) { outcomes.Add(new DeploymentOutcome(change, false, DeploymentService.SkippedPrefix + "not confirmed.", null, [])); continue; }
                var id = "MCM3" + Interlocked.Increment(ref _next).ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
                var o = change.Options!;
                var record = new DeploymentRecord(id, change.Object!.Type, change.Object.Id, change.Object.Name, change.Collection!.CollectionId, change.Collection.Name, o.Purpose,
                    o.AvailableTime, o.Deadline, DeploymentHarness.UtcNow, RingHarness.Account, o.UtcBasis, change.Object.Program);
                Deployments[id] = record;
                audit.Append(new DeploymentAuditRecord(default, RingHarness.Account, "Package", change.Object.Name, change.Object.Program, change.Collection.Name, change.Collection.CollectionId,
                    0, o.Purpose, "Install", o.Deadline, id, AuditResults.Success)
                {
                    Event = AuditEvents.Create, ObjectId = change.Object.Id, AvailableDateTime = o.AvailableTime, DeploymentUniqueID = id, ProgramName = change.Object.Program,
                    TimeBasedOn = o.UtcBasis ? "Utc" : "LocalTime", CreatedUtc = DeploymentHarness.UtcNow
                }.WithRing(change.Ring));
                var undo = new DeploymentChange(DeploymentChangeKind.RemoveDeployment, change.Object, change.Collection, null, [], false, false, false, record, [], [], id);
                outcomes.Add(new DeploymentOutcome(change, true, "Created.", undo, []));
            }
            return Task.FromResult<IReadOnlyList<DeploymentOutcome>>(outcomes);
        }

        public Task<DeploymentLookup> LookupAsync(DeploymentObjectType type, string deploymentId, CancellationToken ct) =>
            Task.FromResult(new DeploymentLookup(true, Deployments.GetValueOrDefault(deploymentId)));

        public Task<IReadOnlyList<DeploymentLiveRow>> LiveAsync(DeploymentObjectType type, IReadOnlyList<string> deploymentIds, CancellationToken ct)
        {
            Interlocked.Increment(ref LiveCalls);
            return Task.FromResult<IReadOnlyList<DeploymentLiveRow>>([.. deploymentIds.Select(i => new DeploymentLiveRow(i, Deployments.ContainsKey(i), true, null, 0, 0, 0, 0, 0, null))]);
        }

        public Task<IReadOnlyList<DeploymentCandidate>> CandidatesAsync(DeployableObject obj, string collectionId, DeploymentOptions planned, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeploymentCandidate>>([.. Deployments.Values.Where(d => d.CollectionId == collectionId).Select(d => new DeploymentCandidate(d, d.Created, d.CreatedBy, []))]);
    }
}
