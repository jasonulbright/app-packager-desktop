using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using K = ApplicationPackagerEngine.Services.MaintenanceScheduleKind;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>Writes that fail after ConfigMgr made the change, and writes against a window whose description changed after the plan.</summary>
public sealed class MaintenanceWindowPartialWriteTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 1, 37, 0);
    private const string Custom = "Owned by the patch team; call the service desk first";
    private const string WeeklyToken = "0043BE00201F2000";
    private const string Stale = "changed after the plan was shown";

    private readonly FakeMaintenanceSession _site = new();

    public MaintenanceWindowPartialWriteTests()
    {
        _site.Collections.Add(new FakeMaintenanceSession.Collection("MCM00015", "All Workstations", "", 2));
    }

    private MaintenanceWindowService Service() => new(_site, new ScheduleProjector(TimeZoneInfo.Utc), new RecordingLog(), () => Now);

    private static MaintenanceSchedule Sunday() =>
        new(K.Weekly, new DateTime(2026, 11, 3, 2, 0, 0), TimeSpan.FromHours(4), false, 1, DayOfWeek.Sunday, null, null, 0, null);

    private static MaintenanceWindowInfo New(string name) => new("MCM00015", "", name, "", "Any", true, Sunday(), "", null, "");

    private async Task<MaintenanceOutcome> Apply(MaintenanceChange change) =>
        Assert.Single(await Service().ApplyAsync([change], new Progress<MaintenanceOutcome>(_ => { }), CancellationToken.None));

    private async Task<MaintenanceWindowInfo> Window(string name) =>
        (await Service().ListWindowsAsync(CancellationToken.None)).Single(w => w.WindowName == name);

    private Task<MaintenanceChange> Prepare(ChangeKind kind, string name, MaintenanceWindowInfo? before, MaintenanceWindowInfo? after = null, string? newName = null) =>
        Service().PrepareAsync(new MaintenanceChange(kind, "MCM00015", "", name, newName, before, after), CancellationToken.None);

    private void FailAfter(string operation, ConfigMgrFailure failure = ConfigMgrFailure.ProcessDied) =>
        _site.AfterWrite = call => Equals(call.Parameters["Operation"], operation)
            ? new ConfigMgrSessionException(failure, "The session ended.")
            : null;

    [WindowFact]
    public async Task ARenameWhoseCreateSucceededBeforeAnAccessDeniedKeepsTheUndoOfTheNewWindow()
    {
        _site.Add("MCM00015", "Old", WeeklyToken, description: Custom);
        var prepared = await Prepare(ChangeKind.Rename, "Old", await Window("Old"), newName: "New");
        FailAfter("Create", ConfigMgrFailure.AccessDenied);

        var outcome = await Apply(prepared);

        Assert.False(outcome.Succeeded);
        Assert.Contains("Both windows exist now", outcome.Message);
        Assert.Equal(["New", "Old"], _site.Windows.Select(w => w.Name).Order().ToArray());
        var undo = outcome.UndoChange!;
        Assert.Equal(ChangeKind.Remove, undo.Kind);
        Assert.Equal(_site.Windows.Single(w => w.Name == "New").Id, undo.Before!.ServiceWindowId);

        _site.AfterWrite = null;
        Assert.True((await Apply(undo)).Succeeded);
        Assert.Equal(Custom, Assert.Single(_site.Windows).Description);
    }

    [WindowFact]
    public async Task ACreateThatFailsAfterTheWindowExistsReportsTheWindowWithItsUndo()
    {
        FailAfter("Create");
        var outcome = await Apply(new MaintenanceChange(ChangeKind.Create, "MCM00015", "All Workstations", "Nightly", null, null, New("Nightly")));

        Assert.False(outcome.Succeeded);
        Assert.Equal(ChangeKind.Remove, outcome.UndoChange!.Kind);
        _site.AfterWrite = null;
        Assert.True((await Apply(outcome.UndoChange)).Succeeded);
        Assert.Empty(_site.Windows);
    }

    [WindowFact]
    public async Task AnUpdateThatFailsAfterTheChangeKeepsTheUndoToTheOldValues()
    {
        _site.Add("MCM00015", "Patch", WeeklyToken);
        var prepared = await Prepare(ChangeKind.Update, "Patch", await Window("Patch"), New("Patch"));
        FailAfter("Update");

        var outcome = await Apply(prepared);

        Assert.False(outcome.Succeeded);
        Assert.NotEqual(WeeklyToken, _site.Windows.Single().Schedules);
        Assert.Equal(ChangeKind.Update, outcome.UndoChange!.Kind);
        _site.AfterWrite = null;
        Assert.True((await Apply(outcome.UndoChange)).Succeeded);
        Assert.Equal(WeeklyToken, _site.Windows.Single().Schedules);
    }

    [WindowFact]
    public async Task AToggleThatFailsAfterTheChangeKeepsItsUndo()
    {
        _site.Add("MCM00015", "Patch", WeeklyToken);
        var prepared = await Prepare(ChangeKind.SetEnabled, "Patch", await Window("Patch"));
        FailAfter("SetEnabled");

        var outcome = await Apply(prepared);

        Assert.False(outcome.Succeeded);
        Assert.False(_site.Windows.Single().Enabled);
        _site.AfterWrite = null;
        Assert.True((await Apply(outcome.UndoChange!)).Succeeded);
        Assert.True(_site.Windows.Single().Enabled);
    }

    [WindowFact]
    public async Task ARemoveThatFailsAfterTheWindowIsGoneKeepsTheUndoThatCreatesItAgain()
    {
        _site.Add("MCM00015", "Patch", WeeklyToken, description: Custom);
        var prepared = await Prepare(ChangeKind.Remove, "Patch", await Window("Patch"));
        FailAfter("Remove", ConfigMgrFailure.Timeout);

        var outcome = await Apply(prepared);

        Assert.False(outcome.Succeeded);
        Assert.Empty(_site.Windows);
        Assert.Equal(ChangeKind.Create, outcome.UndoChange!.Kind);
        _site.AfterWrite = null;
        Assert.True((await Apply(outcome.UndoChange)).Succeeded);
        Assert.Equal(Custom, _site.Windows.Single(w => w.Name == "Patch").Description);
    }

    [WindowFact]
    public async Task AFailureBeforeAnyChangeHasNoUndo()
    {
        _site.Add("MCM00015", "Patch", WeeklyToken);
        var prepared = await Prepare(ChangeKind.Update, "Patch", await Window("Patch"), New("Patch"));
        _site.Throw = call => call.Script == "WriteMaintenanceWindow" ? new ConfigMgrSessionException(ConfigMgrFailure.ProcessDied, "The session ended.") : null;

        var outcome = await Apply(prepared);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
        Assert.Equal(WeeklyToken, _site.Windows.Single().Schedules);
    }

    [WindowFact]
    public async Task AnAccessDeniedPartialWithNoStoredChangeKeepsThePermissionHint()
    {
        var window = _site.Add("MCM00015", "Patch", WeeklyToken);
        var prepared = await Prepare(ChangeKind.Update, "Patch", await Window("Patch"), New("Patch"));
        _site.Answer = call => Equals(call.Parameters["Operation"], "Update") ? ("partial", "Access is denied.") : null;
        _site.AnswerAccessDenied = true;

        var outcome = await Apply(prepared);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
        Assert.Contains(MaintenanceWindowService.AccessHint, outcome.Message);
        Assert.Equal(WeeklyToken, window.Schedules);
    }

    [WindowTheory]
    [InlineData(ChangeKind.Update)]
    [InlineData(ChangeKind.Rename)]
    [InlineData(ChangeKind.Remove)]
    public async Task AWindowWhoseDescriptionChangedAfterThePlanIsNotWritten(ChangeKind kind)
    {
        var window = _site.Add("MCM00015", "Patch", WeeklyToken, description: Custom);
        var prepared = await Prepare(kind, "Patch", await Window("Patch"), kind == ChangeKind.Update ? New("Patch") : null, kind == ChangeKind.Rename ? "Renamed" : null);
        window.Description = "Changed by another administrator";

        var outcome = await Apply(prepared);

        Assert.False(outcome.Succeeded);
        Assert.Contains(Stale, outcome.Message);
        Assert.Null(outcome.UndoChange);
        var left = Assert.Single(_site.Windows);
        Assert.Equal(("Patch", WeeklyToken, "Changed by another administrator"), (left.Name, left.Schedules, left.Description));
    }
}
