using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.DpContent.DpContentHarness;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The real DP content script through the stub site: the last copy read again before each part, the Undo of task sequences and applications, and the audit read window.</summary>
public sealed class DpContentServiceRound2Tests
{
    private const string Cm01 = StubDpContentSite.Nal;

    private static (JsonObject State, List<string> Nals) ThirtyDps()
    {
        var state = StubDpContentSite.Lab();
        var nals = Enumerable.Range(0, 30).Select(i => StubDpContentSite.NalOf($"DP{i:D2}.contoso.com")).ToList();
        foreach (var nal in nals)
        {
            ((JsonArray)state["Dps"]!).Add(StubDpContentSite.Dp(nal, DpContentAggregator.ServerOf(nal).ToUpperInvariant()));
            ((JsonArray)state["Summaries"]!).Add(StubDpContentSite.Sum("MCM0001B", nal, 0, 0));
            ((JsonArray)state["Instances"]!).Add(StubDpContentSite.Inst("MCM0001B", nal, 0, "2026-10-20T09:13:44"));
        }
        return (state, nals);
    }

    [Fact]
    public async Task APartThatWouldRemoveTheLastCopyAfterAnotherProcessRemovedTheOtherIsSkipped()
    {
        var (state, nals) = ThirtyDps();
        using var h = new DpContentHarness(state, window: TimeSpan.FromSeconds(5));
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, nals.Select(n => Pair("MCM0001B", n, 0)).ToArray());
        Assert.False(change.NeedsLastCopyAcknowledgement);
        var applies = 0;
        h.Site.AfterRun = p =>
        {
            if ((string?)p["Operation"] == "Apply" && ++applies == 1) h.Site.Summary("MCM0001B", Cm01)!["State"] = DpContentStates.InstallFailed;
            return null;
        };
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true });

        Assert.Equal(1, applies);
        Assert.Equal(DpContentService.ChunkSize, h.Site.Log.Count(l => l == "Delete:MCM0001B"));
        var skipped = outcome.Pairs.Where(p => p.Result == DpContentResults.Skipped).ToList();
        Assert.Equal(30 - DpContentService.ChunkSize, skipped.Count);
        Assert.All(skipped, p => Assert.StartsWith("Skipped: 1 content of this part would have no distribution point with the content installed", p.Message));
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task UndoLeavesOutAnInstalledTaskSequencePairAndThePlanSaysSo()
    {
        var state = StubDpContentSite.Lab();
        ((JsonArray)state["Summaries"]!).Add(StubDpContentSite.Sum("MCM00008", Cm01, 0, 4));
        ((JsonArray)state["Instances"]!).Add(StubDpContentSite.Inst("MCM00008", Cm01, 0, "2026-10-20T09:13:44"));
        using var h = new DpContentHarness(state);
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM00008", Cm01, 0, DpContentTypes.TaskSequence), Pair("MCM000A6", Cm01, 0));
        Assert.Contains(change.Impact, l => l.StartsWith("Undo does not restore 1 installed pair of a task sequence", StringComparison.Ordinal));
        Assert.Contains(change.Impact, l => l.StartsWith("Undo distributes the 1 pair that is installed now again", StringComparison.Ordinal));

        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true, LastCopyAcknowledged = true });
        Assert.All(outcome.Pairs, p => Assert.Equal(DpContentResults.Confirmed, p.Result));
        Assert.Equal("MCM000A6", Assert.Single(outcome.UndoChange!.Pairs).PackageId);
    }

    [Fact]
    public async Task DistributeAgainOfApplicationContentPassesTheCiIdTheCmdletTakes()
    {
        using var h = new DpContentHarness();
        var removed = await h.ApplyAsync((await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A8", Cm01, 0, DpContentTypes.Application))) with { RemovalConfirmed = true, LastCopyAcknowledged = true });
        var outcome = await h.ApplyAsync(await h.Service.PrepareAsync(removed.UndoChange!, CancellationToken.None));

        Assert.Contains("ApplicationId:16780955", h.Site.Log);
        Assert.Equal(DpContentResults.Confirmed, Assert.Single(outcome.Pairs).Result);
    }

    [Fact]
    public void AnAuditReadOfALogLargerThanTheWindowSaysOlderRecordsWereLeftOut()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-dpc-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, DpContentAuditLog.FileName);
            var message = new string('x', 20_000);
            var text = new StringBuilder();
            for (var i = 0; i < 900; i++)
                text.Append("{\"Timestamp\":\"2026-10-23T08:00:00\",\"User\":\"LAB\\\\admin\",\"Event\":\"Validate\",\"Phase\":\"Finished\",\"Result\":\"Succeeded\",\"RunId\":\"run-")
                    .Append(i).Append("\",\"Chunk\":1,\"PairCount\":0,\"Pairs\":[],\"Message\":\"").Append(message).Append("\"}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            var log = new DpContentAuditLog(() => path);

            var records = log.Read(2001);
            Assert.True(records.Count < 900);
            Assert.True(log.OlderRecordsDropped);
            Assert.Equal("run-899", records[^1].RunId);

            File.WriteAllText(path, text.ToString()[..(text.Length / 100)], new UTF8Encoding(false));
            log.Read(2001);
            Assert.False(log.OlderRecordsDropped);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
