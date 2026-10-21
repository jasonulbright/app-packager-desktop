using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>
/// The real RingService over the real DeploymentService and the stub site, with real plan, run
/// and audit files in a temporary folder and the fixed clocks of <see cref="DeploymentHarness"/>.
/// The site has three empty device collections DH-Test-1 to DH-Test-3 (MCM00098, MCM0009A, MCM0009B).
/// </summary>
internal sealed class RingHarness : IDisposable
{
    public const string C1 = "MCM00098";
    public const string C2 = "MCM0009A";
    public const string C3 = "MCM0009B";

    private readonly DeploymentHarness _deployments;
    public DateTime UtcNow = DeploymentHarness.UtcNow;

    public RingHarness(JsonObject? state = null)
    {
        var lab = state ?? StubDeploymentSite.Lab();
        var collections = lab["Collections"]!.AsArray();
        collections.Add(JsonNode.Parse("""{ "CollectionID": "MCM0009A", "Name": "DH-Test-2", "CollectionType": 2, "MemberCount": 0 }"""));
        collections.Add(JsonNode.Parse("""{ "CollectionID": "MCM0009B", "Name": "DH-Test-3", "CollectionType": 2, "MemberCount": 1 }"""));
        _deployments = new DeploymentHarness(lab);
        _deployments.Collections.Rows.Add(DeploymentHarness.FakeCollections.Row(C2, "DH-Test-2", false, 0));
        _deployments.Collections.Rows.Add(DeploymentHarness.FakeCollections.Row(C3, "DH-Test-3", false, 1));
        Plans = new RingPlanStore(() => Path.Combine(_deployments.Folder, "Rings"));
        Runs = new RingRunStore(() => Path.Combine(_deployments.Folder, "Rings", "runs"), utcNow: () => UtcNow, local: DeploymentHarness.Local, user: () => Account);
        Service = new RingService(_deployments.Service, _deployments.Service, _deployments.Audit, Plans, Runs, utcNow: () => UtcNow, local: DeploymentHarness.Local);
    }

    /// <summary>The account of this process, which the stub site records as the creator.</summary>
    public static string Account => System.Security.Principal.WindowsIdentity.GetCurrent().Name;

    public StubDeploymentSite Site => _deployments.Site;
    public DeploymentAuditLog Audit => _deployments.Audit;
    public DeploymentService Deployments => _deployments.Service;
    public RingPlanStore Plans { get; }
    public RingRunStore Runs { get; }
    public RingService Service { get; }
    public string RunsFolder => Path.Combine(_deployments.Folder, "Rings", "runs");

    public void Dispose() => _deployments.Dispose();

    /// <summary>Available tomorrow at 09:00 local; Required rings with deadlines one, three and five days later.</summary>
    public static readonly DateTime Start = new(2026, 10, 21, 9, 0, 0);

    public static RingPlan ThreeRings(bool hold = false, string purpose = "Available", string utc = "LocalTime", string dpGroup = "") =>
        new("Lab-Rings", "", hold, utc,
        [
            Ring("QA", C1, purpose, 0, 1, threshold: 90, dpGroup: dpGroup),
            Ring("Pilot", C2, purpose, 2, 3),
            Ring("Prod", C3, purpose, 4, 5)
        ], false);

    public static RingDefinition Ring(string name, string collection, string purpose, int available, int deadline, int? threshold = null, string dpGroup = "") =>
        new(name, collection, "", purpose, available, purpose == "Required" ? deadline : null, purpose == "Required" ? "HideAll" : "DisplaySoftwareCenterOnly",
            false, false, false, dpGroup, threshold, null, null, null);

    public async Task<IReadOnlyList<RingExpansionRow>> ExpandAsync(RingPlan plan, DeployableObject obj) => await Service.ExpandAsync(plan, obj, Start, CancellationToken.None);

    public static RingChange CreateRun(RingPlan plan, DeployableObject obj, IReadOnlyList<RingExpansionRow> rows, bool required = false, bool createNow = false) =>
        new(RingChangeKind.CreateRun, plan, obj, rows, Start, null, null, required, false, createNow, [], [], [], null);

    public static RingChange Request(RingChangeKind kind, string runId, int? ring = null, bool required = false) =>
        new(kind, null, null, [], null, runId, ring, required, false, false, [], [], [], null);

    public async Task<RingChange> PrepareAsync(RingChange change) => await Service.PrepareAsync(change, CancellationToken.None);

    public List<RingOutcome> Progress { get; } = [];

    public async Task<RingOutcome> ApplyAsync(RingChange change, CancellationToken ct = default) =>
        (await Service.ApplyAsync([change], new SyncProgress(Progress), ct)).Single();

    public async Task<RingOutcome> CreateRunAsync(RingPlan plan, DeployableObject obj, bool required = false) =>
        await ApplyAsync(await PrepareAsync(CreateRun(plan, obj, await ExpandAsync(plan, obj), required)));

    public RingRun Run(string runId) => Runs.Find(runId, includeClosed: true) ?? throw new InvalidOperationException("The run is not there.");

    public int CreateCalls => Site.Log.Count(l => l.StartsWith("New-CM", StringComparison.Ordinal));

    public IReadOnlyList<DeploymentAuditRecord> Records => Audit.Read(1000);

    /// <summary>A summary row as CM01 holds it for a deployment.</summary>
    public void Summarize(string deploymentId, int targeted, int success, int errors = 0, int inProgress = 0, int unknown = 0)
    {
        var assignment = Site.List("AppAssignments").Concat(Site.List("UgAssignments")).OfType<JsonObject>()
            .FirstOrDefault(a => (string?)a["AssignmentUniqueID"] == deploymentId)?["AssignmentID"]?.GetValue<long>();
        Site.List("Summaries").Add(new JsonObject
        {
            ["DeploymentID"] = deploymentId, ["AssignmentID"] = assignment ?? 0, ["CollectionID"] = C1, ["NumberTargeted"] = targeted, ["NumberSuccess"] = success,
            ["NumberErrors"] = errors, ["NumberInProgress"] = inProgress, ["NumberUnknown"] = unknown, ["NumberOther"] = 0, ["SummarizationTime"] = "2026-10-20T08:30:00"
        });
    }

    private sealed class SyncProgress(List<RingOutcome> sink) : IProgress<RingOutcome>
    {
        public void Report(RingOutcome value)
        {
            lock (sink) sink.Add(value);
        }
    }
}
