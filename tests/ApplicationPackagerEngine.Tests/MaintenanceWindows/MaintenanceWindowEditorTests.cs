using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

public sealed class MaintenanceWindowEditorTests
{
    private static readonly DateTime Now = MaintenanceWindowsHarness.Now;

    private static MaintenanceWindowEditorViewModel Editor(MaintenanceEditorMode mode = MaintenanceEditorMode.NewWindow, Func<MaintenanceWindowEditorViewModel, Task<bool>>? save = null)
    {
        var harness = new MaintenanceWindowsHarness();
        return new MaintenanceWindowEditorViewModel(mode, new FakeProjector(), save ?? (_ => Task.FromResult(true)), () => Now,
            harness.Service.Collections, harness.Service.Windows);
    }

    public static TheoryData<MaintenanceSchedule> Schedules => new()
    {
        new MaintenanceSchedule(MaintenanceScheduleKind.OneTime, new DateTime(2026, 11, 1, 22, 30, 0), TimeSpan.FromMinutes(90), true, 1, null, null, null, 0, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.Daily, new DateTime(2026, 11, 1, 1, 0, 0), TimeSpan.FromHours(3), false, 2, null, null, null, 0, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.Weekly, new DateTime(2026, 11, 1, 2, 0, 0), new TimeSpan(23, 59, 0), false, 2, DayOfWeek.Saturday, null, null, 0,
            new DateTime(2027, 6, 30).AddDays(1).AddTicks(-1)),
        new MaintenanceSchedule(MaintenanceScheduleKind.MonthlyByDate, new DateTime(2026, 11, 1, 3, 15, 0), TimeSpan.FromHours(4), false, 1, null, 0, null, 0, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.MonthlyByWeekday, new DateTime(2026, 11, 1, 4, 0, 0), TimeSpan.FromHours(4), false, 3, DayOfWeek.Friday, null, 5, 0, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.PatchTuesday, new DateTime(2026, 11, 1, 2, 0, 0), TimeSpan.FromHours(4), false, 1, null, null, null, -3, null)
    };

    [Theory]
    [MemberData(nameof(Schedules))]
    public void EveryKindLoadsAndBuildsTheSameSchedule(MaintenanceSchedule schedule)
    {
        var editor = Editor(MaintenanceEditorMode.NewTemplate);
        editor.LoadSchedule(schedule);
        var (built, error) = editor.BuildSchedule();
        Assert.Null(error);
        Assert.Equal(schedule, built);
        Assert.Equal(schedule.Kind == MaintenanceScheduleKind.OneTime ? 1 : 5, editor.Preview.Count);
        Assert.Equal(new FakeProjector().Describe(schedule), editor.ScheduleText);
    }

    [Fact]
    public void TheKindDecidesWhichInputsShow()
    {
        var editor = Editor();
        editor.Kind = MaintenanceScheduleKind.OneTime;
        Assert.False(editor.ShowInterval);
        Assert.False(editor.IsRecurring);
        editor.Kind = MaintenanceScheduleKind.Weekly;
        Assert.Equal((true, true, false, false, false), (editor.ShowInterval, editor.ShowDayOfWeek, editor.ShowDayOfMonth, editor.ShowWeekOrder, editor.ShowPatchOffset));
        Assert.Equal([1, 2, 3, 4], editor.Intervals);
        editor.Interval = 4;
        editor.Kind = MaintenanceScheduleKind.MonthlyByWeekday;
        Assert.Equal((true, true), (editor.ShowDayOfWeek, editor.ShowWeekOrder));
        Assert.Equal(4, editor.Interval);
        editor.Kind = MaintenanceScheduleKind.Daily;
        Assert.Equal(31, editor.Intervals.Count);
        editor.Kind = MaintenanceScheduleKind.PatchTuesday;
        Assert.True(editor.ShowPatchOffset);
        Assert.Equal(-3, editor.PatchTuesdayOffsets[0]);
        Assert.Equal(30, editor.PatchTuesdayOffsets[^1]);
    }

    [Fact]
    public void ValidationGivesPlainMessages()
    {
        var editor = Editor();
        Assert.Equal(MaintenanceWindowEditorViewModel.NameRequired, editor.Validate());
        editor.Name = "Window";
        Assert.Equal(MaintenanceWindowEditorViewModel.CollectionRequired, editor.Validate());
        editor.SelectedCollection = editor.Collections[0];
        Assert.Null(editor.Validate());

        editor.DurationHours = 0;
        editor.DurationMinutes = 0;
        Assert.Equal(MaintenanceWindowEditorViewModel.DurationRange, editor.Validate());
        Assert.Equal(MaintenanceWindowEditorViewModel.DurationRange, editor.ScheduleText);
        Assert.Empty(editor.Preview);
        editor.DurationHours = 23;
        editor.DurationMinutes = 59;
        Assert.Null(editor.Validate());
        Assert.Equal(23, editor.Hours[^1]);

        editor.HasEnd = true;
        editor.EndDate = editor.StartDate!.Value.AddDays(-1);
        Assert.Equal(MaintenanceWindowEditorViewModel.EndBeforeStart, editor.Validate());
        editor.EndDate = editor.StartDate.Value;
        Assert.Null(editor.Validate());

        editor.StartDate = null;
        Assert.Equal(MaintenanceWindowEditorViewModel.StartRequired, editor.Validate());
    }

    [Fact]
    public async Task SaveWithAnInvalidInputShowsTheMessageAndCallsNothing()
    {
        var calls = 0;
        var editor = Editor(save: _ => { calls++; return Task.FromResult(true); });
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(MaintenanceWindowEditorViewModel.NameRequired, editor.ValidationMessage);
        Assert.Equal(0, calls);
        Assert.False(editor.Saved);
    }

    [Fact]
    public async Task TheDialogClosesOnlyAfterThePageReportsTheSave()
    {
        var closes = 0;
        var answer = false;
        var editor = Editor(save: _ => Task.FromResult(answer));
        editor.CloseRequested += (_, _) => closes++;
        editor.Name = "Window";
        editor.SelectedCollection = editor.Collections[0];
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, closes);
        answer = true;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, closes);
        Assert.True(editor.Saved);
    }

    [Fact]
    public void TheCollectionPickerLeavesOutBuiltInCollectionsAndFilters()
    {
        var editor = Editor();
        Assert.Equal(["Kiosks", "Servers", "Workstations"], editor.Collections.Select(c => c.Collection.Name));
        editor.CollectionFilter = "clients";
        Assert.Equal(["Workstations"], editor.Collections.Select(c => c.Collection.Name));
        editor.SelectedCollection = editor.Collections[0];
        Assert.Equal(@"Patching\Clients", editor.SelectedFolderPath);
        editor.CollectionFilter = "";
        Assert.Equal("Workstations", editor.SelectedCollection!.Collection.Name);
    }

    [Fact]
    public void EditKeepsTheWindowIdAndStateAndAllowsItsOwnName()
    {
        var harness = new MaintenanceWindowsHarness();
        var window = harness.Service.Windows.Single(w => w.WindowName == "Weekend");
        var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.EditWindow, new FakeProjector(), _ => Task.FromResult(true), () => Now,
            harness.Service.Collections, harness.Service.Windows);
        editor.Load(window);
        Assert.Null(editor.Validate());
        editor.Name = "Patch Tuesday";
        Assert.Equal(MaintenanceWindowEditorViewModel.NameTaken("Servers", "Patch Tuesday"), editor.Validate());
        editor.Name = "WEEKEND";
        Assert.Null(editor.Validate());
        var built = editor.BuildWindow()!;
        Assert.Equal((window.ServiceWindowId, false, window.Schedule), (built.ServiceWindowId, built.Enabled, built.Schedule));
    }
}
