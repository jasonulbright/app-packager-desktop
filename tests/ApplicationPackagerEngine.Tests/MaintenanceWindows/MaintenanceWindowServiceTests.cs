using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using K = ApplicationPackagerEngine.Services.MaintenanceScheduleKind;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

public sealed class MaintenanceWindowServiceTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly DateTime Now = new(2026, 10, 19, 1, 37, 0);
    private const string RawError = "System.Management.ManagementException: Generic failure at Microsoft.ConfigurationManagement.Foo()";

    private readonly FakeMaintenanceSession _site = new();
    private readonly RecordingLog _log = new();

    public MaintenanceWindowServiceTests()
    {
        _site.Collections.Add(new FakeMaintenanceSession.Collection("MCM00014", "HomeLab - Test Deployments", "/Lab/Test", 1));
        _site.Collections.Add(new FakeMaintenanceSession.Collection("MCM00015", "All Workstations", "", 2));
        _site.Collections.Add(new FakeMaintenanceSession.Collection("MCM00016", "All Servers"));
        _site.Collections.Add(new FakeMaintenanceSession.Collection("SMS00001", "All Systems", "", 8));
    }

    private MaintenanceWindowService Service() => new(_site, new ScheduleProjector(Eastern), _log, () => Now);

    private static MaintenanceSchedule Weekly(DayOfWeek day = DayOfWeek.Saturday, int hour = 2, int minutes = 240) =>
        new(K.Weekly, new DateTime(2026, 11, 3, hour, 0, 0), TimeSpan.FromMinutes(minutes), false, 1, day, null, null, 0, null);

    private static MaintenanceSchedule PatchTuesday(int offset) =>
        new(K.PatchTuesday, new DateTime(2026, 11, 3, 22, 30, 0), TimeSpan.FromMinutes(150), false, 1, null, null, null, offset, null);

    private static MaintenanceWindowInfo New(string collectionId, string name, MaintenanceSchedule schedule, string type = "Any", bool enabled = true) =>
        new(collectionId, "", name, "", type, enabled, schedule, "", null, "");

    private static MaintenanceChange Change(ChangeKind kind, string collectionId, string name, string? newName = null,
        MaintenanceWindowInfo? before = null, MaintenanceWindowInfo? after = null) =>
        new(kind, collectionId, "", name, newName, before, after);

    private async Task<IReadOnlyList<MaintenanceOutcome>> Apply(params MaintenanceChange[] changes) =>
        await Service().ApplyAsync(changes, new Progress<MaintenanceOutcome>(_ => { }), CancellationToken.None);

    private async Task<MaintenanceWindowInfo> Window(string collectionId, string name) =>
        (await Service().ListWindowsAsync(CancellationToken.None)).Single(w => w.CollectionId == collectionId && w.WindowName == name);

    private static void NoRawText(MaintenanceOutcome outcome)
    {
        Assert.DoesNotContain("Exception", outcome.Message);
        Assert.DoesNotContain("Microsoft.ConfigurationManagement", outcome.Message);
    }

    [Fact]
    public async Task ListWindowsDecodesTheScheduleAndProjectsTheNextOccurrence()
    {
        _site.Add("MCM00014", "Daily Maintenance Window", "008C8E0030100008");
        _site.Add("MCM00015", "Patch Saturday 02:00-06:00", "00CF8E00201F2000", type: 4, enabled: false);

        var windows = await Service().ListWindowsAsync(CancellationToken.None);

        var daily = windows.Single(w => w.WindowName == "Daily Maintenance Window");
        Assert.Equal("HomeLab - Test Deployments", daily.CollectionName);
        Assert.Equal("Any", daily.WindowType);
        Assert.Equal(K.Daily, daily.Schedule.Kind);
        Assert.Equal("Occurs every 1 days effective 8/12/2026 4:00 AM", daily.ScheduleText);
        Assert.Equal(new DateTime(2026, 10, 19, 4, 0, 0), daily.NextOccurrence);
        Assert.Equal("Occurs every 1 days effective 8/12/2026 4:00 AM", daily.Description);
        Assert.Equal(daily.Description, daily.Comment);

        var weekly = windows.Single(w => w.WindowName.StartsWith("Patch Saturday"));
        Assert.Equal("SoftwareUpdates", weekly.WindowType);
        Assert.False(weekly.Enabled);
        Assert.Equal(new DateTime(2026, 10, 24, 6, 0, 0), weekly.NextOccurrence);
    }

    [Fact]
    public async Task AWindowWithAScheduleThePageCannotHoldShowsConfigMgrsTextAndNoNextOccurrence()
    {
        var window = _site.Add("MCM00014", "Every 8 hours", "7AC3BE0008100800");
        window.Description = "Occurs every 8 hours effective 11/3/2026 10:30 PM";
        var info = await Window("MCM00014", "Every 8 hours");
        Assert.Equal(TimeSpan.Zero, info.Schedule.Duration);
        Assert.Equal("Occurs every 8 hours effective 11/3/2026 10:30 PM", info.ScheduleText);
        Assert.Null(info.NextOccurrence);

        var remove = await Assert.ThrowsAsync<MaintenanceWindowException>(() =>
            Service().PrepareAsync(Change(ChangeKind.Remove, "MCM00014", "Every 8 hours", before: info), CancellationToken.None));
        Assert.Contains("ConfigMgr console", remove.Message);
        var toggle = await Service().PrepareAsync(Change(ChangeKind.SetEnabled, "MCM00014", "Every 8 hours", before: info), CancellationToken.None);
        var outcome = Assert.Single(await Apply(toggle));
        Assert.True(outcome.Succeeded);
        Assert.False(window.Enabled);
    }

    [Fact]
    public async Task ListCollectionsReadsCollectionsOnlyAndFlagsBuiltInOnes()
    {
        var collections = await Service().ListCollectionsAsync(CancellationToken.None);
        Assert.Equal(4, collections.Count);
        Assert.Equal(new CollectionInfo("MCM00014", "HomeLab - Test Deployments", "Lab\\Test", 1, false), collections[0]);
        Assert.True(collections.Single(c => c.CollectionId == "SMS00001").IsBuiltIn);
        Assert.Equal(true, _site.Calls.Single().Parameters["CollectionsOnly"]);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("/Servers", "Servers")]
    [InlineData("/Servers/Production", "Servers\\Production")]
    public void TheFolderPathIsShownWithBackslashes(string objectPath, string expected)
    {
        Assert.Equal(expected, MaintenanceWindowService.FolderPath(objectPath));
    }

    [Fact]
    public async Task CreateMakesTheWindowAndItsUndoRemovesExactlyThatWindow()
    {
        var request = Change(ChangeKind.Create, "MCM00016", "Patch night", after: New("MCM00016", "Patch night", PatchTuesday(3), "SoftwareUpdates"));
        var prepared = await Service().PrepareAsync(request, CancellationToken.None);
        Assert.Null(prepared.Before);
        Assert.Equal("All Servers", prepared.CollectionName);

        var outcome = Assert.Single(await Apply(prepared));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("Created window Patch night on All Servers.", outcome.Message);
        var stored = Assert.Single(_site.Windows);
        Assert.Equal(PatchTuesday(3), ScheduleToken.Decode(stored.Schedules));
        Assert.Equal(4, stored.Type);
        var write = _site.Writes.Single();
        Assert.DoesNotContain("Patch night", write.Text);
        Assert.Equal("MonthlyByWeekday", write.Parameters["Recurrence"]);
        Assert.Equal("Second", write.Parameters["WeekOrder"]);
        Assert.Equal(3, write.Parameters["OffsetDay"]);
        Assert.Equal(2, write.Parameters["DurationHours"]);
        Assert.Equal(30, write.Parameters["DurationMinutes"]);
        Assert.Equal("2026-11-03T22:30", write.Parameters["Start"]);

        Assert.Equal(ChangeKind.Remove, outcome.UndoChange!.Kind);
        Assert.Equal(stored.Id, outcome.UndoChange.Before!.ServiceWindowId);
        var undo = Assert.Single(await Apply(outcome.UndoChange));
        Assert.True(undo.Succeeded, undo.Message);
        Assert.Empty(_site.Windows);
    }

    [Fact]
    public async Task EveryScheduleKindTravelsAsScriptParametersAndIsStoredAsRequested()
    {
        var schedules = new[]
        {
            new MaintenanceSchedule(K.OneTime, new DateTime(2026, 11, 3, 22, 30, 0), TimeSpan.FromMinutes(1), false, 1, null, null, null, 0, null),
            new MaintenanceSchedule(K.Daily, new DateTime(2026, 11, 3, 22, 30, 0), TimeSpan.FromHours(24), true, 31, null, null, null, 0, null),
            Weekly(DayOfWeek.Friday, 23, 90) with { Interval = 4 },
            new MaintenanceSchedule(K.MonthlyByDate, new DateTime(2026, 11, 3, 1, 0, 0), TimeSpan.FromMinutes(59), false, 12, null, 0, null, 0, null),
            new MaintenanceSchedule(K.MonthlyByWeekday, new DateTime(2026, 11, 3, 1, 0, 0), TimeSpan.FromMinutes(61), false, 2, DayOfWeek.Monday, null, 5, 2, null),
            PatchTuesday(-3), PatchTuesday(0), PatchTuesday(21)
        };
        var changes = schedules.Select((s, i) => Change(ChangeKind.Create, "MCM00016", $"w{i}", after: New("MCM00016", $"w{i}", s))).ToArray();

        var outcomes = await Apply(changes);

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Message));
        var listed = (await Service().ListWindowsAsync(CancellationToken.None)).ToDictionary(w => w.WindowName);
        for (var i = 0; i < schedules.Length; i++)
            Assert.Equal(schedules[i], listed[$"w{i}"].Schedule);
    }

    [Fact]
    public async Task ABuiltInCollectionIsRefusedWithoutAConfigMgrCall()
    {
        var request = Change(ChangeKind.Create, "SMS00001", "x", after: New("SMS00001", "x", Weekly()));
        var prepare = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().PrepareAsync(request, CancellationToken.None));
        Assert.Contains("built-in collection", prepare.Message);
        var outcome = Assert.Single(await Apply(request));
        Assert.False(outcome.Succeeded);
        Assert.Contains("built-in collection", outcome.Message);
        Assert.Empty(_site.Calls);
    }

    [Fact]
    public async Task PrepareReportsAMissingCollectionATakenNameAndAnInvalidSchedulePlainly()
    {
        _site.Add("MCM00016", "Existing", "00CF8E00201F2000");
        var missing = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().PrepareAsync(
            Change(ChangeKind.Create, "MCM000FF", "x", after: New("MCM000FF", "x", Weekly())), CancellationToken.None));
        Assert.Equal("The collection MCM000FF does not exist any more. Refresh the list.", missing.Message);

        var taken = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().PrepareAsync(
            Change(ChangeKind.Create, "MCM00016", "EXISTING", after: New("MCM00016", "EXISTING", Weekly())), CancellationToken.None));
        Assert.Equal("Collection All Servers already has a window named EXISTING.", taken.Message);

        var invalid = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().PrepareAsync(
            Change(ChangeKind.Create, "MCM00016", "x", after: New("MCM00016", "x", Weekly(minutes: 1500))), CancellationToken.None));
        Assert.Contains("24 hours or less", invalid.Message);

        var noName = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().PrepareAsync(
            Change(ChangeKind.Create, "MCM00016", " ", after: New("MCM00016", " ", Weekly())), CancellationToken.None));
        Assert.Equal("Enter a window name.", noName.Message);
    }

    [Fact]
    public async Task UpdateCapturesTheOldValuesAndItsUndoSetsThemBack()
    {
        var original = _site.Add("MCM00015", "Patch Saturday", "00CF8E00201F2000", type: 4);
        var before = await Window("MCM00015", "Patch Saturday");
        var prepared = await Service().PrepareAsync(
            Change(ChangeKind.Update, "MCM00015", "Patch Saturday", before: before,
                after: New("MCM00015", "Patch Saturday", Weekly(DayOfWeek.Sunday, 3, 120), "Any", enabled: false)), CancellationToken.None);
        Assert.Equal(before, prepared.Before);

        var outcome = Assert.Single(await Apply(prepared));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(1, original.Type);
        Assert.False(original.Enabled);
        Assert.Equal(Weekly(DayOfWeek.Sunday, 3, 120), ScheduleToken.Decode(original.Schedules));
        Assert.Equal(original.Id, outcome.UndoChange!.Before!.ServiceWindowId);
        Assert.Equal(before, outcome.UndoChange.After);

        Assert.True(Assert.Single(await Apply(outcome.UndoChange)).Succeeded);
        Assert.Equal("00CF8E00201F2000", original.Schedules);
        Assert.Equal(4, original.Type);
        Assert.True(original.Enabled);
    }

    [Fact]
    public async Task AWindowChangedAfterThePlanIsNotWrittenAndTheMessageSaysSo()
    {
        var window = _site.Add("MCM00015", "Patch Saturday", "00CF8E00201F2000");
        var before = await Window("MCM00015", "Patch Saturday");
        var prepared = await Service().PrepareAsync(
            Change(ChangeKind.Update, "MCM00015", "Patch Saturday", before: before, after: New("MCM00015", "Patch Saturday", Weekly(DayOfWeek.Sunday))), CancellationToken.None);
        window.Schedules = "008C8E0030100008";

        var outcome = Assert.Single(await Apply(prepared));

        Assert.False(outcome.Succeeded);
        Assert.Equal("The window Patch Saturday on All Workstations changed after the plan was shown, so it was not changed. Refresh the list and try again.", outcome.Message);
        Assert.Null(outcome.UndoChange);
        Assert.Equal("008C8E0030100008", window.Schedules);
    }

    [Fact]
    public async Task AWindowRemovedAfterThePlanGivesAPlainMessage()
    {
        var window = _site.Add("MCM00015", "Gone", "00CF8E00201F2000");
        var prepared = await Service().PrepareAsync(Change(ChangeKind.Remove, "MCM00015", "Gone", before: await Window("MCM00015", "Gone")), CancellationToken.None);
        _site.Windows.Remove(window);
        var outcome = Assert.Single(await Apply(prepared));
        Assert.Equal("The window Gone does not exist on All Workstations any more. Refresh the list.", outcome.Message);

        var prepare = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().PrepareAsync(prepared, CancellationToken.None));
        Assert.Equal("The window Gone does not exist on All Workstations any more. Refresh the list.", prepare.Message);
    }

    [Fact]
    public async Task ACollectionDeletedAfterThePlanGivesAPlainMessage()
    {
        var prepared = await Service().PrepareAsync(
            Change(ChangeKind.Create, "MCM00016", "x", after: New("MCM00016", "x", Weekly())), CancellationToken.None);
        _site.Collections.RemoveAll(c => c.Id == "MCM00016");
        var outcome = Assert.Single(await Apply(prepared));
        Assert.Equal("The collection All Servers (MCM00016) does not exist any more. Refresh the list.", outcome.Message);
    }

    [Fact]
    public async Task RenameCreatesTheNewWindowBeforeItRemovesTheOldOneAndItsUndoRestoresTheOldName()
    {
        _site.Add("MCM00015", "Old name", "00CF8E00201F2000", type: 5);
        var before = await Window("MCM00015", "Old name");
        var prepared = await Service().PrepareAsync(Change(ChangeKind.Rename, "MCM00015", "Old name", "New name", before), CancellationToken.None);

        var outcome = Assert.Single(await Apply(prepared));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(["Create", "Remove"], _site.Writes.Select(w => (string)w.Parameters["Operation"]!).ToArray());
        var renamed = Assert.Single(_site.Windows);
        Assert.Equal("New name", renamed.Name);
        Assert.Equal("00CF8E00201F2000", renamed.Schedules);
        Assert.Equal(5, renamed.Type);
        Assert.NotEqual(before.ServiceWindowId, renamed.Id);

        Assert.Equal(ChangeKind.Rename, outcome.UndoChange!.Kind);
        Assert.True(Assert.Single(await Apply(outcome.UndoChange)).Succeeded);
        var restored = Assert.Single(_site.Windows);
        Assert.Equal("Old name", restored.Name);
        Assert.Equal(before with { ServiceWindowId = restored.Id }, await Window("MCM00015", "Old name"));
    }

    [Fact]
    public async Task ARenameWhoseCreateFailsLeavesTheOldWindowAndRemovesNothing()
    {
        _site.Add("MCM00015", "Old name", "00CF8E00201F2000");
        var prepared = await Service().PrepareAsync(
            Change(ChangeKind.Rename, "MCM00015", "Old name", "Taken", await Window("MCM00015", "Old name")), CancellationToken.None);
        _site.Add("MCM00015", "Taken", "008C8E0030100008");

        var outcome = Assert.Single(await Apply(prepared));

        Assert.False(outcome.Succeeded);
        Assert.Equal("Collection All Workstations already has a window named Taken. The window Old name is not changed.", outcome.Message);
        Assert.Null(outcome.UndoChange);
        Assert.DoesNotContain(_site.Writes, w => (string)w.Parameters["Operation"]! == "Remove");
        Assert.Contains(_site.Windows, w => w.Name == "Old name");
    }

    [Fact]
    public async Task ARenameWhoseRemoveFailsSaysBothWindowsExistAndItsUndoRemovesTheNewOne()
    {
        _site.Add("MCM00015", "Old name", "00CF8E00201F2000");
        var prepared = await Service().PrepareAsync(
            Change(ChangeKind.Rename, "MCM00015", "Old name", "New name", await Window("MCM00015", "Old name")), CancellationToken.None);
        _site.Answer = call => (string)call.Parameters["Operation"]! == "Remove" ? ("failed", RawError) : null;

        var outcome = Assert.Single(await Apply(prepared));

        Assert.False(outcome.Succeeded);
        Assert.Contains("Created window New name on All Workstations, but the old window Old name was not removed", outcome.Message);
        Assert.Contains("Both windows exist now. Remove Old name to finish the rename.", outcome.Message);
        NoRawText(outcome);
        Assert.Equal(2, _site.Windows.Count);
        Assert.Equal(ChangeKind.Remove, outcome.UndoChange!.Kind);
        Assert.Equal("New name", outcome.UndoChange.Before!.WindowName);
        Assert.Contains(_log.Lines, l => l.Contains(RawError));
    }

    [Fact]
    public async Task ARenameToTheSameNameInAnotherCaseIsRefused()
    {
        _site.Add("MCM00015", "Old name", "00CF8E00201F2000");
        var before = await Window("MCM00015", "Old name");
        var error = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().PrepareAsync(
            Change(ChangeKind.Rename, "MCM00015", "Old name", "OLD NAME", before), CancellationToken.None));
        Assert.Equal("Collection All Workstations already has a window named OLD NAME.", error.Message);
    }

    [Fact]
    public async Task ToggleSetsTheStateAndItsUndoTogglesBack()
    {
        var a = _site.Add("MCM00014", "A", "008C8E0030100008");
        var b = _site.Add("MCM00015", "B", "00CF8E00201F2000", enabled: false);
        var service = Service();
        var changes = new List<MaintenanceChange>
        {
            await service.PrepareAsync(Change(ChangeKind.SetEnabled, "MCM00014", "A", before: await Window("MCM00014", "A")), CancellationToken.None),
            await service.PrepareAsync(Change(ChangeKind.SetEnabled, "MCM00015", "B", before: await Window("MCM00015", "B")), CancellationToken.None)
        };

        var outcomes = await Apply([.. changes]);

        Assert.Equal(["Disabled window A on HomeLab - Test Deployments.", "Enabled window B on All Workstations."], outcomes.Select(o => o.Message).ToArray());
        Assert.False(a.Enabled);
        Assert.True(b.Enabled);
        await Apply([.. outcomes.Select(o => o.UndoChange!)]);
        Assert.True(a.Enabled);
        Assert.False(b.Enabled);
    }

    [Fact]
    public async Task RemoveCapturesTheWindowAndItsUndoRecreatesTheSameValuesUnderANewId()
    {
        _site.Add("MCM00015", "Patch Saturday", "00CF8E00201F2000", type: 4, enabled: false);
        var before = await Window("MCM00015", "Patch Saturday");
        var prepared = await Service().PrepareAsync(Change(ChangeKind.Remove, "MCM00015", "Patch Saturday", before: before), CancellationToken.None);
        Assert.Equal(before, prepared.Before);

        var outcome = Assert.Single(await Apply(prepared));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("Removed window Patch Saturday from All Workstations. Undo creates it again as a new window with a new ID.", outcome.Message);
        Assert.Empty(_site.Windows);
        Assert.Equal(new MaintenanceChange(ChangeKind.Create, "MCM00015", "All Workstations", "Patch Saturday", null, null, before), outcome.UndoChange);

        Assert.True(Assert.Single(await Apply(outcome.UndoChange!)).Succeeded);
        var recreated = await Window("MCM00015", "Patch Saturday");
        Assert.NotEqual(before.ServiceWindowId, recreated.ServiceWindowId);
        Assert.Equal(before with { ServiceWindowId = recreated.ServiceWindowId }, recreated);
    }

    [Fact]
    public async Task ApplyTemplateFlagsTakenNamesKeepsPartialResultsAndReportsEachOutcomeInOrder()
    {
        _site.Add("MCM00015", "Patch Tuesday + 3 Days", "00CF8E00201F2000");
        var template = New("", "Patch Tuesday + 3 Days", PatchTuesday(3), "SoftwareUpdates");
        var service = Service();
        var prepared = new List<MaintenanceChange>();
        foreach (var id in new[] { "MCM00014", "MCM00015", "MCM00016" })
            prepared.Add(await service.PrepareAsync(Change(ChangeKind.ApplyTemplate, id, template.WindowName, after: template), CancellationToken.None));
        Assert.Null(prepared[0].Before);
        Assert.Equal("Patch Tuesday + 3 Days", prepared[1].Before!.WindowName);
        _site.Answer = call => (string)call.Parameters["CollectionId"]! == "MCM00016" ? ("failed", RawError) : null;
        var reported = new List<MaintenanceOutcome>();

        var outcomes = await service.ApplyAsync(prepared, new SyncProgress(reported.Add), CancellationToken.None);

        Assert.Equal(outcomes, reported);
        Assert.True(outcomes[0].Succeeded);
        Assert.Equal("Skipped: collection All Workstations already has a window named Patch Tuesday + 3 Days.", outcomes[1].Message);
        Assert.False(outcomes[2].Succeeded);
        Assert.Equal("ConfigMgr did not make the change to window Patch Tuesday + 3 Days on All Servers. The log has the details.", outcomes[2].Message);
        NoRawText(outcomes[2]);
        Assert.Equal(2, _site.Windows.Count);
        Assert.Equal(2, _site.Writes.Count());
        Assert.NotNull(outcomes[0].UndoChange);
        Assert.Null(outcomes[1].UndoChange);
        Assert.Null(outcomes[2].UndoChange);

        _site.Answer = null;
        Assert.True(Assert.Single(await service.ApplyAsync([outcomes[0].UndoChange!], new SyncProgress(_ => { }), CancellationToken.None)).Succeeded);
        Assert.Equal(["MCM00015"], _site.Windows.Select(w => w.CollectionId).ToArray());
    }

    [Fact]
    public async Task ApplyTemplateWithANewNameForATakenCollectionCreatesTheWindowUnderThatName()
    {
        _site.Add("MCM00015", "Template", "00CF8E00201F2000");
        var template = New("", "Template", Weekly());
        var prepared = await Service().PrepareAsync(Change(ChangeKind.ApplyTemplate, "MCM00015", "Template", after: template), CancellationToken.None);
        var outcome = Assert.Single(await Apply(prepared with { NewWindowName = "Template 2" }));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains(_site.Windows, w => w.Name == "Template 2");
    }

    [Fact]
    public async Task CancelStopsBetweenChangesAndReportsTheRestAsSkipped()
    {
        using var cancel = new CancellationTokenSource();
        var changes = Enumerable.Range(1, 4)
            .Select(i => Change(ChangeKind.ApplyTemplate, "MCM00016", $"w{i}", after: New("", $"w{i}", Weekly())))
            .ToList();
        var reported = new List<MaintenanceOutcome>();

        var outcomes = await Service().ApplyAsync(changes, new SyncProgress(o => { reported.Add(o); if (reported.Count == 2) cancel.Cancel(); }), cancel.Token);

        Assert.Equal(4, reported.Count);
        Assert.True(outcomes[0].Succeeded && outcomes[1].Succeeded);
        Assert.All(outcomes.Skip(2), o => Assert.Equal("Skipped: the run was cancelled before this change.", o.Message));
        Assert.Equal(2, _site.Writes.Count());
        Assert.All(_site.Writes, w => Assert.False(w.Cancellable));
        Assert.Contains(_log.Lines, l => l.Contains("2 succeeded, 0 failed, 2 skipped"));
    }

    [Fact]
    public async Task AccessDeniedNamesTheMissingPermission()
    {
        _site.Throw = call => call.Script == "WriteMaintenanceWindow"
            ? new ConfigMgrSessionException(ConfigMgrFailure.AccessDenied, "The SMS Provider CM01 denied access. The account needs a ConfigMgr security role for site MCM.")
            : null;
        var outcome = Assert.Single(await Apply(Change(ChangeKind.Create, "MCM00016", "x", after: New("MCM00016", "x", Weekly()))));
        Assert.Equal("ConfigMgr denied the change to window x on MCM00016. " + MaintenanceWindowService.AccessHint, outcome.Message);
    }

    [Fact]
    public async Task AScriptFailureKeepsTheRawTextOutOfTheMessage()
    {
        _site.Throw = call => call.Script == "WriteMaintenanceWindow"
            ? new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, "The ConfigMgr command WriteMaintenanceWindow failed: " + RawError)
            : null;
        var outcome = Assert.Single(await Apply(Change(ChangeKind.Create, "MCM00016", "x", after: New("MCM00016", "x", Weekly()))));
        Assert.Equal("ConfigMgr reported an error for window x on MCM00016. The log has the details.", outcome.Message);
        NoRawText(outcome);
        Assert.Contains(_log.Lines, l => l.Contains(RawError));
    }

    [Fact]
    public async Task ARejectedScheduleGivesAPlainMessage()
    {
        _site.Answer = _ => ("schedule-rejected", RawError);
        var outcome = Assert.Single(await Apply(Change(ChangeKind.Create, "MCM00016", "x", after: New("MCM00016", "x", Weekly()))));
        Assert.Equal("ConfigMgr did not accept the schedule of window x. The log has the details.", outcome.Message);
        NoRawText(outcome);
    }

    [Theory]
    [InlineData(ConfigMgrFailure.Timeout)]
    [InlineData(ConfigMgrFailure.ProcessDied)]
    public async Task AWriteWithAnUnknownResultSaysToRefresh(ConfigMgrFailure failure)
    {
        _site.Throw = call => call.Script == "WriteMaintenanceWindow" ? new ConfigMgrSessionException(failure, "The session ended.") : null;
        var outcome = Assert.Single(await Apply(Change(ChangeKind.Create, "MCM00016", "x", after: New("MCM00016", "x", Weekly()))));
        Assert.Equal("The result of the change to window x on MCM00016 is not known: The session ended. Refresh the list to see the current state.", outcome.Message);
        Assert.Null(outcome.UndoChange);
    }

    [Fact]
    public async Task AReadFailureBecomesAPlainMessageAndTheModuleMessagePassesThrough()
    {
        _site.Throw = _ => new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, "The ConfigMgr command ListMaintenanceWindows failed: " + RawError);
        var read = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().ListWindowsAsync(CancellationToken.None));
        Assert.Equal("The maintenance windows could not be read from ConfigMgr. The log has the details.", read.Message);

        _site.Throw = _ => new ConfigMgrSessionException(ConfigMgrFailure.ModuleNotFound, "The ConfigurationManager PowerShell module was not found.");
        var module = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().ListCollectionsAsync(CancellationToken.None));
        Assert.Equal("The ConfigurationManager PowerShell module was not found.", module.Message);

        _site.Throw = _ => new ConfigMgrSessionException(ConfigMgrFailure.AccessDenied, "The SMS Provider CM01 denied access.");
        var access = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Service().ListWindowsAsync(CancellationToken.None));
        Assert.Equal("The SMS Provider CM01 denied access. " + MaintenanceWindowService.AccessHint, access.Message);
    }

    [Fact]
    public async Task AReadIsCancellable()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ListWindowsAsync(cancel.Token));
    }

    [Fact]
    public async Task AScheduleConfigMgrStoresDifferentlyIsNamedInTheMessage()
    {
        _site.Store = token => token[..8] + "08" + token[10..];
        var outcome = Assert.Single(await Apply(Change(ChangeKind.Create, "MCM00016", "x", after: New("MCM00016", "x", Weekly()))));
        Assert.True(outcome.Succeeded);
        Assert.Contains("ConfigMgr stored a different schedule: Occurs every 1 weeks on Saturday effective 11/3/2026 2:00 AM, 1h long.", outcome.Message);
    }

    [Fact]
    public async Task AnUnpreparedChangeToAnExistingWindowIsNotApplied()
    {
        var outcome = Assert.Single(await Apply(Change(ChangeKind.Remove, "MCM00015", "x")));
        Assert.False(outcome.Succeeded);
        Assert.Contains("no captured window state", outcome.Message);
        Assert.Empty(_site.Writes);
    }

    [Fact]
    public async Task TheLogCarriesKindsAndCountsButNoScheduleValues()
    {
        await Apply(Change(ChangeKind.Create, "MCM00016", "Secret window", after: New("MCM00016", "Secret window", Weekly())));
        Assert.Contains(_log.Lines, l => l.Contains("Create: succeeded."));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("2026-11-03") || l.Contains("Secret window"));
    }

    private sealed class SyncProgress(Action<MaintenanceOutcome> report) : IProgress<MaintenanceOutcome>
    {
        public void Report(MaintenanceOutcome value) => report(value);
    }
}
