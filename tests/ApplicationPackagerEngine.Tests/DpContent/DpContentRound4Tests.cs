using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerEngine.Tests.DpContent.DpContentHarness;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The last-copy check text for content that no DP holds, and the Result of a Started history record.</summary>
public sealed class DpContentRound4Tests
{
    [Fact]
    public async Task TheLastCopyCheckOfNeverInstalledContentSaysNoDpHoldsItInsteadOfNamingOtherCopies()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A7", StubDpContentSite.Nal, DpContentStates.ContentUpdating, name: "DPCM-Test-202610231921-2"));

        var check = change.Checks.Single(c => c.Name == DpContentService.CheckNames.LastCopy);
        Assert.Equal(DeploymentCheckResults.Pass, check.Result);
        Assert.DoesNotContain("stays installed", check.Message);
        Assert.Equal("The removal takes no installed copy. 1 content is installed on no distribution point now and after the removal: DPCM-Test-202610231921-2 (MCM000A7). Clients cannot get it before or after the removal.",
            check.Message);
        var (plan, _) = DpContentText.BuildPlan(change, new DpContentIndex(await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None)));
        Assert.Contains("No DP holds the content after this plan.", plan!.Plan.Items.Single().Detail);
    }

    [Fact]
    public void ALastCopyCheckWithOtherCopiesKeepsItsText()
    {
        var impact = new DpContentImpact(PackageId: "MCM000A6", Name: "App", ContentType: "Package", Pairs: 1, DpsNow: 2, DpsRemaining: 1, DeploymentCount: 0, DeploymentNote: "")
        {
            RemovesInstalledCopy = true
        };
        Assert.Equal("Every content stays installed on at least one other distribution point.", DpContentService.LastCopyPassText([impact]));
    }

    [Fact]
    public void AStartedRecordShowsItsPhaseAsTheResult()
    {
        var started = new DpContentAuditRecord(Timestamp: new DateTime(2026, 10, 24, 0, 27, 0), User: @"CONTOSO\labadmin", Event: "Remove", Phase: "Started", Result: "", RunId: "run-1",
            Chunk: 1, Pairs: [], Message: "Removing 1 pair (part 1 of 1).");
        Assert.Equal("Started", new DpContentHistoryRow(started).Result);
        Assert.Equal("Requested", new DpContentHistoryRow(started with { Phase = "Finished", Result = "Requested" }).Result);
    }
}
