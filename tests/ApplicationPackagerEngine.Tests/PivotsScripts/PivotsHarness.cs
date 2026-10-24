using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The real Pivots and Scripts service over the stub site and the stub administration service, with files in a temp folder and waits that do not wait.</summary>
internal sealed class PivotsHarness : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "apkg-psq-" + Guid.NewGuid().ToString("N"));
    public StubPivotsSite Site { get; }
    public StubAdminService Admin { get; }
    public AdminServiceClient Client { get; }
    public PivotsAuditLog Audit { get; }
    public PivotsSessionStore Sessions { get; }
    public PivotsScriptsService Service { get; private set; }
    public List<PivotsOutcome> Reports { get; } = [];
    private readonly string _user;
    public int Delays { get; private set; }
    public DateTime Clock { get; set; } = new(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Called at each wait between polls; lets a test change the site or cancel during the wait.</summary>
    public Action<int>? OnDelay { get; set; }

    /// <summary>The SMS Provider name the administration service client sends to.</summary>
    public string ProviderName { get; set; } = StubAdminService.Host;

    /// <summary>The site and provider the service reports as its connection.</summary>
    public (string Site, string Provider) Connection { get; set; } = ("MCM", StubAdminService.Host);

    public PivotsHarness(JsonObject? state = null, string? auditPath = null, string user = StubPivotsSite.Lab)
    {
        Directory.CreateDirectory(Folder);
        Site = new StubPivotsSite(state ?? StubPivotsSite.Lab01());
        Admin = new StubAdminService(Members, Devices);
        Client = new AdminServiceClient(() => ProviderName, handler: Admin);
        Audit = new PivotsAuditLog(() => auditPath ?? Path.Combine(Folder, "audit.jsonl"), user: () => user);
        Sessions = new PivotsSessionStore(Path.Combine(Folder, "Sessions"));
        _user = user;
        Service = NewService();
    }

    private PivotsScriptsService NewService() => new(Site, Client, Audit, Sessions,
        delay: (t, ct) => { Delays++; Clock += t; OnDelay?.Invoke(Delays); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
        utcNow: () => Clock, user: () => _user, connection: () => Connection);

    /// <summary>A new service over the same site, files and audit log, as after a restart of the app: it holds no run in memory.</summary>
    public void Restart() => Service = NewService();

    public IReadOnlyList<PivotsAuditRecord> Finished() => Audit.Read(100).Where(r => r.Phase == "Finished").ToList();

    public Task<PivotsOutcome> WaitAsync(string operationId, CancellationToken ct = default, TimeSpan? window = null, string? sessionId = null) =>
        Service.WaitForResultsAsync(operationId, new SyncProgress<PivotsOutcome>(Reports.Add), ct, window: window, sessionId: sessionId);

    private IReadOnlyList<(int, string, bool, bool?)> Devices() =>
        Site.List("Devices").OfType<JsonObject>().Select(d => ((int)d["ResourceID"]!, (string)d["Name"]!, (bool)d["IsClient"]!, (bool?)d["CNIsOnline"])).ToList();

    private IReadOnlyList<(int, string, bool, bool?)> Members(string collection)
    {
        var devices = Devices().ToDictionary(d => d.Item1);
        return Site.List("Members").OfType<JsonObject>().Where(m => (string?)m["CollectionID"] == collection)
            .Select(m => devices.TryGetValue((int)m["ResourceID"]!, out var d) ? d : ((int)m["ResourceID"]!, (string)m["Name"]!, (bool)m["IsClient"]!, (bool?)null)).ToList();
    }

    public static PivotsChange Request(PivotsChangeKind kind, ResolvedTargets? targets = null, string? query = null, string? guid = null, IReadOnlyDictionary<string, string>? parameters = null,
        string? submitName = null, string? submitText = null, string? comment = null, string? hash = null) =>
        new(Kind: kind, Targets: targets, QueryText: query, ScriptGuid: guid, ScriptHash: hash, Parameters: parameters, SubmitName: submitName, SubmitText: submitText, Comment: comment,
            RunConfirmed: false, EveryDeviceAcknowledged: false, Impact: [], Checks: [], Fingerprint: null);

    public Task<ResolvedTargets> ResolveAsync(RunTargetKind kind, string text) => Service.ResolveTargetsAsync(new RunTarget(kind, text), CancellationToken.None);

    public async Task<PivotsChange> PreparePivotAsync(RunTargetKind kind, string text, string query = "OS | project Caption", TimeSpan? window = null)
    {
        var targets = await ResolveAsync(kind, text);
        return await Service.PrepareAsync(Request(PivotsChangeKind.RunPivot, targets, query) with { ResultWindow = window }, CancellationToken.None);
    }

    public async Task<PivotsChange> PrepareScriptAsync(RunTargetKind kind, string text, IReadOnlyDictionary<string, string>? parameters = null, string guid = StubPivotsSite.ScriptGuid, TimeSpan? window = null)
    {
        var targets = await ResolveAsync(kind, text);
        return await Service.PrepareAsync(Request(PivotsChangeKind.RunScript, targets, guid: guid, parameters: parameters) with { ResultWindow = window }, CancellationToken.None);
    }

    /// <summary>Applies the change as the desktop writer does after a confirmed plan; <paramref name="confirmed"/> false sends it without the plan confirmation.</summary>
    public async Task<PivotsOutcome> ApplyAsync(PivotsChange change, CancellationToken ct = default, bool confirmed = true) =>
        await Service.ApplyAsync(confirmed ? change with { ChangeConfirmed = true } : change, new SyncProgress<PivotsOutcome>(Reports.Add), ct);

    public IReadOnlyList<string> AuditLines()
    {
        if (!File.Exists(Audit.Path)) return [];
        using var stream = new FileStream(Audit.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public void Dispose()
    {
        Client.Dispose();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
