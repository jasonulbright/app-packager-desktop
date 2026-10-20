using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Collections;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>A clock fixed at one instant, in a UTC local zone so local and UTC times agree.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>
/// A small deployment site: objects, DP groups and the deployments the fake creates and removes. Like the
/// real service, it refuses a Required create without the acknowledgement and fails closed on the checks.
/// </summary>
internal sealed class FakeDeploymentService : IDeploymentService
{
    public const string CancelledSkip = "Skipped: the run was cancelled before this change.";
    public const string RequiredRefused = "Refused: a Required deployment needs the second confirmation. Nothing was changed.";

    private int _next = 0x100;

    public Dictionary<DeploymentObjectType, List<DeployableObject>> Objects { get; } = new()
    {
        [DeploymentObjectType.Application] = [],
        [DeploymentObjectType.Package] = [],
        [DeploymentObjectType.TaskSequence] = [],
        [DeploymentObjectType.SoftwareUpdateGroup] = []
    };

    public Dictionary<string, List<string>> Programs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DpGroupInfo> Groups { get; } = [];
    public HashSet<string> Distributed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DeploymentRecord> Deployments { get; } = [];
    public Dictionary<string, int> MemberCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int ListCalls;
    public int ProgramCalls;
    public int GroupCalls;
    public int PrepareCalls;
    public List<DeploymentObjectType> ListedTypes { get; } = [];

    /// <summary>Holds the object list of a type until the test releases it.</summary>
    public Dictionary<DeploymentObjectType, TaskCompletionSource> ListGates { get; } = [];

    /// <summary>Holds every PrepareAsync until the test releases it.</summary>
    public TaskCompletionSource? PrepareGate;

    public TaskCompletionSource? GroupGate;
    public Exception? ListFailure;
    public Func<DeploymentChange, Exception?>? PrepareFails;

    /// <summary>A message makes the change fail; null lets it succeed.</summary>
    public Func<DeploymentChange, string?>? Fail;

    /// <summary>True makes a failed create report the deployment it made anyway, with its undo.</summary>
    public bool FailAfterWrite;

    /// <summary>Called after each change was reported; a test can cancel here.</summary>
    public Action<int>? AfterChange;

    /// <summary>Set while a confirmed change plan runs; a write outside it fails the test.</summary>
    public Func<bool>? InConfirmedRun;

    public List<IReadOnlyList<DeploymentChange>> Applied { get; } = [];
    public List<DeploymentChange> Prepared { get; } = [];

    public DeployableObject Add(DeploymentObjectType type, string id, string name, string version = "1.0", bool packaged = false, bool distributed = true)
    {
        var obj = new DeployableObject(Type: type, Id: id, Name: name, Version: version, Program: "", PackagedByProduct: packaged);
        Objects[type].Add(obj);
        if (distributed) Distributed.Add(id);
        return obj;
    }

    public async Task<IReadOnlyList<DeployableObject>> ListObjectsAsync(DeploymentObjectType type, CancellationToken ct)
    {
        Interlocked.Increment(ref ListCalls);
        lock (ListedTypes) ListedTypes.Add(type);
        TaskCompletionSource? gate;
        lock (ListGates) ListGates.TryGetValue(type, out gate);
        if (gate is not null) await gate.Task.WaitAsync(ct);
        if (ListFailure is { } failure) throw failure;
        lock (Objects) return Objects[type].ToList();
    }

    public Task<IReadOnlyList<string>> ListProgramsAsync(string packageId, CancellationToken ct)
    {
        Interlocked.Increment(ref ProgramCalls);
        lock (Objects) return Task.FromResult<IReadOnlyList<string>>(Programs.TryGetValue(packageId, out var list) ? list.ToList() : []);
    }

    public Task<IReadOnlyList<DeploymentCollection>> ListCollectionsAsync(CancellationToken ct) => throw new NotSupportedException("The page reads collections from the collection service.");

    public async Task<IReadOnlyList<DpGroupInfo>> ListDpGroupsAsync(DeployableObject obj, CancellationToken ct)
    {
        Interlocked.Increment(ref GroupCalls);
        if (GroupGate is { } gate) await gate.Task.WaitAsync(ct);
        lock (Objects)
            return Groups.Select(g => Distributed.Contains(obj.Id) ? g with { Status = "Distributed", Success = g.Targeted } : g).ToList();
    }

    public Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(DeployableObject obj, CancellationToken ct)
    {
        lock (Objects) return Task.FromResult<IReadOnlyList<DeploymentRecord>>(Deployments.Where(d => d.ObjectId == obj.Id).ToList());
    }

    public async Task<DeploymentChange> PrepareAsync(DeploymentChange request, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCalls);
        if (PrepareGate is { } gate) await gate.Task.WaitAsync(ct);
        if (PrepareFails?.Invoke(request) is { } failure) throw failure;
        DeploymentChange prepared;
        lock (Objects)
        {
            prepared = request.Kind switch
            {
                DeploymentChangeKind.Create => PrepareCreate(request),
                DeploymentChangeKind.Distribute => request with
                {
                    Impact = request.DpGroups.Select(g => $"Distribution point group {g}: {(Distributed.Contains(request.Object!.Id) ? "Distributed" : "NotTargeted")} now.").ToList()
                },
                _ => Deployments.Any(d => d.DeploymentId == request.Existing!.DeploymentId)
                    ? request with { Impact = [$"The deployment {request.Existing!.DeploymentId} exists and has the captured values."] }
                    : throw new DeploymentServiceException($"The deployment {request.Existing!.DeploymentId} no longer exists. Nothing was changed.")
            };
            Prepared.Add(prepared);
        }
        return prepared;
    }

    private DeploymentChange PrepareCreate(DeploymentChange request)
    {
        var obj = request.Object!;
        var collection = request.Collection!;
        var exists = Objects[obj.Type].Any(o => o.Id == obj.Id);
        var duplicate = Deployments.Any(d => d.ObjectId == obj.Id && d.CollectionId == collection.CollectionId);
        var distributed = Distributed.Contains(obj.Id);
        // As the service: content that the same change distributes first is a warning, not a failure.
        var content = distributed ? "Pass" : request.DpGroups.Count > 0 ? "Warn" : "Fail";
        var checks = new List<PreflightCheck>
        {
            new(Name: "TargetExists", Result: exists ? "Pass" : "Fail", Message: exists ? "" : "The object no longer exists."),
            new(Name: "ContentDistributed", Result: content, Message: distributed ? "" : "No distribution point group has the content."),
            new(Name: "CollectionValid", Result: "Pass", Message: ""),
            new(Name: "CollectionSafe", Result: collection.CollectionId.StartsWith("SMS", StringComparison.OrdinalIgnoreCase) ? "Fail" : "Pass", Message: ""),
            new(Name: "NoDuplicate", Result: duplicate ? "Fail" : "Pass", Message: duplicate ? "The object already has a deployment to this collection." : "")
        };
        var members = MemberCounts.TryGetValue(collection.CollectionId, out var count) ? count : collection.MemberCount;
        return request with { Collection = collection with { MemberCount = members }, Checks = checks, Impact = ["The audit log can be written."] };
    }

    public async Task<IReadOnlyList<DeploymentOutcome>> ApplyAsync(IReadOnlyList<DeploymentChange> changes, IProgress<DeploymentOutcome> progress, CancellationToken ct)
    {
        if (InConfirmedRun is { } inRun) Assert.True(inRun(), "A write ran outside a confirmed change plan.");
        lock (Applied) Applied.Add(changes);
        await Task.Yield();
        var outcomes = new List<DeploymentOutcome>();
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            DeploymentOutcome outcome;
            if (ct.IsCancellationRequested) outcome = new DeploymentOutcome(Change: change, Succeeded: false, Message: CancelledSkip, UndoChange: null, Steps: []);
            else outcome = Apply(change);
            outcomes.Add(outcome);
            progress.Report(outcome);
            AfterChange?.Invoke(i);
        }
        return outcomes;
    }

    private DeploymentOutcome Apply(DeploymentChange change)
    {
        lock (Objects)
        {
            if (change.Kind == DeploymentChangeKind.Create && DeploymentWriter.IsRequiredCreate(change) && !change.RequiredConfirmed)
                return new DeploymentOutcome(Change: change, Succeeded: false, Message: RequiredRefused, UndoChange: null, Steps: []);
            var failure = Fail?.Invoke(change);
            if (failure is not null && !(FailAfterWrite && change.Kind == DeploymentChangeKind.Create))
                return new DeploymentOutcome(Change: change, Succeeded: false, Message: failure, UndoChange: null, Steps: []);
            switch (change.Kind)
            {
                case DeploymentChangeKind.Create:
                {
                    if (change.DpGroups.Count > 0) Distributed.Add(change.Object!.Id);
                    var id = $"{{DEP-{_next++:X4}}}";
                    var record = new DeploymentRecord(DeploymentId: id, Type: change.Object!.Type, ObjectId: change.Object.Id, ObjectName: change.Object.Name,
                        CollectionId: change.Collection!.CollectionId, CollectionName: change.Collection.Name, Purpose: change.Options!.Purpose,
                        AvailableTime: change.Options.AvailableTime, Deadline: change.Options.Deadline, Created: new DateTime(2026, 10, 20, 9, 0, 0), CreatedBy: @"LAB\admin");
                    Deployments.Add(record);
                    // The flags of the create travel into its undo, so a test proves the writer clears them.
                    var undo = new DeploymentChange(Kind: DeploymentChangeKind.RemoveDeployment, Object: change.Object, Collection: change.Collection, Options: null,
                        DpGroups: [], CreateNowContentDistributing: change.CreateNowContentDistributing, RequiredConfirmed: change.RequiredConfirmed,
                        BackdatedAcknowledged: change.BackdatedAcknowledged, Existing: record, Checks: [], Impact: [], CreatedId: id);
                    return new DeploymentOutcome(Change: change with { CreatedId = id }, Succeeded: failure is null, Message: failure ?? $"Created deployment {id}.",
                        UndoChange: undo, Steps: ["Create: done.", "Read back: equal."]);
                }
                case DeploymentChangeKind.Distribute:
                    Distributed.Add(change.Object!.Id);
                    return new DeploymentOutcome(Change: change, Succeeded: true, Message: $"Requested the content on {change.DpGroups.Count} groups.", UndoChange: null, Steps: []);
                default:
                    var removed = Deployments.RemoveAll(d => d.DeploymentId == change.Existing!.DeploymentId);
                    return removed > 0
                        ? new DeploymentOutcome(Change: change, Succeeded: true, Message: "Removed the deployment. It is gone.", UndoChange: null, Steps: [])
                        : new DeploymentOutcome(Change: change, Succeeded: false, Message: "The deployment no longer exists.", UndoChange: null, Steps: []);
            }
        }
    }
}

internal sealed class FakeAuditLog : IDeploymentAuditLog
{
    public List<DeploymentAuditRecord> Records { get; } = [];
    public AuditProbe NextProbe { get; set; } = new(true, "");
    public int Probes;
    public int Reads;
    public string Path { get; set; } = @"C:\Data\Deployments\deployment-audit.jsonl";
    public Exception? ReadFailure;

    public AuditProbe Probe()
    {
        Interlocked.Increment(ref Probes);
        return NextProbe;
    }

    public AuditWrite Append(DeploymentAuditRecord r)
    {
        lock (Records) Records.Add(r);
        return new AuditWrite(true, "");
    }

    public IReadOnlyList<DeploymentAuditRecord> Read(int max)
    {
        Interlocked.Increment(ref Reads);
        if (ReadFailure is { } failure) throw failure;
        lock (Records) return Records.TakeLast(max).ToList();
    }
}

internal sealed class FakeDeploymentTemplates : IDeploymentTemplateStore
{
    public List<DeploymentTemplate> Templates { get; } = [];
    public string? Message { get; set; }
    public Exception? SaveFailure;

    public IReadOnlyList<DeploymentTemplate> All()
    {
        lock (Templates) return Templates.ToList();
    }

    public void Save(DeploymentTemplate t)
    {
        if (SaveFailure is { } failure) throw failure;
        lock (Templates)
        {
            if (Templates.Any(x => x.IsSeeded && x.Id == t.Id)) throw new DeploymentServiceException("A shipped template has this id.");
            Templates.RemoveAll(x => x.Id == t.Id);
            Templates.Add(t);
        }
    }

    public void Delete(string id)
    {
        lock (Templates) Templates.RemoveAll(x => x.Id == id && !x.IsSeeded);
    }
}

internal sealed class FakeDeploymentReports : IDeploymentReportWriter
{
    public List<IReadOnlyList<DeploymentAuditRecord>> Written { get; } = [];
    public Exception? Failure;

    public string WriteCsv(IReadOnlyList<DeploymentAuditRecord> rows) => Write(rows, "csv");
    public string WriteHtml(IReadOnlyList<DeploymentAuditRecord> rows) => Write(rows, "html");

    private string Write(IReadOnlyList<DeploymentAuditRecord> rows, string extension)
    {
        if (Failure is { } failure) throw failure;
        lock (Written) Written.Add(rows);
        return $@"C:\Data\Deployments\Reports\deployments.{extension}";
    }
}

internal sealed class FakeDeploymentDialogs : IDeploymentHelperDialogs
{
    public Action<TextPromptViewModel>? Prompt;
    public Action<DeploymentTemplateEditorViewModel>? Editor;
    public bool ConfirmDelete = true;
    public List<string> OpenedFolders { get; } = [];
    public int Prompts;
    public int Editors;

    public void ShowTextPrompt(TextPromptViewModel prompt)
    {
        Prompts++;
        Prompt?.Invoke(prompt);
    }

    public void ShowTemplateEditor(DeploymentTemplateEditorViewModel editor)
    {
        Editors++;
        Editor?.Invoke(editor);
    }

    public Task<bool> ConfirmDeleteTemplateAsync(string name) => Task.FromResult(ConfirmDelete);

    public void OpenFolder(string folder) => OpenedFolders.Add(folder);
}

internal sealed class DeploymentPageHarness
{
    public static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    public FakeDeploymentService Service { get; } = new();
    public FakeCollectionService Collections { get; } = new();
    public FakeAuditLog Audit { get; } = new();
    public FakeDeploymentTemplates Templates { get; } = new();
    public FakeDeploymentReports Reports { get; } = new();
    public FakeDeploymentDialogs Dialogs { get; } = new();
    public ScriptedPlanDialogs Plans { get; } = new();
    public RecordingSiteHistory History { get; } = new();
    public RecordingMaintenanceLog Log { get; } = new();
    public FixedClock Clock { get; } = new(Now);
    public AppSettings Settings { get; } = new() { SiteCode = "LAB", ProviderMachine = "cm01.lab.local" };
    public DeploymentHelperSettings? Saved { get; set; }
    public Action<DeploymentHelperSettings>? BeforeSave;
    public int PreferenceLoads;
    public int PreferenceSaves;
    public int RingStarts;
    public string? RingProblem;

    public DeployableObject SevenZip { get; }
    public DeployableObject Notepad { get; }
    public DeployableObject Office { get; }
    public DeployableObject Win11 { get; }
    public DeployableObject Updates { get; }

    public DeploymentPageHarness()
    {
        SevenZip = Service.Add(DeploymentObjectType.Application, "16777300", "7-Zip", "26.04", packaged: true);
        Notepad = Service.Add(DeploymentObjectType.Application, "16777301", "Notepad++", "8.9.8", distributed: false);
        Office = Service.Add(DeploymentObjectType.Package, "LAB00020", "Office Tools", "16.0");
        Service.Programs["LAB00020"] = ["Install", "Uninstall"];
        Win11 = Service.Add(DeploymentObjectType.TaskSequence, "LAB00030", "Windows 11 Upgrade", "");
        Updates = Service.Add(DeploymentObjectType.SoftwareUpdateGroup, "16777400", "2026-10 Cumulative Updates", "");
        Service.Groups.Add(new DpGroupInfo(Name: "All DPs", Id: "{GRP-1}", Status: "NotTargeted", Targeted: 0, Success: 0, InProgress: 0, Error: 0));
        Service.Groups.Add(new DpGroupInfo(Name: "Branch DPs", Id: "{GRP-2}", Status: "InProgress", Targeted: 3, Success: 1, InProgress: 2, Error: 0));

        Collections.Add("SMS00001", "All Systems", members: 5);
        Collections.Add("LAB00010", "Pilot Workstations", @"Deploy\Pilot", members: 3);
        Collections.Add("LAB00011", "Server Group A", @"Deploy\Servers", members: 4);

        Templates.Templates.Add(new DeploymentTemplate(Id: "workstation-pilot", Name: "Workstation Pilot", Purpose: "Available", UserNotification: "DisplaySoftwareCenterOnly",
            UtcBasis: false, OverrideServiceWindow: false, RebootOutsideServiceWindow: false, AllowMetered: false, AllowBoundaryFallback: true, AllowMicrosoftUpdate: false,
            PostRebootScan: true, DeadlineOffsetHours: 0, IsSeeded: true));
        Templates.Templates.Add(new DeploymentTemplate(Id: "workstation-production", Name: "Workstation Production", Purpose: "Required", UserNotification: "DisplayAll",
            UtcBasis: false, OverrideServiceWindow: false, RebootOutsideServiceWindow: true, AllowMetered: true, AllowBoundaryFallback: true, AllowMicrosoftUpdate: false,
            PostRebootScan: true, DeadlineOffsetHours: 168, IsSeeded: true));
        Templates.Templates.Add(new DeploymentTemplate(Id: "lab-nightly", Name: "Lab Nightly", Purpose: "Required", UserNotification: "HideAll",
            UtcBasis: true, OverrideServiceWindow: true, RebootOutsideServiceWindow: false, AllowMetered: false, AllowBoundaryFallback: false, AllowMicrosoftUpdate: false,
            PostRebootScan: false, DeadlineOffsetHours: 24, IsSeeded: false));

        Service.InConfirmedRun = () => Plans.InRun;
    }

    public DeploymentWriter Writer() => new(Service, History, Log, () => "LAB");

    public DeploymentPageServices Services() => new(Service, Collections, Audit, Templates, Reports, Dialogs, Plans, Writer(), () => Settings,
        () =>
        {
            Interlocked.Increment(ref PreferenceLoads);
            return Saved;
        },
        s =>
        {
            BeforeSave?.Invoke(s);
            Interlocked.Increment(ref PreferenceSaves);
            Saved = s;
        },
        () =>
        {
            RingStarts++;
            return Task.FromResult(RingProblem);
        },
        Log, Clock);

    public DeploymentHelperViewModel Create() => new(Services());

    public async Task<DeploymentHelperViewModel> OpenAsync(int tab = DeploymentHelperViewModel.DeployTab)
    {
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync();
        if (viewModel.SelectedTabIndex != tab)
        {
            viewModel.SelectedTabIndex = tab;
            await viewModel.CurrentTab.EnsureLoadedAsync();
        }
        return viewModel;
    }

    /// <summary>Picks the object and collection and waits for the object's DP groups.</summary>
    public static async Task PickAsync(DeploymentFormViewModel form, DeployableObject obj, string collectionId)
    {
        if (form.Type != obj.Type)
        {
            form.TypeIndex = (int)obj.Type;
            await form.ReloadObjectsAsync();
        }
        form.SelectedObject = form.Objects.Single(o => o.Object.Id == obj.Id);
        form.SelectedCollection = form.Collections.Single(c => c.CollectionId == collectionId);
        await WaitAsync(() => !form.IsLoadingDetails && form.DpGroups.Count > 0);
        if (obj.Type == DeploymentObjectType.Package) form.SelectedProgram = form.Programs[0];
    }

    public static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met.");
            await Task.Delay(5);
        }
    }

    public DeploymentAuditRecord Record(string evt, DeploymentObjectType type, string name, string collectionId, string deploymentId, string result = "Success",
        DateTime? when = null, string purpose = "Available") =>
        new(Timestamp: when ?? new DateTime(2026, 10, 19, 8, 0, 0), User: @"LAB\admin", DeploymentType: type.ToString(), ApplicationName: name, ApplicationVersion: "1.0",
            CollectionName: collectionId == "LAB00010" ? "Pilot Workstations" : "Server Group A", CollectionID: collectionId, MemberCount: 3,
            DeployPurpose: purpose, DeployAction: "Install", DeadlineDateTime: purpose == "Required" ? new DateTime(2026, 10, 21, 8, 0, 0) : null,
            DeploymentID: deploymentId, Result: result)
        {
            Event = evt,
            ObjectId = "16777300",
            AvailableDateTime = new DateTime(2026, 10, 19, 8, 0, 0),
            DeploymentUniqueID = deploymentId,
            TimeBasedOn = "LocalTime"
        };
}
