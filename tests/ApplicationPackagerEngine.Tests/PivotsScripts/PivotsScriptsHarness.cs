using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>
/// A small site for the page tests with the refusal rules of the real service: a script run without
/// RunConfirmed, a built-in collection without EveryDeviceAcknowledged, a script whose text changed
/// after Prepare, a collection that grew past the confirmed count and a script that is not approved
/// are refused, each with a message that starts with "Skipped". A run saves its session before it
/// sends; with a zero result window it returns at once with every device Sent.
/// </summary>
internal sealed class FakePivotsService : IPivotsScriptsService
{
    public const string NotConfirmedRefusal = "Skipped: the run needs its second confirmation. Nothing was sent.";
    public const string EveryDeviceRefusal = "Skipped: the target is a built-in collection and the plan did not accept that. Nothing was sent.";
    public const string HashRefusal = "Skipped: the script text changed after the plan was shown. Nothing was sent.";
    public const string GrewRefusal = "Skipped: the collection has more members than the plan confirmed. Nothing was sent.";
    public const string AuthorRefusal = "The site refused the approval: the author of a script cannot approve it. Approve it with another account.";
    public const int MaxDevices = 25_000;

    public FakePivotsService(FakePivotsSessions sessions) => _sessions = sessions;

    private readonly FakePivotsSessions _sessions;

    public List<ResolvedDevice> Devices { get; } = [];
    public Dictionary<string, (string Name, List<int> Members)> Collections { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SiteScript> Scripts { get; } = [];

    public int ResolveCalls;
    public int LoadScriptCalls;
    public int PrepareCalls;
    public int WaitCalls;
    public TaskCompletionSource? ResolveGate;
    public TaskCompletionSource? PrepareGate;
    public TaskCompletionSource? WaitGate;
    public Exception? ResolveFailure;
    public Exception? PrepareFailure;
    public Exception? WaitFailure;
    public Exception? ConnectionFailure;
    public bool SubmitFailsAfterCreate;
    public string CurrentUser = @"LAB\author";
    public List<PivotsChange> Prepared { get; } = [];
    public List<PivotsChange> Applied { get; } = [];
    public List<CancellationToken> WaitTokens { get; } = [];

    /// <summary>Changes the site between Prepare and Apply, as another admin or process.</summary>
    public Action? BetweenPrepareAndApply;

    /// <summary>The device states the wait reports before it ends; null reports Running for every device.</summary>
    public Func<int, string>? ProgressState;

    /// <summary>The final device state of a device; null gives Completed for every device.</summary>
    public Func<int, string>? FinalState;

    /// <summary>Set while a confirmed change plan runs; a write outside it fails the test.</summary>
    public Func<bool>? InConfirmedRun;

    private int _operations;
    private readonly Dictionary<string, (PivotsChange Change, string Session)> _runs = new(StringComparer.Ordinal);

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public ResolvedDevice AddDevice(int id, string name, bool? online = true, bool client = true)
    {
        var d = new ResolvedDevice(ResourceId: id, Name: name, IsOnline: online) { IsClient = client };
        lock (Devices) Devices.Add(d);
        return d;
    }

    public SiteScript AddScript(string name, string text, string state = SiteScriptStates.Approved, params ScriptParameter[] parameters)
    {
        var s = new SiteScript(Guid: Guid.NewGuid().ToString().ToUpperInvariant(), Name: name, Author: @"LAB\author", Approver: state == SiteScriptStates.Approved ? @"LAB\approver" : "",
            State: state, Version: 1, Language: "PowerShell", Hash: Hash(text), Parameters: parameters, Text: text) { SiteHash = Hash(text).ToUpperInvariant() };
        lock (Scripts) Scripts.Add(s);
        return s;
    }

    public void ChangeScriptText(string guid, string text)
    {
        lock (Scripts)
        {
            var i = Scripts.FindIndex(s => s.Guid == guid);
            Scripts[i] = Scripts[i] with { Text = text, Hash = Hash(text), SiteHash = Hash(text).ToUpperInvariant(), Version = Scripts[i].Version + 1 };
        }
    }

    private SiteScript? Script(string? guid)
    {
        lock (Scripts) return Scripts.FirstOrDefault(s => string.Equals(s.Guid, guid, StringComparison.OrdinalIgnoreCase));
    }

    private ResolvedDevice? DeviceById(int id)
    {
        lock (Devices) return Devices.FirstOrDefault(d => d.ResourceId == id);
    }

    private ResolvedTargets Resolve(RunTarget target)
    {
        List<ResolvedDevice> all;
        lock (Devices) all = Devices.ToList();
        switch (target.Kind)
        {
            case RunTargetKind.Collection:
                var hit = Collections.FirstOrDefault(c => string.Equals(c.Key, target.Text, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Value.Name, target.Text, StringComparison.OrdinalIgnoreCase));
                if (hit.Key is null) throw new PivotsServiceException($"No collection has the ID or the exact name {target.Text}.");
                var members = hit.Value.Members.Select(DeviceById).OfType<ResolvedDevice>().ToList();
                return new ResolvedTargets(Devices: members, CollectionId: hit.Key, Description: $"Collection {hit.Value.Name} ({hit.Key})",
                    IsBuiltInCollection: hit.Key.StartsWith("SMS", StringComparison.OrdinalIgnoreCase), Unmatched: [], Truncated: false)
                { Kind = target.Kind, Text = target.Text, TotalMatched = members.Count, CollectionName = hit.Value.Name };
            case RunTargetKind.Pattern:
                var regex = new Regex("^" + Regex.Escape(target.Text).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
                var matched = all.Where(d => regex.IsMatch(d.Name)).ToList();
                return new ResolvedTargets(matched, null, $"Pattern {target.Text}", false, [], false) { Kind = target.Kind, Text = target.Text, TotalMatched = matched.Count };
            default:
                var names = target.Kind == RunTargetKind.Device ? [target.Text.Trim()] : target.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var found = new List<ResolvedDevice>();
                var unmatched = new List<string>();
                foreach (var name in names)
                {
                    if (all.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) is { } d) found.Add(d);
                    else unmatched.Add(name);
                }
                return new ResolvedTargets(found, null, target.Kind == RunTargetKind.Device ? $"Device {target.Text.Trim()}" : $"List of {names.Length} names", false, unmatched, false)
                { Kind = target.Kind, Text = target.Text, TotalMatched = found.Count };
        }
    }

    public async Task<ResolvedTargets> ResolveTargetsAsync(RunTarget target, CancellationToken ct)
    {
        Interlocked.Increment(ref ResolveCalls);
        if (ResolveGate is { } gate) await gate.Task.WaitAsync(ct);
        if (ResolveFailure is { } failure) throw failure;
        return Resolve(target);
    }

    public Task<IReadOnlyList<SiteScript>> LoadScriptsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref LoadScriptCalls);
        lock (Scripts) return Task.FromResult<IReadOnlyList<SiteScript>>(Scripts.Select(s => s with { Text = null, Hash = "" }).ToList());
    }

    public Task<SiteScript?> LoadScriptAsync(string guid, CancellationToken ct) => Task.FromResult(Script(guid));

    public async Task<PivotsChange> PrepareAsync(PivotsChange request, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCalls);
        if (PrepareGate is { } gate) await gate.Task.WaitAsync(ct);
        if (PrepareFailure is { } failure) throw failure;
        var checks = new List<PreflightCheck>();
        var impact = new List<string>();
        var prepared = request;
        if (request.IsRun)
        {
            var count = request.Targets?.Devices.Count(d => d.IsClient) ?? 0;
            checks.Add(new PreflightCheck(Name: "Cap", Result: count > MaxDevices ? "Fail" : "Pass",
                Message: count > MaxDevices ? $"The run targets {count} devices; the cap is {MaxDevices}. Narrow the collection." : $"The run targets {count} devices."));
            impact.Add("Requests: one RunCMPivot or one script operation for the whole target.");
            prepared = prepared with { DeviceCount = count };
        }
        if (request.Kind != PivotsChangeKind.SubmitScript && request.Kind != PivotsChangeKind.RunPivot)
        {
            var script = Script(request.ScriptGuid);
            if (script is null) checks.Add(new PreflightCheck("ScriptExists", "Fail", "The site no longer holds the script."));
            else
            {
                prepared = prepared with { Script = script, ScriptHash = script.Hash };
                if (request.Kind == PivotsChangeKind.RunScript && script.State != SiteScriptStates.Approved)
                    checks.Add(new PreflightCheck("Approved", "Fail", "The script is not approved on the site."));
            }
        }
        if (request.Kind == PivotsChangeKind.SubmitScript)
            prepared = prepared with { ScriptHash = Hash(request.SubmitText ?? ""), SubmitParameters = [new ScriptParameter("Name", "System.String", false, null)] };
        prepared = prepared with { Impact = impact, Checks = checks, Fingerprint = $"fp-{PrepareCalls}" };
        lock (Prepared) Prepared.Add(prepared);
        BetweenPrepareAndApply?.Invoke();
        return prepared;
    }

    private static PivotsOutcome Refused(PivotsChange change, string message) => new(change, false, message, null,
        (change.Targets?.Devices ?? []).Select(d => new DeviceResult(d.ResourceId, d.Name, PivotsDeviceStates.Skipped, "", message)).ToList(), false, null);

    public Task<PivotsOutcome> ApplyAsync(PivotsChange change, IProgress<PivotsOutcome> progress, CancellationToken ct)
    {
        if (InConfirmedRun is { } inRun && !inRun()) throw new InvalidOperationException("A write ran outside a confirmed plan.");
        lock (Applied) Applied.Add(change);
        progress.Report(new PivotsOutcome(change, true, "Saving the session and writing the audit record.", null, [], false, null) { Final = false });
        switch (change.Kind)
        {
            case PivotsChangeKind.RunPivot or PivotsChangeKind.RunScript:
                if (change.NeedsRunConfirmation && !change.RunConfirmed) return Task.FromResult(Refused(change, NotConfirmedRefusal));
                if (change.NeedsEveryDeviceAcknowledgement && !change.EveryDeviceAcknowledged) return Task.FromResult(Refused(change, EveryDeviceRefusal));
                if (change.Kind == PivotsChangeKind.RunScript && Script(change.ScriptGuid)?.Hash != change.ScriptHash) return Task.FromResult(Refused(change, HashRefusal));
                if (change.Targets is { CollectionId: { } id } && Collections.TryGetValue(id, out var c) && c.Members.Count > change.Targets.Devices.Count)
                    return Task.FromResult(Refused(change, GrewRefusal));
                var op = "op-" + Interlocked.Increment(ref _operations);
                var session = "session-" + op;
                _sessions.Save(new PivotsSessionRecord(session, DateTime.UtcNow, CurrentUser, "LAB", "cm01.lab.local", change.Kind, change.Targets!.Description, change.Targets.CollectionId,
                    change.Targets.Devices, change.QueryText, change.ScriptGuid, change.Script?.Name, change.ScriptHash, change.Parameters ?? new Dictionary<string, string>()) { OperationId = op });
                lock (_runs) _runs[op] = (change, session);
                return Task.FromResult(new PivotsOutcome(change, true, "The site accepted the run.", op,
                    change.Targets.Devices.Select(d => d.IsClient ? new DeviceResult(d.ResourceId, d.Name, PivotsDeviceStates.Sent, "", "")
                        : new DeviceResult(d.ResourceId, d.Name, PivotsDeviceStates.Skipped, "", "Skipped: the device has no ConfigMgr client.")).ToList(), true, session) { Sent = true });
            case PivotsChangeKind.SubmitScript:
                var created = Guid.NewGuid().ToString().ToUpperInvariant();
                lock (Scripts) Scripts.Add(new SiteScript(created, change.SubmitName!, CurrentUser, "", SiteScriptStates.Waiting, 1, "PowerShell", Hash(change.SubmitText!), [], change.SubmitText));
                if (SubmitFailsAfterCreate)
                    return Task.FromResult(new PivotsOutcome(change, false, "The parameter definitions could not be written.", null, [], false, null)
                    {
                        CreatedScriptGuid = created,
                        CleanupChange = new PivotsChange(PivotsChangeKind.RemoveScript, null, null, created, null, null, null, null, null, false, false, [], [], null)
                    });
                return Task.FromResult(new PivotsOutcome(change, true, "The site stores the script as Waiting for approval.", null, [], false, null) { CreatedScriptGuid = created });
            default:
                var script = Script(change.ScriptGuid);
                if (script is null) return Task.FromResult(new PivotsOutcome(change, false, "Skipped: the site no longer holds the script.", null, [], false, null));
                if (change.Kind == PivotsChangeKind.ApproveScript && string.Equals(script.Author, CurrentUser, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new PivotsOutcome(change, false, AuthorRefusal, null, [], false, null));
                lock (Scripts)
                {
                    var i = Scripts.FindIndex(s => s.Guid == script.Guid);
                    if (change.Kind == PivotsChangeKind.RemoveScript) Scripts.RemoveAt(i);
                    else Scripts[i] = script with { State = change.Kind == PivotsChangeKind.ApproveScript ? SiteScriptStates.Approved : SiteScriptStates.Declined };
                }
                return Task.FromResult(new PivotsOutcome(change, true, "Done.", null, [], false, null));
        }
    }

    public async Task<PivotsOutcome> WaitForResultsAsync(string operationId, IProgress<PivotsOutcome> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref WaitCalls);
        lock (WaitTokens) WaitTokens.Add(ct);
        PivotsChange change;
        string session;
        lock (_runs) (change, session) = _runs[operationId];
        var devices = change.Targets!.Devices.Where(d => d.IsClient).ToList();
        progress.Report(new PivotsOutcome(change, true, "Polling.", operationId,
            devices.Select(d => new DeviceResult(d.ResourceId, d.Name, ProgressState?.Invoke(d.ResourceId) ?? PivotsDeviceStates.Running, "", "")).ToList(), true, session) { Final = false, Sent = true });
        if (WaitGate is { } gate) await gate.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (WaitFailure is { } failure) throw failure;
        var pivot = change.Kind == PivotsChangeKind.RunPivot;
        var final = devices.Select(d =>
        {
            var state = FinalState?.Invoke(d.ResourceId) ?? PivotsDeviceStates.Completed;
            var ok = state == PivotsDeviceStates.Completed;
            return new DeviceResult(d.ResourceId, d.Name, state, ok ? $"Output of {d.Name}" : "",
                ok ? "" : state == PivotsDeviceStates.NoResponse ? "The device did not answer in the wait window." : "The script failed on the device.")
            {
                ExitCode = ok && !pivot ? 0 : null,
                Rows = ok && pivot ? [new Dictionary<string, string> { ["Caption"] = "Windows 11 Enterprise", ["Version"] = "10.0.26100" }] : []
            };
        }).ToList();
        var partial = final.Any(d => d.State != PivotsDeviceStates.Completed);
        var record = _sessions.Read(session)!;
        _sessions.Update(record with
        {
            Status = partial ? "Partial" : "Completed", Results = final, Partial = partial, FinishedUtc = DateTime.UtcNow,
            Raw = [new PivotsRawRecord(DateTime.UtcNow, "Status", 200, "{\"OperationId\":\"" + operationId + "\"}")]
        });
        return new PivotsOutcome(change, true, "The wait ended.", operationId, final, partial, session) { Sent = true };
    }

    public Task<ScriptCheck> CheckScriptAsync(string text, CancellationToken ct) =>
        Task.FromResult(text.Contains("{{", StringComparison.Ordinal)
            ? new ScriptCheck(false, ["Line 1: Missing closing '}'."], [], [])
            : new ScriptCheck(true, [], text.Contains("param", StringComparison.OrdinalIgnoreCase) ? [new ScriptParameter("Name", "System.String", true, null)] : [], []));

    public Task<string> TestConnectionAsync(CancellationToken ct) =>
        ConnectionFailure is { } failure ? Task.FromException<string>(failure) : Task.FromResult("The AdminService on cm01.lab.local answered.");
}

internal sealed class FakePivotsSessions : IPivotsSessionStore
{
    public List<PivotsSessionRecord> Sessions { get; } = [];
    public string Folder => @"C:\Data\PivotsScripts\Sessions";

    public void Save(PivotsSessionRecord record)
    {
        lock (Sessions) Sessions.Add(record);
    }

    public void Update(PivotsSessionRecord record)
    {
        lock (Sessions)
        {
            var i = Sessions.FindIndex(s => s.Id == record.Id);
            if (i >= 0) Sessions[i] = record;
            else Sessions.Add(record);
        }
    }

    public IReadOnlyList<PivotsSessionSummary> List(int max)
    {
        lock (Sessions)
            return Sessions.OrderByDescending(s => s.CreatedUtc).Take(max)
                .Select(s => new PivotsSessionSummary(s.Id, s.CreatedUtc, s.Kind, s.TargetDescription, s.Devices.Count, s.Status, s.OperationId)).ToList();
    }

    public PivotsSessionRecord? Read(string id)
    {
        lock (Sessions) return Sessions.FirstOrDefault(s => s.Id == id);
    }
}

internal sealed class FakePivotsAudit : IPivotsAuditLog
{
    public List<PivotsAuditRecord> Records { get; } = [];
    public string Path => @"C:\Data\PivotsScripts\pivots-audit.jsonl";
    public int Reads;
    public AuditProbe Probe() => new(true, "");
    public AuditWrite Append(PivotsAuditRecord r)
    {
        lock (Records) Records.Add(r);
        return new AuditWrite(true, "");
    }

    public IReadOnlyList<PivotsAuditRecord> Read(int max)
    {
        Interlocked.Increment(ref Reads);
        // Each read parses the file again, so each read returns new objects.
        lock (Records) return Records.TakeLast(max).Select(r => r with { }).ToList();
    }
}

internal sealed class FakePivotsReports : IPivotsReportWriter
{
    public List<(string Format, PivotsReport Report)> Written { get; } = [];
    public string Folder => @"C:\Reports";
    public string WriteCsv(PivotsReport report) => Write("csv", report);
    public string WriteJson(PivotsReport report) => Write("json", report);

    private string Write(string format, PivotsReport report)
    {
        lock (Written) Written.Add((format, report));
        return $@"C:\Reports\pivots-{Written.Count}.{format}";
    }
}

internal sealed class FakePivotsLibrary : IPivotsLibrary
{
    public int Loads;
    public List<string?> Folders { get; } = [];

    public IReadOnlyList<PivotsLibraryItem> Load(string? userFolder)
    {
        Interlocked.Increment(ref Loads);
        lock (Folders) Folders.Add(userFolder);
        return
        [
            new("Operating system", "Query", "OS | project Caption, Version", true, "a1"),
            new("Stopped services", "Query", "Service | where State == 'Stopped'", true, "a2"),
            new("Disk space", "Script", "Get-CimInstance Win32_LogicalDisk | Select-Object DeviceID, FreeSpace", true, "a3")
        ];
    }
}

internal sealed class FakePivotsDialogs : IPivotsDialogs
{
    public string? TargetFile;
    public string? Folder;
    public string? Comment = "Not needed on the lab.";
    public Action<PivotsParametersViewModel>? OnParameters;
    public List<string> Clipboard { get; } = [];
    public List<string> OpenedFolders { get; } = [];
    public int CommentAsks;

    public string? PickTargetFile() => TargetFile;
    public string? PickFolder(string? current) => Folder;

    public void ShowParameters(PivotsParametersViewModel parameters)
    {
        OnParameters?.Invoke(parameters);
        if (parameters.OkCommand.CanExecute(null)) parameters.OkCommand.Execute(null);
        else parameters.CancelCommand.Execute(null);
    }

    public string? AskComment(string title, string prompt)
    {
        CommentAsks++;
        return Comment;
    }

    public void SetClipboard(string text) => Clipboard.Add(text);
    public void OpenFolder(string folder) => OpenedFolders.Add(folder);
}

internal sealed class PivotsScriptsHarness
{
    public FakePivotsSessions Sessions { get; } = new();
    public FakePivotsService Service { get; }
    public FakePivotsAudit Audit { get; } = new();
    public FakePivotsReports Reports { get; } = new();
    public FakePivotsLibrary Library { get; } = new();
    public FakePivotsDialogs Dialogs { get; } = new();
    public ScriptedPlanDialogs Plans { get; } = new();
    public RecordingSiteHistory History { get; } = new();
    public RecordingMaintenanceLog Log { get; } = new();
    public AppSettings Settings { get; } = new() { SiteCode = "LAB", ProviderMachine = "cm01.lab.local" };
    public PivotsScriptsSettings? Saved { get; set; }
    public int PreferenceSaves;
    public TimeSpan FilterDelay = TimeSpan.Zero;

    public SiteScript DiskScript { get; }
    public SiteScript WaitingScript { get; }
    public SiteScript ParamScript { get; }

    public PivotsScriptsHarness()
    {
        Service = new FakePivotsService(Sessions);
        for (var i = 1; i <= 6; i++) Service.AddDevice(16777200 + i, $"WKS-{i:000}", online: i != 6);
        Service.AddDevice(16777300, "SRV-001");
        Service.Collections["LAB00012"] = ("Workstations", Enumerable.Range(1, 6).Select(i => 16777200 + i).ToList());
        Service.Collections["SMS00001"] = ("All Systems", [.. Enumerable.Range(1, 6).Select(i => 16777200 + i), 16777300]);
        DiskScript = Service.AddScript("Disk space", "Get-CimInstance Win32_LogicalDisk | Select-Object DeviceID, FreeSpace");
        WaitingScript = Service.AddScript("Clear temp", "Remove-Item $env:TEMP\\* -Recurse", SiteScriptStates.Waiting);
        ParamScript = Service.AddScript("Read key", "param([string]$Path, [int]$Depth) Get-ItemProperty $Path", SiteScriptStates.Approved,
            new ScriptParameter("Path", "System.String", true, null), new ScriptParameter("Depth", "System.Int32", false, "1") { Description = "How many levels to read." });
        Service.InConfirmedRun = () => Plans.InRun;
    }

    public PivotsScriptsWriter Writer() => new(Service, History, Log, () => "LAB");

    public PivotsScriptsPageServices Services() => new(Service, Sessions, Audit, Reports, Library, Dialogs, Plans, Writer(), () => Settings, () => Saved,
        s =>
        {
            Interlocked.Increment(ref PreferenceSaves);
            Saved = s;
            return true;
        }, Log) { FilterDelay = FilterDelay };

    public PivotsScriptsViewModel Create() => new(Services());

    public async Task<PivotsScriptsViewModel> OpenAsync(int tab = PivotsScriptsViewModel.PivotTab)
    {
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync();
        if (viewModel.SelectedTabIndex != tab)
        {
            viewModel.SelectedTabIndex = tab;
            await viewModel.EnsureTabLoadedAsync();
        }
        return viewModel;
    }

    /// <summary>Resolves the Workstations collection, or another target.</summary>
    public static async Task ResolveAsync(PivotsScriptsViewModel vm, RunTargetKind kind = RunTargetKind.Collection, string text = "LAB00012")
    {
        vm.TargetKind = kind;
        vm.TargetText = text;
        await vm.ResolveCommand.ExecuteAsync(null);
    }

    public static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
