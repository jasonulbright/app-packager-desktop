using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The real DeploymentService over the stub site, a real audit log in a temporary folder and fixed clocks.</summary>
internal sealed class DeploymentHarness : IDisposable
{
    /// <summary>12:00 UTC; the clients' local time is UTC-4, as on the lab site.</summary>
    public static readonly DateTime UtcNow = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    public static readonly TimeZoneInfo Local = TimeZoneInfo.CreateCustomTimeZone("Lab", TimeSpan.FromHours(-4), "Lab", "Lab");
    public static readonly DateTime LocalNow = new(2026, 10, 20, 8, 0, 0);

    public readonly string Folder = Path.Combine(Path.GetTempPath(), "apkg-dhsvc-" + Guid.NewGuid().ToString("N"));

    /// <param name="maxRows">The row limit of the object and deployment lists.</param>
    /// <param name="failAppend">Picks the appends of the service, counted from 0, that throw.</param>
    public DeploymentHarness(JsonObject? state = null, IDeploymentSigningCheck? signing = null, Func<string?>? auditPath = null, int maxRows = 1000,
        Func<int, Exception?>? failAppend = null)
    {
        Site = new StubDeploymentSite(state ?? StubDeploymentSite.Lab());
        Audit = new DeploymentAuditLog(auditPath ?? (() => AuditPath), clock: () => LocalNow, user: () => @"CONTOSO\Admin");
        Collections = new FakeCollections();
        IDeploymentAuditLog audit = failAppend is null ? Audit : new AppendFailingAudit(Audit, failAppend);
        Service = new DeploymentService(Site, Collections, audit, signing, utcNow: () => UtcNow, local: Local) { MaxListRows = maxRows };
    }

    public StubDeploymentSite Site { get; }
    public DeploymentAuditLog Audit { get; }
    public FakeCollections Collections { get; }
    public DeploymentService Service { get; }
    public string AuditPath => Path.Combine(Folder, "audit", "deployment-audit.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }

    public static DeployableObject App => new(DeploymentObjectType.Application, "16779607", "7-Zip (x64 edition) - 26.03", "26.03", "", false, "16778866");
    public static DeployableObject AppWithoutContent => new(DeploymentObjectType.Application, "16779604", "7-Zip 26.03 (x64 edition)", "26.03", "", false, "16778870");
    public static DeployableObject Package => new(DeploymentObjectType.Package, "MCM0001B", "CMCO-CANARY-Package", "1", "CMCO-CANARY-Run", false);
    public static DeployableObject TaskSequence => new(DeploymentObjectType.TaskSequence, "MCM00030", "DH Test TS", "", "", false);
    public static DeployableObject UpdateGroup => new(DeploymentObjectType.SoftwareUpdateGroup, "16778893", "202609-W11-25H2", "", "", false);
    public static DeploymentCollection Collection => new("MCM00098", "DH-Test-1", "", 0);

    public static DeployableObject Of(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => App,
        DeploymentObjectType.Package => Package,
        DeploymentObjectType.TaskSequence => TaskSequence,
        _ => UpdateGroup
    };

    /// <summary>Available tomorrow at 09:00, a deadline two days later at 17:30 for Required, and options other than the defaults.</summary>
    public static DeploymentOptions Options(DeploymentObjectType type, string purpose = "Available", bool utc = false)
    {
        var available = new DateTime(2026, 10, 21, 9, 0, 0);
        var deadline = purpose == "Required" ? new DateTime(2026, 10, 23, 17, 30, 0) : (DateTime?)null;
        return type switch
        {
            DeploymentObjectType.Package => new DeploymentOptions(purpose, available, deadline, utc, "DisplayAll", true, false, true,
                "RunProgramFromDistributionPoint", "RerunIfFailedPreviousAttempt", false, true, false, false, false, "DownloadContentFromDistributionPointAndLocally"),
            DeploymentObjectType.TaskSequence => new DeploymentOptions(purpose, available, deadline, utc, "DisplayAll", false, true, true,
                "DownloadAllContentLocallyBeforeStartingTaskSequence", "NeverRerunDeployedProgram", false, true, false, false, false, TaskSequenceAvailability: "Clients"),
            DeploymentObjectType.SoftwareUpdateGroup => new DeploymentOptions(purpose, available, deadline, utc, "DisplaySoftwareCenterOnly", true, true, true,
                "", "", false, false, false, false, true, AllowMicrosoftUpdate: true),
            _ => new DeploymentOptions(purpose, available, deadline, utc, purpose == "Required" ? "HideAll" : "DisplaySoftwareCenterOnly", true, true, true,
                "", "", false, false, true, true, false)
        };
    }

    public static DeploymentChange Create(DeployableObject obj, DeploymentOptions options, DeploymentCollection? collection = null, bool required = false, bool backdated = false,
        IReadOnlyList<string>? groups = null, bool createNow = false) =>
        new(DeploymentChangeKind.Create, obj, collection ?? Collection, options, groups ?? [], createNow, required, backdated, null, [], [], null);

    public async Task<DeploymentChange> PrepareAsync(DeploymentChange change) => await Service.PrepareAsync(change, CancellationToken.None);

    public async Task<IReadOnlyList<DeploymentOutcome>> ApplyAsync(params DeploymentChange[] changes) =>
        await Service.ApplyAsync(changes, new Progress<DeploymentOutcome>(), CancellationToken.None);

    public async Task<DeploymentOutcome> PrepareAndApplyAsync(DeploymentChange change) => (await ApplyAsync(await PrepareAsync(change))).Single();

    public int CreateCalls => Site.Log.Count(l => l.StartsWith("New-CM", StringComparison.Ordinal));

    public JsonObject Stored(string list) => Site.List(list).Single()!.AsObject();

    internal sealed class FakeCollections : ICollectionService
    {
        public List<CollectionRow> Rows { get; } =
        [
            Row("MCM00098", "DH-Test-1", false, 0), Row("SMS00001", "All Systems", true, 8), Row("MCM00014", "HomeLab - Test Deployments", false, 1), Row("SMSDM003", "All Desktop and Server Clients", false, 4)
        ];

        public static CollectionRow Row(string id, string name, bool builtIn, int members) =>
            new(id, name, @"Lab\Pilot", "SMS00001", "All Systems", members, builtIn, "Manual", "", 0, 0, 0, 0);

        public Task<IReadOnlyList<CollectionRow>> ListCollectionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CollectionRow>>(Rows);
        public Task<CollectionRow?> GetCollectionAsync(string collectionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<CollectionDetail> GetDetailAsync(string collectionId, int memberPage, CancellationToken ct) => throw new NotSupportedException();
        public Task<WqlResult> ValidateWqlAsync(string expression, CancellationToken ct) => throw new NotSupportedException();
        public Task<WqlResult> PreviewWqlAsync(string expression, int maxRows, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SavedQuery>> ListSavedQueriesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<CollectionChange> PrepareAsync(CollectionChange request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CollectionOutcome>> ApplyAsync(IReadOnlyList<CollectionChange> changes, IProgress<CollectionOutcome> progress, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>The real log with the appends a function picks throwing.</summary>
    internal sealed class AppendFailingAudit(DeploymentAuditLog inner, Func<int, Exception?> fail) : IDeploymentAuditLog
    {
        private int _appends;
        private Exception? Next() => fail(Interlocked.Increment(ref _appends) - 1);
        public AuditProbe Probe() => inner.Probe();
        public AuditWrite Append(DeploymentAuditRecord r) => Next() is { } ex ? throw ex : inner.Append(r);
        public IReadOnlyList<DeploymentAuditRecord> Read(int max) => inner.Read(max);
        public string Path => inner.Path;
        public IDeploymentAuditReservation Reserve() => new Reservation(this, inner.Reserve());

        private sealed class Reservation(AppendFailingAudit owner, IDeploymentAuditReservation held) : IDeploymentAuditReservation
        {
            public AuditProbe Probe => held.Probe;
            public AuditWrite Append(DeploymentAuditRecord r) => owner.Next() is { } ex ? throw ex : held.Append(r);
            public void Dispose() => held.Dispose();
        }
    }

    internal sealed class FakeSigning(IReadOnlySet<string> packaged, string? refusal = null, bool fail = false) : IDeploymentSigningCheck
    {
        public IReadOnlySet<string> PackagedApplications() => fail ? throw new DeploymentServiceException("The inventory could not be read.") : packaged;
        public string? RequiredDeploymentRefusal(string applicationName) => fail ? throw new DeploymentServiceException("The inventory could not be read.") : refusal;
    }
}
