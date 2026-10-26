using System.Globalization;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using VM = ApplicationPackagerUI.ViewModels.Pages.DpContentManagerViewModel;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The DP Content Manager page: local times, the failed state text, the last-copy rule for never-installed content, and the export names and filters.</summary>
public sealed class PageRound3Tests
{
    [Fact]
    public void LastStatusShowsTheUtcSummaryDateInLocalTime()
    {
        var utc = new DateTime(2026, 10, 24, 2, 31, 0, DateTimeKind.Utc);
        var pair = new PairRow(PackageId: "LAB00001", ContentName: "App", ContentType: "Package", DpName: "DP01", NalPath: "nal", State: 0, StateName: "Installed", SourceVersion: "1",
            SummaryDate: utc);

        Assert.Equal(utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), new PairGridRow(pair).LastStatus);
    }

    [Fact]
    public void AFailedPairWhoseStateIsNotAFailedStateSaysFailedInItsStateText()
    {
        var h = new DpContentPageHarness();
        var updating = h.Service.AddPair(h.Office, h.Dp02, DpContentStates.ContentUpdating) with { MessageState = 4, MessageId = 2306 };
        h.Service.Pairs[^1] = updating;

        var row = new PairGridRow(updating);
        Assert.Equal(DpContentLabels.FailedLabel, row.Status);
        Assert.Equal("Failed (content updating)", row.StateName);
        var prepared = new DpContentChange(Kind: DpContentChangeKind.Redistribute, Pairs: [updating], RemovalConfirmed: false, LastCopyAcknowledged: false, Impact: [], Checks: [], Fingerprint: "fp");
        var (plan, _) = DpContentText.BuildPlan(prepared, new DpContentIndex(h.Service.Snapshot()));
        Assert.Contains("State now: Failed (content updating).", plan!.Plan.Items.Single().Detail);
    }

    [Fact]
    public void RemovingAPairThatWasNeverInstalledAsksNoLastCopyBox()
    {
        var h = new DpContentPageHarness();
        var never = h.Service.AddPair(h.Office, h.Dp02, DpContentStates.InstallFailed);
        h.Service.RemovePair(h.OfficeOnDp01);
        var prepared = new DpContentChange(Kind: DpContentChangeKind.Remove, Pairs: [never], RemovalConfirmed: false, LastCopyAcknowledged: false, Impact: [],
            Checks: [new PreflightCheck(Name: "LastCopy", Result: "Pass", Message: "Every content stays installed on at least one other distribution point.")], Fingerprint: "fp")
        {
            ContentImpact = [new DpContentImpact(PackageId: "LAB00002", Name: "Office Tools", ContentType: "Package", Pairs: 1, DpsNow: 1, DpsRemaining: 0, DeploymentCount: 0,
                DeploymentNote: "Counted.") { RemovesInstalledCopy = false }]
        };
        var (plan, _) = DpContentText.BuildPlan(prepared, new DpContentIndex(h.Service.Snapshot()));

        Assert.Null(plan!.Plan.SecondConfirmation);
        Assert.Equal("Remove 1 pair from distribution points. Clients that use these DPs cannot get the content from them.", plan.Plan.ExtraConfirmation);
    }

    [Fact]
    public async Task AnExportNamesItsViewOnceAndStatesTheFilter()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync(VM.ContentTab);
        Assert.Equal("Content", vm.ActiveReport()!.View);
        Assert.Equal("", vm.ActiveReport()!.Filter);
        vm.ContentText = "7-Zip";
        vm.ContentType = "Application";
        var report = vm.ActiveReport()!;
        Assert.Equal("Text: 7-Zip. Type: Application.", report.Filter);
        vm.SelectedTabIndex = VM.IssuesTab;
        Assert.Equal("Status issues", vm.ActiveReport()!.View);
        vm.SelectedTabIndex = VM.DpsTab;
        vm.DpStatus = VM.HasFailures;
        Assert.Equal("Distribution points", vm.ActiveReport()!.View);
        Assert.Equal("Status: Has failures.", vm.ActiveReport()!.Filter);
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Contains("Filter: Status: Has failures.", vm.StatusMessage);
    }

    [Fact]
    public void TheReportFilesNameTheViewOnceAndCarryTheFilterInTheirHeader()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-dpc-r3-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new DpContentReportWriter(folder, () => new DateTimeOffset(2026, 10, 23, 22, 0, 0, TimeSpan.FromHours(-4)));
            var report = new DpContentReport("Content", ["Name", "PackageID"], [["7-Zip", "LAB00001"]], false, null) { Filter = "Text: 7-Zip." };
            var csv = writer.WriteCsv(report);
            var html = writer.WriteHtml(report);

            Assert.StartsWith("dp-content-content-", Path.GetFileName(csv), StringComparison.Ordinal);
            var lines = File.ReadAllLines(csv);
            Assert.StartsWith("\"DP content: Content. Generated 2026-10-23 22:00:00 -04:00. Rows: 1. Filter: Text: 7-Zip.\"", lines[0], StringComparison.Ordinal);
            Assert.Equal("\"Name\",\"PackageID\"", lines[1]);
            var text = File.ReadAllText(html);
            Assert.Contains("<title>DP content: Content</title>", text, StringComparison.Ordinal);
            Assert.Contains("Filter: Text: 7-Zip.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DP content: DP content", text, StringComparison.Ordinal);

            var unfiltered = File.ReadAllLines(writer.WriteCsv(report with { Filter = "" }));
            Assert.Contains(DpContentReportWriter.NoFilter, unfiltered[0], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
