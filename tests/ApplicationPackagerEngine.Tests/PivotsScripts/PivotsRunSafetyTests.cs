using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>
/// The send, the wait and the plan rules of a run through the real service, the real session script
/// on the stub site and the stub administration service: what reaches the clients, what the audit
/// log and the session say when something fails after the Started record, and how the wait reads.
/// </summary>
public sealed class PivotsRunSafetyTests
{
    private const string Client01 = "CLIENT01";
    private const int Client01Id = 16777221;
    private const int Cm01Id = 16777219;

    // ---- A collection target goes to the fixed devices of the plan --------------------------------

    [Fact]
    public async Task ADeviceThatJoinsTheCollectionAfterTheMemberCheckDoesNotGetTheQuery()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00014");
        h.Admin.BeforeSend = () =>
        {
            h.Site.List("Members").Add(StubPivotsSite.Member("MCM00014", Cm01Id, "CM01"));
            return Task.CompletedTask;
        };
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Sent);
        Assert.Equal([Client01Id], Assert.Single(h.Admin.SentTo));
        Assert.DoesNotContain(h.Admin.Requests, r => r.PathAndQuery.Contains("Collections(", StringComparison.Ordinal));
        Assert.Equal([Client01Id], o.Devices.Select(d => d.ResourceId));
        Assert.Contains(plan.Impact, l => l.Contains("fixed list of devices in the plan, not for collection MCM00014", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeviceThatJoinsTheCollectionAfterTheMemberCheckDoesNotGetTheScript()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Collection, "MCM00014");
        h.Site.BeforeRun = (values, site) =>
        {
            if ((string?)values["Operation"] == "Run") site.List("Members").Add(StubPivotsSite.Member("MCM00014", Cm01Id, "CM01"));
        };
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.True(o.Sent, o.Message);
        var run = Assert.Single(h.Site.Log, l => l.StartsWith("Invoke-CMScript:", StringComparison.Ordinal));
        Assert.Contains($":devices {Client01Id}:", run);
        Assert.DoesNotContain("collection", run);
        Assert.Equal([Client01Id], o.Devices.Select(d => d.ResourceId));
    }

    // ---- Anything that throws after the Started record ---------------------------------------------

    [Fact]
    public async Task AStatusAnswerThatIsNotJsonOrNotAnObjectDoesNotEndTheRun()
    {
        using var h = new PivotsHarness();
        h.Admin.AnswerAfterPolls = 0;
        h.Admin.StatusBodies.Enqueue("<html><body>Service starting</body></html>");
        h.Admin.StatusBodies.Enqueue("[1,2]");
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Sent);
        Assert.NotNull(o.OperationId);
        Assert.True(o.Succeeded, o.Message);
        Assert.Equal(PivotsDeviceStates.Completed, o.Devices.Single().State);
        Assert.Equal(["Started", "Finished"], h.Audit.Read(10).Select(r => r.Phase));
    }

    [Fact]
    public async Task AnUnexpectedErrorAfterTheStartedRecordSaysThatItIsUnknownWhetherTheRunWasSent()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01);
        h.Site.BeforeRun = (values, _) =>
        {
            if ((string?)values["Operation"] == "Run") throw new InvalidOperationException("The pipe broke. SECRET-DETAIL-1234");
        };
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.True(o.Sent);
        Assert.True(o.Partial);
        Assert.False(o.Succeeded);
        Assert.Contains("unknown whether the request was sent", o.Message);
        Assert.DoesNotContain("SECRET", o.Message);
        Assert.NotNull(o.SessionId);
        Assert.Equal(Client01Id, o.Devices.Single().ResourceId);
        var records = h.Audit.Read(10);
        Assert.Equal(["Started", "Finished"], records.Select(r => r.Phase));
        Assert.Equal("Partial", records[1].Result);
        Assert.Contains("unknown whether the request was sent", records[1].Message);
        Assert.Equal(o.SessionId, records[1].SessionId);
        var session = h.Sessions.Read(o.SessionId!)!;
        Assert.Equal("Unknown", session.Status);
        Assert.True(session.Partial);
    }

    // ---- The session script refuses text that changed after the plan ------------------------------

    [Fact]
    public async Task AScriptWhoseSiteHashChangesAfterTheServiceCheckIsRefusedInTheRun()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01);
        h.Site.BeforeRun = (values, site) =>
        {
            if ((string?)values["Operation"] == "Run") site.Script(StubPivotsSite.ScriptGuid)!["ScriptHash"] = "APPROVEDAGAIN";
        };
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.Equal(0, h.Site.Count("Invoke-CMScript:"));
        Assert.Contains("changed after the plan was made", o.Message);
        Assert.Contains("Nothing reached a client", o.Message);
        Assert.Equal("Failed", h.Audit.Read(5).Last().Result);
    }

    [Fact]
    public async Task AScriptWhoseTextChangesAfterTheServiceCheckIsRefusedInTheRun()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01);
        h.Site.BeforeRun = (values, site) =>
        {
            if ((string?)values["Operation"] == "Run") site.Script(StubPivotsSite.ScriptGuid)!["Script"] = StubPivotsSite.Encoded(StubPivotsSite.ScriptText + "\nRemove-Item C:\\Data");
        };
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.Equal(0, h.Site.Count("Invoke-CMScript:"));
        Assert.Contains("changed after the plan was made", o.Message);
    }

    // ---- A target that holds every client of the site -----------------------------------------------

    public static TheoryData<RunTargetKind, string> EveryClient() => new()
    {
        { RunTargetKind.List, "CM01, DC01, CLIENT01, CLIENT02" },
        { RunTargetKind.Pattern, "*" },
        { RunTargetKind.Collection, "MCM00020" },
        { RunTargetKind.Collection, "MCM00021" }
    };

    private static JsonObject WithEveryClientCollections()
    {
        var state = StubPivotsSite.Lab01();
        var collections = state["Collections"]!.AsArray();
        var members = state["Members"]!.AsArray();
        collections.Add(new JsonObject { ["CollectionID"] = "MCM00020", ["Name"] = "Every client", ["MemberCount"] = 4 });
        foreach (var (id, name) in new[] { (16777219, "CM01"), (16777220, "DC01"), (Client01Id, Client01), (16777222, "CLIENT02") })
            members.Add(StubPivotsSite.Member("MCM00020", id, name));
        // A collection with as many members as All Systems, whose member rows the site has not refreshed yet.
        collections.Add(new JsonObject { ["CollectionID"] = "MCM00021", ["Name"] = "Copy of All Systems", ["MemberCount"] = 8 });
        members.Add(StubPivotsSite.Member("MCM00021", Client01Id, Client01));
        return state;
    }

    [Theory]
    [MemberData(nameof(EveryClient))]
    public async Task ATargetThatHoldsEveryClientOfTheSiteNeedsTheEveryDeviceBoxInTheService(RunTargetKind kind, string text)
    {
        using var h = new PivotsHarness(WithEveryClientCollections());
        var plan = await h.PreparePivotAsync(kind, text, window: TimeSpan.FromSeconds(30));
        Assert.False(plan.Blocked, string.Join(" ", plan.Checks.Select(c => c.Message)));
        Assert.True(plan.Targets!.CoversEverySiteDevice);
        Assert.False(plan.Targets.IsBuiltInCollection);
        Assert.True(plan.NeedsEveryDeviceAcknowledgement);
        Assert.Contains(plan.Checks, c => c.Name == PivotsScriptsService.CheckNames.EveryDevice && c.Result == DeploymentCheckResults.Warn);
        var refused = await h.ApplyAsync(plan);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, refused.Message);
        Assert.Contains("every client of the site", refused.Message);
        Assert.Equal(0, h.Admin.Sends);
        Assert.Empty(h.AuditLines());
        Assert.True((await h.ApplyAsync(plan with { EveryDeviceAcknowledged = true })).Sent);
    }

    [Theory]
    [InlineData(RunTargetKind.List, "CLIENT01, CLIENT02")]
    [InlineData(RunTargetKind.Pattern, "CLIENT*")]
    [InlineData(RunTargetKind.Collection, "MCM00015")]
    public async Task ATargetThatHoldsPartOfTheClientsNeedsNoEveryDeviceBox(RunTargetKind kind, string text)
    {
        using var h = new PivotsHarness();
        var targets = await h.ResolveAsync(kind, text);
        Assert.False(targets.CoversEverySiteDevice);
        Assert.Equal(4, targets.SiteClientCount);
    }

    // ---- The send ------------------------------------------------------------------------------------

    [Fact]
    public async Task ASendAnswerOverTheSizeLimitCountsAsSentAndWritesTheFinishedRecord()
    {
        using var h = new PivotsHarness();
        h.Admin.SendTooLarge = true;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Sent);
        Assert.True(o.Partial);
        Assert.Contains("can have reached the site", o.Message);
        Assert.Equal(["Started", "Finished"], h.Audit.Read(10).Select(r => r.Phase));
        Assert.Equal("Unknown", h.Sessions.Read(o.SessionId!)!.Status);
    }

    [Fact]
    public async Task AProviderNameThatIsNotAHostBlocksThePlanAndTheRunBeforeTheAuditRecord()
    {
        using var h = new PivotsHarness();
        h.ProviderName = "https://cm01.contoso.com/AdminService";
        var blocked = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        Assert.True(blocked.Blocked);
        Assert.Contains(blocked.Checks, c => c.Name == PivotsScriptsService.CheckNames.Provider && c.Result == DeploymentCheckResults.Fail);

        h.ProviderName = StubAdminService.Host;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        h.ProviderName = "cm01 contoso";
        var o = await h.ApplyAsync(plan);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, o.Message);
        Assert.False(o.Sent);
        Assert.Empty(h.AuditLines());
        Assert.Equal(0, h.Admin.Sends);
    }

    // ---- The wait --------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSameRefusedStatusAnswerEndsTheWaitAfterTheDocumentedNumberOfReads()
    {
        using var h = new PivotsHarness();
        h.Admin.StatusAnswer = 401;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01, window: TimeSpan.FromMinutes(5));
        var o = await h.ApplyAsync(plan);
        Assert.Equal(PivotsScriptsService.MaxIdenticalStatusRefusals, h.Admin.StatusReads);
        var d = o.Devices.Single();
        Assert.Equal(PivotsDeviceStates.NoResponse, d.State);
        Assert.Contains("HTTP 401", d.Message);
        Assert.Contains("wait ended early", d.Message);
        Assert.Equal("Partial", h.Audit.Read(5).Last().Result);
    }

    [Fact]
    public async Task AnUnavailableServiceForLongerThanTheRefusalLimitDoesNotEndTheWait()
    {
        using var h = new PivotsHarness();
        h.Admin.Unavailable = PivotsScriptsService.MaxIdenticalStatusRefusals + 5;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Succeeded, o.Message);
        Assert.Equal(PivotsDeviceStates.Completed, o.Devices.Single().State);
    }

    [Fact]
    public async Task AStatusAnswerOverTheSizeLimitFallsBackToTheDeviceResults()
    {
        using var h = new PivotsHarness();
        h.Admin.StatusTooLarge = true;
        h.Admin.AnswerAfterPolls = 0;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        var o = await h.ApplyAsync(plan);
        var d = o.Devices.Single();
        Assert.Equal(PivotsDeviceStates.Completed, d.State);
        Assert.Single(d.Rows);
        Assert.Single(h.Admin.Requests, r => r.PathAndQuery.Contains("CMPivotResult", StringComparison.Ordinal));
        Assert.Equal(1, h.Admin.StatusReads);
    }

    private static JsonObject WithDevices(int count, int first)
    {
        var state = StubPivotsSite.Lab01();
        for (var i = 0; i < count; i++) state["Devices"]!.AsArray().Add(StubPivotsSite.Device(first + i, $"WKS{i:000}"));
        return state;
    }

    [Fact]
    public async Task TheStatusReadIsPagedAndDoesNotReadDevicesThatAnswered()
    {
        const int first = 30_000_000;
        using var h = new PivotsHarness(WithDevices(250, first));
        // The first 120 devices answer on the first read; the others later.
        h.Admin.AnswerAfter = id => id < first + 120 ? 0 : 4;
        var plan = await h.PreparePivotAsync(RunTargetKind.Pattern, "WKS*", window: TimeSpan.FromMinutes(5));
        Assert.Equal(250, plan.DeviceCount);
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Succeeded, o.Message);
        Assert.All(o.Devices, d => Assert.Equal(PivotsDeviceStates.Completed, d.State));
        Assert.All(h.Admin.StatusRowCounts, n => Assert.InRange(n, 0, PivotsScriptsService.StatusPageSize));
        var reads = h.Admin.Requests.Where(r => r.PathAndQuery.Contains("SMS_CMPivotStatus?", StringComparison.Ordinal)).Select(r => r.PathAndQuery).ToList();
        Assert.All(reads, r => Assert.Contains("$top=100", r));
        Assert.Contains(reads, r => r.Contains($"ResourceId%20ge%20{first + 120}", StringComparison.Ordinal));
        Assert.True(reads.Count(r => r.Contains($"ResourceId%20ge%20{first}&", StringComparison.Ordinal)) <= 2);
    }

    [Fact]
    public async Task ASiteThatRefusesThePagedReadGetsTheReadOfAllRows()
    {
        using var h = new PivotsHarness();
        h.Admin.RefusePaging = true;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Succeeded, o.Message);
        Assert.Single(h.Admin.Requests, r => r.PathAndQuery.Contains("$top=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AScriptPollReadsOnlyTheDevicesThatDidNotAnswer()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Collection, "MCM00015", window: TimeSpan.FromSeconds(30));
        await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.Contains(h.Site.Log, l => l.EndsWith("ResourceId IN (16777221,16777222)", StringComparison.Ordinal));
        Assert.Contains(h.Site.Log, l => l.EndsWith("AND ResourceId IN (16777222)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheWaitOfARunDoesNotHoldTheAuditLogOpen()
    {
        using var h = new PivotsHarness();
        h.Admin.AnswerAfterPolls = 3;
        var probes = new List<bool>();
        h.OnDelay = _ => probes.Add(h.Audit.Probe().Writable);
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Succeeded, o.Message);
        Assert.NotEmpty(probes);
        Assert.All(probes, Assert.True);
        Assert.Equal(["Started", "Finished"], h.Audit.Read(10).Select(r => r.Phase));
        Assert.All(h.Audit.Read(10), r => Assert.True(r.Verified));
    }

    // ---- The plan ----------------------------------------------------------------------------------------

    [Fact]
    public async Task APlanOlderThanTheLimitIsRefused()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        h.Clock += PivotsScriptsService.MaxPlanAge + TimeSpan.FromSeconds(1);
        var o = await h.ApplyAsync(plan);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, o.Message);
        Assert.Contains("older than 30 minutes", o.Message);
        Assert.Equal(0, h.Admin.Sends);
        Assert.Empty(h.AuditLines());
    }

    // ---- A reloaded run --------------------------------------------------------------------------------

    [Fact]
    public async Task ASavedRunOfAnotherSiteIsNotReadOnTheCurrentConnection()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01, window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        h.Restart();
        h.Connection = ("LAB", "cm02.contoso.com");
        var ex = await Assert.ThrowsAsync<PivotsServiceException>(() => h.WaitAsync(o.OperationId!, sessionId: o.SessionId));
        Assert.Contains("belongs to site MCM on cm01.contoso.com", ex.Message);
        Assert.Contains("connected to site LAB on cm02.contoso.com", ex.Message);
        Assert.Equal(0, h.Admin.StatusReads);
    }

    [Fact]
    public async Task AReloadedRunWhoseStartIsOutsideTheReadPartOfTheLogWritesNoSecondFinishedRecord()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01, window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        var other = "{\"Timestamp\":\"2026-10-24T09:00:00\",\"Event\":\"RunPivot\",\"Phase\":\"Started\",\"RunId\":\"x\",\"SessionId\":\"ffffffffffffffffffffffffffffffff\",\"Message\":\"\"}";
        File.AppendAllLines(h.Audit.Path, Enumerable.Repeat(other, PivotsScriptsService.MaxAuditRecordsSearched));
        h.Restart();
        var again = await h.WaitAsync(o.OperationId!, sessionId: o.SessionId);
        Assert.Equal(PivotsDeviceStates.Completed, again.Devices.Single().State);
        Assert.DoesNotContain(h.Audit.Read(20), r => r.Phase == "Finished" && r.SessionId == o.SessionId);
    }

    [Fact]
    public void ASessionListReadsNoMoreFilesThanItReturns()
    {
        using var h = new PivotsHarness();
        var store = h.Sessions;
        for (var i = 0; i < 3; i++)
        {
            var id = PivotsSessionStore.NewId();
            store.Save(new PivotsSessionRecord(id, h.Clock.AddMinutes(i), "u", "MCM", StubAdminService.Host, PivotsChangeKind.RunPivot, "t", null,
                [new ResolvedDevice(Client01Id, Client01)], "OS", null, null, null, new Dictionary<string, string>()) { OperationId = (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture) });
            File.SetLastWriteTimeUtc(Path.Combine(store.Folder, id + ".json"), h.Clock.AddMinutes(i));
        }
        Assert.Single(store.List(1));
        Assert.Equal(1, store.FilesRead);
        var newest = store.List(10)[0];
        Assert.NotNull(((IPivotsSessionStore)store).FindByOperation(newest.OperationId!, 10));
        Assert.Equal(1, store.FilesRead);
    }
}
