using ApplicationPackagerEngine.Services;
using K = ApplicationPackagerEngine.Services.MaintenanceScheduleKind;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>Write paths that could lose or mistarget a window: read-only schedules, descriptions, shared names, renames and unexpected failures.</summary>
public sealed class MaintenanceWindowServiceSafetyTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly DateTime Now = new(2026, 10, 19, 1, 37, 0);
    private const string RawError = "System.Management.ManagementException: Generic failure at Microsoft.ConfigurationManagement.Foo()";
    private const string Custom = "Owned by the patch team; call the service desk first";

    /// <summary>Weekly on Saturday at 02:00 for 4 hours from 2026-11-03, computed from the bit layout outside the product.</summary>
    private const string WeeklyToken = "0043BE00201F2000";

    private readonly FakeMaintenanceSession _site = new();
    private readonly RecordingLog _log = new();

    public MaintenanceWindowServiceSafetyTests()
    {
        _site.Collections.Add(new FakeMaintenanceSession.Collection("MCM00015", "All Workstations", "", 2));
        _site.Collections.Add(new FakeMaintenanceSession.Collection("MCM00016", "All Servers"));
    }

    private MaintenanceWindowService Service() => new(_site, new ScheduleProjector(Eastern), _log, () => Now);

    private static MaintenanceSchedule Weekly(DayOfWeek day = DayOfWeek.Saturday, int hour = 2, int minutes = 240) =>
        new(K.Weekly, new DateTime(2026, 11, 3, hour, 0, 0), TimeSpan.FromMinutes(minutes), false, 1, day, null, null, 0, null);

    private static MaintenanceWindowInfo New(string collectionId, string name, MaintenanceSchedule schedule) =>
        new(collectionId, "", name, "", "Any", true, schedule, "", null, "");

    private static MaintenanceChange Change(ChangeKind kind, string collectionId, string name, string? newName = null,
        MaintenanceWindowInfo? before = null, MaintenanceWindowInfo? after = null) =>
        new(kind, collectionId, "", name, newName, before, after);

    private async Task<IReadOnlyList<MaintenanceOutcome>> Apply(params MaintenanceChange[] changes) =>
        await Service().ApplyAsync(changes, new Progress<MaintenanceOutcome>(_ => { }), CancellationToken.None);

    private async Task<MaintenanceWindowInfo> Window(string collectionId, string name) =>
        (await Service().ListWindowsAsync(CancellationToken.None)).Single(w => w.CollectionId == collectionId && w.WindowName == name);

    private Task<MaintenanceChange> Prepare(MaintenanceChange change) => Service().PrepareAsync(change, CancellationToken.None);

    private static void NoRawText(MaintenanceOutcome outcome)
    {
        Assert.DoesNotContain("Exception", outcome.Message);
        Assert.DoesNotContain("Microsoft.ConfigurationManagement", outcome.Message);
    }

    // Tokens ConfigMgr can store but cannot take back through New-CMSchedule.
    public static TheoryData<string, string> TokensOutsideTheLimits() => new()
    {
        { "0043BE00C81F2000", "25 hours" },
        { "0043BE00F81F2000", "31 hours" },
        { "0043BE00085F2000", "1 day and 1 hour" },
        { "0043BE00201FA000", "every 5 weeks" },
        { "0043BE00201FC000", "every 6 weeks" },
        { "0043BE00201FE000", "every 7 weeks" },
        { "0043BE00202BF400", "every 13 months" },
        { "0043BE00202BFC00", "every 15 months" },
    };

    [WindowTheory]
    [MemberData(nameof(TokensOutsideTheLimits))]
    public async Task AWindowOutsideTheLimitsIsReadOnlyAndCanOnlyBeEnabledOrDisabled(string token, string why)
    {
        var window = _site.Add("MCM00015", "Odd", token, description: "ConfigMgr text " + why);
        var info = await Window("MCM00015", "Odd");
        Assert.Equal(TimeSpan.Zero, info.Schedule.Duration);
        Assert.Equal("ConfigMgr text " + why, info.ScheduleText);
        Assert.Null(info.NextOccurrence);

        foreach (var kind in new[] { ChangeKind.Update, ChangeKind.Rename, ChangeKind.Remove })
        {
            var error = await Assert.ThrowsAsync<MaintenanceWindowException>(() =>
                Prepare(Change(kind, "MCM00015", "Odd", kind == ChangeKind.Rename ? "Odd 2" : null, info, New("MCM00015", "Odd", Weekly()))));
            Assert.Contains("ConfigMgr console", error.Message);
        }

        var toggle = await Prepare(Change(ChangeKind.SetEnabled, "MCM00015", "Odd", before: info));
        Assert.True(Assert.Single(await Apply(toggle)).Succeeded);
        Assert.False(window.Enabled);
        Assert.Equal(token, window.Schedules);
    }

    [WindowTheory]
    [MemberData(nameof(TokensOutsideTheLimits))]
    public async Task UpdateRenameAndRemoveRefuseABeforeOutsideTheLimitsWithoutAWrite(string token, string why)
    {
        var window = _site.Add("MCM00015", "Odd", token);
        var decoded = ScheduleToken.Decode(token);
        Assert.NotNull(decoded);
        var before = new MaintenanceWindowInfo("MCM00015", "All Workstations", "Odd", window.Id, "Any", true, decoded!, why, null, "", window.Description);

        var outcomes = await Apply(
            Change(ChangeKind.Update, "MCM00015", "Odd", before: before, after: New("MCM00015", "Odd", Weekly())),
            Change(ChangeKind.Rename, "MCM00015", "Odd", "Odd 2", before, New("MCM00015", "Odd 2", Weekly())),
            Change(ChangeKind.Remove, "MCM00015", "Odd", before: before));

        Assert.All(outcomes, o => Assert.False(o.Succeeded));
        Assert.All(outcomes, o => Assert.Contains("ConfigMgr console", o.Message));
        Assert.Empty(_site.Calls);
        Assert.Equal(token, Assert.Single(_site.Windows).Schedules);
    }

    [WindowFact]
    public async Task ADayLongDurationStoredInTheDayFieldIsReadable()
    {
        _site.Add("MCM00015", "Day", "0043BE00005F2000");
        var info = await Window("MCM00015", "Day");
        Assert.Equal(TimeSpan.FromHours(24), info.Schedule.Duration);
        Assert.NotNull(info.NextOccurrence);
    }

    [WindowFact]
    public async Task TheFakeStoresTheTokenTheLabSiteStoredForTheSameParameters()
    {
        var schedule = new MaintenanceSchedule(K.Weekly, new DateTime(2026, 11, 3, 22, 30, 0), TimeSpan.FromHours(2), false, 1, DayOfWeek.Saturday, null, null, 0, null);
        Assert.True(Assert.Single(await Apply(Change(ChangeKind.Create, "MCM00016", "Lab", after: New("MCM00016", "Lab", schedule)))).Succeeded);
        Assert.Equal("7AC3BE00101F2000", Assert.Single(_site.Windows).Schedules);
    }

    [WindowFact]
    public async Task ListedWindowsCarryTheirDescription()
    {
        _site.Add("MCM00015", "Patch", WeeklyToken, description: Custom);
        var info = await Window("MCM00015", "Patch");
        Assert.Equal(Custom, info.Description);
        Assert.Equal(Custom, info.Comment);
        Assert.Equal("Occurs every 1 weeks on Saturday effective 11/3/2026 2:00 AM", info.ScheduleText);
    }

    [WindowFact]
    public async Task RenameKeepsAnOwnDescription()
    {
        _site.Add("MCM00015", "Old", WeeklyToken, description: Custom);
        var before = await Window("MCM00015", "Old");
        var prepared = await Prepare(Change(ChangeKind.Rename, "MCM00015", "Old", "New", before, New("MCM00015", "New", before.Schedule)));
        Assert.Equal(Custom, prepared.After!.Description);

        var outcome = Assert.Single(await Apply(prepared));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.DoesNotContain("description", outcome.Message);
        Assert.Equal(Custom, Assert.Single(_site.Windows).Description);
        Assert.True(Assert.Single(await Apply(outcome.UndoChange!)).Succeeded);
        var restored = Assert.Single(_site.Windows);
        Assert.Equal(("Old", Custom), (restored.Name, restored.Description));
    }

    [WindowFact]
    public async Task RenameOfAWindowWithConfigMgrsOwnTextWritesNoDescription()
    {
        _site.Add("MCM00015", "Old", WeeklyToken);
        var before = await Window("MCM00015", "Old");
        var outcome = Assert.Single(await Apply(await Prepare(Change(ChangeKind.Rename, "MCM00015", "Old", "New", before))));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.All(_site.Writes, w => Assert.Equal("", (string?)w.Parameters.GetValueOrDefault("Description") ?? ""));
        Assert.Equal(before.Description, Assert.Single(_site.Windows).Description);
    }

    [WindowFact]
    public async Task UndoOfRemoveRecreatesTheDescription()
    {
        _site.Add("MCM00015", "Patch", WeeklyToken, type: 4, enabled: false, description: Custom);
        var before = await Window("MCM00015", "Patch");
        var outcome = Assert.Single(await Apply(await Prepare(Change(ChangeKind.Remove, "MCM00015", "Patch", before: before))));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Empty(_site.Windows);

        var undo = Assert.Single(await Apply(outcome.UndoChange!));

        Assert.True(undo.Succeeded, undo.Message);
        var recreated = await Window("MCM00015", "Patch");
        Assert.Equal(before with { ServiceWindowId = recreated.ServiceWindowId }, recreated);
    }

    [WindowFact]
    public async Task AnUndoOfRemoveWhoseDescriptionIsNotKeptSaysSo()
    {
        _site.Add("MCM00015", "Patch", WeeklyToken, description: Custom);
        var outcome = Assert.Single(await Apply(await Prepare(Change(ChangeKind.Remove, "MCM00015", "Patch", before: await Window("MCM00015", "Patch")))));
        _site.DescriptionWriteFails = true;

        var undo = Assert.Single(await Apply(outcome.UndoChange!));

        Assert.True(undo.Succeeded, undo.Message);
        Assert.Equal("Created window Patch on All Workstations. ConfigMgr did not keep the description.", undo.Message);
        Assert.Equal((ChangeKind.Remove, "Patch"), (undo.UndoChange!.Kind, undo.UndoChange.WindowName));
        Assert.Contains(_log.Lines, l => l.Contains("The description could not be set."));
    }

    [WindowFact]
    public async Task UpdateKeepsAnOwnDescriptionAndItsUndoKeepsItToo()
    {
        var window = _site.Add("MCM00015", "Patch", WeeklyToken, description: Custom);
        var before = await Window("MCM00015", "Patch");
        var outcome = Assert.Single(await Apply(await Prepare(
            Change(ChangeKind.Update, "MCM00015", "Patch", before: before, after: New("MCM00015", "Patch", Weekly(DayOfWeek.Sunday, 3, 120))))));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(Custom, window.Description);
        Assert.True(Assert.Single(await Apply(outcome.UndoChange!)).Succeeded);
        Assert.Equal((WeeklyToken, Custom), (window.Schedules, window.Description));
    }

    [WindowFact]
    public async Task UpdateOfAWindowWithConfigMgrsOwnTextTakesConfigMgrsNewText()
    {
        var window = _site.Add("MCM00015", "Patch", WeeklyToken);
        var before = await Window("MCM00015", "Patch");
        var after = before with { Schedule = Weekly(DayOfWeek.Sunday, 3, 120) };
        var outcome = Assert.Single(await Apply(await Prepare(Change(ChangeKind.Update, "MCM00015", "Patch", before: before, after: after))));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("Occurs every 1 weeks on Sunday effective 11/3/2026 3:00 AM", window.Description);
    }

    [WindowFact]
    public async Task WhenTheDescriptionCannotBeWrittenRenameAndRemoveOfAWindowWithAnOwnDescriptionAreRefused()
    {
        _site.DescriptionWritable = false;
        _site.Add("MCM00015", "Patch", WeeklyToken, description: Custom);
        var before = await Window("MCM00015", "Patch");

        var remove = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Prepare(Change(ChangeKind.Remove, "MCM00015", "Patch", before: before)));
        Assert.Equal(
            "Window Patch on All Workstations has its own description, and the ConfigMgr PowerShell module on this computer cannot write a window description. Undo could not restore it, so the window is not removed here.",
            remove.Message);
        var rename = await Assert.ThrowsAsync<MaintenanceWindowException>(() => Prepare(Change(ChangeKind.Rename, "MCM00015", "Patch", "Patch 2", before)));
        Assert.Contains("so the window is not renamed here", rename.Message);
        var update = await Assert.ThrowsAsync<MaintenanceWindowException>(() =>
            Prepare(Change(ChangeKind.Update, "MCM00015", "Patch", before: before, after: New("MCM00015", "Patch", Weekly(DayOfWeek.Sunday)))));
        Assert.Contains("so the window is not changed here", update.Message);
        Assert.Empty(_site.Writes);

        _site.DescriptionWritable = true;
        var prepared = await Prepare(Change(ChangeKind.Remove, "MCM00015", "Patch", before: before));
        _site.DescriptionWritable = false;
        var outcome = Assert.Single(await Apply(prepared));
        Assert.False(outcome.Succeeded);
        Assert.Contains("so the window is not removed here", outcome.Message);
        Assert.Single(_site.Windows);
    }

    [WindowFact]
    public async Task WhenTheDescriptionCannotBeWrittenAWindowWithConfigMgrsOwnTextCanStillBeRemoved()
    {
        _site.DescriptionWritable = false;
        _site.Add("MCM00015", "Patch", WeeklyToken);
        var outcome = Assert.Single(await Apply(await Prepare(Change(ChangeKind.Remove, "MCM00015", "Patch", before: await Window("MCM00015", "Patch")))));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Empty(_site.Windows);
    }

    [WindowFact]
    public async Task ARenameWhoseDescriptionIsNotKeptLeavesTheOldWindow()
    {
        _site.Add("MCM00015", "Old", WeeklyToken, description: Custom);
        var prepared = await Prepare(Change(ChangeKind.Rename, "MCM00015", "Old", "New", await Window("MCM00015", "Old")));
        _site.DescriptionWriteFails = true;

        var outcome = Assert.Single(await Apply(prepared));

        Assert.False(outcome.Succeeded);
        Assert.Equal("Created window New on All Workstations, but ConfigMgr did not keep the description of Old, so Old was not removed. Both windows exist now.", outcome.Message);
        Assert.Equal(2, _site.Windows.Count);
        Assert.Equal((ChangeKind.Remove, "New"), (outcome.UndoChange!.Kind, outcome.UndoChange.WindowName));
    }

    [WindowFact]
    public async Task RemoveOfAWindowThatSharesItsNameRemovesOnlyThatWindow()
    {
        var target = _site.Add("MCM00015", "Patch", WeeklyToken);
        var twin = _site.Add("MCM00015", "PATCH", WeeklyToken, type: 4);
        var before = (await Service().ListWindowsAsync(CancellationToken.None)).Single(w => w.ServiceWindowId == target.Id);

        var outcome = Assert.Single(await Apply(await Prepare(Change(ChangeKind.Remove, "MCM00015", "Patch", before: before))));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Same(twin, Assert.Single(_site.Windows));
        Assert.Empty(_site.RemovedByName);
    }

    [WindowFact]
    public async Task RemoveOfAWindowThatSharesItsNameIsRefusedWhenOnlyARemoveByNameIsPossible()
    {
        _site.DescriptionWritable = false;
        var target = _site.Add("MCM00015", "Patch", WeeklyToken);
        _site.Add("MCM00015", "Patch", WeeklyToken, type: 4);
        var before = (await Service().ListWindowsAsync(CancellationToken.None)).Single(w => w.ServiceWindowId == target.Id);

        var outcome = Assert.Single(await Apply(await Prepare(Change(ChangeKind.Remove, "MCM00015", "Patch", before: before))));

        Assert.False(outcome.Succeeded);
        Assert.Equal("Collection All Workstations has more than one window named Patch. The ConfigMgr PowerShell module on this computer removes all windows with that name together, so nothing was removed.", outcome.Message);
        Assert.Equal(2, _site.Windows.Count);
    }

    [WindowFact]
    public async Task PrepareWithoutAnIdRefusesANameThatSeveralWindowsShare()
    {
        _site.Add("MCM00015", "Patch", WeeklyToken);
        _site.Add("MCM00015", "patch", WeeklyToken);
        foreach (var kind in new[] { ChangeKind.Update, ChangeKind.Rename, ChangeKind.SetEnabled, ChangeKind.Remove })
        {
            var error = await Assert.ThrowsAsync<MaintenanceWindowException>(() =>
                Prepare(Change(kind, "MCM00015", "Patch", kind == ChangeKind.Rename ? "Other" : null, after: New("MCM00015", "Patch", Weekly()))));
            Assert.Equal("Collection All Workstations has more than one window named Patch, so the window to change is not certain. Refresh the list and select the window again.", error.Message);
        }
    }

    [WindowFact]
    public async Task ARenameOfAWindowRemovedAfterThePlanCreatesNothing()
    {
        var old = _site.Add("MCM00015", "Old", WeeklyToken);
        var prepared = await Prepare(Change(ChangeKind.Rename, "MCM00015", "Old", "New", await Window("MCM00015", "Old")));
        _site.Windows.Remove(old);

        var outcome = Assert.Single(await Apply(prepared));

        Assert.False(outcome.Succeeded);
        Assert.Equal("The window Old does not exist on All Workstations any more. Refresh the list.", outcome.Message);
        Assert.Empty(_site.Windows);
        Assert.Empty(_site.Writes);
        Assert.Null(outcome.UndoChange);
    }

    [WindowFact]
    public async Task ARenameOfAWindowChangedAfterThePlanCreatesNothing()
    {
        var old = _site.Add("MCM00015", "Old", WeeklyToken);
        var prepared = await Prepare(Change(ChangeKind.Rename, "MCM00015", "Old", "New", await Window("MCM00015", "Old")));
        old.Enabled = false;

        var outcome = Assert.Single(await Apply(prepared));

        Assert.False(outcome.Succeeded);
        Assert.Contains("changed after the plan was shown", outcome.Message);
        Assert.Empty(_site.Writes);
    }

    [WindowFact]
    public async Task ARenameWhoseOldWindowIsGoneAtTheRemoveDoesNotSayBothExist()
    {
        _site.Add("MCM00015", "Old", WeeklyToken);
        var prepared = await Prepare(Change(ChangeKind.Rename, "MCM00015", "Old", "New", await Window("MCM00015", "Old")));
        _site.Answer = call => (string)call.Parameters["Operation"]! == "Remove" ? ("window-missing", "") : null;

        var outcome = Assert.Single(await Apply(prepared));

        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("Both windows exist", outcome.Message);
        Assert.Equal("Created window New on All Workstations, but the old window Old was removed by someone else after the check. Only New exists now.", outcome.Message);
        Assert.Equal((ChangeKind.Remove, "New"), (outcome.UndoChange!.Kind, outcome.UndoChange.WindowName));
    }

    [WindowFact]
    public async Task AnUnexpectedFailureInOneChangeKeepsTheOtherOutcomesAndTheirUndo()
    {
        _site.Throw = call => call.Script == "WriteMaintenanceWindow" && (string?)call.Parameters.GetValueOrDefault("Name") == "w2"
            ? new InvalidOperationException(RawError)
            : null;
        var changes = Enumerable.Range(1, 3)
            .Select(i => Change(ChangeKind.Create, "MCM00016", $"w{i}", after: New("MCM00016", $"w{i}", Weekly())))
            .ToArray();
        var reported = new List<MaintenanceOutcome>();

        var outcomes = await Service().ApplyAsync(changes, new SyncProgress(reported.Add), CancellationToken.None);

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(outcomes, reported);
        Assert.True(outcomes[0].Succeeded);
        Assert.NotNull(outcomes[0].UndoChange);
        Assert.False(outcomes[1].Succeeded);
        Assert.Equal("The change to window w2 on MCM00016 stopped with an unexpected error. Refresh the list to see the current state. The log has the details.", outcomes[1].Message);
        NoRawText(outcomes[1]);
        Assert.True(outcomes[2].Succeeded);
        Assert.Contains(_log.Lines, l => l.Contains("InvalidOperationException"));
    }

    [WindowFact]
    public async Task ATuesdayScheduleStoredInAnotherEquivalentFormIsNotReportedAsDifferent()
    {
        var thirdTuesday = new MaintenanceSchedule(K.MonthlyByWeekday, new DateTime(2026, 11, 3, 2, 0, 0), TimeSpan.FromHours(4), false, 1, DayOfWeek.Tuesday, null, 3, 0, null);
        _site.Store = token => token == "0043BE0020231600" ? "0043BE0020331600" : token;

        var outcome = Assert.Single(await Apply(Change(ChangeKind.Create, "MCM00016", "Third", after: New("MCM00016", "Third", thirdTuesday))));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("0043BE0020331600", Assert.Single(_site.Windows).Schedules);
        Assert.Equal("Created window Third on MCM00016.", outcome.Message);
    }

    [WindowFact]
    public async Task ASecondTuesdayMonthlyByWeekdayIsTheSameScheduleAsPatchTuesday()
    {
        var secondTuesday = new MaintenanceSchedule(K.MonthlyByWeekday, new DateTime(2026, 11, 3, 2, 0, 0), TimeSpan.FromHours(4), false, 1, DayOfWeek.Tuesday, null, 2, 0, null);
        var outcome = Assert.Single(await Apply(Change(ChangeKind.Create, "MCM00016", "Second", after: New("MCM00016", "Second", secondTuesday))));
        Assert.Equal("Created window Second on MCM00016.", outcome.Message);
    }

    [WindowFact]
    public async Task AWindowStoredInTheOffsetFormOfAPatchTuesdayScheduleCanBeChanged()
    {
        var window = _site.Add("MCM00015", "Third", "0043BE0020331600");
        var before = await Window("MCM00015", "Third");
        Assert.Equal((K.PatchTuesday, 7), (before.Schedule.Kind, before.Schedule.PatchTuesdayOffsetDays));

        var outcome = Assert.Single(await Apply(await Prepare(Change(ChangeKind.SetEnabled, "MCM00015", "Third", before: before))));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False(window.Enabled);
    }

    private sealed class SyncProgress(Action<MaintenanceOutcome> report) : IProgress<MaintenanceOutcome>
    {
        public void Report(MaintenanceOutcome value) => report(value);
    }
}
