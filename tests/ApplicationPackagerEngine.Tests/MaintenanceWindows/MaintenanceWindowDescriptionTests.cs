using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>
/// The window description on the page, in the editor and in the plans, over the real service
/// and projector; only ConfigMgr itself is the in-memory session.
/// </summary>
public sealed class MaintenanceWindowDescriptionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0);
    private const string Own = "Patch the file servers first.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mw-desc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMaintenanceSession _site = new();
    private readonly ScriptedPlanDialogs _plans = new();
    private readonly FakeMaintenancePrompts _prompts = new();
    private readonly ScheduleProjector _projector = new(TimeZoneInfo.Utc);
    private readonly MaintenanceWindowService _service;
    private readonly MaintenanceWindowsViewModel _vm;

    public MaintenanceWindowDescriptionTests()
    {
        _site.Collections.Add(new FakeMaintenanceSession.Collection("LAB00010", "Servers", "/Patching/Servers", 2));
        var token = ScheduleToken.Encode(new MaintenanceSchedule(MaintenanceScheduleKind.Weekly, new DateTime(2026, 10, 3, 22, 0, 0),
            TimeSpan.FromHours(3), false, 1, DayOfWeek.Saturday, null, null, 0, null));
        _site.Add("LAB00010", "Own text", token, description: Own);
        _site.Add("LAB00010", "ConfigMgr text", token);
        _site.Add("LAB00010", "No text", token, description: "");
        _site.Throw = call =>
        {
            if (call.Script == "WriteMaintenanceWindow") Assert.True(_plans.InRun, "A write ran outside a confirmed change plan.");
            return null;
        };
        var log = new RecordingMaintenanceLog();
        _service = new MaintenanceWindowService(_site, _projector, log, () => Now);
        var store = new MaintenanceTemplateStore(Path.Combine(_root, "Templates"), log);
        var reports = new MaintenanceReportWriter(Path.Combine(_root, "Reports"), () => new DateTimeOffset(Now));
        var writer = new MaintenanceWindowWriter(_service, new RecordingSiteHistory(), log, () => "LAB");
        var settings = new AppSettings { SiteCode = "LAB", ProviderMachine = "cm01.lab.local" };
        _vm = new MaintenanceWindowsViewModel(new MaintenanceWindowsPageServices(_service, _projector, store, reports, _prompts, _plans, writer,
            () => settings, () => null, _ => { }, log, () => Now));
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

    [Fact]
    public async Task TheServiceHelperSeparatesTheOwnDescriptionFromConfigMgrsText()
    {
        await _vm.OnNavigatedToAsync();
        Assert.Equal(Own, MaintenanceWindowService.OwnDescription(Row("Own text").Window, _projector));
        Assert.NotEmpty(Row("ConfigMgr text").Window!.Description);
        Assert.Null(MaintenanceWindowService.OwnDescription(Row("ConfigMgr text").Window, _projector));
        Assert.Null(MaintenanceWindowService.OwnDescription(Row("No text").Window, _projector));
        Assert.Null(MaintenanceWindowService.OwnDescription(null, _projector));
    }

    [Fact]
    public async Task ThePropertiesShowTheOwnDescriptionOnly()
    {
        await _vm.OnNavigatedToAsync();
        Select("Own text");
        Assert.Equal(Own, Assert.Single(_vm.Properties, p => p.Name == "Description").Value);

        Select("ConfigMgr text");
        Assert.DoesNotContain(_vm.Properties, p => p.Name == "Description");

        Select("No text");
        Assert.DoesNotContain(_vm.Properties, p => p.Name == "Description");
    }

    [Fact]
    public async Task TheEditorShowsTheOwnDescriptionReadOnlyAndKeepsIt()
    {
        await _vm.OnNavigatedToAsync();
        MaintenanceWindowEditorViewModel? shown = null;
        _prompts.Editor = editor => shown = editor;
        Select("Own text");
        await _vm.EditWindowCommand.ExecuteAsync(null);
        Assert.NotNull(shown);
        Assert.True(shown!.ShowsWindowDescription);
        Assert.Equal(Own, shown.WindowDescription);
        Assert.Equal("The description of this window is kept when you save.", MaintenanceWindowEditorViewModel.DescriptionKeptNote);
        Assert.Equal(Own, shown.BuildWindow()!.Description);

        foreach (var name in new[] { "ConfigMgr text", "No text" })
        {
            shown = null;
            Select(name);
            await _vm.EditWindowCommand.ExecuteAsync(null);
            Assert.False(shown!.ShowsWindowDescription);
            Assert.Equal("", shown.WindowDescription);
        }
    }

    private async Task<ChangePlanItem> EditPlanItem(string window, string? newName = null)
    {
        _plans.Confirm = false;
        _prompts.Editor = editor =>
        {
            editor.DurationHours = 2;
            if (newName is not null) editor.Name = newName;
            FakeMaintenancePrompts.Save(editor);
        };
        Select(window);
        await _vm.EditWindowCommand.ExecuteAsync(null);
        return Assert.Single(_plans.Shown[^1].Items);
    }

    [Fact]
    public async Task TheEditAndRenamePlansSayTheDescriptionIsKeptWhenThereIsOne()
    {
        await _vm.OnNavigatedToAsync();
        Assert.Contains(MaintenanceWindowsViewModel.KeepsDescription, (await EditPlanItem("Own text")).Detail);
        Assert.Contains(MaintenanceWindowsViewModel.KeepsDescription, (await EditPlanItem("Own text", "Own text renamed")).Detail);
        Assert.DoesNotContain(MaintenanceWindowsViewModel.KeepsDescription, (await EditPlanItem("ConfigMgr text")).Detail);
        Assert.DoesNotContain(MaintenanceWindowsViewModel.KeepsDescription, (await EditPlanItem("No text", "Renamed")).Detail);
        Assert.Empty(_site.Writes);
    }

    [Fact]
    public async Task TheUndoPlanOfARemoveSaysTheDescriptionIsKeptAndTheUndoKeepsIt()
    {
        await _vm.OnNavigatedToAsync();
        Select("Own text", "ConfigMgr text");
        await _vm.RemoveWindowsCommand.ExecuteAsync(null);
        Assert.Equal(2, _vm.LastResults.Count(r => r.Succeeded));

        await _vm.UndoLastCommand.ExecuteAsync(null);
        var undo = _plans.Shown[^1].Items;
        Assert.Contains(MaintenanceWindowsViewModel.KeepsDescription, undo.Single(i => i.Target.EndsWith("Own text", StringComparison.Ordinal)).Detail);
        Assert.DoesNotContain(MaintenanceWindowsViewModel.KeepsDescription, undo.Single(i => i.Target.EndsWith("ConfigMgr text", StringComparison.Ordinal)).Detail);
        Assert.Equal(Own, _site.Windows.Single(w => w.Name == "Own text").Description);
    }

    [Fact]
    public async Task WhenTheDescriptionCannotBeKeptEditShowsTheServicesRefusalAndWritesNothing()
    {
        await _vm.OnNavigatedToAsync();
        _site.DescriptionWritable = false;
        var window = Row("Own text").Window!;
        var refusal = await Assert.ThrowsAsync<MaintenanceWindowException>(() => _service.PrepareAsync(
            new MaintenanceChange(ChangeKind.Update, window.CollectionId, window.CollectionName, window.WindowName, null, window, window), CancellationToken.None));

        string? message = null;
        bool saved = true;
        _prompts.Editor = editor =>
        {
            editor.DurationHours = 2;
            FakeMaintenancePrompts.Save(editor);
            message = editor.ValidationMessage;
            saved = editor.Saved;
        };
        Select("Own text");
        await _vm.EditWindowCommand.ExecuteAsync(null);
        Assert.Equal(refusal.Message, message);
        Assert.Equal(refusal.Message, _vm.StatusMessage);
        Assert.False(saved);
        Assert.Empty(_plans.Shown);
        Assert.Empty(_site.Writes);
    }

    [Fact]
    public async Task WhenTheDescriptionCannotBeKeptRemoveSkipsThatWindowWithTheServicesRefusal()
    {
        await _vm.OnNavigatedToAsync();
        _site.DescriptionWritable = false;
        var window = Row("Own text").Window!;
        var refusal = await Assert.ThrowsAsync<MaintenanceWindowException>(() => _service.PrepareAsync(
            new MaintenanceChange(ChangeKind.Remove, window.CollectionId, window.CollectionName, window.WindowName, null, window, null), CancellationToken.None));

        _plans.Confirm = false;
        Select("Own text", "ConfigMgr text");
        await _vm.RemoveWindowsCommand.ExecuteAsync(null);
        var items = _plans.Shown[^1].Items;
        var skip = items.Single(i => i.Target.EndsWith("Own text", StringComparison.Ordinal));
        Assert.Equal("Skip", skip.Action);
        Assert.Equal(refusal.Message, skip.Detail);
        Assert.Equal("Remove", items.Single(i => i.Target.EndsWith("ConfigMgr text", StringComparison.Ordinal)).Action);

        Select("Own text");
        await _vm.RemoveWindowsCommand.ExecuteAsync(null);
        Assert.Equal(refusal.Message, _vm.StatusMessage);
        Assert.Single(_plans.Shown);
        Assert.Empty(_site.Writes);
    }
}
