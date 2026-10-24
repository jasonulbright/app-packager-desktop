using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The real Site Hygiene service over the stub site with the CM01 rows, an audit log in a temp folder, a fixed clock and delays that do not wait.</summary>
internal sealed class SiteHygieneHarness : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "apkg-sh-" + Guid.NewGuid().ToString("N"));
    public StubHygieneSite Site { get; }
    public HygieneAuditLog Audit { get; }
    public SiteHygieneService Service { get; private set; }
    public List<HygieneOutcome> Reports { get; } = [];
    public List<string> Progress { get; } = [];
    public List<TimeSpan> Delays { get; } = [];
    public DateTime Clock { get; set; } = SiteHygieneFixtures.CaptureUtc;

    public SiteHygieneHarness(JsonObject? state = null, string? auditPath = null, Func<SiteHygieneService, SiteHygieneService>? configure = null)
    {
        Directory.CreateDirectory(Folder);
        Site = new StubHygieneSite(state ?? SiteHygieneFixtures.StubState());
        Audit = new HygieneAuditLog(() => auditPath ?? Path.Combine(Folder, "site-hygiene-audit.jsonl"));
        Service = new SiteHygieneService(Site, Audit, delay: (t, ct) => { Delays.Add(t); Clock += t; return Task.CompletedTask; }, utcNow: () => Clock);
        if (configure is not null) Service = configure(Service);
    }

    public SiteHygieneService With(int? maxRows = null, int? maxObjects = null) =>
        Service = new SiteHygieneService(Site, Audit, delay: (t, ct) => { Delays.Add(t); Clock += t; return Task.CompletedTask; }, utcNow: () => Clock)
        {
            MaxRows = maxRows ?? SiteHygieneService.DefaultMaxRows,
            MaxObjects = maxObjects ?? SiteHygieneService.MaxPerObjectReads
        };

    public Task<HygieneScanResult> ScanAsync(params string[] areas) =>
        Service.ScanAsync(new HygieneScanOptions(areas.ToHashSet(StringComparer.OrdinalIgnoreCase), TimeSpan.Zero, false), new SyncProgress<string>(Progress.Add), CancellationToken.None);

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
