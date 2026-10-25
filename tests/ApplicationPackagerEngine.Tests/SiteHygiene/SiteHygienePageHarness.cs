using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// A small Site Hygiene site for the page tests, with the safety rules of the real service: a delete
/// without DeleteConfirmed is refused, a finding that stopped holding is dropped at Prepare and skipped
/// at Apply, a delete above the cap is refused at Prepare, and the run reports a progress outcome
/// before the final outcome of each object.
/// </summary>
internal sealed class FakeSiteHygieneService : ISiteHygieneService
{
    public const string DeleteRefused = "Skipped: the delete needs its second confirmation. Nothing was deleted.";
    public const string NoLongerHolds = "Skipped: the finding no longer holds. The object was not changed.";
    public const string CancelledSkip = "Skipped: the run was cancelled before this object.";
    public const int Cap = 50;

    public List<HygieneFinding> Findings { get; } = [];
    public List<HygieneCheckStatus> Checks { get; } = [];
    public List<RelationEdge> Edges { get; } = [];
    public bool Truncated;
    public bool RelationsTruncated;
    public DateTime ScannedUtc = new(2026, 10, 24, 8, 30, 0, DateTimeKind.Utc);

    public int ScanCalls;
    public int RelationCalls;
    public int PrepareCalls;
    public List<HygieneScanOptions> ScanOptions { get; } = [];
    public List<HygieneChange> PrepareRequests { get; } = [];
    public List<HygieneChange> Applied { get; } = [];
    public TaskCompletionSource? ScanGate;
    public TaskCompletionSource? PrepareGate;
    public Exception? ScanFailure;

    /// <summary>True makes the scan finish its work although the token was cancelled, as a service that checks the token only between steps.</summary>
    public bool IgnoreCancel;

    /// <summary>False for a finding that stopped holding on the site; checked at Prepare and before each write.</summary>
    public Func<HygieneFinding, bool> StillHolds = _ => true;

    /// <summary>A message makes the object fail; null lets it succeed.</summary>
    public Func<HygieneFinding, string?>? Fail;

    /// <summary>True makes the read-back of the object show the old value: the first report says Done, the final says Failed.</summary>
    public Func<HygieneFinding, bool>? ReadBackFails;

    /// <summary>Changes the site between Prepare and Apply, as another admin or process.</summary>
    public Action? BetweenPrepareAndApply;

    /// <summary>Called after each object was reported; a test can cancel here.</summary>
    public Action<int>? AfterObject;

    public TaskCompletionSource? ApplyGate;

    /// <summary>True makes the run report that its Finished audit record could not be written.</summary>
    public bool AuditIncomplete;

    public const string AuditMissing = @" The audit log has no Finished record for this run: the record could not be written to C:\Data\SiteHygiene\site-hygiene-audit.jsonl.";

    /// <summary>Set while a confirmed change plan runs; a write outside it fails the test.</summary>
    public Func<bool>? InConfirmedRun;

    /// <summary>The site code the scan returns, as the real service reads it from the ConfigMgr drive.</summary>
    public string SiteCode = "MCM";

    /// <summary>The SMS Provider the scan records, as the real service takes it from the connection.</summary>
    public string? Provider = "cm01.contoso.com";

    public HygieneScanResult Result() => new(Findings.ToList(), Checks.ToList(), ScannedUtc, Truncated, null) { SiteCode = SiteCode, Provider = Provider };

    public async Task<HygieneScanResult> ScanAsync(HygieneScanOptions options, IProgress<string> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref ScanCalls);
        lock (ScanOptions) ScanOptions.Add(options);
        progress.Report("Reading the applications.");
        if (ScanGate is { } gate) await (IgnoreCancel ? gate.Task : gate.Task.WaitAsync(ct));
        if (!IgnoreCancel) ct.ThrowIfCancellationRequested();
        if (ScanFailure is { } failure) throw failure;
        progress.Report("Running the checks.");
        return Result();
    }

    public async Task<RelationInventory> LoadRelationsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref RelationCalls);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        return new RelationInventory(Edges.ToList(), RelationsTruncated, RelationsTruncated ? "The site holds more relationships than the read takes." : null);
    }

    public async Task<HygieneChange> PrepareAsync(HygieneChange request, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCalls);
        lock (PrepareRequests) PrepareRequests.Add(request);
        if (PrepareGate is { } gate) await gate.Task.WaitAsync(ct);
        var kept = request.Findings.Where(f => StillHolds(f)).ToList();
        var dropped = request.Findings.Where(f => !StillHolds(f)).Select(f => new HygieneObjectOutcome(f, HygieneResults.Skipped, "Skipped: the finding no longer holds: the object changed.")).ToList();
        var impact = new List<string>();
        if (dropped.Count > 0) impact.Add($"{dropped.Count} findings are left out.");
        var checks = new List<PreflightCheck>();
        if (SiteHygieneLabels.IsDelete(request.Kind))
        {
            checks.Add(new PreflightCheck(Name: "Cap", Result: kept.Count > Cap ? "Fail" : "Pass",
                Message: kept.Count > Cap ? $"A run deletes at most {Cap} objects. Filter the findings to {Cap} or fewer." : $"At most {Cap} objects per run."));
            checks.Add(new PreflightCheck(Name: "References", Result: "Pass", Message: "Nothing references the objects."));
            impact.Add($"Deletes {kept.Count} objects. The delete cannot be undone.");
        }
        BetweenPrepareAndApply?.Invoke();
        return request with { Findings = kept, Impact = impact, Checks = checks, Fingerprint = $"fp-{PrepareCalls}", Dropped = dropped };
    }

    public async Task<HygieneOutcome> ApplyAsync(HygieneChange change, IProgress<HygieneOutcome> progress, CancellationToken ct)
    {
        if (InConfirmedRun is { } inRun && !inRun()) throw new InvalidOperationException("A write ran outside a confirmed plan.");
        lock (Applied) Applied.Add(change);
        if (ApplyGate is { } gate) await gate.Task;
        if (!change.ChangeConfirmed) throw new InvalidOperationException("A change reached the service without ChangeConfirmed.");
        if (SiteHygieneLabels.IsDelete(change.Kind) && !change.DeleteConfirmed)
        {
            var refused = new HygieneOutcome(change, false, DeleteRefused, change.Findings.Select(f => new HygieneObjectOutcome(f, "Skipped", DeleteRefused)).ToList(), Final: true);
            progress.Report(refused);
            return refused;
        }
        var results = new List<HygieneObjectOutcome>();
        for (var i = 0; i < change.Findings.Count; i++)
        {
            var finding = change.Findings[i];
            HygieneObjectOutcome result;
            if (ct.IsCancellationRequested) result = new HygieneObjectOutcome(finding, "Skipped", CancelledSkip);
            else if (!StillHolds(finding)) result = new HygieneObjectOutcome(finding, "Skipped", NoLongerHolds);
            else if (Fail?.Invoke(finding) is { } message) result = new HygieneObjectOutcome(finding, "Failed", message);
            else
            {
                progress.Report(new HygieneOutcome(change, true, $"Object {i + 1} of {change.Findings.Count}: done, read-back follows.", [new HygieneObjectOutcome(finding, "Done", "Done before the read-back.")], Final: false));
                result = ReadBackFails?.Invoke(finding) == true
                    ? new HygieneObjectOutcome(finding, "Failed", "Failed: the read-back shows the object as before.")
                    : new HygieneObjectOutcome(finding, "Done", SiteHygieneLabels.IsDelete(change.Kind) ? "Deleted. The read-back finds no object." : "Changed. The read-back shows the new value.");
                if (result.Result == "Done" && SiteHygieneLabels.IsDelete(change.Kind)) lock (Findings) Findings.RemoveAll(f => SiteHygieneLabels.Key(f) == SiteHygieneLabels.Key(finding));
            }
            results.Add(result);
            AfterObject?.Invoke(i);
        }
        var outcome = new HygieneOutcome(change, results.All(r => r.Result != "Failed"), $"{results.Count(r => r.Result == "Done")} done." + (AuditIncomplete ? AuditMissing : ""), results, Final: true)
        {
            AuditIncomplete = AuditIncomplete,
            AuditPath = AuditIncomplete ? @"C:\Data\SiteHygiene\site-hygiene-audit.jsonl" : ""
        };
        progress.Report(outcome);
        return outcome;
    }
}

internal sealed class FakeSuppressionStore : IHygieneSuppressionStore
{
    public List<Suppression> Items { get; } = [];
    public int Loads;

    /// <summary>The number of writes that succeed before every later write throws; null never fails.</summary>
    public int? FailAfter;

    /// <summary>The writes of the store: each Add, AddRange, Remove and RemoveRange call is one read and one write of the file.</summary>
    public int Writes;

    /// <summary>Holds Load until it is set; Loading is set when Load starts.</summary>
    public ManualResetEventSlim? LoadGate;
    public readonly ManualResetEventSlim Loading = new();

    public IReadOnlyList<Suppression> Load()
    {
        Interlocked.Increment(ref Loads);
        Loading.Set();
        LoadGate?.Wait(TimeSpan.FromSeconds(30));
        lock (Items) return Items.ToList();
    }

    public void Add(Suppression suppression) => AddRange([suppression]);

    public void AddRange(IReadOnlyList<Suppression> suppressions)
    {
        Check();
        Interlocked.Increment(ref Writes);
        lock (Items)
            foreach (var suppression in suppressions)
            {
                Items.RemoveAll(s => s.StoreKey == suppression.StoreKey);
                Items.Add(suppression);
            }
    }

    public void Remove(string checkId, string objectKey) => RemoveRange("", [(checkId, objectKey)]);

    public void RemoveRange(string siteCode, IReadOnlyList<(string CheckId, string ObjectKey)> keys, string? provider = null)
    {
        Check();
        Interlocked.Increment(ref Writes);
        var wanted = keys.Select(k => SiteHygieneLabels.Key(k.CheckId, k.ObjectKey)).ToHashSet();
        lock (Items) Items.RemoveAll(s => wanted.Contains(SiteHygieneLabels.Key(s.CheckId, s.ObjectKey)) && s.AppliesTo(siteCode, provider));
    }

    public string Path => @"C:\Data\SiteHygiene\suppressions.json";

    private void Check()
    {
        if (FailAfter is { } left)
        {
            if (left <= 0) throw new IOException(@"Access to C:\Data\SiteHygiene\suppressions.json is denied.");
            FailAfter = left - 1;
        }
    }
}

internal sealed class FakeScanStore : IHygieneScanStore
{
    public HygieneScanResult? Stored;
    public List<HygieneScanResult> Saved { get; } = [];
    public int Loads;
    public bool SaveFails;

    /// <summary>Holds Save until it is set, as a slow disk; Saving is set when Save starts.</summary>
    public ManualResetEventSlim? SaveGate;
    public readonly ManualResetEventSlim Saving = new();

    public void Save(HygieneScanResult result)
    {
        Saving.Set();
        SaveGate?.Wait(TimeSpan.FromSeconds(30));
        if (SaveFails) throw new IOException("The disk is full.");
        lock (Saved) Saved.Add(result);
        Stored = result;
    }

    public HygieneScanResult? Load()
    {
        Interlocked.Increment(ref Loads);
        return Stored;
    }

    public IReadOnlySet<string>? Keys() => Stored?.Findings.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string Path => @"C:\Data\SiteHygiene\last-scan.json";
}

internal sealed class FakeHygieneReports : IHygieneReportWriter
{
    public List<(string Format, HygieneReport Report)> Written { get; } = [];
    public string Folder => @"C:\Data\SiteHygiene\Reports";
    public bool Fails;
    public string WriteCsv(HygieneReport report) => Write("csv", report);
    public string WriteHtml(HygieneReport report) => Write("html", report);

    private string Write(string format, HygieneReport report)
    {
        if (Fails) throw new IOException(@"Access to C:\Data\SiteHygiene\Reports\secret.csv is denied.");
        lock (Written) Written.Add((format, report));
        return $@"C:\Data\SiteHygiene\Reports\site-hygiene-{Written.Count}.{format}";
    }
}

/// <summary>An audit log of records in memory for the History tab; it writes nothing.</summary>
internal sealed class FakeHygieneAudit(IReadOnlyList<HygieneAuditRecord> records) : IHygieneAuditLog
{
    public int Reads;
    public AuditProbe Probe() => new(true, "The audit log can be written.");
    public AuditWrite Append(HygieneAuditRecord r) => new(false, "The fake audit log writes nothing.");

    public IReadOnlyList<HygieneAuditRecord> Read(int max)
    {
        Interlocked.Increment(ref Reads);
        return records.TakeLast(max).ToList();
    }

    public string Path => @"C:\Data\SiteHygiene\site-hygiene-audit.jsonl";

    /// <summary>Records of every fix kind and result, with long object names, the last one of a broken chain.</summary>
    public static FakeHygieneAudit Long(int count, string longText)
    {
        var kinds = Enum.GetValues<HygieneFixKind>().Where(HygieneFixKinds.IsDelete).Concat([HygieneFixKind.SetCollectionRefreshType, HygieneFixKind.SetSiteMaintenanceTask]).ToList();
        var results = new[] { "", "Succeeded", "Partial", "Failed", "Skipped" };
        var list = new List<HygieneAuditRecord>();
        for (var i = 0; i < count; i++)
        {
            var result = results[i % results.Length];
            var objects = Enumerable.Range(0, 3).Select(n => new HygieneAuditObject("APP-01", "Application", $"ScopeId_{i}/Application_{n}", $"{longText} {i}.{n}", result.Length == 0 ? "" : "Done")).ToList();
            list.Add(new HygieneAuditRecord(new DateTime(2026, 12, 28, 23, 59, 59).AddMinutes(-i), @"CONTOSO\labadmin-with-a-long-name", kinds[i % kinds.Count].ToString(),
                result.Length == 0 ? HygieneAuditPhases.Started : HygieneAuditPhases.Finished, result, $"run{i}", objects, $"{longText} message {i}.")
            {
                Site = "MCM",
                Provider = "cm01.contoso.com",
                Verified = i < count - 1,
                HasChain = true,
                ChainBrokenAt = i < count - 1 ? null : count
            });
        }
        return new FakeHygieneAudit(list);
    }
}

internal sealed class FakeHygieneDialogs : ISiteHygieneDialogs
{
    /// <summary>What the operator does in the finding list; null continues with the findings as checked.</summary>
    public Action<HygieneFindingsPickerViewModel>? OnFindings;
    public List<HygieneFindingsPickerViewModel> Shown { get; } = [];
    public bool CancelPicker;
    public string? Comment = "Kept for the audit in November.";
    public List<string> OpenedFiles { get; } = [];
    public List<HygieneImportViewModel> Imports { get; } = [];

    /// <summary>True confirms the import plan; false cancels it.</summary>
    public bool ConfirmImport = true;
    public List<(string Title, string Prompt)> Asked { get; } = [];
    public List<string> Clipboard { get; } = [];
    public List<string> OpenedFolders { get; } = [];

    public void ShowFindings(HygieneFindingsPickerViewModel findings)
    {
        Shown.Add(findings);
        OnFindings?.Invoke(findings);
        if (CancelPicker) findings.CancelCommand.Execute(null);
        else if (findings.ContinueCommand.CanExecute(null)) findings.ContinueCommand.Execute(null);
    }

    public string? AskComment(string title, string prompt)
    {
        Asked.Add((title, prompt));
        return Comment;
    }

    public void SetClipboard(string text) => Clipboard.Add(text);

    public void OpenFolder(string folder) => OpenedFolders.Add(folder);

    public void OpenFile(string path) => OpenedFiles.Add(path);

    void ISiteHygieneDialogs.ConfirmImport(HygieneImportViewModel import)
    {
        Imports.Add(import);
        if (ConfirmImport) import.ImportCommand.Execute(null);
        else import.CancelCommand.Execute(null);
    }
}

internal sealed class SiteHygienePageHarness
{
    public FakeSiteHygieneService Service { get; } = new();
    public FakeSuppressionStore Suppressions { get; } = new();
    public FakeScanStore ScanStore { get; } = new();
    public FakeHygieneReports Reports { get; } = new();
    public FakeHygieneDialogs Dialogs { get; } = new();
    public ScriptedPlanDialogs Plans { get; } = new();
    public IChangePlanDialogs? PlanDialogs;
    public RecordingSiteHistory History { get; } = new();
    public RecordingMaintenanceLog Log { get; } = new();
    public AppSettings Settings { get; set; } = new() { SiteCode = "MCM", ProviderMachine = "cm01.contoso.com" };
    public SiteHygieneSettings? Saved { get; set; }
    public int PreferenceSaves;
    public TimeSpan FilterDelay = TimeSpan.Zero;

    /// <summary>The Live services; null builds the page without them, as a build without the engine services.</summary>
    public SiteHygieneLiveFakes? Live = new();

    /// <summary>The audit log the History tab reads; null shows no history.</summary>
    public IHygieneAuditLog? AuditLog;

    public HygieneFinding UnusedApp { get; }
    public HygieneFinding UnusedApp2 { get; }
    public HygieneFinding RetiredDeployed { get; }
    public HygieneFinding EmptyCollection { get; }
    public HygieneFinding RefreshType { get; }
    public HygieneFinding Duplicate { get; }
    public HygieneFinding Boundary { get; }
    public HygieneFinding Backup { get; }

    public static HygieneFinding Finding(string checkId, string category, HygieneSeverity severity, string type, string key, string name, HygieneFixKind kind,
        string? fixText = null, bool isNew = false, string? evidence = null) =>
        new(CheckId: checkId, Category: category, Severity: severity, ObjectType: type, ObjectKey: key, ObjectName: name,
            Evidence: evidence ?? $"{name} has no deployment, no task sequence reference and no dependency.", Recommendation: $"Review {name}, then remove it when nothing needs it.",
            FixKind: kind, FixParameters: new Dictionary<string, string> { ["Id"] = key }, FixText: kind == HygieneFixKind.None ? null : fixText ?? $"Remove-CMApplication -Id {key} -Force",
            IsNew: isNew, IsSuppressed: false);

    public SiteHygienePageHarness()
    {
        UnusedApp = Add(Finding("APP-01", "Applications", HygieneSeverity.Warning, "Application", "16777300", "Contoso Fonts 2.1", HygieneFixKind.RemoveApplication));
        UnusedApp2 = Add(Finding("APP-01", "Applications", HygieneSeverity.Warning, "Application", "16777301", "Contoso Teams Backgrounds", HygieneFixKind.RemoveApplication));
        RetiredDeployed = Add(Finding("APP-02", "Applications", HygieneSeverity.Error, "Deployment", "16777412", "Contoso Printer Drivers to All Desktops", HygieneFixKind.RemoveApplicationDeployment,
            "Remove-CMApplicationDeployment -Id 16777412 -Force", evidence: "The application is retired and still has 1 deployment."));
        EmptyCollection = Add(Finding("COL-01", "Collections", HygieneSeverity.Info, "Collection", "MCM00012", "SHL Empty Collection", HygieneFixKind.RemoveCollection,
            "Remove-CMCollection -Id MCM00012 -Force"));
        RefreshType = Add(Finding("COL-05", "Collections", HygieneSeverity.Warning, "Collection", "MCM00014", "Pilot Ring 1", HygieneFixKind.SetCollectionRefreshType,
            "Set-CMCollection -CollectionId MCM00014 -RefreshType Continuous", evidence: "Incremental and a full evaluation every 2 hours."));
        Duplicate = Add(Finding("DEV-02", "Devices", HygieneSeverity.Warning, "Device", "16777221", "CLIENT01", HygieneFixKind.RemoveDevice, "Remove-CMResource -ResourceId 16777221 -Force",
            evidence: "Two records share the SMBIOS GUID 4C4C4544-0042.\nThe newer record is 16777230."));
        Boundary = Add(Finding("BND-01", "Boundaries", HygieneSeverity.Warning, "Boundary", "16777217", "10.0.5.0/24", HygieneFixKind.None));
        Backup = Add(Finding("MNT-02", "Site", HygieneSeverity.Warning, "Maintenance task", "Backup SMS Site Server", "Backup SMS Site Server", HygieneFixKind.SetSiteMaintenanceTask,
            "Set-CMSiteMaintenanceTask -SiteCode MCM -MaintenanceTaskName 'Backup SMS Site Server' -Enabled $true"));
        foreach (var (id, name, category, state, reason) in new[]
                 {
                     ("APP-01", "Application with no deployments or references", "Applications", "Ran", (string?)null),
                     ("APP-02", "Retired application still deployed", "Applications", "Ran", null),
                     ("APP-04", "Deployment type content source missing or unreachable", "Applications", "NotRun", "The content source probe is off."),
                     ("COL-01", "Empty collection nothing references", "Collections", "Ran", null),
                     ("COL-05", "Sub-daily full evaluation on an incremental collection", "Collections", "Ran", null),
                     ("DEV-02", "Duplicate device records", "Devices", "Ran", null),
                     ("DEV-03", "Clients below the newest client version", "Devices", "NeedsSiteDatabase", "The check reads inventory that only the site database holds."),
                     ("BND-01", "Boundary in no boundary group", "Boundaries", "Ran", null),
                     ("MNT-02", "Site backup task disabled", "Site", "Ran", null)
                 })
            Service.Checks.Add(new HygieneCheckStatus(CheckId: id, Name: name, Category: category, State: state,
                Findings: state == "Ran" ? Service.Findings.Count(f => f.CheckId == id) : 0, Reason: reason));
        Service.Edges.AddRange(
        [
            new RelationEdge(FromKey: "16777500", FromName: "7-Zip 26.04", ToKey: "16777499", ToName: "7-Zip 25.01", Kind: "Supersedes", Health: "Healthy"),
            new RelationEdge(FromKey: "16777499", FromName: "7-Zip 25.01", ToKey: "16777498", ToName: "7-Zip 24.08", Kind: "Supersedes", Health: "Retired"),
            new RelationEdge(FromKey: "16777600", FromName: "Contoso CRM Client", ToKey: "16777601", ToName: "Visual C++ 2015-2022", Kind: "DependsOn", Health: "Healthy"),
            new RelationEdge(FromKey: "16777700", FromName: "Loop A", ToKey: "16777701", ToName: "Loop B", Kind: "Supersedes", Health: "Circular"),
            new RelationEdge(FromKey: "16777701", FromName: "Loop B", ToKey: "16777700", ToName: "Loop A", Kind: "Supersedes", Health: "Circular")
        ]);
        Service.InConfirmedRun = () => Plans.InRun;
    }

    /// <summary>Makes every check of the scan run, or leave it out by an option as APP-04 with the probe off, so the result may replace the stored last scan.</summary>
    public void CompleteScan()
    {
        for (var i = 0; i < Service.Checks.Count; i++)
            if (Service.Checks[i].State == HygieneCheckStates.NotRun) Service.Checks[i] = Service.Checks[i] with { State = HygieneCheckStates.NotSelected };
    }

    private HygieneFinding Add(HygieneFinding finding)
    {
        Service.Findings.Add(finding);
        return finding;
    }

    public SiteHygieneWriter Writer() => new(Service, History, Log, () => "MCM");

    public SiteHygienePageServices Services() => new(Service, Suppressions, ScanStore, Reports, Dialogs, PlanDialogs ?? Plans, Writer(), () => Settings, () => Saved,
        s =>
        {
            Interlocked.Increment(ref PreferenceSaves);
            Saved = s;
            return true;
        }, Log) { FilterDelay = FilterDelay, AuditLog = AuditLog, Live = Live?.Services() };

    public SiteHygieneViewModel Create() => new(Services());

    public async Task<SiteHygieneViewModel> OpenAsync()
    {
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync();
        return viewModel;
    }

    public async Task<SiteHygieneViewModel> ScannedAsync()
    {
        var viewModel = await OpenAsync();
        await viewModel.ScanAsync();
        return viewModel;
    }

    public static FindingGridRow Row(SiteHygieneViewModel viewModel, HygieneFinding finding) =>
        viewModel.FindingRows.Single(r => r.Key == SiteHygieneLabels.Key(finding));

    public static void Select(SiteHygieneViewModel viewModel, params HygieneFinding[] findings) =>
        viewModel.SetSelectedFindings(findings.Select(f => Row(viewModel, f)));

    /// <summary>A large site: <paramref name="count"/> findings over every check, category and fix kind, with optional long text.</summary>
    public static SiteHygienePageHarness Large(int count, string? longText = null)
    {
        var h = new SiteHygienePageHarness();
        h.Service.Findings.Clear();
        var kinds = Enum.GetValues<HygieneFixKind>();
        var categories = SiteHygieneViewModel.KnownCategories;
        for (var i = 0; i < count; i++)
        {
            var kind = kinds[i % kinds.Length];
            h.Service.Findings.Add(Finding($"{categories[i % categories.Count][..3].ToUpperInvariant()}-{i % 10 + 1:00}", categories[i % categories.Count], (HygieneSeverity)(i % 3),
                "Application", (16_777_216 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), $"{longText ?? "Contoso App"} {i}", kind, isNew: i % 9 == 0,
                evidence: $"{longText ?? "Evidence"} {i}"));
        }
        return h;
    }
}
