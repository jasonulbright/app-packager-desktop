using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>The page against the shapes the real services return and throw.</summary>
public sealed class MaintenanceWindowsServiceShapeTests
{
    private const string HourlyText = "Occurs every 1 hours effective 10/1/2026 12:00 AM";

    private static MaintenanceWindowRow Row(MaintenanceWindowsViewModel vm, string window) => vm.Rows.Single(r => r.WindowName == window);

    private static void Select(MaintenanceWindowsViewModel vm, params string[] windows)
    {
        var rows = windows.Select(w => Row(vm, w)).ToList();
        vm.SelectedRow = rows[0];
        vm.SetSelectedWindows(rows);
    }

    /// <summary>A window as the service returns one whose schedule the model cannot hold.</summary>
    private static MaintenanceWindowInfo Hourly(CollectionInfo collection) => new(collection.CollectionId, collection.Name, "Hourly", "{900}", "Any", true,
        new MaintenanceSchedule(MaintenanceScheduleKind.OneTime, DateTime.MinValue, TimeSpan.Zero, false, 1, null, null, null, 0, null),
        HourlyText, null, HourlyText);

    [Fact]
    public async Task AWindowWhoseScheduleTheModelCannotHoldCanOnlyBeEnabledOrDisabled()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.Windows.Add(Hourly(harness.Kiosks));
        var vm = await harness.OpenAsync();

        var row = Row(vm, "Hourly");
        Assert.False(row.IsScheduleHeld);
        Assert.Equal((MaintenanceWindowsText.OtherSchedule, "", "None", HourlyText), (row.Recurrence, row.Duration, row.Next, row.ScheduleText));

        Select(vm, "Hourly");
        Assert.False(vm.EditWindowCommand.CanExecute(null));
        Assert.False(vm.RemoveWindowsCommand.CanExecute(null));
        Assert.True(vm.ToggleEnabledCommand.CanExecute(null));
        Assert.Equal(MaintenanceWindowsViewModel.ScheduleNotHeld, vm.EditTooltip);
        Assert.Equal(MaintenanceWindowsViewModel.ScheduleNotHeld, vm.RemoveTooltip);

        Assert.Equal(HourlyText, vm.Properties.Single(p => p.Name == "Schedule").Value);
        Assert.DoesNotContain(vm.Properties, p => p.Name is "Start" or "Duration" or "Time");
        Assert.Equal([MaintenanceWindowsViewModel.NoOccurrencesShown], vm.NextOccurrences);

        await vm.EditWindowCommand.ExecuteAsync(null);
        Assert.Equal(0, harness.Prompts.EditorShows);

        await vm.ToggleEnabledCommand.ExecuteAsync(null);
        var change = Assert.Single(Assert.Single(harness.Service.Applied));
        Assert.Equal(ChangeKind.SetEnabled, change.Kind);
        Assert.False(Row(vm, "Hourly").IsEnabled);

        Select(vm, "Nightly", "Hourly");
        Assert.False(vm.RemoveWindowsCommand.CanExecute(null));
        Assert.True(vm.ToggleEnabledCommand.CanExecute(null));
        Select(vm, "Nightly");
        Assert.True(vm.RemoveWindowsCommand.CanExecute(null));
        Assert.True(vm.EditWindowCommand.CanExecute(null));
        Assert.Null(vm.EditTooltip);
        Assert.Null(vm.RemoveTooltip);
    }

    [Fact]
    public async Task ATemplateIsAppliedWithItsTimeOnTodaysDate()
    {
        var harness = new MaintenanceWindowsHarness();
        var placeholder = new MaintenanceTemplate("daily", "Daily 03:30", "", new MaintenanceSchedule(MaintenanceScheduleKind.Daily,
            MaintenanceTemplateStore.PlaceholderDate.AddHours(3).AddMinutes(30), TimeSpan.FromHours(2), false, 1, null, null, null, 0, null), "Any", true);
        harness.Store.Templates.Add(placeholder);
        var vm = await harness.OpenAsync();
        harness.Prompts.ApplyTemplate = dialog =>
        {
            dialog.SelectedTemplate = dialog.Templates.Single(t => t.Id == "daily");
            foreach (var t in dialog.Targets.Where(t => t.Name == "Kiosks")) t.IsChecked = true;
            FakeMaintenancePrompts.Apply(dialog);
        };
        await vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);

        var after = Assert.Single(Assert.Single(harness.Service.Applied)).After!;
        Assert.Equal(placeholder.Schedule.EffectiveOn(MaintenanceWindowsHarness.Now.Date), after.Schedule);
        Assert.Equal(new DateTime(2026, 10, 19, 3, 30, 0), after.Schedule.Start);
        Assert.Equal(harness.Projector.Describe(after.Schedule), after.ScheduleText);
        Assert.Contains("2026-10-20 03:30", Assert.Single(harness.Plans.Shown).Items[0].Detail);
    }

    [Fact]
    public async Task APlainMessageFromTheServiceReachesThePageAndTheEditor()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.ListFailure = new MaintenanceWindowException("The maintenance windows could not be read from ConfigMgr. The log has the details.");
        var vm = await harness.OpenAsync();
        Assert.Equal("The maintenance windows could not be read from ConfigMgr. The log has the details.", vm.ConnectionMessage);

        harness.Service.ListFailure = null;
        await vm.RefreshCommand.ExecuteAsync(null);
        harness.Service.PrepareGate = new TaskCompletionSource();
        harness.Service.PrepareGate.SetException(new MaintenanceWindowException("The collection Kiosks (LAB00012) does not exist any more. Refresh the list."));
        string? shown = null;
        harness.Prompts.Editor = editor =>
        {
            editor.Name = "New";
            editor.SelectedCollection = editor.Collections.Single(c => c.Collection.Name == "Kiosks");
            FakeMaintenancePrompts.Save(editor);
            shown = editor.ValidationMessage;
        };
        await vm.NewWindowCommand.ExecuteAsync(null);
        Assert.Equal("The collection Kiosks (LAB00012) does not exist any more. Refresh the list.", shown);
        Assert.Empty(harness.Plans.Shown);
    }

    [Fact]
    public async Task TemplateStoreRefusalsShowTheirPlainMessage()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        harness.Store.SaveFailure = new MaintenanceWindowException("The Maintenance Windows templates folder is a junction or symbolic link. The template was not saved or deleted.");
        string? shown = null;
        harness.Prompts.Editor = editor =>
        {
            editor.Name = "Mine";
            FakeMaintenancePrompts.Save(editor);
            shown = editor.ValidationMessage;
        };
        vm.NewTemplateCommand.Execute(null);
        Assert.Equal(harness.Store.SaveFailure.Message, shown);

        harness.Store.SaveFailure = new IOException(@"The process cannot access the file C:\Users\someone\secret.json.");
        vm.NewTemplateCommand.Execute(null);
        Assert.Equal(MaintenanceWindowsViewModel.TemplateSaveFailed, shown);

        harness.Store.DeleteFailure = new MaintenanceWindowException("A shipped template cannot be changed or deleted.");
        vm.SelectedTemplate = vm.Templates.Single(t => !t.Template.IsShipped);
        await vm.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.Equal("A shipped template cannot be changed or deleted.", vm.StatusMessage);
    }

    [Fact]
    public async Task AnExportFailureShowsAPlainMessageWithoutRawText()
    {
        var harness = new MaintenanceWindowsHarness();
        var vm = await harness.OpenAsync();
        harness.Reports.Failure = new MaintenanceWindowException(MaintenanceReportWriter.LinkMessage);
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(MaintenanceReportWriter.LinkMessage, vm.StatusMessage);

        harness.Reports.Failure = new IOException(@"Access to C:\Users\someone\AppData is denied.");
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal("The HTML report could not be written. The log has the details.", vm.StatusMessage);
        Assert.DoesNotContain(harness.Log.Entries, e => e.Message.Contains("someone"));
        Assert.Empty(harness.Prompts.OpenedFolders);
    }

    [Fact]
    public async Task AnOutcomeTheServiceMarksAsSkippedCountsAsSkipped()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.Fail = c => c.CollectionName == "Servers" ? MaintenanceWindowService.SkippedPrefix + "collection Servers already has a window named Weekly Sunday 2 AM." : null;
        var vm = await harness.OpenAsync();
        harness.Prompts.ApplyTemplate = dialog =>
        {
            dialog.SelectedTemplate = dialog.Templates.Single(t => t.IsShipped);
            foreach (var t in dialog.Targets.Where(t => t.Name is "Kiosks" or "Servers")) t.IsChecked = true;
            FakeMaintenancePrompts.Apply(dialog);
        };
        await vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);
        Assert.Equal(["Changed", "Skipped"], vm.LastResults.Select(r => r.OutcomeText));
        var entry = Assert.Single(harness.History.Entries);
        Assert.Equal((1, 0, 1), (entry.Counts["changed"], entry.Counts["failed"], entry.Counts["skipped"]));
        Assert.Equal(SiteOperationOutcome.Succeeded, entry.Outcome);
    }

    [Fact]
    public async Task ARenameWhoseRemoveFailedCanBeUndoneByRemovingTheNewWindow()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.RenameRemoveFails = true;
        var vm = await harness.OpenAsync();
        Select(vm, "Nightly");
        harness.Prompts.Editor = editor =>
        {
            editor.Name = "Nightly 2";
            FakeMaintenancePrompts.Save(editor);
        };
        await vm.EditWindowCommand.ExecuteAsync(null);
        Assert.True(vm.HasUndo);
        await vm.UndoLastCommand.ExecuteAsync(null);
        var undo = Assert.Single(harness.Service.Applied[1]);
        Assert.Equal((ChangeKind.Remove, "Nightly 2"), (undo.Kind, undo.WindowName));
        Assert.DoesNotContain(harness.Service.Windows, w => w.WindowName == "Nightly 2");
        Assert.Contains(harness.Service.Windows, w => w.WindowName == "Nightly");
    }
}
