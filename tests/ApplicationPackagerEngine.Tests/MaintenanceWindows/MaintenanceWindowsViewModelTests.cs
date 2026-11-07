using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

public sealed class MaintenanceWindowsViewModelTests
{
    private static MaintenanceWindowRow Row(MaintenanceWindowsViewModel vm, string window) => vm.Rows.Single(r => r.WindowName == window);

    private static void Select(MaintenanceWindowsViewModel vm, params string[] windows)
    {
        var rows = windows.Select(w => Row(vm, w)).ToList();
        vm.SelectedRow = rows[0];
        vm.SetSelectedWindows(rows);
    }

    // --- Loading and connection ------------------------------------------------------------

    [Fact]
    public async Task TheConstructorReadsNothingAndTheFirstVisitReadsTheSite()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = harness.Create();
        Assert.Equal(0, harness.Service.ListCalls);
        Assert.Equal(0, harness.SettingsLoads);
        Assert.Equal(0, harness.PreferenceLoads);

        await vm.OnNavigatedToAsync();
        Assert.Equal(1, harness.Service.ListCalls);
        Assert.Equal(["Patch Tuesday", "Weekend", "Nightly"], vm.Rows.Select(r => r.WindowName));
        Assert.Equal("3 windows", vm.RowCount);
        Assert.Equal(2, vm.Templates.Count);
        Assert.Equal("", vm.ConnectionMessage);
        Assert.Contains(harness.Log.Entries, e => e.Message == "Read 3 maintenance windows on 4 collections.");

        await vm.OnNavigatedToAsync();
        Assert.Equal(1, harness.Service.ListCalls);
    }

    [Theory]
    [InlineData("", true, MaintenanceWindowsViewModel.SiteCodeMessage)]
    [InlineData("LAB", false, MaintenanceWindowsViewModel.NotInUseMessage)]
    public async Task AMissingSettingGivesOnePlainMessageAndNoConfigMgrCall(string siteCode, bool configMgr, string message)
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Settings.SiteCode = siteCode;
        harness.Settings.Systems = new ApplicationPackagerEngine.Models.SystemsInUse { ConfigMgr = configMgr };
        var vm = await harness.OpenAsync();
        Assert.Equal(message, vm.ConnectionMessage);
        Assert.True(vm.HasConnectionMessage);
        Assert.Equal(0, harness.Service.ListCalls);
        Assert.Empty(vm.Rows);

        await vm.NewWindowCommand.ExecuteAsync(null);
        Assert.Equal(message, vm.StatusMessage);
        Assert.Equal(0, harness.Prompts.EditorShows);
    }

    [Fact]
    public async Task ASessionFailureShowsItsPlainMessageWithTheSettingToCheck()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.ListFailure = new ConfigMgrSessionException(ConfigMgrFailure.DriveNotConnected, "The CMSite drive LAB could not be connected.");
        var vm = await harness.OpenAsync();
        Assert.Equal("The CMSite drive LAB could not be connected." + MaintenanceWindowsViewModel.SiteHint, vm.ConnectionMessage);

        harness.Service.ListFailure = new ConfigMgrSessionException(ConfigMgrFailure.AccessDenied, "Access is denied.");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.EndsWith(MaintenanceWindowsViewModel.AccessHint, vm.ConnectionMessage);

        harness.Service.ListFailure = new InvalidOperationException("at Secret.Stack.Trace()");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(MaintenanceWindowsViewModel.LoadFailed, vm.ConnectionMessage);
        Assert.DoesNotContain(harness.Log.Entries, e => e.Message.Contains("Secret.Stack"));

        harness.Service.ListFailure = null;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ConnectionMessage);
        Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public async Task LeavingThePageDuringALoadDiscardsTheLateResultAndReleasesTheData()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Assert.NotEmpty(vm.Rows);

        harness.Service.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsLoading);
        await vm.OnNavigatedFromAsync();
        Assert.Empty(vm.Rows);
        Assert.Empty(vm.Coverage);
        Assert.False(vm.IsLoading);
        harness.Service.ListGate.SetResult();
        await refresh;
        Assert.Empty(vm.Rows);
        Assert.False(vm.IsDataLoaded);

        harness.Service.ListGate = null;
        await vm.OnNavigatedToAsync();
        Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public async Task CancelStopsALoadAndSaysSo()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = harness.Create();
        var open = vm.OnNavigatedToAsync();
        await WaitFor(() => vm.IsLoading);
        vm.CancelLoadCommand.Execute(null);
        await open;
        Assert.False(vm.IsLoading);
        Assert.Equal("The read was cancelled.", vm.StatusMessage);
        Assert.Empty(vm.Rows);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    // --- Filters, details and coverage -----------------------------------------------------

    [Fact]
    public async Task TheFiltersNarrowTheGrid()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();

        vm.TextFilter = "weekend";
        Assert.Equal(["Weekend"], vm.Rows.Select(r => r.WindowName));
        Assert.Equal("1 of 3 windows", vm.RowCount);
        vm.TextFilter = "";

        vm.TypeFilter = MaintenanceWindowsText.SoftwareUpdates;
        Assert.Equal(["Patch Tuesday"], vm.Rows.Select(r => r.WindowName));
        vm.TypeFilter = "";

        vm.EnabledFilter = MaintenanceWindowsViewModel.Disabled;
        Assert.Equal(["Weekend"], vm.Rows.Select(r => r.WindowName));
        vm.EnabledFilter = MaintenanceWindowsViewModel.All;

        Assert.Contains(vm.Folders, f => f.Value == @"Patching\Clients");
        vm.FolderFilter = @"Patching\Clients";
        Assert.Equal(["Nightly"], vm.Rows.Select(r => r.WindowName));
        vm.FolderFilter = "";

        vm.OnlyCollectionsWithWindows = false;
        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(["All Systems", "Kiosks"], vm.Rows.Where(r => !r.HasWindow).Select(r => r.CollectionName));
        Assert.All(vm.Rows.Where(r => !r.HasWindow), r => Assert.Equal("No window", r.Status));
    }

    [Fact]
    public async Task AParentFolderEntryFiltersTheWindowsAndTheCoverageToThatFolderAndEveryFolderBelowIt()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Assert.Equal(["", "Patching", @"Patching\Clients", @"Patching\Servers"], vm.Folders.Select(f => f.Value));
        Assert.Equal("Patching (and sub-folders)", vm.Folders.Single(f => f.Value == "Patching").Text);
        vm.FolderFilter = "Patching";
        Assert.Equal(["Servers", "Workstations"], vm.Rows.Select(r => r.CollectionName).Distinct());
        Assert.Equal(["Servers", "Workstations"], vm.Coverage.Select(r => r.Name));
        vm.FolderFilter = "Patch";
        Assert.Empty(vm.Rows);
        Assert.Empty(vm.Coverage);
    }

    [Fact]
    public async Task TheDetailShowsThePropertiesAndTheNextFiveOccurrences()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Assert.False(vm.HasSelectedWindow);
        vm.SelectedRow = Row(vm, "Patch Tuesday");
        Assert.True(vm.HasSelectedWindow);
        Assert.Contains(vm.Properties, p => p.Name == "Folder" && p.Value == @"Patching\Servers");
        Assert.Contains(vm.Properties, p => p.Name == "Type" && p.Value == "Software updates");
        Assert.Equal(5, vm.NextOccurrences.Count);
        Assert.StartsWith("Sun 2026-10-25 02:00 to 06:00", vm.NextOccurrences[0]);
        vm.SelectedRow = null;
        Assert.Empty(vm.Properties);
    }

    [Fact]
    public async Task CoverageFlagsEveryCollectionAndFiltersThoseWithoutAWindow()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Assert.Equal(["All Systems", "Kiosks", "Servers", "Workstations"], vm.Coverage.Select(c => c.Name));
        Assert.Equal([false, false, true, true], vm.Coverage.Select(c => c.HasWindow));
        Assert.Equal(2, vm.Coverage.Single(c => c.Name == "Servers").WindowCount);
        Assert.Equal("Yes", vm.Coverage.Single(c => c.Name == "All Systems").BuiltIn);
        Assert.Equal("4 collections, 2 without a window", vm.CoverageCount);
        vm.CoverageWithoutWindowOnly = true;
        Assert.Equal(["All Systems", "Kiosks"], vm.Coverage.Select(c => c.Name));
    }

    [Fact]
    public async Task TheCoverageCountLineCountsOnlyTheCollectionsOfTheSelectedFolder()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        vm.FolderFilter = @"Patching\Servers";
        var shown = vm.Coverage.ToList();
        Assert.NotEmpty(shown);
        Assert.True(shown.Count < 4);
        var without = shown.Count(r => !r.HasWindow);
        var noun = shown.Count == 1 ? "collection" : "collections";
        Assert.Equal($"{shown.Count} {noun}, {without} without a window", vm.CoverageCount);
    }
    // --- New, edit and rename --------------------------------------------------------------

    [Fact]
    public async Task NewWindowShowsAOneCreatePlanAndWritesOnlyAfterTheConfirm()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        MaintenanceWindowEditorViewModel? shown = null;
        harness.Prompts.Editor = editor =>
        {
            shown = editor;
            Assert.DoesNotContain(editor.Collections, c => c.Collection.IsBuiltIn);
            editor.SelectedCollection = editor.Collections.Single(c => c.Collection.Name == "Kiosks");
            editor.Name = "Kiosk nights";
            editor.WindowType = MaintenanceWindowsText.TaskSequences;
            editor.Kind = MaintenanceScheduleKind.Daily;
            FakeMaintenancePrompts.Save(editor);
        };
        await vm.NewWindowCommand.ExecuteAsync(null);

        var plan = Assert.Single(harness.Plans.Shown);
        var item = Assert.Single(plan.Items);
        Assert.Equal(("Create", "Kiosks: Kiosk nights"), (item.Action, item.Target));
        Assert.Contains("Task sequences", item.Detail);
        Assert.Contains("Daily at 02:00 for 240 minutes", item.Detail);
        Assert.Contains("Next: Tue 2026-10-20 02:00 to 06:00", item.Detail);
        Assert.False(plan.IsDestructive);
        Assert.True(plan.HasUndo);

        var change = Assert.Single(Assert.Single(harness.Service.Applied));
        Assert.Equal((ChangeKind.Create, "LAB00012", "Kiosk nights"), (change.Kind, change.CollectionId, change.WindowName));
        Assert.True(shown!.Saved);
        Assert.Contains(vm.Rows, r => r.WindowName == "Kiosk nights");
        Assert.Equal("Changed", Assert.Single(vm.LastResults).OutcomeText);
        Assert.True(vm.HasUndo);
        Assert.Equal(2, harness.Service.ListCalls);
    }

    [Fact]
    public async Task ADeclinedPlanWritesNothingAndKeepsTheEditorOpen()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Plans.Confirm = false;
        var vm = await harness.OpenAsync();
        MaintenanceWindowEditorViewModel? shown = null;
        harness.Prompts.Editor = editor =>
        {
            shown = editor;
            editor.SelectedCollection = editor.Collections[0];
            editor.Name = "Unconfirmed";
            FakeMaintenancePrompts.Save(editor);
        };
        await vm.NewWindowCommand.ExecuteAsync(null);
        Assert.Single(harness.Plans.Shown);
        Assert.Empty(harness.Service.Applied);
        Assert.False(shown!.Saved);
        Assert.Equal(MaintenanceWindowsViewModel.NotConfirmed, shown.ValidationMessage);
        Assert.Equal(MaintenanceWindowsViewModel.NotConfirmed, vm.StatusMessage);
        Assert.False(vm.HasUndo);
    }

    [Fact]
    public async Task ADoubleSaveShowsOnePlan()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = await harness.OpenAsync();
        harness.Prompts.Editor = editor =>
        {
            editor.SelectedCollection = editor.Collections.Single(c => c.Collection.Name == "Kiosks");
            editor.Name = "Once";
            var first = editor.SaveCommand.ExecuteAsync(null);
            var second = editor.SaveCommand.ExecuteAsync(null);
            Assert.False(editor.SaveCommand.CanExecute(null));
            harness.Service.PrepareGate.SetResult();
            Task.WaitAll(first, second);
        };
        await vm.NewWindowCommand.ExecuteAsync(null);
        Assert.Single(harness.Plans.Shown);
        Assert.Equal(1, harness.Service.PrepareCalls);
        Assert.Single(harness.Service.Applied);
    }

    [Fact]
    public async Task EditWithTheSameNameShowsTheOldAndTheNewSchedule()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        harness.Prompts.Editor = editor =>
        {
            Assert.False(editor.CanPickCollection);
            Assert.Equal("Nightly", editor.Name);
            editor.DurationHours = 2;
            FakeMaintenancePrompts.Save(editor);
        };
        await vm.EditWindowCommand.ExecuteAsync(null);

        var item = Assert.Single(Assert.Single(harness.Plans.Shown).Items);
        Assert.Equal("Update", item.Action);
        Assert.Contains("Old: All deployments, Weekly at 02:00, duration 4h 0m.", item.Detail);
        Assert.Contains("New: All deployments, Weekly at 02:00 for 120 minutes, duration 2h 0m.", item.Detail);
        var change = Assert.Single(Assert.Single(harness.Service.Applied));
        Assert.Equal(ChangeKind.Update, change.Kind);
        Assert.Equal(TimeSpan.FromHours(4), change.Before!.Schedule.Duration);
        Assert.Equal(TimeSpan.FromHours(2), change.After!.Schedule.Duration);
        Assert.Equal(change.Before.ServiceWindowId, change.After.ServiceWindowId);
    }

    [Fact]
    public async Task AnEditOfTheMinutesOfTheDurationShowsBothDurationsInThePlan()
    {
        var harness = new MaintenanceWindowsHarness();
        var schedule = FakeMaintenanceService.Weekly(new DateTime(2026, 10, 25, 2, 0, 0)) with { Duration = new TimeSpan(2, 15, 0) };
        harness.Service.AddWindow(harness.Kiosks, "Short", schedule: schedule);
        var vm = await harness.OpenAsync();
        Select(vm, "Short");
        harness.Prompts.Editor = editor =>
        {
            editor.DurationMinutes = 30;
            FakeMaintenancePrompts.Save(editor);
        };
        await vm.EditWindowCommand.ExecuteAsync(null);

        var item = Assert.Single(Assert.Single(harness.Plans.Shown).Items);
        Assert.Matches(@"Old: [^.]*, duration 2h 15m\.", item.Detail);
        Assert.Matches(@"New: [^.]*, duration 2h 30m\.", item.Detail);
    }

    [Fact]
    public async Task ARenameIsOneRenameChangeAndThePlanSaysTheNewWindowComesFirst()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        harness.Prompts.Editor = editor =>
        {
            editor.Name = "Nightly clients";
            FakeMaintenancePrompts.Save(editor);
        };
        await vm.EditWindowCommand.ExecuteAsync(null);
        var item = Assert.Single(Assert.Single(harness.Plans.Shown).Items);
        Assert.Equal("Rename", item.Action);
        Assert.Contains("Nightly clients is created first; Nightly is removed only after that.", item.Detail);
        var change = Assert.Single(Assert.Single(harness.Service.Applied));
        Assert.Equal((ChangeKind.Rename, "Nightly", "Nightly clients"), (change.Kind, change.WindowName, change.NewWindowName));
        Assert.Equal(["Nightly clients"], vm.Rows.Where(r => r.CollectionName == "Workstations").Select(r => r.WindowName));
    }

    [Fact]
    public async Task ARenameWhoseRemoveFailsSaysBothWindowsExistAndSelectsTheOldOne()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.RenameRemoveFails = true;
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        harness.Prompts.Editor = editor =>
        {
            editor.Name = "Nightly clients";
            FakeMaintenancePrompts.Save(editor);
            Assert.False(editor.Saved);
            Assert.Contains("Both windows exist", editor.ValidationMessage);
            editor.CancelCommand.Execute(null);
        };
        await vm.EditWindowCommand.ExecuteAsync(null);
        Assert.Equal(2, harness.Service.ListCalls);
        Assert.Equal(["Nightly", "Nightly clients"], vm.Rows.Where(r => r.CollectionName == "Workstations").Select(r => r.WindowName));
        Assert.Equal("Failed", Assert.Single(vm.LastResults).OutcomeText);
        Assert.Equal(MaintenanceWindowsViewModel.BothWindowsExist("Workstations", "Nightly", "Nightly clients"), vm.StatusMessage);
        Assert.Equal("Nightly", vm.SelectedRow!.WindowName);
        Assert.Equal(["Nightly"], vm.SelectedWindows.Select(r => r.WindowName));
        Assert.True(vm.RemoveWindowsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARenameWhoseCreateFailsSaysNothingWasChangedAndGivesNoRemoveAdvice()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.Fail = change =>
        {
            if (change.Kind != ChangeKind.Rename) return null;
            harness.Service.AddWindow(harness.Workstations, change.NewWindowName!);
            return FakeMaintenanceService.NameTakenMessage("Workstations", change.NewWindowName!);
        };
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        harness.Prompts.Editor = editor =>
        {
            editor.Name = "Nightly clients";
            FakeMaintenancePrompts.Save(editor);
            Assert.False(editor.Saved);
            Assert.DoesNotContain("Both windows exist", editor.ValidationMessage);
            editor.CancelCommand.Execute(null);
        };
        await vm.EditWindowCommand.ExecuteAsync(null);

        Assert.Equal(["Nightly", "Nightly clients"], vm.Rows.Where(r => r.CollectionName == "Workstations").Select(r => r.WindowName));
        Assert.Equal("Failed", Assert.Single(vm.LastResults).OutcomeText);
        Assert.Equal(MaintenanceWindowsViewModel.RenameNotMade("Workstations", "Nightly", "Nightly clients"), vm.StatusMessage);
        Assert.Contains(MaintenanceWindowsViewModel.NotConfirmed, vm.StatusMessage);
        Assert.DoesNotContain("Remove", vm.StatusMessage);
        Assert.DoesNotContain("was created", vm.StatusMessage);
    }

    [Fact]
    public async Task ANameThatIsTakenIsRefusedInTheEditorBeforeAnyPlan()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        harness.Prompts.Editor = editor =>
        {
            editor.SelectedCollection = editor.Collections.Single(c => c.Collection.Name == "Servers");
            editor.Name = "weekend";
            FakeMaintenancePrompts.Save(editor);
            Assert.Equal(MaintenanceWindowEditorViewModel.NameTaken("Servers", "weekend"), editor.ValidationMessage);
        };
        await vm.NewWindowCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Equal(0, harness.Service.PrepareCalls);
    }

    [Fact]
    public async Task AWindowTakenSinceTheListWasReadStopsBeforeThePlanWithAPlainMessage()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        harness.Service.AddWindow(harness.Kiosks, "Late");
        harness.Prompts.Editor = editor =>
        {
            editor.SelectedCollection = editor.Collections.Single(c => c.Collection.Name == "Kiosks");
            editor.Name = "Late";
            FakeMaintenancePrompts.Save(editor);
            Assert.False(editor.Saved);
            Assert.Equal(FakeMaintenanceService.NameTakenMessage("Kiosks", "Late"), editor.ValidationMessage);
        };
        await vm.NewWindowCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Empty(harness.Service.Applied);
    }

    // --- Toggle and remove -----------------------------------------------------------------

    [Fact]
    public async Task ToggleOfTwoRowsIsOnePlanWithTwoItems()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Patch Tuesday", "Weekend");
        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal(["Disable", "Enable"], plan.Items.Select(i => i.Action));
        Assert.Equal("Change 2 windows", plan.ActionName);
        var changes = Assert.Single(harness.Service.Applied);
        Assert.Equal([false, true], changes.Select(c => c.After!.Enabled));
        Assert.Equal(["Disabled", "Enabled"], vm.Rows.Where(r => r.CollectionName == "Servers").Select(r => r.Status));
    }

    [Fact]
    public async Task RemoveIsADestructivePlanThatShowsEachScheduleAndUndoRecreatesFromTheCapture()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        var captured = harness.Service.Windows.Single(w => w.WindowName == "Nightly");
        Select(vm, "Nightly");
        await vm.RemoveWindowsCommand.ExecuteAsync(null);

        var plan = Assert.Single(harness.Plans.Shown);
        Assert.True(plan.IsDestructive);
        Assert.Equal("Remove 1 window", plan.ActionName);
        var item = Assert.Single(plan.Items);
        Assert.Contains("Weekly at 02:00, 4h 0m.", item.Detail);
        Assert.Contains(MaintenanceWindowWriter.RecreatedNote, item.Detail);
        Assert.DoesNotContain(vm.Rows, r => r.WindowName == "Nightly");
        Assert.True(vm.HasUndo);

        await vm.UndoLastCommand.ExecuteAsync(null);
        var undoPlan = harness.Plans.Shown[1];
        Assert.Equal("Undo: Remove maintenance windows", undoPlan.Title);
        Assert.Equal("Undo Remove", Assert.Single(undoPlan.Items).Action);
        Assert.Contains(MaintenanceWindowWriter.RecreatedNote, undoPlan.Items[0].Detail);
        var recreate = Assert.Single(harness.Service.Applied[1]);
        Assert.Equal(ChangeKind.Create, recreate.Kind);
        Assert.Equal(captured, recreate.After);
        var restored = Assert.Single(harness.Service.Windows, w => w.WindowName == "Nightly");
        Assert.Equal(captured.Schedule, restored.Schedule);
        Assert.Equal(captured.WindowType, restored.WindowType);
        Assert.Contains(vm.Rows, r => r.WindowName == "Nightly");
    }

    [Fact]
    public async Task ARemoveWithoutTheDestructiveCheckWritesNothing()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Plans.AcknowledgeDestructive = false;
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        await vm.RemoveWindowsCommand.ExecuteAsync(null);
        Assert.Single(harness.Plans.Shown);
        Assert.Empty(harness.Service.Applied);
        Assert.Contains(vm.Rows, r => r.WindowName == "Nightly");
    }

    [Fact]
    public async Task AWindowRemovedSinceTheListWasReadStopsBeforeThePlan()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        harness.Service.Windows.RemoveAll(w => w.WindowName == "Nightly");
        await vm.RemoveWindowsCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Equal(MaintenanceWindowsViewModel.WindowGone("Workstations", "Nightly"), vm.StatusMessage);
    }

    [Fact]
    public async Task AFailedPrepareShowsAPlainMessageAndNoPlan()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        harness.Service.PrepareGate = new TaskCompletionSource();
        harness.Service.PrepareGate.SetException(new InvalidOperationException("raw provider text"));
        await vm.RemoveWindowsCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Equal(MaintenanceWindowsViewModel.PrepareFailed, vm.StatusMessage);
        Assert.DoesNotContain(harness.Log.Entries, e => e.Message.Contains("raw provider text"));
    }

    [Fact]
    public async Task AnUndoInThePlanWindowLeavesNoPageUndo()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Plans.RunUndo = true;
        var vm = await harness.OpenAsync();
        Select(vm, "Weekend");
        await vm.ToggleEnabledCommand.ExecuteAsync(null);
        Assert.Equal(2, harness.Service.Applied.Count);
        Assert.False(vm.HasUndo);
        Assert.Equal("Disabled", Row(vm, "Weekend").Status);
        Assert.Contains("Undo: 1 changed, 0 failed.", vm.ResultSummary);
    }

    // --- Apply template --------------------------------------------------------------------

    [Fact]
    public async Task ApplyTemplateListsEveryCollectionAndSkipsTheOneWithTheName()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        vm.SetSelectedCoverage(vm.Coverage.Where(c => c.Name is "Kiosks" or "Servers" or "Workstations" or "All Systems"));
        harness.Prompts.ApplyTemplate = dialog =>
        {
            Assert.DoesNotContain(dialog.Targets, t => t.Collection.IsBuiltIn);
            Assert.Equal(3, dialog.CheckedCount);
            dialog.SelectedTemplate = dialog.Templates.Single(t => t.Name == "Nightly");
            FakeMaintenancePrompts.Apply(dialog);
            Assert.True(dialog.Applied);
        };
        await vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);

        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal("Create 2 windows", plan.ActionName);
        Assert.Equal(["Skip", "Create", "Create"], plan.Items.Select(i => i.Action));
        Assert.Equal("Workstations: Nightly", plan.Items[0].Target);
        Assert.Contains("already has a window named Nightly", plan.Items[0].Detail);
        Assert.All(plan.Items.Skip(1), i => Assert.StartsWith("From template Nightly.", i.Detail));
        var changes = Assert.Single(harness.Service.Applied);
        Assert.Equal(["Kiosks", "Servers"], changes.Select(c => c.CollectionName));
        Assert.All(changes, c => Assert.Equal(ChangeKind.ApplyTemplate, c.Kind));
        Assert.Equal(["Skipped", "Changed", "Changed"], vm.LastResults.Select(r => r.OutcomeText));
        Assert.Equal(1, harness.History.Entries.Single().Counts["skipped"]);
    }

    [Fact]
    public async Task ApplyTemplateCanRenameOnACollectionThatHasTheName()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        harness.Prompts.ApplyTemplate = dialog =>
        {
            dialog.SelectedTemplate = dialog.Templates.Single(t => t.Name == "Nightly");
            foreach (var t in dialog.Targets.Where(t => t.Name == "Workstations")) t.IsChecked = true;
            dialog.RenameExisting = true;
            FakeMaintenancePrompts.Apply(dialog);
        };
        vm.SelectedTemplate = vm.Templates.Single(t => t.Name == "Nightly");
        await vm.ApplySelectedTemplateCommand.ExecuteAsync(null);
        var change = Assert.Single(Assert.Single(harness.Service.Applied));
        Assert.Equal("Nightly (2)", change.WindowName);
        Assert.Contains("Named Nightly (2) because Nightly exists.", Assert.Single(harness.Plans.Shown).Items[0].Detail);
    }

    [Fact]
    public async Task ApplyTemplateWhereEveryCollectionHasTheNameShowsNoPlan()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        harness.Prompts.ApplyTemplate = dialog =>
        {
            dialog.SelectedTemplate = dialog.Templates.Single(t => t.Name == "Nightly");
            foreach (var t in dialog.Targets.Where(t => t.Name == "Workstations")) t.IsChecked = true;
            FakeMaintenancePrompts.Apply(dialog);
            Assert.False(dialog.Applied);
            Assert.Equal(MaintenanceWindowsViewModel.AllSkipped("Nightly"), dialog.ValidationMessage);
        };
        await vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
    }

    [Fact]
    public async Task APartlyFailedBulkApplyKeepsItsResultsAndUndoesOnlyWhatItCreated()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.Fail = c => c.CollectionName == "Servers" ? "Access denied. The console role needs collection modify rights." : null;
        var vm = await harness.OpenAsync();
        harness.Prompts.ApplyTemplate = dialog =>
        {
            dialog.SelectedTemplate = dialog.Templates.Single(t => t.IsShipped);
            foreach (var t in dialog.Targets) t.IsChecked = true;
            FakeMaintenancePrompts.Apply(dialog);
        };
        await vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);
        Assert.Equal(["Changed", "Failed", "Changed"], vm.LastResults.Select(r => r.OutcomeText));
        Assert.Equal(SiteOperationOutcome.PartlySucceeded, harness.History.Entries.Single().Outcome);

        harness.Service.Fail = null;
        await vm.UndoLastCommand.ExecuteAsync(null);
        var undo = harness.Service.Applied[1];
        Assert.Equal(["Kiosks", "Workstations"], undo.Select(c => c.CollectionName));
        Assert.All(undo, c => Assert.Equal(ChangeKind.Remove, c.Kind));
        Assert.DoesNotContain(harness.Service.Windows, w => w.WindowName == "Weekly Sunday 2 AM");
    }

    [Theory]
    [InlineData(false, "Skipped")]
    [InlineData(true, "Failed")]
    public async Task ACancelInTheMiddleOfABulkApplyKeepsThePartialResults(bool throwOnCancel, string notStarted)
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.ThrowOnCancel = throwOnCancel;
        var vm = await harness.OpenAsync();
        harness.Service.AfterChange = i => { if (i == 0) harness.Plans.LastViewModel!.RequestCancel(); };
        harness.Prompts.ApplyTemplate = dialog =>
        {
            dialog.SelectedTemplate = dialog.Templates.Single(t => t.IsShipped);
            foreach (var t in dialog.Targets) t.IsChecked = true;
            FakeMaintenancePrompts.Apply(dialog);
        };
        await vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);
        var result = harness.Plans.LastOutcome!.Result!;
        Assert.Equal(ChangePlanStop.Cancelled, result.Stop);
        Assert.Equal(["Changed", notStarted, notStarted], vm.LastResults.Select(r => r.OutcomeText));
        Assert.Equal(SiteOperationOutcome.Cancelled, harness.History.Entries.Single().Outcome);
        Assert.Single(harness.Service.Windows, w => w.WindowName == "Weekly Sunday 2 AM");
        Assert.True(vm.HasUndo);
    }

    [Fact]
    public async Task ADoubleApplyShowsOnePlan()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = await harness.OpenAsync();
        harness.Prompts.ApplyTemplate = dialog =>
        {
            foreach (var t in dialog.Targets.Where(t => t.Name == "Kiosks")) t.IsChecked = true;
            var first = dialog.ApplyCommand.ExecuteAsync(null);
            var second = dialog.ApplyCommand.ExecuteAsync(null);
            harness.Service.PrepareGate.SetResult();
            Task.WaitAll(first, second);
        };
        await vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);
        Assert.Single(harness.Plans.Shown);
        Assert.Single(harness.Service.Applied);
    }

    // --- Templates and export --------------------------------------------------------------

    [Fact]
    public async Task ATemplateScheduleShowsNoPlaceholderDateButTheNextOccurrenceIfAppliedToday()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Projector.UseReal = true;
        harness.Store.Templates.Clear();
        harness.Store.Templates.Add(new MaintenanceTemplate("shipped", "Sunday", "Every Sunday.",
            FakeMaintenanceService.Weekly(new DateTime(2000, 1, 2, 2, 0, 0)), "Any", true));
        var vm = await harness.OpenAsync();

        var text = Assert.Single(vm.Templates).ScheduleText;
        Assert.DoesNotContain("2000", text);
        Assert.DoesNotContain("effective", text);
        Assert.StartsWith("Occurs every 1 weeks on Sunday", text);
        Assert.Contains("Next if applied today: Sun 2026-10-25 02:00 to 06:00.", text);
    }

    [Fact]
    public async Task UserTemplatesAreCreatedEditedAndDeletedAndShippedOnesAreProtected()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        vm.SelectedTemplate = vm.Templates.Single(t => t.Template.IsShipped);
        Assert.False(vm.EditTemplateCommand.CanExecute(null));
        Assert.False(vm.DeleteTemplateCommand.CanExecute(null));
        Assert.True(vm.ApplySelectedTemplateCommand.CanExecute(null));

        harness.Prompts.Editor = editor =>
        {
            Assert.True(editor.IsTemplate);
            editor.Name = "Monthly first Saturday";
            editor.Description = "Servers.";
            editor.Kind = MaintenanceScheduleKind.MonthlyByWeekday;
            editor.DayOfWeek = DayOfWeek.Saturday;
            editor.WeekOrder = 1;
            FakeMaintenancePrompts.Save(editor);
        };
        vm.NewTemplateCommand.Execute(null);
        await WaitFor(() => vm.Templates.Count == 3);
        var created = harness.Store.Templates.Single(t => t.Name == "Monthly first Saturday");
        Assert.False(created.IsShipped);
        Assert.Equal((DayOfWeek.Saturday, 1), (created.Schedule.DayOfWeek!.Value, created.Schedule.WeekOrder!.Value));
        Assert.Empty(harness.Plans.Shown);

        vm.SelectedTemplate = vm.Templates.Single(t => t.Name == "Monthly first Saturday");
        await vm.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.DoesNotContain(harness.Store.Templates, t => t.Name == "Monthly first Saturday");
        Assert.Equal(2, vm.Templates.Count);

        harness.Prompts.ConfirmDelete = false;
        vm.SelectedTemplate = vm.Templates.Single(t => !t.Template.IsShipped);
        await vm.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Templates.Count);
        Assert.Equal(1, harness.Store.Deletes);
    }

    [Fact]
    public async Task ExportWritesTheShownWindowsAndOpensTheFolder()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        vm.TextFilter = "Patch";
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(["Patch Tuesday"], Assert.Single(harness.Reports.Written).Select(w => w.WindowName));
        Assert.Equal([@"C:\data\MaintenanceWindows\Reports"], harness.Prompts.OpenedFolders);
        Assert.StartsWith("Saved the CSV report to", vm.StatusMessage);

        vm.TextFilter = "nothing matches";
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal(MaintenanceWindowsViewModel.NothingToExport, vm.StatusMessage);
        Assert.Single(harness.Reports.Written);
    }

    // --- Preferences and log ---------------------------------------------------------------

    [Fact]
    public async Task TheTabAndFiltersComeBackOnTheNextStartAndOlderFilesUseTheDefaults()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Assert.Equal(0, vm.SelectedTabIndex);
        Assert.True(vm.OnlyCollectionsWithWindows);
        vm.SelectedTabIndex = 1;
        vm.TypeFilter = MaintenanceWindowsText.SoftwareUpdates;
        vm.TextFilter = "Patch";
        vm.CoverageWithoutWindowOnly = true;
        await vm.OnNavigatedFromAsync();
        Assert.Equal((1, "SoftwareUpdates", "Patch", true), (harness.Saved!.SelectedTab, harness.Saved.TypeFilter, harness.Saved.TextFilter, harness.Saved.CoverageWithoutWindowOnly));

        var again = harness.Create();
        await again.OnNavigatedToAsync();
        Assert.Equal(1, again.SelectedTabIndex);
        Assert.Equal(["Patch Tuesday"], again.Rows.Select(r => r.WindowName));
        Assert.Equal(["All Systems", "Kiosks"], again.Coverage.Select(c => c.Name));

        harness.Saved = new MaintenanceWindowsSettings { SelectedTab = 9, TypeFilter = "Bogus", EnabledFilter = "Maybe" };
        var odd = await harness.OpenAsync();
        Assert.Equal((0, "", MaintenanceWindowsViewModel.All), (odd.SelectedTabIndex, odd.TypeFilter, odd.EnabledFilter));
    }

    [Fact]
    public void ASettingsFileFromBeforeThePageLoadsWithoutPageChoices()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "SiteCode": "LAB", "DetectionTester": { "Hive": "HKCU" } }""")!;
        Assert.Null(settings.MaintenanceWindows);
        Assert.DoesNotContain("MaintenanceWindows", System.Text.Json.JsonSerializer.Serialize(settings));
    }

    [Fact]
    public async Task TheLogHoldsCountsAndOutcomesOnly()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Patch Tuesday");
        await vm.ToggleEnabledCommand.ExecuteAsync(null);
        Assert.All(harness.Log.Entries, e => Assert.Equal(MaintenanceWindowsViewModel.LogArea, e.Area));
        Assert.All(harness.Log.Entries, e =>
        {
            Assert.DoesNotContain("Patch Tuesday", e.Message);
            Assert.DoesNotContain("Servers", e.Message);
        });
        Assert.Contains(harness.Log.Entries, e => e.Message == "Toggle: outcome=Succeeded changed=1 failed=0 skipped=0.");
    }

    [Fact]
    public void FreeNameCountsUpFromTwo()
    {
        Assert.Equal("A", MaintenanceWindowsViewModel.FreeName("A", new HashSet<string>()));
        Assert.Equal("A (3)", MaintenanceWindowsViewModel.FreeName("A", new HashSet<string>(["A", "A (2)"], StringComparer.OrdinalIgnoreCase)));
    }

    // --- State that changes between the list read and the write ----------------------------

    private static void SetEnabledOnSite(MaintenanceWindowsHarness harness, string window, bool enabled)
    {
        lock (harness.Service.Windows)
        {
            var index = harness.Service.Windows.FindIndex(w => w.WindowName == window);
            harness.Service.Windows[index] = harness.Service.Windows[index] with { Enabled = enabled };
        }
    }

    [Theory]
    [InlineData("Nightly", ChangeKind.Update)]
    [InlineData("Nightly clients", ChangeKind.Rename)]
    public async Task EditAndRenameKeepTheEnabledStateConfigMgrHasAtSave(string newName, ChangeKind kind)
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        SetEnabledOnSite(harness, "Nightly", false);
        harness.Prompts.Editor = editor =>
        {
            editor.Name = newName;
            editor.DurationHours = 2;
            FakeMaintenancePrompts.Save(editor);
        };
        await vm.EditWindowCommand.ExecuteAsync(null);

        var change = Assert.Single(Assert.Single(harness.Service.Applied));
        Assert.Equal(kind, change.Kind);
        Assert.False(change.After!.Enabled);
        Assert.False(harness.Service.Windows.Single(w => w.WindowName == newName).Enabled);
        Assert.Equal("Disabled", Row(vm, newName).Status);
    }

    [Fact]
    public async Task UndoRunsWithoutAStateCheckSoAStaleReadCannotBlockIt()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        await vm.RemoveWindowsCommand.ExecuteAsync(null);
        Assert.True(vm.HasUndo);

        harness.Service.PrepareFails = _ => new InvalidOperationException("stale");
        var prepares = harness.Service.PrepareCalls;
        await vm.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal(prepares, harness.Service.PrepareCalls);
        Assert.Equal(2, harness.Plans.Shown.Count);
        Assert.Equal(ChangeKind.Create, Assert.Single(harness.Service.Applied[1]).Kind);
        Assert.Contains(harness.Service.Windows, w => w.WindowName == "Nightly");
    }

    [Fact]
    public async Task InABulkPlanAnItemWhoseStateCheckFailsIsASkipLineWithAPlainReason()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Patch Tuesday", "Weekend", "Nightly");
        const string gone = "The window Weekend does not exist on Servers any more. Refresh the list.";
        harness.Service.PrepareFails = c => c.WindowName switch
        {
            "Weekend" => new MaintenanceWindowException(gone),
            "Nightly" => new InvalidOperationException("raw provider text"),
            _ => null
        };
        await vm.ToggleEnabledCommand.ExecuteAsync(null);

        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal(["Skip", "Skip", "Disable"], plan.Items.Select(i => i.Action));
        Assert.Equal(["Servers: Weekend", "Workstations: Nightly"], plan.Items.Take(2).Select(i => i.Target));
        Assert.Equal(gone, plan.Items[0].Detail);
        Assert.Equal(MaintenanceWindowsViewModel.PrepareFailed, plan.Items[1].Detail);
        Assert.Equal("Disable 1 window", plan.ActionName);
        Assert.Equal(["Patch Tuesday"], Assert.Single(harness.Service.Applied).Select(c => c.WindowName));
        Assert.Equal(["Skipped", "Skipped", "Changed"], vm.LastResults.Select(r => r.OutcomeText));
        Assert.DoesNotContain(harness.Log.Entries, e => e.Message.Contains("raw provider text"));
    }

    [Fact]
    public async Task ABulkPlanWhoseEveryStateCheckFailsShowsNoPlanAndTheFirstReason()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Patch Tuesday", "Weekend");
        harness.Service.PrepareFails = c => new MaintenanceWindowException($"Gone: {c.WindowName}.");
        await vm.ToggleEnabledCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Equal("Gone: Patch Tuesday.", vm.StatusMessage);
    }

    [Fact]
    public async Task AWriteAfterARefreshThatReadTheOldStateShowsTheNewState()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        Select(vm, "Weekend");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.ListWindowsGate = gate;
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        await WaitFor(() => Volatile.Read(ref harness.Service.WindowReads) == 2);

        var toggle = vm.ToggleEnabledCommand.ExecuteAsync(null);
        await WaitFor(() => harness.Service.Applied.Count == 1);
        harness.Service.ListWindowsGate = null;
        gate.SetResult();
        await Task.WhenAll(refresh, toggle);

        Assert.True(harness.Service.Windows.Single(w => w.WindowName == "Weekend").Enabled);
        Assert.Equal("Enabled", Row(vm, "Weekend").Status);
        Assert.Equal(3, harness.Service.ListCalls);
    }

    [Fact]
    public async Task ADeclinedApplyPlanSaysSoInTheDialog()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Plans.Confirm = false;
        var vm = await harness.OpenAsync();
        MaintenanceApplyTemplateViewModel? shown = null;
        harness.Prompts.ApplyTemplate = dialog =>
        {
            shown = dialog;
            foreach (var t in dialog.Targets.Where(t => t.Name == "Kiosks")) t.IsChecked = true;
            FakeMaintenancePrompts.Apply(dialog);
        };
        await vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);
        Assert.Single(harness.Plans.Shown);
        Assert.False(shown!.Applied);
        Assert.Equal(MaintenanceWindowsViewModel.NotConfirmed, shown.ValidationMessage);
    }

    // --- Filter ----------------------------------------------------------------------------

    [Fact]
    public async Task TheTextFilterKeepsTheRowsItStillShowsAndDropsHiddenRowsFromTheSelection()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        var patch = Row(vm, "Patch Tuesday");
        var weekend = Row(vm, "Weekend");
        Select(vm, "Patch Tuesday", "Weekend");
        var resets = 0;
        vm.Rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        };

        vm.TextFilter = "Servers";
        Assert.Same(patch, Row(vm, "Patch Tuesday"));
        Assert.Same(weekend, Row(vm, "Weekend"));
        Assert.Same(patch, vm.SelectedRow);
        Assert.Equal(["Patch Tuesday", "Weekend"], vm.SelectedWindows.Select(r => r.WindowName));

        vm.TextFilter = "weekend";
        Assert.Equal(["Weekend"], vm.Rows.Select(r => r.WindowName));
        Assert.Null(vm.SelectedRow);
        Assert.Equal(["Weekend"], vm.SelectedWindows.Select(r => r.WindowName));

        vm.TextFilter = "";
        Assert.Equal(["Patch Tuesday", "Weekend", "Nightly"], vm.Rows.Select(r => r.WindowName));
        Assert.Same(weekend, Row(vm, "Weekend"));
        Assert.Equal(0, resets);
    }

    // --- Page choices ----------------------------------------------------------------------

    [Fact]
    public async Task PageChoiceSavesRunOneAtATimeAndTheNewestChoiceIsWrittenLast()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        using var hold = new ManualResetEventSlim(false);
        var written = new List<MaintenanceWindowsSettings>();
        var running = 0;
        var overlapped = false;
        harness.BeforeSave = s =>
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            if (s.SelectedTab == 1 && s.TypeFilter == "") hold.Wait(TimeSpan.FromSeconds(10));
            lock (written) written.Add(s);
            Interlocked.Decrement(ref running);
        };

        vm.SelectedTabIndex = 1;
        await WaitFor(() => Volatile.Read(ref running) == 1);
        vm.TypeFilter = MaintenanceWindowsText.SoftwareUpdates;
        vm.TextFilter = "Patch";
        await Task.Delay(50);
        hold.Set();
        await vm.OnNavigatedFromAsync();

        Assert.False(overlapped);
        Assert.Equal((1, "SoftwareUpdates", "Patch"), (harness.Saved!.SelectedTab, harness.Saved.TypeFilter, harness.Saved.TextFilter));
        lock (written) Assert.Same(harness.Saved, written[^1]);
    }

    [Fact]
    public async Task LeavingThePageWaitsForTheSaveOfTheChoices()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        harness.BeforeSave = _ => Thread.Sleep(100);
        vm.CoverageWithoutWindowOnly = true;
        await vm.OnNavigatedFromAsync();
        Assert.True(harness.Saved!.CoverageWithoutWindowOnly);
    }

    [Fact]
    public async Task AnEditorSizeFromTheSettingsThatIsNotAFiniteNumberIsIgnored()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Saved = new MaintenanceWindowsSettings { EditorWidth = double.PositiveInfinity, EditorHeight = double.NaN };
        var vm = await harness.OpenAsync();
        MaintenanceWindowEditorViewModel? shown = null;
        harness.Prompts.Editor = editor => shown = editor;
        await vm.NewWindowCommand.ExecuteAsync(null);
        Assert.Equal((760d, 720d), (shown!.Width, shown.Height));
    }

    // ---- A plan window force closed while the run still works ----------------------------------

    [Fact]
    public async Task AWindowClosedDuringARunKeepsEveryWriteBlockedUntilTheRunEnds()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var harness = new MaintenanceWindowsHarness();
        harness.Service.InConfirmedRun = null;
        // The second run is the one that detaches; the first one ran to its end and left an Undo.
        var dialogs = new DetachingPlanDialogs(() => { lock (harness.Service.Applied) return harness.Service.Applied.Count > 1; }) { Before = harness.Plans };
        var vm = new MaintenanceWindowsViewModel(new MaintenanceWindowsPageServices(
            harness.Service, harness.Projector, harness.Store, harness.Reports, harness.Prompts, dialogs, harness.Writer(),
            () => harness.Settings, () => harness.Saved, s => harness.Saved = s, harness.Log, () => MaintenanceWindowsHarness.Now));
        await vm.OnNavigatedToAsync();
        Select(vm, "Nightly");
        await vm.RemoveWindowsCommand.ExecuteAsync(null);
        Assert.True(vm.HasUndo);
        Assert.True(vm.UndoLastCommand.CanExecute(null));
        harness.Service.ApplyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Select(vm, "Weekend");

        await vm.RemoveWindowsCommand.ExecuteAsync(null);

        Assert.True(dialogs.Dialog!.IsDetached);
        Assert.False(vm.RemoveWindowsCommand.CanExecute(null));
        Assert.False(vm.ToggleEnabledCommand.CanExecute(null));
        Assert.False(vm.EditWindowCommand.CanExecute(null));
        Assert.False(vm.UndoLastCommand.CanExecute(null));
        Assert.Equal(MaintenanceWindowsViewModel.BackgroundRunText, vm.StatusMessage);

        harness.Service.ApplyGate.SetResult();
        Assert.True(SpinWait.SpinUntil(() => vm.StatusMessage == MaintenanceWindowsViewModel.BackgroundRunEndedText
            && vm.RemoveWindowsCommand.CanExecute(null), TimeSpan.FromSeconds(10)));
        // The cancelled run changed nothing, so it leaves no Undo and the Undo of the earlier run is gone.
        Assert.False(vm.HasUndo);
        Assert.False(vm.UndoLastCommand.CanExecute(null));
    }
}
