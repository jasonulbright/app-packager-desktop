using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>
/// The page over the real service, projector, template store, report writer and plan writer;
/// only ConfigMgr itself is the in-memory session.
/// </summary>
public sealed class MaintenanceWindowsEndToEndTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0);
    private const string RawError = "System.Management.ManagementException: Generic failure at Microsoft.ConfigurationManagement.Foo()";
    private const string Shipped = "Weekly Sunday 2 AM";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mw-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMaintenanceSession _site = new();
    private readonly ScriptedPlanDialogs _plans = new();
    private readonly FakeMaintenancePrompts _prompts = new();
    private readonly RecordingSiteHistory _history = new();
    private readonly RecordingMaintenanceLog _log = new();
    private readonly ScheduleProjector _projector = new(TimeZoneInfo.Utc);
    private readonly MaintenanceTemplateStore _store;
    private readonly MaintenanceWindowsViewModel _vm;

    public MaintenanceWindowsEndToEndTests()
    {
        _site.Collections.Add(new FakeMaintenanceSession.Collection("LAB00010", "Servers", "/Patching/Servers", 2));
        _site.Collections.Add(new FakeMaintenanceSession.Collection("LAB00011", "Workstations", "/Patching/Clients", 5));
        _site.Collections.Add(new FakeMaintenanceSession.Collection("LAB00012", "Kiosks", "", 1));
        _site.Collections.Add(new FakeMaintenanceSession.Collection("SMS00001", "All Systems", "", 8));
        _site.Add("LAB00010", "Patch night", ScheduleToken.Encode(new MaintenanceSchedule(MaintenanceScheduleKind.Weekly, new DateTime(2026, 10, 3, 22, 0, 0),
            TimeSpan.FromHours(3), false, 1, DayOfWeek.Saturday, null, null, 0, null)));
        _site.Add("LAB00010", "Hourly", "7AC3BE0008100800");
        _site.Add("LAB00011", Shipped, ScheduleToken.Encode(new MaintenanceSchedule(MaintenanceScheduleKind.Daily, new DateTime(2026, 10, 1, 1, 0, 0),
            TimeSpan.FromHours(1), false, 1, null, null, null, 0, null)));
        _site.Throw = call =>
        {
            if (call.Script == "WriteMaintenanceWindow") Assert.True(_plans.InRun, "A write ran outside a confirmed change plan.");
            return null;
        };

        var service = new MaintenanceWindowService(_site, _projector, _log, () => Now);
        _store = new MaintenanceTemplateStore(Path.Combine(_root, "Templates"), _log);
        var reports = new MaintenanceReportWriter(Path.Combine(_root, "Reports"), () => new DateTimeOffset(Now));
        var writer = new MaintenanceWindowWriter(service, _history, _log, () => "LAB");
        var settings = new AppSettings { SiteCode = "LAB", ProviderMachine = "cm01.lab.local" };
        _vm = new MaintenanceWindowsViewModel(new MaintenanceWindowsPageServices(service, _projector, _store, reports, _prompts, _plans, writer,
            () => settings, () => null, _ => { }, _log, () => Now));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private MaintenanceWindowRow Row(string window) => _vm.Rows.Single(r => r.WindowName == window);

    private void Select(params string[] windows)
    {
        var rows = windows.Select(Row).ToList();
        _vm.SelectedRow = rows[0];
        _vm.SetSelectedWindows(rows);
    }

    private FakeMaintenanceSession.Window SiteWindow(string collectionId, string name) => _site.Windows.Single(w => w.CollectionId == collectionId && w.Name == name);

    /// <summary>Every text the page shows; none may carry raw exception text.</summary>
    private void NoRawTextOnThePage(params string?[] more)
    {
        var texts = new List<string?> { _vm.StatusMessage, _vm.ConnectionMessage, _vm.ResultSummary };
        texts.AddRange(_vm.LastResults.Select(r => r.Message));
        texts.AddRange(more);
        foreach (var text in texts.Where(t => t is not null))
        {
            Assert.DoesNotContain("Exception", text);
            Assert.DoesNotContain("Microsoft.ConfigurationManagement", text);
            Assert.DoesNotContain(" at ", text);
        }
    }

    private IEnumerable<string> Operations() => _site.Writes.Select(c => (string)c.Parameters["Operation"]!);

    [WindowFact]
    public async Task ThePageDrivesTheRealServiceThroughConfirmedPlansOnly()
    {
        await _vm.OnNavigatedToAsync();
        Assert.Equal("", _vm.ConnectionMessage);
        Assert.Equal(["Hourly", "Patch night", Shipped], _vm.Rows.Select(r => r.WindowName));
        Assert.Equal(@"Patching\Servers", Row("Patch night").FolderPath);
        Assert.Equal(5, _vm.Templates.Count(t => t.Template.IsShipped));

        // A window whose schedule the model cannot hold can only be enabled or disabled.
        Select("Hourly");
        Assert.False(Row("Hourly").IsScheduleHeld);
        Assert.False(_vm.EditWindowCommand.CanExecute(null));
        Assert.False(_vm.RemoveWindowsCommand.CanExecute(null));
        await _vm.ToggleEnabledCommand.ExecuteAsync(null);
        Assert.False(SiteWindow("LAB00010", "Hourly").Enabled);
        Assert.Equal("Disabled window Hourly on Servers.", Assert.Single(_vm.LastResults).Message);

        // Create: a declined plan writes nothing, a confirmed plan creates the window.
        _plans.Confirm = false;
        _prompts.Editor = editor =>
        {
            editor.SelectedCollection = editor.Collections.Single(c => c.Collection.Name == "Kiosks");
            editor.Name = "Created";
            Assert.Equal("", editor.ValidationMessage);
            FakeMaintenancePrompts.Save(editor);
            Assert.False(editor.Saved);
        };
        var writesBefore = _site.Writes.Count();
        await _vm.NewWindowCommand.ExecuteAsync(null);
        Assert.Equal(writesBefore, _site.Writes.Count());
        Assert.Equal(MaintenanceWindowsViewModel.NotConfirmed, _vm.StatusMessage);

        _plans.Confirm = true;
        _prompts.Editor = editor =>
        {
            editor.SelectedCollection = editor.Collections.Single(c => c.Collection.Name == "Kiosks");
            editor.Name = "Created";
            FakeMaintenancePrompts.Save(editor);
            Assert.True(editor.Saved);
        };
        await _vm.NewWindowCommand.ExecuteAsync(null);
        var created = SiteWindow("LAB00012", "Created");
        Assert.Equal("Created window Created on Kiosks.", Assert.Single(_vm.LastResults).Message);
        Assert.Equal(MaintenanceScheduleKind.Weekly, Row("Created").Window!.Schedule.Kind);

        // Rename creates the new window first and removes the old one after it.
        Select("Created");
        _prompts.Editor = editor =>
        {
            editor.Name = "Renamed";
            FakeMaintenancePrompts.Save(editor);
        };
        await _vm.EditWindowCommand.ExecuteAsync(null);
        Assert.Equal(["Create", "Remove"], Operations().TakeLast(2));
        Assert.Equal("Renamed", _site.Writes.SkipLast(1).Last().Parameters["Name"]);
        Assert.Equal(created.Id, _site.Writes.Last().Parameters["ServiceWindowId"]);
        Assert.DoesNotContain(_site.Windows, w => w.Name == "Created");
        Assert.Equal("Renamed window Created to Renamed on Kiosks. The window has a new ID.", Assert.Single(_vm.LastResults).Message);

        // Toggle.
        Select("Renamed");
        await _vm.ToggleEnabledCommand.ExecuteAsync(null);
        Assert.False(SiteWindow("LAB00012", "Renamed").Enabled);

        // Remove, then undo: the window comes back from the captured values with a new id.
        Select("Renamed");
        var captured = SiteWindow("LAB00012", "Renamed");
        var (capturedId, capturedToken) = (captured.Id, captured.Schedules);
        await _vm.RemoveWindowsCommand.ExecuteAsync(null);
        Assert.True(_plans.Shown[^1].IsDestructive);
        Assert.DoesNotContain(_site.Windows, w => w.Name == "Renamed");
        Assert.True(_vm.HasUndo);
        await _vm.UndoLastCommand.ExecuteAsync(null);
        var recreated = SiteWindow("LAB00012", "Renamed");
        Assert.Equal((capturedToken, false), (recreated.Schedules, recreated.Enabled));
        Assert.NotEqual(capturedId, recreated.Id);
        Assert.Contains(MaintenanceWindowWriter.RecreatedNote, Assert.Single(_plans.Shown[^1].Items).Detail);
        Assert.Equal("Created window Renamed on Kiosks.", Assert.Single(_vm.LastResults).Message);

        NoRawTextOnThePage();
    }

    [WindowFact]
    public async Task ApplyTemplateSkipsTakenNamesAndUsesTodayAsTheEffectiveDate()
    {
        await _vm.OnNavigatedToAsync();
        _site.Answer = call => (string)call.Parameters["Operation"]! == "Create" && (string)call.Parameters["CollectionId"]! == "LAB00010"
            ? ("name-taken", "") : null;
        _prompts.ApplyTemplate = dialog =>
        {
            dialog.SelectedTemplate = dialog.Templates.Single(t => t.Name == Shipped);
            Assert.DoesNotContain(dialog.Targets, t => t.Collection.IsBuiltIn);
            foreach (var t in dialog.Targets) t.IsChecked = true;
            FakeMaintenancePrompts.Apply(dialog);
        };
        await _vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);

        var plan = _plans.Shown.Single();
        Assert.Equal(["Skip", "Create", "Create"], plan.Items.Select(i => i.Action));
        Assert.Equal("Workstations: " + Shipped, plan.Items[0].Target);
        Assert.Equal(["Skipped", "Changed", "Skipped"], _vm.LastResults.Select(r => r.OutcomeText));
        Assert.StartsWith(MaintenanceWindowService.SkippedPrefix, _vm.LastResults[2].Message);

        var kiosk = SiteWindow("LAB00012", Shipped);
        var stored = ScheduleToken.Decode(kiosk.Schedules)!;
        Assert.Equal(new DateTime(2026, 10, 19, 2, 0, 0), stored.Start);
        Assert.Equal((MaintenanceScheduleKind.Weekly, DayOfWeek.Sunday), (stored.Kind, stored.DayOfWeek!.Value));

        var entry = _history.Entries.Single();
        Assert.Equal(("Maintenance Windows", "Apply template", "LAB", SiteOperationOutcome.Succeeded), (entry.Tool, entry.Action, entry.Target, entry.Outcome));
        Assert.Equal((1, 0, 2), (entry.Counts["changed"], entry.Counts["failed"], entry.Counts["skipped"]));

        await _vm.UndoLastCommand.ExecuteAsync(null);
        Assert.DoesNotContain(_site.Windows, w => w.CollectionId == "LAB00012" && w.Name == Shipped);
        Assert.Single(_site.Windows, w => w.CollectionId == "LAB00011" && w.Name == Shipped);
        NoRawTextOnThePage();
    }

    [WindowFact]
    public async Task ACancelMidRunKeepsThePartialResultsAndRecordsACancelledRun()
    {
        await _vm.OnNavigatedToAsync();
        _prompts.Editor = editor =>
        {
            editor.Name = "Nightly run";
            editor.Kind = MaintenanceScheduleKind.Daily;
            FakeMaintenancePrompts.Save(editor);
        };
        _vm.NewTemplateCommand.Execute(null);
        var template = _store.All().Single(t => t.Name == "Nightly run");
        Assert.True(File.Exists(Path.Combine(_root, "Templates", template.Id + ".json")));

        var writes = 0;
        _site.Throw = call =>
        {
            if (call.Script != "WriteMaintenanceWindow") return null;
            Assert.True(_plans.InRun);
            if (++writes == 1) _plans.LastViewModel!.RequestCancel();
            return null;
        };
        _prompts.ApplyTemplate = dialog =>
        {
            dialog.SelectedTemplate = dialog.Templates.Single(t => t.Name == "Nightly run");
            foreach (var t in dialog.Targets) t.IsChecked = true;
            FakeMaintenancePrompts.Apply(dialog);
        };
        await _vm.ApplyTemplateToSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, writes);
        Assert.Equal(ChangePlanStop.Cancelled, _plans.LastOutcome!.Result!.Stop);
        Assert.Equal(["Changed", "Skipped", "Skipped"], _vm.LastResults.Select(r => r.OutcomeText));
        Assert.Single(_site.Windows, w => w.Name == "Nightly run");
        var entry = _history.Entries.Single();
        Assert.Equal(SiteOperationOutcome.Cancelled, entry.Outcome);
        Assert.Equal((1, 0, 2), (entry.Counts["changed"], entry.Counts["failed"], entry.Counts["skipped"]));
        Assert.True(_vm.HasUndo);
        NoRawTextOnThePage();
    }

    [WindowFact]
    public async Task RawErrorsFromConfigMgrNeverReachThePage()
    {
        await _vm.OnNavigatedToAsync();
        _site.Throw = call => call.Script == "WriteMaintenanceWindow" ? new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, RawError) : null;
        Select("Patch night");
        await _vm.ToggleEnabledCommand.ExecuteAsync(null);
        var result = Assert.Single(_vm.LastResults);
        Assert.Equal("Failed", result.OutcomeText);
        Assert.Equal("ConfigMgr reported an error for window Patch night on Servers. The log has the details.", result.Message);
        Assert.True(SiteWindow("LAB00010", "Patch night").Enabled);
        Assert.Equal(SiteOperationOutcome.Failed, _history.Entries.Single().Outcome);
        NoRawTextOnThePage();

        _site.Throw = call => call.Script == "ListMaintenanceWindows" ? new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, RawError) : null;
        string? editorMessage = null;
        _prompts.Editor = editor =>
        {
            editor.SelectedCollection = editor.Collections.Single(c => c.Collection.Name == "Kiosks");
            editor.Name = "Blocked";
            FakeMaintenancePrompts.Save(editor);
            editorMessage = editor.ValidationMessage;
        };
        await _vm.NewWindowCommand.ExecuteAsync(null);
        Assert.Equal("The maintenance windows could not be read from ConfigMgr. The log has the details.", editorMessage);
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("The maintenance windows could not be read from ConfigMgr. The log has the details.", _vm.ConnectionMessage);
        NoRawTextOnThePage(editorMessage);
    }

    [WindowFact]
    public async Task TheHistoryHoldsCountsOnlyAndExportsAndTemplatesUseTheirFolders()
    {
        await _vm.OnNavigatedToAsync();
        Select("Patch night");
        await _vm.ToggleEnabledCommand.ExecuteAsync(null);
        await _vm.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal(["Toggle", "Undo Toggle"], _history.Entries.Select(e => e.Action));
        Assert.All(_history.Entries, e =>
        {
            Assert.Equal(("Maintenance Windows", "LAB"), (e.Tool, e.Target));
            Assert.Equal(["changed", "failed", "skipped"], e.Counts.Keys.Order());
        });
        Assert.All(_log.Entries, e =>
        {
            Assert.DoesNotContain("Patch night", e.Message);
            Assert.DoesNotContain("Servers", e.Message);
        });

        await _vm.ExportCsvCommand.ExecuteAsync(null);
        await _vm.ExportHtmlCommand.ExecuteAsync(null);
        var reports = Directory.GetFiles(Path.Combine(_root, "Reports"));
        Assert.Equal(2, reports.Length);
        Assert.Equal([Path.Combine(_root, "Reports"), Path.Combine(_root, "Reports")], _prompts.OpenedFolders);

        _vm.SelectedTemplate = _vm.Templates.First(t => t.Template.IsShipped);
        Assert.False(_vm.EditTemplateCommand.CanExecute(null));
        Assert.False(_vm.DeleteTemplateCommand.CanExecute(null));
        Assert.Throws<MaintenanceWindowException>(() => _store.Delete(_vm.SelectedTemplate.Template.Id));

        _prompts.Editor = editor =>
        {
            editor.Name = "Mine";
            FakeMaintenancePrompts.Save(editor);
        };
        _vm.NewTemplateCommand.Execute(null);
        var mine = _vm.Templates.Single(t => t.Name == "Mine");
        Assert.Equal("User", mine.Source);
        _vm.SelectedTemplate = mine;
        await _vm.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Templates")));
        Assert.DoesNotContain(_vm.Templates, t => t.Name == "Mine");
    }
}
