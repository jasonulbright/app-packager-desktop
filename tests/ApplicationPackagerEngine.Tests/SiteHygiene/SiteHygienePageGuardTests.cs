using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygienePageHarness;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// The page against results and suppressions of another site, a scan that must not become the baseline of
/// the New column, bulk suppressions, the export header, the summary cells, a late Cancel and a selection
/// that changes while the finding list is open.
/// </summary>
public sealed class SiteHygienePageGuardTests
{
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static void RunNow(Task task) => task.GetAwaiter().GetResult();

    [Fact]
    public async Task ABulkSuppressAndUnsuppressWriteTheStoreOnceEach()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.UnusedApp, h.UnusedApp2, h.Boundary);
        await vm.SuppressCommand.ExecuteAsync(null);
        Assert.Equal(1, h.Suppressions.Writes);
        Assert.Equal(3, h.Suppressions.Items.Count);
        Assert.All(h.Suppressions.Items, s => Assert.Equal("MCM", s.SiteCode));

        vm.ShowSuppressed = true;
        Select(vm, h.UnusedApp, h.UnusedApp2, h.Boundary);
        await vm.UnsuppressCommand.ExecuteAsync(null);
        Assert.Equal(2, h.Suppressions.Writes);
        Assert.Empty(h.Suppressions.Items);
    }

    [Fact]
    public async Task TheRealStoreKeepsEarlierSuppressionsWritesARangeOnceAndRefusesAFileItCannotRead()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-shsup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, HygieneSuppressionStore.FileName);
            File.WriteAllText(path, """{"SchemaVersion":1,"Suppressions":[{"CheckId":"BND-01","ObjectKey":"16777217","Comment":"Old","CreatedUtc":"2026-10-01T00:00:00Z","Key":"BND-01|16777217"}]}""");
            var store = new HygieneSuppressionStore(path);
            var legacy = Assert.Single(store.Load());
            Assert.Equal("", legacy.SiteCode);
            Assert.True(legacy.AppliesTo("MCM") && legacy.AppliesTo("LAB"));

            store.AddRange([new Suppression("APP-01", "A", "c", DateTime.UtcNow) { SiteCode = "mcm" }, new Suppression("APP-01", "B", "c", DateTime.UtcNow) { SiteCode = "MCM" }]);
            Assert.Equal(3, store.Load().Count);
            Assert.All(store.Load().Where(s => s.CheckId == "APP-01"), s => Assert.Equal("MCM", s.SiteCode));

            store.RemoveRange("LAB", [("APP-01", "A")]);
            Assert.Equal(3, store.Load().Count);
            store.RemoveRange("MCM", [("APP-01", "A"), ("BND-01", "16777217")]);
            Assert.Equal(["APP-01|B"], store.Load().Select(s => s.Key));

            var before = File.ReadAllBytes(path);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<HygieneServiceException>(() => store.Add(new Suppression("APP-01", "C", "c", DateTime.UtcNow)));
                Assert.Throws<HygieneServiceException>(() => store.Remove("APP-01", "B"));
            }
            Assert.Equal(before, File.ReadAllBytes(path));

            File.WriteAllText(path, "{ not json");
            var damaged = File.ReadAllBytes(path);
            var refused = Assert.Throws<HygieneServiceException>(() => store.Add(new Suppression("APP-01", "C", "c", DateTime.UtcNow)));
            Assert.Contains(path, refused.Message);
            Assert.Equal(damaged, File.ReadAllBytes(path));
            Assert.Empty(store.Load());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AStoredScanOfAnotherSiteIsNotShownAndNotUsedForTheNewColumn()
    {
        var h = new SiteHygienePageHarness();
        h.ScanStore.Stored = h.Service.Result() with { SiteCode = "LAB", Findings = h.Service.Findings.Where(f => f != h.UnusedApp).ToList() };
        var vm = await h.OpenAsync();
        Assert.False(vm.HasResult);
        Assert.Contains("The stored last scan is of site LAB, not of site MCM, so it is not shown.", vm.StatusMessage);
        await vm.ScanAsync();
        Assert.DoesNotContain(vm.FindingRows, r => r.Finding.IsNew);

        h.ScanStore.Stored = h.ScanStore.Stored with { SiteCode = "MCM" };
        await vm.ScanAsync();
        Assert.Equal([h.UnusedApp.Key], vm.FindingRows.Where(r => r.Finding.IsNew).Select(r => r.Finding.Key));
    }

    [Fact]
    public async Task ASuppressionOfAnotherSiteDoesNotHideAFindingAndOneOfEverySiteDoes()
    {
        var h = new SiteHygienePageHarness();
        h.Suppressions.Items.Add(new Suppression("APP-01", h.UnusedApp.ObjectKey, "Other site.", DateTime.UtcNow) { SiteCode = "LAB" });
        h.Suppressions.Items.Add(new Suppression("BND-01", h.Boundary.ObjectKey, "Every site.", DateTime.UtcNow));
        var vm = await h.ScannedAsync();
        Assert.False(Row(vm, h.UnusedApp).IsSuppressed);
        Assert.DoesNotContain(vm.FindingRows, r => r.Key == h.Boundary.Key);
        Assert.Contains("1 suppressed.", vm.SummaryLine);
    }

    [Fact]
    public async Task AFixOnAResultOfAnotherSiteIsRefusedBeforeTheSiteIsRead()
    {
        var h = new SiteHygienePageHarness();
        h.ScanStore.Stored = h.Service.Result();
        var vm = await h.OpenAsync();
        Assert.True(vm.IsStoredResult);
        h.Settings = new ApplicationPackagerUI.Models.AppSettings { SiteCode = "LAB", ProviderMachine = "cm02.contoso.com" };
        Select(vm, h.EmptyCollection);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(VM.OtherSite("MCM", "LAB"), vm.StatusMessage);
        Assert.Equal(0, h.Service.PrepareCalls);
    }

    [Fact]
    public async Task AScanThatIsTruncatedOrHasAChecksThatDidNotRunDoesNotReplaceTheStoredScan()
    {
        var h = new SiteHygienePageHarness();
        h.Service.Truncated = true;
        var vm = await h.ScannedAsync();
        Assert.Empty(h.ScanStore.Saved);
        Assert.Contains("The read was truncated, so the result was not stored as the last scan.", vm.StatusMessage);
        Assert.Contains("not stored as the last scan", vm.SummaryLine);

        h.Service.Truncated = false;
        await vm.ScanAsync();
        Assert.Empty(h.ScanStore.Saved);
        Assert.Contains("1 check did not run, so the result was not stored as the last scan.", vm.StatusMessage);

        h.CompleteScan();
        await vm.ScanAsync();
        Assert.Single(h.ScanStore.Saved);
        Assert.DoesNotContain("not stored", vm.SummaryLine);
    }

    [Fact]
    public async Task AFindingOfACheckThatDidNotRunInTheStoredScanIsNotNew()
    {
        var h = new SiteHygienePageHarness();
        var checks = h.Service.Checks.Select(c => c.CheckId == "APP-01" ? c with { State = HygieneCheckStates.NotRun, Reason = "The read failed." } : c).ToList();
        h.ScanStore.Stored = h.Service.Result() with { Findings = h.Service.Findings.Where(f => f.CheckId != "APP-01" && f != h.EmptyCollection).ToList(), Checks = checks };
        var vm = await h.ScannedAsync();
        Assert.Equal([h.EmptyCollection.Key], vm.FindingRows.Where(r => r.Finding.IsNew).Select(r => r.Finding.Key));
    }

    [Fact]
    public async Task TheExportHeaderNamesTheHiddenSuppressedRowsAndTheRowsFixedSinceTheScan()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.Boundary);
        await vm.SuppressCommand.ExecuteAsync(null);
        Select(vm, h.EmptyCollection);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal("Done", Assert.Single(vm.LastResults).OutcomeText);
        await vm.ExportCsvCommand.ExecuteAsync(null);
        var (_, report) = Assert.Single(h.Reports.Written);
        Assert.Contains("Suppressed: 1 hidden.", report.Filter);
        Assert.Contains("Fixed since the scan: 1 omitted.", report.Filter);
        Assert.Equal(6, report.Rows.Count);

        var writer = new HygieneReportWriter(Path.Combine(Path.GetTempPath(), "apkg-shrep-" + Guid.NewGuid().ToString("N")));
        var file = writer.WriteCsv(report);
        try { Assert.DoesNotContain(HygieneReportWriter.NoFilter, File.ReadAllText(file)); }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }
    }

    [Fact]
    public async Task ASummaryCellWithACheckThatDidNotRunNeverShowsAPlainCount()
    {
        var h = new SiteHygienePageHarness();
        for (var i = 0; i < h.Service.Checks.Count; i++)
            if (h.Service.Checks[i].CheckId == "APP-04") h.Service.Checks[i] = h.Service.Checks[i] with { Severity = HygieneSeverity.Warning };
        var vm = await h.ScannedAsync();
        var apps = vm.CategoryRows.Single(r => r.Category == "Applications");
        Assert.Equal(("1", "At least 2", "Not known", "At least 3", "1"), (apps.Error, apps.Warning, apps.Info, apps.Total, apps.NotRun));
        var devices = vm.CategoryRows.Single(r => r.Category == "Devices");
        Assert.Equal(("1", "Not known", "At least 1"), (devices.Warning, devices.Info, devices.Total));
        var site = vm.CategoryRows.Single(r => r.Category == "Site");
        Assert.Equal(("1", "1"), (site.Warning, site.Total));
    }

    [Fact]
    public async Task ACancelAfterTheResultShowsChangesNothingAndACancelDuringTheLastReadsEndsAsCancelled()
    {
        var h = new SiteHygienePageHarness();
        h.CompleteScan();
        var vm = await h.OpenAsync();
        h.ScanStore.SaveGate = new ManualResetEventSlim();
        var scan = vm.ScanAsync();
        await WaitUntil(() => h.ScanStore.Saving.IsSet);
        Assert.True(vm.IsScanning);
        vm.CancelScanCommand.Execute(null);
        Assert.NotEqual(VM.ScanCancelling, vm.StatusMessage);
        h.ScanStore.SaveGate.Set();
        await scan;
        Assert.StartsWith("The scan ended:", vm.StatusMessage);
        Assert.Single(h.ScanStore.Saved);

        h.Suppressions.Loading.Reset();
        h.Suppressions.LoadGate = new ManualResetEventSlim();
        var second = vm.ScanAsync();
        await WaitUntil(() => h.Suppressions.Loading.IsSet);
        vm.CancelScanCommand.Execute(null);
        Assert.Equal(VM.ScanCancelling, vm.StatusMessage);
        h.Suppressions.LoadGate.Set();
        await second;
        Assert.Equal(VM.ScanCancelled, vm.StatusMessage);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task ASelectionOrResultThatChangesWhileTheFindingListIsOpenDiscardsThePick()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.UnusedApp, h.UnusedApp2);
        h.Dialogs.OnFindings = _ => Select(vm, h.UnusedApp);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(VM.SelectionChangedAtPick, vm.StatusMessage);
        Assert.Equal(0, h.Service.PrepareCalls);

        Select(vm, h.UnusedApp, h.UnusedApp2);
        h.Dialogs.OnFindings = _ => RunNow(vm.ScanAsync());
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(VM.ResultChangedAtPick, vm.StatusMessage);
        Assert.Equal(0, h.Service.PrepareCalls);
    }
}
