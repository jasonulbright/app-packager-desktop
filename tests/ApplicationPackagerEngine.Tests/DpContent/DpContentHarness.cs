using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The real DP content service over the stub site, with an audit log in a temp folder and polls that do not wait.</summary>
internal sealed class DpContentHarness : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "apkg-dpc-" + Guid.NewGuid().ToString("N"));
    public StubDpContentSite Site { get; }
    public DpContentAuditLog Audit { get; }
    public DpContentService Service { get; }
    public List<DpContentOutcome> Reports { get; } = [];
    public int Delays { get; private set; }

    public DpContentHarness(JsonObject? state = null, TimeSpan? window = null, string? auditPath = null)
    {
        Directory.CreateDirectory(Folder);
        Site = new StubDpContentSite(state ?? StubDpContentSite.Lab());
        Audit = new DpContentAuditLog(() => auditPath ?? Path.Combine(Folder, "dp-content-audit.jsonl"));
        var clock = new DateTime(2026, 10, 23, 23, 0, 0, DateTimeKind.Utc);
        Service = new DpContentService(Site, Audit, delay: (t, ct) => { Delays++; clock += t; return Task.CompletedTask; }, utcNow: () => clock)
        {
            ReadBackWindow = window ?? TimeSpan.FromSeconds(60)
        };
    }

    public static PairRow Pair(string id, string nal, int state, string type = DpContentTypes.Package, string name = "") =>
        new(id, name.Length > 0 ? name : id, type, DpContentAggregator.ServerOf(nal), nal, state, DpContentStates.Name(state), "1", null);

    public Task<DpContentChange> PrepareAsync(DpContentChangeKind kind, params PairRow[] pairs) =>
        Service.PrepareAsync(new DpContentChange(kind, pairs, false, false, [], [], null), CancellationToken.None);

    public async Task<DpContentOutcome> ApplyAsync(DpContentChange change, CancellationToken ct = default)
    {
        var progress = new SyncProgress<DpContentOutcome>(Reports.Add);
        var outcomes = await Service.ApplyAsync([change], progress, ct);
        return Assert.Single(outcomes);
    }

    /// <summary>The lines of the audit log, read while a run may hold it open for appends.</summary>
    public IReadOnlyList<string> AuditLines()
    {
        if (!File.Exists(Audit.Path)) return [];
        using var stream = new FileStream(Audit.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
