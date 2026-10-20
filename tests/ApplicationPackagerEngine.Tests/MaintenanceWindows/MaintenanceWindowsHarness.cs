using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

internal sealed class RecordingMaintenanceLog : IAppLog
{
    private readonly List<AppLogEntry> _entries = [];
    public IReadOnlyList<AppLogEntry> Entries { get { lock (_entries) return _entries.ToArray(); } }
    public string? SessionFile => null;
    public AppLogLevel MinimumLevel { get; set; }
    public event EventHandler<AppLogEntry>? Written;
    public void RegisterSecret(string value) { }
    public IReadOnlyList<AppLogEntry> Tail() => Entries;

    public void Write(AppLogLevel level, string area, string message)
    {
        var entry = new AppLogEntry(DateTimeOffset.Now, level, area, message);
        lock (_entries) _entries.Add(entry);
        Written?.Invoke(this, entry);
    }
}

internal sealed class RecordingSiteHistory : ISiteOperationHistory
{
    private readonly List<SiteOperationEntry> _entries = [];
    public IReadOnlyList<SiteOperationEntry> Entries { get { lock (_entries) return _entries.ToArray(); } }

    public long? Record(SiteOperationEntry entry)
    {
        lock (_entries) _entries.Add(entry);
        return _entries.Count;
    }

    public IReadOnlyList<SiteOperation> Recent(string? tool, int count) => [];
}

/// <summary>Projects every kind as a fixed step from the start, enough for the page's previews.</summary>
internal sealed class FakeProjector : IScheduleProjector
{
    public IReadOnlyList<DateTime> NextOccurrences(MaintenanceSchedule schedule, DateTime from, int count)
    {
        if (UseReal) return new ScheduleProjector().NextOccurrences(schedule, from, count);
        var list = new List<DateTime>();
        var next = schedule.Start;
        for (var i = 0; i < 500 && list.Count < count; i++)
        {
            if (next >= from && (schedule.End is null || next <= schedule.End)) list.Add(next);
            if (schedule.Kind == MaintenanceScheduleKind.OneTime) break;
            next = schedule.Kind switch
            {
                MaintenanceScheduleKind.Daily => next.AddDays(schedule.Interval),
                MaintenanceScheduleKind.Weekly => next.AddDays(7 * schedule.Interval),
                _ => next.AddMonths(schedule.Interval)
            };
        }
        return list;
    }

    public Exception? DescribeFailure;

    /// <summary>True describes and projects with the real projector.</summary>
    public bool UseReal;

    public string Describe(MaintenanceSchedule schedule) => DescribeFailure is { } failure ? throw failure
        : UseReal ? new ScheduleProjector().Describe(schedule)
        : $"{schedule.Kind} at {schedule.Start:HH:mm} for {(int)schedule.Duration.TotalMinutes} minutes";

    /// <summary>A reason replaces the real rules when set; otherwise the real rules apply.</summary>
    public Func<MaintenanceSchedule, string?>? Rejects;

    public string? Validate(MaintenanceSchedule schedule) => Rejects is { } rejects ? rejects(schedule) : new ScheduleProjector().Validate(schedule);
}

/// <summary>A small ConfigMgr site: the collections and windows the fake changes as the real service would.</summary>
internal sealed class FakeMaintenanceService : IMaintenanceWindowService
{
    private int _nextId = 100;

    public List<CollectionInfo> Collections { get; } = [];
    public List<MaintenanceWindowInfo> Windows { get; } = [];
    public int ListCalls;
    public int PrepareCalls;
    public Exception? ListFailure;
    public TaskCompletionSource? ListGate;
    public TaskCompletionSource? PrepareGate;
    public List<IReadOnlyList<MaintenanceChange>> Applied { get; } = [];

    /// <summary>The window list is read first and returned only after this gate opens.</summary>
    public TaskCompletionSource? ListWindowsGate;
    public int WindowReads;

    /// <summary>An exception makes the state check of that request fail, as the service's checks do.</summary>
    public Func<MaintenanceChange, Exception?>? PrepareFails;

    /// <summary>
    /// False (the real service's behavior): a cancelled run returns the changes it did not start as
    /// skipped outcomes. True: the run throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public bool ThrowOnCancel;

    public const string CancelledSkip = MaintenanceWindowService.SkippedPrefix + "the run was cancelled before this change.";

    public static string NameTakenMessage(string collection, string name) => $"Collection {collection} already has a window named {name}.";

    /// <summary>A message makes the change fail; null lets it succeed.</summary>
    public Func<MaintenanceChange, string?>? Fail;

    /// <summary>A rename creates the new window but cannot remove the old one.</summary>
    public bool RenameRemoveFails;

    /// <summary>Set while a confirmed change plan runs; a write outside it fails the test.</summary>
    public Func<bool>? InConfirmedRun;

    /// <summary>Called after each change was reported; a test can cancel here.</summary>
    public Action<int>? AfterChange;

    public static MaintenanceSchedule Weekly(DateTime start, int hours = 4) =>
        new(MaintenanceScheduleKind.Weekly, start, TimeSpan.FromHours(hours), false, 1, DayOfWeek.Sunday, null, null, 0, null);

    public CollectionInfo AddCollection(string id, string name, string folder = "", bool builtIn = false)
    {
        var c = new CollectionInfo(id, name, folder, 3, builtIn);
        Collections.Add(c);
        return c;
    }

    public MaintenanceWindowInfo AddWindow(CollectionInfo c, string name, bool enabled = true, string type = "Any", MaintenanceSchedule? schedule = null)
    {
        var s = schedule ?? Weekly(new DateTime(2026, 10, 25, 2, 0, 0));
        var w = new MaintenanceWindowInfo(c.CollectionId, c.Name, name, "{" + (_nextId++) + "}", type, enabled, s, $"Weekly at {s.Start:HH:mm}", s.Start, "");
        Windows.Add(w);
        return w;
    }

    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ListCalls);
        if (ListGate is { } gate) await gate.Task.WaitAsync(ct);
        if (ListFailure is { } failure) throw failure;
        lock (Windows) return Collections.ToList();
    }

    public async Task<IReadOnlyList<MaintenanceWindowInfo>> ListWindowsAsync(CancellationToken ct)
    {
        List<MaintenanceWindowInfo> read;
        lock (Windows) read = Windows.ToList();
        Interlocked.Increment(ref WindowReads);
        if (ListWindowsGate is { } gate) await gate.Task.WaitAsync(ct);
        return read;
    }

    private MaintenanceWindowInfo? Find(string collectionId, string name)
    {
        lock (Windows) return Windows.FirstOrDefault(w => w.CollectionId == collectionId && string.Equals(w.WindowName, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<MaintenanceChange> PrepareAsync(MaintenanceChange request, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCalls);
        if (PrepareGate is { } gate) await gate.Task;
        if (PrepareFails?.Invoke(request) is { } failure) throw failure;
        var before = Find(request.CollectionId, request.WindowName);
        if (request.Kind == ChangeKind.Create && before is not null)
            throw new MaintenanceWindowException(NameTakenMessage(request.CollectionName, request.WindowName));
        return request with { Before = before };
    }

    public async Task<IReadOnlyList<MaintenanceOutcome>> ApplyAsync(IReadOnlyList<MaintenanceChange> changes, IProgress<MaintenanceOutcome> progress, CancellationToken ct)
    {
        if (InConfirmedRun is { } check) Assert.True(check(), "A write ran outside a confirmed change plan.");
        lock (Applied) Applied.Add(changes.ToList());
        var outcomes = new List<MaintenanceOutcome>();
        for (var i = 0; i < changes.Count; i++)
        {
            if (ThrowOnCancel) ct.ThrowIfCancellationRequested();
            var outcome = ct.IsCancellationRequested ? new MaintenanceOutcome(changes[i], false, CancelledSkip, null) : Apply(changes[i]);
            outcomes.Add(outcome);
            progress.Report(outcome);
            AfterChange?.Invoke(i);
            await Task.Yield();
        }
        return outcomes;
    }

    private MaintenanceOutcome Apply(MaintenanceChange change)
    {
        if (Fail?.Invoke(change) is { } message) return new MaintenanceOutcome(change, false, message, null);
        lock (Windows)
        {
            var before = Find(change.CollectionId, change.WindowName);
            switch (change.Kind)
            {
                case ChangeKind.Create:
                case ChangeKind.ApplyTemplate:
                {
                    var created = change.After! with { ServiceWindowId = "{" + (_nextId++) + "}" };
                    Windows.Add(created);
                    return new MaintenanceOutcome(change, true, "Created.",
                        new MaintenanceChange(ChangeKind.Remove, change.CollectionId, change.CollectionName, created.WindowName, null, created, null));
                }
                case ChangeKind.Update:
                    Windows.Remove(before!);
                    Windows.Add(change.After! with { ServiceWindowId = before!.ServiceWindowId });
                    return new MaintenanceOutcome(change, true, "Updated.",
                        new MaintenanceChange(ChangeKind.Update, change.CollectionId, change.CollectionName, change.WindowName, null, change.After, before));
                case ChangeKind.Rename:
                {
                    var created = change.After! with { WindowName = change.NewWindowName!, ServiceWindowId = "{" + (_nextId++) + "}" };
                    Windows.Add(created);
                    if (RenameRemoveFails)
                        return new MaintenanceOutcome(change, false, $"Created {created.WindowName}, but {change.WindowName} could not be removed. Both windows exist.",
                            new MaintenanceChange(ChangeKind.Remove, change.CollectionId, change.CollectionName, created.WindowName, null, created, null));
                    Windows.Remove(before!);
                    return new MaintenanceOutcome(change, true, "Renamed.",
                        new MaintenanceChange(ChangeKind.Rename, change.CollectionId, change.CollectionName, created.WindowName, before!.WindowName, created, before));
                }
                case ChangeKind.SetEnabled:
                    Windows.Remove(before!);
                    Windows.Add(before! with { Enabled = change.After!.Enabled });
                    return new MaintenanceOutcome(change, true, change.After.Enabled ? "Enabled." : "Disabled.",
                        new MaintenanceChange(ChangeKind.SetEnabled, change.CollectionId, change.CollectionName, change.WindowName, null, change.After, before));
                default:
                    Windows.Remove(before!);
                    return new MaintenanceOutcome(change, true, "Removed.",
                        new MaintenanceChange(ChangeKind.Create, change.CollectionId, change.CollectionName, change.WindowName, null, null, before));
            }
        }
    }
}

internal sealed class FakeTemplateStore : IMaintenanceTemplateStore
{
    public List<MaintenanceTemplate> Templates { get; } = [];
    public int Saves;
    public int Deletes;
    public Exception? SaveFailure;
    public Exception? DeleteFailure;

    public IReadOnlyList<MaintenanceTemplate> All() { lock (Templates) return Templates.ToList(); }

    public void Save(MaintenanceTemplate t)
    {
        if (SaveFailure is { } failure) throw failure;
        lock (Templates)
        {
            Saves++;
            Templates.RemoveAll(x => x.Id == t.Id);
            Templates.Add(t);
        }
    }

    public void Delete(string id)
    {
        if (DeleteFailure is { } failure) throw failure;
        lock (Templates)
        {
            Deletes++;
            Templates.RemoveAll(x => x.Id == id && !x.IsShipped);
        }
    }
}

internal sealed class FakeReportWriter : IMaintenanceReportWriter
{
    public List<IReadOnlyList<MaintenanceWindowInfo>> Written { get; } = [];
    public Exception? Failure;

    public string WriteCsv(IReadOnlyList<MaintenanceWindowInfo> rows)
    {
        if (Failure is { } failure) throw failure;
        Written.Add(rows);
        return @"C:\data\MaintenanceWindows\Reports\windows.csv";
    }

    public string WriteHtml(IReadOnlyList<MaintenanceWindowInfo> rows)
    {
        if (Failure is { } failure) throw failure;
        Written.Add(rows);
        return @"C:\data\MaintenanceWindows\Reports\windows.html";
    }
}

internal sealed class FakeMaintenancePrompts : IMaintenanceWindowsPrompts
{
    public Action<MaintenanceWindowEditorViewModel>? Editor;
    public Action<MaintenanceApplyTemplateViewModel>? ApplyTemplate;
    public bool ConfirmDelete = true;
    public List<string> OpenedFolders { get; } = [];
    public int EditorShows;

    public void ShowEditor(MaintenanceWindowEditorViewModel editor)
    {
        EditorShows++;
        Editor?.Invoke(editor);
    }

    public void ShowApplyTemplate(MaintenanceApplyTemplateViewModel dialog) => ApplyTemplate?.Invoke(dialog);

    public Task<bool> ConfirmDeleteTemplateAsync(string name) => Task.FromResult(ConfirmDelete);

    public void OpenFolder(string folder) => OpenedFolders.Add(folder);

    public static void Save(MaintenanceWindowEditorViewModel editor) => ScriptedPlanDialogs.Wait(editor.SaveCommand.ExecuteAsync(null));

    public static void Apply(MaintenanceApplyTemplateViewModel dialog) => ScriptedPlanDialogs.Wait(dialog.ApplyCommand.ExecuteAsync(null));
}

/// <summary>The change plan window without a window: the real view model confirms, runs and undoes.</summary>
internal sealed class ScriptedPlanDialogs : IChangePlanDialogs
{
    private int _running;
    public List<ChangePlan> Shown { get; } = [];
    public bool Confirm = true;
    public bool AcknowledgeDestructive = true;
    public bool AcknowledgeExtra = true;
    public bool ChooseOption;
    public bool RunUndo;
    public ChangePlanOutcome? LastOutcome;
    public ChangePlanViewModel? LastViewModel;

    public bool InRun => Volatile.Read(ref _running) > 0;

    /// <summary>Waits for the task as a modal window would: on a dispatcher thread the queue keeps running.</summary>
    public static void Wait(Task task)
    {
        if (SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext) StaTestHelper.PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
    {
        Shown.Add(plan);
        if (!Confirm) return ChangePlanOutcome.NotConfirmed;
        var viewModel = new ChangePlanViewModel(plan, Wrap(run), undo is null ? null : Wrap(undo));
        LastViewModel = viewModel;
        viewModel.DestructiveAcknowledged = AcknowledgeDestructive;
        viewModel.ExtraAcknowledged = AcknowledgeExtra;
        viewModel.OptionChosen = ChooseOption;
        Wait(viewModel.ConfirmCommand.ExecuteAsync(null));
        if (RunUndo && viewModel.UndoCommand.CanExecute(null)) Wait(viewModel.UndoCommand.ExecuteAsync(null));
        LastOutcome = viewModel.Outcome;
        return viewModel.Outcome;
    }

    private ChangePlanRun Wrap(ChangePlanRun run) => async (confirmed, progress, ct) =>
    {
        Interlocked.Increment(ref _running);
        try { return await run(confirmed, progress, ct); }
        finally { Interlocked.Decrement(ref _running); }
    };
}

internal sealed class MaintenanceWindowsHarness
{
    public static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0);

    public FakeMaintenanceService Service { get; } = new();
    public FakeProjector Projector { get; } = new();
    public FakeTemplateStore Store { get; } = new();
    public FakeReportWriter Reports { get; } = new();
    public FakeMaintenancePrompts Prompts { get; } = new();
    public ScriptedPlanDialogs Plans { get; } = new();
    public RecordingSiteHistory History { get; } = new();
    public RecordingMaintenanceLog Log { get; } = new();
    public AppSettings Settings { get; } = new() { SiteCode = "LAB", ProviderMachine = "cm01.lab.local" };
    public MaintenanceWindowsSettings? Saved { get; set; }

    /// <summary>Runs on the saving thread before the choices are stored; a test can hold a save here.</summary>
    public Action<MaintenanceWindowsSettings>? BeforeSave;

    public int PreferenceLoads;
    public int PreferenceSaves;
    public int SettingsLoads;

    public CollectionInfo Servers { get; }
    public CollectionInfo Workstations { get; }
    public CollectionInfo Kiosks { get; }
    public CollectionInfo AllSystems { get; }

    public MaintenanceWindowsHarness()
    {
        Servers = Service.AddCollection("LAB00010", "Servers", @"Patching\Servers");
        Workstations = Service.AddCollection("LAB00011", "Workstations", @"Patching\Clients");
        Kiosks = Service.AddCollection("LAB00012", "Kiosks", "");
        AllSystems = Service.AddCollection("SMS00001", "All Systems", "", builtIn: true);
        Service.AddWindow(Servers, "Patch Tuesday", type: "SoftwareUpdates");
        Service.AddWindow(Servers, "Weekend", enabled: false);
        Service.AddWindow(Workstations, "Nightly");
        Store.Templates.Add(new MaintenanceTemplate("weekly-sunday-2am", "Weekly Sunday 2 AM", "Every Sunday.",
            FakeMaintenanceService.Weekly(new DateTime(2026, 10, 25, 2, 0, 0)), "Any", true));
        Store.Templates.Add(new MaintenanceTemplate("user-1", "Nightly", "Every night.",
            new MaintenanceSchedule(MaintenanceScheduleKind.Daily, new DateTime(2026, 10, 20, 1, 0, 0), TimeSpan.FromHours(2), false, 1, null, null, null, 0, null), "Any", false));
        Service.InConfirmedRun = () => Plans.InRun;
    }

    public MaintenanceWindowWriter Writer() => new(Service, History, Log, () => "LAB");

    public MaintenanceWindowsViewModel Create() => new(new MaintenanceWindowsPageServices(
        Service, Projector, Store, Reports, Prompts, Plans, Writer(),
        () => { Interlocked.Increment(ref SettingsLoads); return Settings; },
        () => { Interlocked.Increment(ref PreferenceLoads); return Saved; },
        s =>
        {
            BeforeSave?.Invoke(s);
            Interlocked.Increment(ref PreferenceSaves);
            Saved = s;
        },
        Log,
        () => Now));

    public async Task<MaintenanceWindowsViewModel> OpenAsync()
    {
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync();
        return viewModel;
    }
}
