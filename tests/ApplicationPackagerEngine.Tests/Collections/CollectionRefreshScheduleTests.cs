using System.Globalization;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerEngine.Tests.Collections.StubSitePowerShell;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// The refresh schedule from the Edit dialog through the page, the real service and the real
/// write script on the stub site: the plan names the interval that is written, ConfigMgr holds
/// it afterwards, and a schedule ConfigMgr did not store is a failed outcome.
/// </summary>
public sealed class CollectionRefreshScheduleTests
{
    /// <summary>An interval token as the stub's New-CMSchedule builds it.</summary>
    private static string Token(string unit, int count)
    {
        var v = (1UL << 48) | (1UL << 44) | (56UL << 38) | (2UL << 19);
        var shift = unit switch { "Minutes" => 13, "Hours" => 8, _ => 3 };
        return (v | ((ulong)count << shift)).ToString("X16", CultureInfo.InvariantCulture);
    }

    private sealed record Page(CollectionComplianceViewModel ViewModel, CollectionComplianceHarness Harness, StubSitePowerShell Site);

    private static async Task<Page> OpenAsync(int refreshType, string schedule)
    {
        var site = new StubSitePowerShell(State(new { Reject = "xx" },
            new { CollectionID = "SMS00001", Name = "All Systems", LimitToCollectionID = "", Rules = Array.Empty<object>() },
            new { CollectionID = "MCM00020", Name = "Pilot", LimitToCollectionID = "SMS00001", Comment = "", RefreshType = refreshType, Schedule = schedule, Rules = Array.Empty<object>() }));
        var harness = new CollectionComplianceHarness();
        var service = new CollectionService(site, null, () => "t1");
        var viewModel = new CollectionComplianceViewModel(new CollectionCompliancePageServices(
            service, harness.Store, harness.Reports, harness.Prompts, harness.Plans, new CollectionWriter(service, harness.History, harness.Log, () => "LAB"),
            () => harness.Settings, () => null, _ => { }, harness.Log));
        await viewModel.OnNavigatedToAsync();
        viewModel.SelectedRow = viewModel.Rows.Single(r => r.CollectionId == "MCM00020");
        viewModel.SetSelectedRows([viewModel.SelectedRow]);
        await viewModel.DetailLoad;
        return new Page(viewModel, harness, site);
    }

    private static string StoredSchedule(StubSitePowerShell site) => (string?)site.CollectionState("MCM00020")["Schedule"] ?? "";

    [Theory]
    [InlineData("45", "Minutes", "every 45 minutes")]
    [InlineData("2", "Days", "every 2 days")]
    [InlineData("3", "Hours", "every 3 hours")]
    public async Task AnEditOfTheIntervalOnlyWritesTheNewIntervalThatThePlanNames(string count, string unit, string text)
    {
        if (!WindowsPowerShellPresent) return;
        var page = await OpenAsync(2, Token("Hours", 6));
        page.Harness.Prompts.Editor = e =>
        {
            Assert.Equal(("6", "Hours"), (e.RefreshCount, e.RefreshUnit));
            e.RefreshCount = count;
            e.RefreshUnit = unit;
            FakeCollectionPrompts.Save(e);
        };

        await page.ViewModel.EditCollectionCommand.ExecuteAsync(null);

        var item = Assert.Single(page.Harness.Plans.Shown[^1].Items);
        Assert.Contains($"Refresh interval: every 6 hours to {text}", item.Detail);
        Assert.True(page.Harness.Plans.LastOutcome!.Result!.Items.Single().Succeeded, page.Harness.Plans.LastOutcome.Result.Items.Single().Message);
        Assert.Equal(new RefreshInterval(int.Parse(count, CultureInfo.InvariantCulture), unit), CollectionService.IntervalOf(StoredSchedule(page.Site)));
    }

    [Fact]
    public async Task ManualToPeriodicWithAnOldThreeDaySchedulePlansAndWritesTheEnteredInterval()
    {
        if (!WindowsPowerShellPresent) return;
        var page = await OpenAsync(1, Token("Days", 3));
        page.Harness.Prompts.Editor = e =>
        {
            Assert.Equal(("Manual", "3", "Days"), (e.RefreshType, e.RefreshCount, e.RefreshUnit));
            e.RefreshType = "Periodic";
            e.RefreshCount = "1";
            FakeCollectionPrompts.Save(e);
        };

        await page.ViewModel.EditCollectionCommand.ExecuteAsync(null);

        var item = Assert.Single(page.Harness.Plans.Shown[^1].Items);
        Assert.Contains("Refresh: Manual to Periodic, every 1 day", item.Detail);
        Assert.True(page.Harness.Plans.LastOutcome!.Result!.Items.Single().Succeeded);
        Assert.Equal(2, (int)page.Site.CollectionState("MCM00020")["RefreshType"]!);
        Assert.Equal(new RefreshInterval(1, "Days"), CollectionService.IntervalOf(StoredSchedule(page.Site)));
    }

    [Fact]
    public async Task ManualToPeriodicThatKeepsTheIntervalKeepsTheExactSchedule()
    {
        if (!WindowsPowerShellPresent) return;
        var page = await OpenAsync(1, Token("Days", 3));
        page.Harness.Prompts.Editor = e =>
        {
            e.RefreshType = "Periodic";
            FakeCollectionPrompts.Save(e);
        };

        await page.ViewModel.EditCollectionCommand.ExecuteAsync(null);

        Assert.Contains("Refresh: Manual to Periodic, every 3 days (unchanged)", page.Harness.Plans.Shown[^1].Items[0].Detail);
        Assert.True(page.Harness.Plans.LastOutcome!.Result!.Items.Single().Succeeded);
        Assert.Equal(Token("Days", 3), StoredSchedule(page.Site));
    }

    [Fact]
    public async Task ASetWhoseScheduleConfigMgrDidNotStoreFailsAndKeepsItsUndo()
    {
        if (!WindowsPowerShellPresent) return;
        var site = new StubSitePowerShell(State(new { Reject = "xx", KeepSchedule = true },
            new { CollectionID = "SMS00001", Name = "All Systems", LimitToCollectionID = "", Rules = Array.Empty<object>() },
            new { CollectionID = "MCM00020", Name = "Pilot", LimitToCollectionID = "SMS00001", Comment = "", RefreshType = 2, Schedule = Token("Hours", 6), Rules = Array.Empty<object>() }));
        var service = new CollectionService(site, null, () => "t1");
        var current = (await service.GetCollectionAsync("MCM00020", CancellationToken.None))!;
        var prepared = await service.PrepareAsync(new CollectionChange(CollectionChangeKind.SetProperties, "MCM00020", "Pilot", null,
            current with { Comment = "new", RefreshInterval = new RefreshInterval(45, "Minutes") }, null, null, null, null, []), CancellationToken.None);

        var outcome = Assert.Single(await service.ApplyAsync([prepared], new CollectionServiceTests.Recorder<CollectionOutcome>(), CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("Updated", outcome.Message);
        Assert.Contains("every 45 minutes", outcome.Message);
        Assert.Contains("every 6 hours", outcome.Message);
        Assert.Equal(CollectionChangeKind.SetProperties, outcome.UndoChange?.Kind);
        Assert.Equal("new", (string?)site.CollectionState("MCM00020")["Comment"]);
    }

    [Fact]
    public async Task ACreateWhoseScheduleConfigMgrDidNotStoreFailsAndKeepsItsUndo()
    {
        if (!WindowsPowerShellPresent) return;
        var site = new StubSitePowerShell(State(new { Reject = "xx", KeepSchedule = true },
            new { CollectionID = "SMS00001", Name = "All Systems", LimitToCollectionID = "", Rules = Array.Empty<object>() }));
        var service = new CollectionService(site, null, () => "t1");
        var prepared = await service.PrepareAsync(new CollectionChange(CollectionChangeKind.Create, "", "", null,
            new CollectionRow("", "Hourly", "", "SMS00001", "", 0, false, "Periodic", "", 0, 0, 0, 0, "", new RefreshInterval(4, "Hours")), null, null, null, null, []), CancellationToken.None);

        var outcome = Assert.Single(await service.ApplyAsync([prepared], new CollectionServiceTests.Recorder<CollectionOutcome>(), CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.Contains("every 4 hours", outcome.Message);
        Assert.Equal(CollectionChangeKind.Remove, outcome.UndoChange?.Kind);
    }

    [Fact]
    public async Task TheServiceWritesTheRequestedIntervalWhenTheRowStillCarriesAnOlderToken()
    {
        if (!WindowsPowerShellPresent) return;
        var site = new StubSitePowerShell(State(new { Reject = "xx" },
            new { CollectionID = "SMS00001", Name = "All Systems", LimitToCollectionID = "", Rules = Array.Empty<object>() },
            new { CollectionID = "MCM00020", Name = "Pilot", LimitToCollectionID = "SMS00001", Comment = "", RefreshType = 2, Schedule = Token("Hours", 6), Rules = Array.Empty<object>() }));
        var service = new CollectionService(site, null, () => "t1");
        var current = (await service.GetCollectionAsync("MCM00020", CancellationToken.None))!;
        var change = new CollectionChange(CollectionChangeKind.SetProperties, "MCM00020", "Pilot", current,
            current with { RefreshInterval = new RefreshInterval(2, "Days") }, null, null, null, null, []);

        var outcome = Assert.Single(await service.ApplyAsync([change], new CollectionServiceTests.Recorder<CollectionOutcome>(), CancellationToken.None));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(new RefreshInterval(2, "Days"), CollectionService.IntervalOf(StoredSchedule(site)));
    }
}
