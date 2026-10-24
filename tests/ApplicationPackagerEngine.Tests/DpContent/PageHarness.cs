using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>
/// A small DP content site for the page tests: DPs, content and pairs, with the safety rules of the
/// real service: a removal without RemovalConfirmed is refused, a content left on no DP without
/// LastCopyAcknowledged is refused, a pair that changed since Prepare is dropped, and the run goes in
/// chunks with a cancel between them.
/// </summary>
internal sealed class PageFakeDpContentService : IDpContentService
{
    public const string RemovalRefused = "Skipped: the removal needs its second confirmation. Nothing was changed.";
    public const string LastCopyRefused = "Skipped: no DP would hold the content after the removal, and the plan did not accept that. Nothing was changed.";
    public const string CancelledSkip = "Skipped: the run was cancelled before this pair.";

    public List<DpRow> Dps { get; } = [];
    public List<ContentRow> Content { get; } = [];
    public List<PairRow> Pairs { get; } = [];
    public bool Truncated;
    public string? Note;
    public int ChunkSize = 2;

    /// <summary>Unchanged makes Distribute again report that the site shows no distribution yet.</summary>
    public string DistributeAgainResult = "Requested";

    public int LoadCalls;
    public int PrepareCalls;
    public TaskCompletionSource? LoadGate;
    public TaskCompletionSource? PrepareGate;
    public TaskCompletionSource? ApplyGate;
    public Exception? LoadFailure;
    public List<CancellationToken> LoadTokens { get; } = [];
    public List<DpContentChange> Prepared { get; } = [];
    public List<IReadOnlyList<DpContentChange>> Applied { get; } = [];

    /// <summary>A failed check the next Prepare returns, as the service's cap check; null for none.</summary>
    public PreflightCheck? BlockingCheck;

    /// <summary>A message makes the pair fail; null lets it succeed.</summary>
    public Func<PairRow, string?>? Fail;

    /// <summary>Changes the site between Prepare and Apply, as another admin or process.</summary>
    public Action? BetweenPrepareAndApply;

    /// <summary>Called after each chunk was reported; a test can cancel here.</summary>
    public Action<int>? AfterChunk;

    /// <summary>Set while a confirmed change plan runs; a write outside it fails the test.</summary>
    public Func<bool>? InConfirmedRun;

    public static string Nal(string server) => $"[\"Display=\\\\{server}\\\"]MSWNET:[\"SMS_SITE=LAB\"]\\\\{server}\\";

    public DpRow AddDp(string name, bool pull = false, double sizeGb = 12.5)
    {
        var dp = new DpRow(DpName: name, NalPath: Nal(name), SiteCode: "LAB", IsPullDp: pull, Total: 0, Installed: 0, InProgress: 0, Failed: 0, PercentOk: 0,
            TotalSizeGb: sizeGb, CountsKnown: true);
        Dps.Add(dp);
        return dp;
    }

    public ContentRow AddContent(string packageId, string name, string type = "Package", int deployments = 1, double sizeMb = 120)
    {
        var content = new ContentRow(PackageId: packageId, Name: name, ContentType: type, SizeMb: sizeMb, DpsTargeted: 0, Installed: 0, InProgress: 0, Failed: 0,
            PercentOk: 0, DeploymentCount: deployments);
        Content.Add(content);
        return content;
    }

    public static string StateName(int state) => state switch
    {
        0 => "Installed", 1 => "Install Pending", 2 => "Install Retrying", 3 => "Install Failed", 4 => "Removal Pending",
        5 => "Removal Retrying", 6 => "Removal Failed", 7 => "Content Validating", 8 => "Content Valid", _ => $"Unknown ({state})"
    };

    public PairRow AddPair(ContentRow content, DpRow dp, int state)
    {
        var pair = new PairRow(PackageId: content.PackageId, ContentName: content.Name, ContentType: content.ContentType, DpName: dp.DpName, NalPath: dp.NalPath,
            State: state, StateName: StateName(state), SourceVersion: "1", SummaryDate: new DateTime(2026, 10, 23, 8, 0, 0));
        lock (Pairs) Pairs.Add(pair);
        return pair;
    }

    public void SetState(PairRow pair, int state)
    {
        lock (Pairs)
        {
            var i = Pairs.FindIndex(p => DpContentLabels.Key(p) == DpContentLabels.Key(pair));
            Pairs[i] = Pairs[i] with { State = state, StateName = StateName(state) };
        }
    }

    public void RemovePair(PairRow pair)
    {
        lock (Pairs) Pairs.RemoveAll(p => DpContentLabels.Key(p) == DpContentLabels.Key(pair));
    }

    public DpContentSnapshot Snapshot()
    {
        List<PairRow> pairs;
        lock (Pairs) pairs = Pairs.ToList();
        static double Pct(int ok, int total) => total == 0 ? 0 : Math.Round(ok * 100.0 / total, 1);
        var dps = Dps.Select(d =>
        {
            var mine = pairs.Where(p => p.NalPath == d.NalPath).ToList();
            var ok = mine.Count(p => DpContentLabels.IsOk(p.State));
            return d with
            {
                Total = mine.Count, Installed = ok, InProgress = mine.Count(p => DpContentLabels.IsInProgress(p.State)),
                Failed = mine.Count(p => DpContentLabels.IsFailed(p.State)), PercentOk = Pct(ok, mine.Count)
            };
        }).ToList();
        var byContent = pairs.GroupBy(p => p.PackageId).ToDictionary(g => g.Key, g => g.ToList());
        var content = Content.Select(c =>
        {
            var mine = byContent.TryGetValue(c.PackageId, out var list) ? list : [];
            var ok = mine.Count(p => DpContentLabels.IsOk(p.State));
            return c with
            {
                DpsTargeted = mine.Count, Installed = ok, InProgress = mine.Count(p => DpContentLabels.IsInProgress(p.State)),
                Failed = mine.Count(p => DpContentLabels.IsFailed(p.State)), PercentOk = Pct(ok, mine.Count)
            };
        }).ToList();
        return new DpContentSnapshot(dps, content, pairs, Truncated, Note);
    }

    public async Task<DpContentSnapshot> LoadAsync(IProgress<string> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref LoadCalls);
        lock (LoadTokens) LoadTokens.Add(ct);
        progress.Report("Reading the distribution points.");
        if (LoadGate is { } gate) await gate.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (LoadFailure is { } failure) throw failure;
        progress.Report("Reading the status rows.");
        return Snapshot();
    }

    private PairRow? Current(PairRow pair)
    {
        lock (Pairs) return Pairs.FirstOrDefault(p => DpContentLabels.Key(p) == DpContentLabels.Key(pair));
    }

    private int Remaining(string packageId, IReadOnlyList<PairRow> removed)
    {
        var keys = removed.Select(DpContentLabels.Key).ToHashSet();
        lock (Pairs) return Pairs.Count(p => p.PackageId == packageId && !keys.Contains(DpContentLabels.Key(p)) && !DpContentLabels.IsRemoval(p.State));
    }

    public async Task<DpContentChange> PrepareAsync(DpContentChange request, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCalls);
        if (PrepareGate is { } gate) await gate.Task.WaitAsync(ct);
        var kept = new List<PairRow>();
        var impact = new List<string>();
        foreach (var pair in request.Pairs)
        {
            if (request.Kind == DpContentChangeKind.DistributeAgain) kept.Add(pair);
            else if (Current(pair) is { } now) kept.Add(now);
            else impact.Add($"Skipped: {pair.ContentName} ({pair.PackageId}) on {pair.DpName} has no status row any more.");
        }
        var checks = new List<PreflightCheck>();
        if (BlockingCheck is { } blocking) checks.Add(blocking);
        if (request.Kind == DpContentChangeKind.Remove)
        {
            var empty = kept.Select(p => p.PackageId).Distinct().Where(id => Remaining(id, kept) == 0).ToList();
            checks.Add(new PreflightCheck(Name: "LastCopy", Result: empty.Count == 0 ? "Pass" : "Fail",
                Message: empty.Count == 0 ? "Every content keeps a DP." : $"{empty.Count} content will have no DP left."));
            impact.Add("Deployments counted: applications, packages and task sequences.");
        }
        var prepared = request with { Pairs = kept, Impact = impact, Checks = checks, Fingerprint = $"fp-{PrepareCalls}" };
        lock (Prepared) Prepared.Add(prepared);
        BetweenPrepareAndApply?.Invoke();
        return prepared;
    }

    public async Task<IReadOnlyList<DpContentOutcome>> ApplyAsync(IReadOnlyList<DpContentChange> changes, IProgress<DpContentOutcome> progress, CancellationToken ct)
    {
        if (InConfirmedRun is { } inRun && !inRun()) throw new InvalidOperationException("A write ran outside a confirmed plan.");
        lock (Applied) Applied.Add(changes);
        if (ApplyGate is { } gate) await gate.Task;
        var outcomes = new List<DpContentOutcome>();
        foreach (var change in changes)
        {
            if (change.Kind == DpContentChangeKind.Remove && !change.RemovalConfirmed)
            {
                var refused = new DpContentOutcome(change, false, RemovalRefused, change.Pairs.Select(p => new DpContentPairOutcome(p, "Skipped", RemovalRefused, null)).ToList(), null);
                progress.Report(refused);
                outcomes.Add(refused);
                continue;
            }
            var emptied = change.Kind == DpContentChangeKind.Remove && !change.LastCopyAcknowledged
                ? change.Pairs.Select(p => p.PackageId).Distinct().Where(id => Remaining(id, change.Pairs) == 0).ToHashSet()
                : [];
            var chunks = change.Pairs.Chunk(ChunkSize).ToList();
            var undo = new List<PairRow>();
            for (var c = 0; c < chunks.Count; c++)
            {
                var results = new List<DpContentPairOutcome>();
                foreach (var pair in chunks[c])
                {
                    if (ct.IsCancellationRequested) results.Add(new DpContentPairOutcome(pair, "Skipped", CancelledSkip, null));
                    else if (emptied.Contains(pair.PackageId)) results.Add(new DpContentPairOutcome(pair, "Skipped", LastCopyRefused, null));
                    else if (change.Kind != DpContentChangeKind.DistributeAgain && (Current(pair) is not { } now || now.State != pair.State))
                        results.Add(new DpContentPairOutcome(pair, "Skipped", "Skipped: the pair changed after the plan was shown. Nothing was changed for it.", Current(pair)?.State));
                    else if (Fail?.Invoke(pair) is { } message) results.Add(new DpContentPairOutcome(pair, "Failed", message, pair.State));
                    else results.Add(Write(change.Kind, pair, undo));
                }
                var outcome = new DpContentOutcome(change, results.All(r => r.Result != "Failed"), $"Chunk {c + 1} of {chunks.Count}.", results,
                    undo.Count > 0 ? change with { Kind = DpContentChangeKind.DistributeAgain, Pairs = undo.ToList(), RemovalConfirmed = false, LastCopyAcknowledged = false } : null);
                progress.Report(outcome);
                outcomes.Add(outcome);
                AfterChunk?.Invoke(c);
            }
        }
        return outcomes;
    }

    private DpContentPairOutcome Write(DpContentChangeKind kind, PairRow pair, List<PairRow> undo)
    {
        switch (kind)
        {
            case DpContentChangeKind.Remove:
                if (DpContentLabels.IsOk(pair.State)) undo.Add(pair);
                RemovePair(pair);
                return new DpContentPairOutcome(pair, "Confirmed", "The content is gone from the DP.", null);
            case DpContentChangeKind.Validate:
                return new DpContentPairOutcome(pair, "Unchanged", "The validation found the content valid.", 8);
            case DpContentChangeKind.DistributeAgain:
                if (DistributeAgainResult == "Unchanged") return new DpContentPairOutcome(pair, "Unchanged", "The site shows no distribution for the pair.", null);
                lock (Pairs) Pairs.Add(pair with { State = 1, StateName = StateName(1) });
                return new DpContentPairOutcome(pair, "Requested", "The site accepted the distribution.", 1);
            default:
                SetState(pair, 2);
                return new DpContentPairOutcome(pair, "Requested", "The site accepted the request.", 2);
        }
    }
}

internal sealed class PageFakeAuditLog : IDpContentAuditLog
{
    public List<DpContentAuditRecord> Records { get; } = [];
    public string Path { get; set; } = @"C:\Data\DpContent\dp-content-audit.jsonl";
    public int Reads;
    public bool Dropped;
    public bool OlderRecordsDropped => Dropped;
    public AuditProbe Probe() => new(true, "The audit log can be written.");
    public AuditWrite Append(DpContentAuditRecord record)
    {
        lock (Records) Records.Add(record);
        return new AuditWrite(true, "");
    }

    public IReadOnlyList<DpContentAuditRecord> Read(int max)
    {
        Interlocked.Increment(ref Reads);
        // The audit log parses each line again on every read, so each read returns new objects.
        lock (Records) return Records.TakeLast(max).Select(r => r with { }).ToList();
    }
}

internal sealed class PageFakeReports : IDpContentReportWriter
{
    public List<(string Format, DpContentReport Report)> Written { get; } = [];
    public string Folder => @"C:Reports";
    public bool Fails;
    public string WriteCsv(DpContentReport report) => Write("csv", report);
    public string WriteHtml(DpContentReport report) => Write("html", report);

    private string Write(string format, DpContentReport report)
    {
        if (Fails) throw new IOException(@"Access to C:\Reports\secret.csv is denied.");
        lock (Written) Written.Add((format, report));
        return $@"C:\Reports\dp-content-{Written.Count}.{format}";
    }
}

internal sealed class PageFakeDpDialogs : IDpContentDialogs
{
    /// <summary>What the operator does in the pair list; null continues with the pairs as checked.</summary>
    public Action<DpContentPairsViewModel>? OnPairs;
    public List<DpContentPairsViewModel> Shown { get; } = [];
    public List<string> OpenedFolders { get; } = [];
    public bool Cancel;

    public void ShowPairs(DpContentPairsViewModel pairs)
    {
        Shown.Add(pairs);
        OnPairs?.Invoke(pairs);
        if (Cancel) pairs.CancelCommand.Execute(null);
        else if (pairs.ContinueCommand.CanExecute(null)) pairs.ContinueCommand.Execute(null);
    }

    public void OpenFolder(string folder) => OpenedFolders.Add(folder);
}

internal sealed class DpContentPageHarness
{
    public PageFakeDpContentService Service { get; } = new();
    public PageFakeAuditLog Audit { get; } = new();
    public PageFakeReports Reports { get; } = new();
    public PageFakeDpDialogs Dialogs { get; } = new();
    public ScriptedPlanDialogs Plans { get; } = new();
    public IChangePlanDialogs? PlanDialogs;
    public RecordingSiteHistory History { get; } = new();
    public RecordingMaintenanceLog Log { get; } = new();
    public AppSettings Settings { get; } = new() { SiteCode = "LAB", ProviderMachine = "cm01.lab.local" };
    public DpContentSettings? Saved { get; set; }
    public int PreferenceSaves;
    public TimeSpan FilterDelay = TimeSpan.Zero;

    public DpRow Dp01 { get; }
    public DpRow Dp01Branch { get; }
    public DpRow Dp02 { get; }
    public ContentRow SevenZip { get; }
    public ContentRow Office { get; }
    public ContentRow Boot { get; }
    public PairRow SevenZipOnDp01 { get; }
    public PairRow SevenZipOnDp02 { get; }
    public PairRow SevenZipOnBranch { get; }
    public PairRow OfficeOnDp01 { get; }
    public PairRow BootOnDp01 { get; }
    public PairRow BootOnDp02 { get; }

    public DpContentPageHarness()
    {
        Dp01 = Service.AddDp("DP01.lab.local");
        Dp01Branch = Service.AddDp("DP01.lab.local.branch", pull: true);
        Dp02 = Service.AddDp("DP02.lab.local");
        SevenZip = Service.AddContent("LAB00001", "7-Zip 26.04", "Application", deployments: 2);
        Office = Service.AddContent("LAB00002", "Office Tools", "Package", deployments: -1);
        Boot = Service.AddContent("LAB00003", "Boot image (x64)", "BootImage", deployments: 0);
        SevenZipOnDp01 = Service.AddPair(SevenZip, Dp01, 0);
        SevenZipOnDp02 = Service.AddPair(SevenZip, Dp02, 3);
        SevenZipOnBranch = Service.AddPair(SevenZip, Dp01Branch, 2);
        OfficeOnDp01 = Service.AddPair(Office, Dp01, 0);
        BootOnDp01 = Service.AddPair(Boot, Dp01, 6);
        BootOnDp02 = Service.AddPair(Boot, Dp02, 0);
        Service.InConfirmedRun = () => Plans.InRun;
    }

    public DpContentWriter Writer() => new(Service, History, Log, () => "LAB");

    public DpContentPageServices Services() => new(Service, Audit, Reports, Dialogs, PlanDialogs ?? Plans, Writer(), () => Settings, () => Saved,
        s =>
        {
            Interlocked.Increment(ref PreferenceSaves);
            Saved = s;
            return true;
        }, Log) { FilterDelay = FilterDelay };

    public DpContentManagerViewModel Create() => new(Services());

    public async Task<DpContentManagerViewModel> OpenAsync(int tab = DpContentManagerViewModel.DpsTab)
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

    /// <summary>A large site: <paramref name="dps"/> DPs and <paramref name="contents"/> contents on each, every tenth pair failed.</summary>
    public static DpContentPageHarness Large(int dps, int contents, string? longText = null)
    {
        var h = new DpContentPageHarness();
        h.Service.Pairs.Clear();
        h.Service.Dps.Clear();
        h.Service.Content.Clear();
        var dpRows = Enumerable.Range(1, dps).Select(i => h.Service.AddDp($"DP{i:000}.{longText ?? "lab.local"}", pull: i % 7 == 0)).ToList();
        var contentRows = Enumerable.Range(1, contents).Select(i => h.Service.AddContent($"LAB{i:00000}", $"{longText ?? "Content"} {i}", DpContentLabels.ContentTypes[i % 7], deployments: i % 5 - 1)).ToList();
        var n = 0;
        foreach (var dp in dpRows)
            foreach (var content in contentRows)
                h.Service.AddPair(content, dp, n++ % 10 == 0 ? 3 : n % 10 == 5 ? 2 : 0);
        return h;
    }
}
