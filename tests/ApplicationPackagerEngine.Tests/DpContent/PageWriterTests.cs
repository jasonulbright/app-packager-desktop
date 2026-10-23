using System.Reflection;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The DP content writer: it sends only the prepared change of a confirmed plan, and the removal flags only as that plan asked them.</summary>
public sealed class PageWriterTests
{
    private static (DpContentPlan Plan, DpContentPageHarness Harness) Plan(DpContentChangeKind kind, params Func<DpContentPageHarness, PairRow>[] pairs)
    {
        var h = new DpContentPageHarness();
        h.Service.InConfirmedRun = null;
        var chosen = pairs.Select(p => p(h)).ToList();
        var prepared = h.Service.PrepareAsync(new DpContentChange(kind, chosen, false, false, [], [], null), CancellationToken.None).Result;
        var (plan, problem) = DpContentText.BuildPlan(prepared, new DpContentIndex(h.Service.Snapshot()));
        Assert.Null(problem);
        return (plan!, h);
    }

    private static ChangePlanViewModel Confirmed(DpContentPlan plan, ChangePlanRun run, bool destructive = true, bool extra = true, bool second = true)
    {
        var dialog = new ChangePlanViewModel(plan.Plan, run) { DestructiveAcknowledged = destructive, ExtraAcknowledged = extra, SecondAcknowledged = second };
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        return dialog;
    }

    [Fact]
    public void ARemovalCarriesRemovalConfirmedAndTheLastCopyFlagOnlyAsTheConfirmedPlanAskedThem()
    {
        var (keep, h) = Plan(DpContentChangeKind.Remove, x => x.SevenZipOnDp01);
        Assert.Null(keep.Plan.SecondConfirmation);
        Confirmed(keep, h.Writer().RunFor(keep));
        var sent = h.Service.Applied.Single().Single();
        Assert.True(sent.RemovalConfirmed);
        Assert.False(sent.LastCopyAcknowledged);

        var (last, h2) = Plan(DpContentChangeKind.Remove, x => x.OfficeOnDp01);
        Assert.NotNull(last.Plan.SecondConfirmation);
        Confirmed(last, h2.Writer().RunFor(last));
        Assert.True(h2.Service.Applied.Single().Single().LastCopyAcknowledged);
    }

    [Fact]
    public void AChangeThatRemovesNothingNeverCarriesTheRemovalFlags()
    {
        var (plan, h) = Plan(DpContentChangeKind.Redistribute, x => x.SevenZipOnDp02);
        var forged = new DpContentPlan(plan.Action, plan.Change with { RemovalConfirmed = true, LastCopyAcknowledged = true }, plan.Plan, plan.Entries);
        Confirmed(forged, h.Writer().RunFor(forged));
        var sent = h.Service.Applied.Single().Single();
        Assert.False(sent.RemovalConfirmed);
        Assert.False(sent.LastCopyAcknowledged);
    }

    [Fact]
    public async Task TheWriterRefusesAPlanThatIsNotTheConfirmedOne()
    {
        var (plan, h) = Plan(DpContentChangeKind.Validate, x => x.SevenZipOnDp01);
        var (other, _) = Plan(DpContentChangeKind.Validate, x => x.SevenZipOnDp01);
        ConfirmedChangePlan? captured = null;
        Confirmed(other, (confirmed, _, _) =>
        {
            captured = confirmed;
            return Task.FromResult(new ChangePlanResult([]));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Writer().ApplyAsync(captured!, plan, new Progress<ChangePlanItemResult>(), CancellationToken.None));
        Assert.Empty(h.Service.Applied);
    }

    [Fact]
    public void TheConfirmWindowNeedsEveryBoxOfARemovalBeforeTheChangeIsSent()
    {
        var (plan, h) = Plan(DpContentChangeKind.Remove, x => x.OfficeOnDp01);
        foreach (var (d, e, s) in new[] { (false, true, true), (true, false, true), (true, true, false) })
        {
            var dialog = Confirmed(plan, h.Writer().RunFor(plan), d, e, s);
            Assert.False(dialog.Outcome.Confirmed);
        }
        Assert.Empty(h.Service.Applied);
    }

    [Fact]
    public void EachPairGetsItsOwnResultOnceAndTheSiteHistoryGetsCountsOnly()
    {
        var (plan, h) = Plan(DpContentChangeKind.Redistribute, x => x.SevenZipOnDp02, x => x.SevenZipOnBranch, x => x.BootOnDp01);
        h.Service.Fail = p => p.NalPath == h.Dp01.NalPath ? @"Failed: the provider at \\cm01 returned an error." : null;
        var dialog = Confirmed(plan, h.Writer().RunFor(plan));
        var result = dialog.Outcome.Result!;
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(["Requested", "Requested", "Failed"], result.Items.Select(r => r.OutcomeText));
        Assert.Equal("2 requested, 1 failed.", result.Headline);
        var entry = h.History.Entries.Single();
        Assert.Equal("Redistribute", entry.Action);
        Assert.Equal(new[] { "changed", "confirmed", "failed", "requested", "skipped", "unchanged" }, entry.Counts!.Keys.Order());
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("DP0", StringComparison.Ordinal) || e.Message.Contains("LAB0", StringComparison.Ordinal));
    }

    [Fact]
    public void ACancelBetweenChunksReportsTheRestSkippedAndKeepsTheResultsMade()
    {
        var (plan, h) = Plan(DpContentChangeKind.Redistribute, x => x.SevenZipOnDp02, x => x.SevenZipOnBranch, x => x.BootOnDp01);
        h.Service.ChunkSize = 2;
        ChangePlanViewModel? dialog = null;
        h.Service.AfterChunk = c =>
        {
            if (c == 0) dialog!.RequestCancel();
        };
        dialog = new ChangePlanViewModel(plan.Plan, h.Writer().RunFor(plan));
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        var result = dialog.Outcome.Result!;
        Assert.Equal(ChangePlanStop.Cancelled, result.Stop);
        Assert.Equal(["Requested", "Requested", "Skipped"], result.Items.Select(r => r.OutcomeText));
        Assert.StartsWith("Skipped", result.Items[2].Message);
        Assert.Equal(SiteOperationOutcome.Cancelled, h.History.Entries.Single().Outcome);
    }

    [Fact]
    public void AnOutcomeWithoutPairsGivesItsMessageToEveryPairAndASkippedMessageStaysSkipped()
    {
        var item = new ChangePlanItem("Remove from DP", "x", null);
        Assert.Equal("Skipped", DpContentWriter.Row(item, "Skipped", "the pair changed").OutcomeText);
        Assert.Equal("Skipped: the pair changed", DpContentWriter.Row(item, "Skipped", "the pair changed").Message);
        Assert.True(DpContentWriter.Row(item, "Skipped", "x").Skipped);
        Assert.Equal("Failed", DpContentWriter.Row(item, "Odd", "x").OutcomeText);
        Assert.False(DpContentWriter.Row(item, "Odd", "x").Succeeded);
        Assert.True(DpContentWriter.Row(item, "Confirmed", "x").Succeeded);
        Assert.True(DpContentWriter.Row(item, "Unchanged", "x").Succeeded);
    }

    [Fact]
    public void ARemovalTheServiceRefusesShowsSkippedForEveryPair()
    {
        var (plan, h) = Plan(DpContentChangeKind.Remove, x => x.SevenZipOnDp01, x => x.SevenZipOnDp02);
        var unconfirmed = new DpContentPlan(plan.Action, plan.Change, new ChangePlan(plan.Plan.Title, plan.Plan.ActionName, plan.Plan.Items), plan.Entries);
        var dialog = Confirmed(unconfirmed, h.Writer().RunFor(unconfirmed));
        Assert.False(h.Service.Applied.Single().Single().RemovalConfirmed);
        Assert.All(dialog.Outcome.Result!.Items, r => Assert.Equal(PageFakeDpContentService.RemovalRefused, r.Message));
        Assert.All(dialog.Outcome.Result!.Items, r => Assert.True(r.Skipped));
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "ApplicationPackagerUI"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src");
    }

    private static IReadOnlyList<string> UiFilesWith(Regex pattern)
    {
        var root = SourceRoot();
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(root, "ApplicationPackagerUI"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) && !f.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            .Where(f => pattern.IsMatch(MaintenanceWindowWriterTests.Code(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string P(params string[] parts) => Path.Combine(["ApplicationPackagerUI", .. parts]);

    [Fact]
    public void OnlyTheWriterCallsTheDpContentServiceWrite()
    {
        var naming = UiFilesWith(new Regex(@"\bIDpContentService\b", RegexOptions.CultureInvariant));
        Assert.Equal(
        [
            P("Services", "DpContentPlan.cs"), P("Services", "DpContentPrompts.cs"), P("Services", "DpContentServiceFallbacks.cs"),
            P("ViewModels", "Pages", "DpContentManagerViewModel.cs")
        ], naming.Where(f => !f.EndsWith("PackagerServiceRegistration.cs", StringComparison.Ordinal)).ToList());
        Assert.Matches(@"\bservice\s*\.\s*ApplyAsync\b", MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), P("Services", "DpContentPlan.cs")))));
        var pageFiles = UiFilesWith(new Regex(@"\bDpContent", RegexOptions.CultureInvariant))
            .Where(f => !f.EndsWith("DpContentPlan.cs", StringComparison.Ordinal) && !f.EndsWith("DpContentServiceFallbacks.cs", StringComparison.Ordinal)).ToList();
        Assert.Contains(P("ViewModels", "Pages", "DpContentManagerViewModel.Actions.cs"), pageFiles);
        foreach (var file in pageFiles)
            Assert.DoesNotMatch(@"\bApplyAsync\b", MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), file))));
        // The page reads through the service only with LoadAsync and PrepareAsync.
        var calls = pageFiles.SelectMany(f => Regex.Matches(MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), f))), @"\bServices\s*\.\s*Content\s*\.\s*(\w+)").Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.Equal(["LoadAsync", "PrepareAsync"], calls.Order());
    }

    [Fact]
    public void NoTypeOfTheDesktopButTheWriterAndThePageServicesHoldsTheDpContentService()
    {
        var allowed = new[] { typeof(DpContentWriter), typeof(DpContentPageServices), typeof(DpContentRegistration) };
        var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = typeof(DpContentWriter).Assembly.GetTypes()
            .Where(t => typeof(IDpContentService).IsAssignableFrom(t)
                || t.GetFields(all).Any(f => typeof(IDpContentService).IsAssignableFrom(f.FieldType))
                || t.GetProperties(all).Any(p => typeof(IDpContentService).IsAssignableFrom(p.PropertyType)))
            .Select(t => { while (t.DeclaringType is { } outer) t = outer; return t; })
            .Where(t => !t.Name.StartsWith("<", StringComparison.Ordinal))
            .Distinct().ToList();
        Assert.Contains(typeof(DpContentWriter), holders);
        Assert.All(holders, t => Assert.Contains(t, allowed));
    }
}
