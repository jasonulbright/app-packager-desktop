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
        new MaintenanceSchedule(MaintenanceScheduleKind.Weekly, new DateTime(2026, 11, 1, 2, 0, 0), new TimeSpan(23, 59, 0), false, 2, DayOfWeek.Saturday, null, null, 0, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.Weekly, new DateTime(2026, 11, 1, 0, 0, 0), TimeSpan.FromHours(24), false, 4, DayOfWeek.Sunday, null, null, 0, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.MonthlyByDate, new DateTime(2026, 11, 1, 3, 15, 0), TimeSpan.FromHours(4), false, 1, null, 0, null, 0, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.MonthlyByWeekday, new DateTime(2026, 11, 1, 4, 0, 0), TimeSpan.FromHours(4), false, 3, DayOfWeek.Friday, null, 5, 0, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.MonthlyByWeekday, new DateTime(2026, 11, 1, 4, 0, 0), TimeSpan.FromHours(4), false, 12, DayOfWeek.Monday, null, 2, 7, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.PatchTuesday, new DateTime(2026, 11, 1, 2, 0, 0), TimeSpan.FromHours(4), false, 1, null, null, null, -7, null),
        new MaintenanceSchedule(MaintenanceScheduleKind.PatchTuesday, new DateTime(2033, 12, 31, 2, 0, 0), TimeSpan.FromMinutes(1), true, 12, null, null, null, 21, null)
    };

    [Fact]
    public void AScheduleThePreviewCannotDescribeShowsAFixedTextWithoutTheExceptionText()
    {
        var projector = new FakeProjector { DescribeFailure = new ArgumentException("raw projector detail") };
        var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.NewWindow, projector, _ => Task.FromResult(true), () => Now);
        Assert.Equal(MaintenanceWindowEditorViewModel.ScheduleNotShown, editor.ScheduleText);
        editor.DurationHours = 2;
        Assert.Equal(MaintenanceWindowEditorViewModel.ScheduleNotShown, editor.ScheduleText);
    }

    [Theory]
    [InlineData(5000, 4000, 1280, 680, 1280, 680)]
    [InlineData(800, 600, 1920, 1040, 800, 600)]
    [InlineData(1000, 900, 1366, 728, 1000, 728)]
    public void AStoredSizeIsBoundedByTheWorkArea(double width, double height, double areaWidth, double areaHeight, double expectedWidth, double expectedHeight)
    {
        var editor = Editor();
        editor.Width = width;
        editor.Height = height;
        editor.FitTo(areaWidth, areaHeight);
        Assert.Equal((expectedWidth, expectedHeight), (editor.Width, editor.Height));
    }

    private static MaintenanceWindowEditorViewModel Ready()
    {
        var editor = Editor();
        editor.Name = "Window";
        editor.SelectedCollection = editor.Collections[0];
        return editor;
    }

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
        Assert.False(editor.ShowWeekdayOffset);
        Assert.Equal(Enumerable.Range(-7, 29), editor.PatchTuesdayOffsets);
        editor.Kind = MaintenanceScheduleKind.MonthlyByWeekday;
        Assert.True(editor.ShowWeekdayOffset);
        Assert.False(editor.ShowPatchOffset);
        Assert.Equal(Enumerable.Range(0, 8), editor.WeekdayOffsets);
        Assert.Equal(Enumerable.Range(0, 25), editor.DurationHourChoices);
        Assert.Equal(Enumerable.Range(0, 60), editor.Minutes);
    }

    [Fact]
    public void TheEditorOffersOnlyValuesValidateAccepts()
    {
        var projector = new ScheduleProjector();
        var editor = Ready();
        foreach (var kind in Enum.GetValues<MaintenanceScheduleKind>())
        {
            editor.Kind = kind;
            foreach (var interval in editor.Intervals)
            {
                editor.Interval = interval;
                Assert.Null(projector.Validate(editor.BuildSchedule().Schedule!));
            }
            foreach (var offset in kind == MaintenanceScheduleKind.PatchTuesday ? editor.PatchTuesdayOffsets : kind == MaintenanceScheduleKind.MonthlyByWeekday ? editor.WeekdayOffsets : [0])
            {
                if (kind == MaintenanceScheduleKind.PatchTuesday) editor.PatchTuesdayOffsetDays = offset;
                else if (kind == MaintenanceScheduleKind.MonthlyByWeekday) editor.WeekdayOffsetDays = offset;
                Assert.Null(projector.Validate(editor.BuildSchedule().Schedule!));
            }
        }
        Assert.Equal(new DateTime(ScheduleToken.FirstYear, 1, 1), editor.FirstStartDate);
        Assert.Equal(new DateTime(ScheduleToken.LastYear, 12, 31), editor.LastStartDate);
    }

    [Fact]
    public void AReasonFromValidateIsShownAndSaveIsDisabledUntilItIsGone()
    {
        var editor = Ready();
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.StartDate = new DateTime(2034, 1, 1);
        var reason = new ScheduleProjector().Validate(editor.BuildSchedule().Schedule!);
        Assert.NotNull(reason);
        Assert.Equal(reason, editor.ValidationMessage);
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.StartDate = new DateTime(2033, 12, 31);
        Assert.Equal("", editor.ValidationMessage);
        Assert.True(editor.SaveCommand.CanExecute(null));

        editor.DurationHours = 24;
        editor.DurationMinutes = 30;
        Assert.Equal(MaintenanceWindowEditorViewModel.DurationRange, editor.ValidationMessage);
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.DurationMinutes = 0;
        Assert.Equal("", editor.ValidationMessage);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheProjectorsReasonWinsOverTheEditorsOwnChecksPassing()
    {
        var harness = new MaintenanceWindowsHarness();
        var projector = new FakeProjector { Rejects = _ => "ConfigMgr cannot store this schedule." };
        var calls = 0;
        var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.NewWindow, projector, _ => { calls++; return Task.FromResult(true); }, () => Now,
            harness.Service.Collections, harness.Service.Windows) { Name = "Window" };
        editor.SelectedCollection = editor.Collections[0];
        Assert.Equal("ConfigMgr cannot store this schedule.", editor.ValidationMessage);
        Assert.False(editor.SaveCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, calls);
        Assert.Equal("ConfigMgr cannot store this schedule.", editor.Validate());
    }

    [Fact]
    public void TheEditorHasNoEndDateAndSaysSo()
    {
        var editor = Editor(MaintenanceEditorMode.NewTemplate);
        editor.LoadSchedule(new MaintenanceSchedule(MaintenanceScheduleKind.Daily, new DateTime(2026, 11, 1, 1, 0, 0), TimeSpan.FromHours(1), false, 1, null, null, null, 0,
            new DateTime(2027, 1, 1)));
        Assert.Null(editor.BuildSchedule().Schedule!.End);
        Assert.Equal("", editor.ValidationMessage);
        Assert.Equal("ConfigMgr stores no end date. The window recurs until it is removed or disabled.", MaintenanceWindowEditorViewModel.NoEndDateNote);
        Assert.Null(typeof(MaintenanceWindowEditorViewModel).GetProperty("HasEnd"));
        Assert.Null(typeof(MaintenanceWindowEditorViewModel).GetProperty("EndDate"));
    }

    [Theory]
    [InlineData(28, "")]
    [InlineData(29, "A month without day 29 has no occurrence.")]
    [InlineData(31, "A month without day 31 has no occurrence.")]
    [InlineData(0, "")]
    public void ADayOfTheMonthThatSomeMonthsLackHasANote(int day, string note)
    {
        var editor = Ready();
        editor.Kind = MaintenanceScheduleKind.MonthlyByDate;
        editor.DayOfMonth = day;
        Assert.Equal(note, editor.DayOfMonthNote);
        Assert.Equal("Last day", editor.DaysOfMonth[0].Text);
        Assert.Null(new ScheduleProjector().Validate(editor.BuildSchedule().Schedule!));
    }

    [Fact]
    public void TheWindowKeepsTheOriginalDescriptionBecauseWritesIgnoreIt()
    {
        var harness = new MaintenanceWindowsHarness();
        var window = harness.Service.Windows[0] with { Comment = "Occurs every 1 weeks on Sunday" };
        var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.EditWindow, new FakeProjector(), _ => Task.FromResult(true), () => Now,
            harness.Service.Collections, harness.Service.Windows);
        editor.Load(window);
        Assert.Equal(window.Comment, editor.BuildWindow()!.Comment);
        Assert.Null(typeof(MaintenanceWindowEditorViewModel).GetProperty("Comment"));
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
        editor.DurationHours = 24;
        editor.DurationMinutes = 0;
        Assert.Null(editor.Validate());
        Assert.Equal(23, editor.Hours[^1]);

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
