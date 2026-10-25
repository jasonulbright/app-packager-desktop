using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The History tab: the records of the real audit log file of the page, read only, with the hash chain state of each line.</summary>
public sealed class SiteHygieneHistoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-shhist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string LogPath => Path.Combine(_folder, "site-hygiene-audit.jsonl");

    private HygieneAuditLog Audit() => new(() => LogPath, user: () => "CONTOSO\\labadmin");

    private static readonly DateTime Second = new(2026, 10, 24, 20, 4, 31);

    /// <summary>Two runs: a delete of two packages in one second (Started and Finished), then a collection run that was skipped.</summary>
    private void WriteRuns(HygieneAuditLog log)
    {
        HygieneAuditObject Pkg(string id, string result) => new("PKG-01", "Package", id, "SHL-Pkg-" + id, result);
        Assert.True(log.Append(new HygieneAuditRecord(Second, "", "RemovePackage", HygieneAuditPhases.Started, "", "run1", [Pkg("MCM00118", ""), Pkg("MCM00119", "")], "Started.") { Site = "MCM", Provider = "cm01.contoso.com" }).Succeeded);
        Assert.True(log.Append(new HygieneAuditRecord(Second, "", "RemovePackage", HygieneAuditPhases.Finished, "Succeeded", "run1", [Pkg("MCM00118", "Done"), Pkg("MCM00119", "Done")], "2 done.") { Site = "MCM", Provider = "cm01.contoso.com" }).Succeeded);
        Assert.True(log.Append(new HygieneAuditRecord(Second.AddMinutes(2), "", "RemoveCollection", HygieneAuditPhases.Started, "", "run2", [new("COL-01", "Collection", "MCM00227", "SHL-Col-Late", "")], "Started.") { Site = "MCM", Provider = "cm01.contoso.com" }).Succeeded);
        Assert.True(log.Append(new HygieneAuditRecord(Second.AddMinutes(2), "", "RemoveCollection", HygieneAuditPhases.Finished, "Skipped", "run2", [new("COL-01", "Collection", "MCM00227", "SHL-Col-Late", "Skipped")], "1 skipped.") { Site = "MCM", Provider = "cm01.contoso.com" }).Succeeded);
    }

    private SiteHygienePageHarness Harness(out HygieneAuditLog log)
    {
        log = Audit();
        return new SiteHygienePageHarness { AuditLog = log };
    }

    [Fact]
    public async Task TheHistoryTabListsTheRecordsNewestFirstWithTheirObjectsSiteProviderUserAndVerification()
    {
        var h = Harness(out var log);
        WriteRuns(log);
        var vm = await h.OpenAsync();
        Assert.False(vm.IsHistoryLoaded);
        vm.SelectedTabIndex = VM.HistoryTab;
        await vm.HistoryTask;

        Assert.Equal(0, h.Service.ScanCalls);
        Assert.Equal(0, h.Service.RelationCalls);
        Assert.Equal(0, h.Service.PrepareCalls);
        Assert.Equal(4, vm.HistoryRows.Count);
        Assert.Equal("4 records", vm.HistoryCount);
        Assert.Equal(LogPath, vm.HistoryPath);
        Assert.Equal([("run2", "Finished"), ("run2", "Started"), ("run1", "Finished"), ("run1", "Started")], vm.HistoryRows.Select(r => (r.Record.RunId, r.Record.Phase)));

        var finished = vm.HistoryRows[2];
        Assert.Equal(("Delete package", "Succeeded", "2: 2 done", "MCM", "cm01.contoso.com", "CONTOSO\\labadmin", "✓ Verified"),
            (finished.Action, finished.Result, finished.Objects, finished.Site, finished.Provider, finished.User, finished.Verified));
        Assert.Equal(("Started", "2 planned"), (vm.HistoryRows[3].Result, vm.HistoryRows[3].Objects));
        Assert.Equal(("Delete collection", "Skipped", "1: 1 skipped"), (vm.HistoryRows[0].Action, vm.HistoryRows[0].Result, vm.HistoryRows[0].Objects));

        vm.SelectedHistory = finished;
        Assert.True(vm.HasHistoryDetails);
        Assert.Contains(AuditVerification.Verified, vm.HistoryDetails);
        Assert.Contains("Delete package, Finished: Succeeded. Run run1. Site MCM on cm01.contoso.com. User CONTOSO\\labadmin.", vm.HistoryDetails);
        Assert.Contains("PKG-01 Package SHL-Pkg-MCM00118 (MCM00118): Done", vm.HistoryDetails);
        Assert.Contains("2 done.", vm.HistoryDetails);
    }

    [Fact]
    public async Task AHandEditOfTheLogShowsNotVerifiedFromTheChangedLineOn()
    {
        var h = Harness(out var log);
        WriteRuns(log);
        var lines = File.ReadAllLines(LogPath);
        lines[2] = lines[2].Replace("SHL-Col-Late", "SHL-Col-Edit", StringComparison.Ordinal);
        File.WriteAllLines(LogPath, lines);

        var vm = await h.OpenAsync();
        await vm.LoadHistoryAsync();
        var byLine = vm.HistoryRows.OrderBy(r => r.Index).ToList();
        Assert.Equal(["✓ Verified", "✓ Verified", "! Not verified", "! Not verified"], byLine.Select(r => r.Verified));
        Assert.Equal(AuditVerification.BrokenAt(3), byLine[2].VerifiedText);
        vm.SelectedHistory = byLine[3];
        Assert.StartsWith(AuditVerification.BrokenAt(3), vm.HistoryDetails);
    }

    [Fact]
    public async Task TheFilterFindsARecordByObjectNameAndKeepsTheSelection()
    {
        var h = Harness(out var log);
        WriteRuns(log);
        var vm = await h.OpenAsync();
        await vm.LoadHistoryAsync();
        vm.SelectedHistory = vm.HistoryRows[2];
        vm.HistoryText = "MCM00119";
        Assert.Equal(2, vm.HistoryRows.Count);
        Assert.Equal("2 of 4 records", vm.HistoryCount);
        Assert.Equal("run1", vm.SelectedHistory!.Record.RunId);
        Assert.Equal(HygieneAuditPhases.Finished, vm.SelectedHistory.Record.Phase);
        vm.HistoryText = "labadmin";
        Assert.Equal(4, vm.HistoryRows.Count);
        vm.HistoryText = "no such text";
        Assert.Empty(vm.HistoryRows);
    }

    [Fact]
    public async Task TheHistoryExportsAsCsvAndHtmlAndRefusesBeforeTheRead()
    {
        var h = Harness(out var log);
        WriteRuns(log);
        var fresh = new SiteHygienePageHarness { AuditLog = log }.Create();
        fresh.SelectedTabIndex = VM.HistoryTab;
        var (none, problem) = fresh.ActiveReport();
        Assert.Null(none);
        Assert.Equal(VM.HistoryNotRead, problem);

        var vm = await h.OpenAsync();
        await vm.LoadHistoryAsync();
        vm.SelectedTabIndex = VM.HistoryTab;
        vm.HistoryText = "run1";
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.EndsWith(" The notes are in site-hygiene-1.about.txt.", vm.StatusMessage);
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        Assert.DoesNotContain("about.txt", vm.StatusMessage);
        var (format, report) = h.Reports.Written[0];
        Assert.Equal("csv", format);
        Assert.Equal("History", report.View);
        Assert.Equal(["Time", "Action", "Phase", "Result", "Objects", "Site", "Provider", "User", "Verified", "Run", "Message"], report.Columns);
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(["2026-10-24 20:04:31", "Delete package", "Finished", "Succeeded", "2: 2 done", "MCM", "cm01.contoso.com", "CONTOSO\\labadmin", AuditVerification.Verified, "run1", "2 done."], report.Rows[0]);
        Assert.Equal("Text: run1.", report.Filter);
        Assert.Equal("html", h.Reports.Written[1].Format);
    }

    [Fact]
    public async Task AFixRunReadsTheHistoryAgainWhenTheHistoryTabShows()
    {
        var h = Harness(out var log);
        var vm = await h.ScannedAsync();
        vm.SelectedTabIndex = VM.HistoryTab;
        await vm.HistoryTask;
        Assert.Empty(vm.HistoryRows);
        WriteRuns(log);
        SiteHygienePageHarness.Select(vm, h.EmptyCollection);
        await vm.RunFixCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.HistoryRows.Count == 4);
    }

    [Fact]
    public async Task AnUnreadableLogShowsAPlainMessageAndNoRows()
    {
        var h = Harness(out var log);
        WriteRuns(log);
        var vm = await h.OpenAsync();
        using (new FileStream(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await vm.LoadHistoryAsync();
        Assert.Empty(vm.HistoryRows);
        Assert.Contains("audit log", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", vm.StatusMessage);
    }

    [Fact]
    public async Task TheHistoryTabIsRememberedAndAnEarlierTabIndexStillOpensItsTab()
    {
        var h = Harness(out var log);
        WriteRuns(log);
        h.Saved = new ApplicationPackagerUI.Models.SiteHygieneSettings { SelectedTab = VM.OptionsTab };
        var vm = await h.OpenAsync();
        Assert.Equal(VM.OptionsTab, vm.SelectedTabIndex);
        h.Saved = new ApplicationPackagerUI.Models.SiteHygieneSettings { SelectedTab = VM.HistoryTab, HistoryText = "run2" };
        var second = await h.OpenAsync();
        Assert.Equal(VM.HistoryTab, second.SelectedTabIndex);
        await WaitUntil(() => second.HistoryRows.Count == 2);
        Assert.Equal(2, second.HistoryRows.Count);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("The condition did not hold in time.");
            await Task.Delay(10);
        }
    }
}
