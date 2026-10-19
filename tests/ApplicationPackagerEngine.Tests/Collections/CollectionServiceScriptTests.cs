using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using static ApplicationPackagerEngine.Tests.Collections.StubSitePowerShell;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// Every change kind of the service through its real embedded scripts on the stub site in
/// Windows PowerShell, with the Undo of each change run the same way.
/// </summary>
public sealed class CollectionServiceScriptTests
{
    private const string WsQuery = "select * from SMS_R_System where Name like 'WS%'";
    private const string PcQuery = "select * from SMS_R_System where Name like 'PC%'";
    private const string SevenDays = "0001200000100038";

    private static object Members(int count) =>
        Enumerable.Range(0, count).Select(i => new { ResourceID = (16777300 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), Name = $"PC{i:D4}", IsClient = true, IsActive = i % 2 == 0, IsDirect = false }).ToArray();

    private static StubSitePowerShell Site(params object[] extra)
    {
        var collections = new List<object>
        {
            new { CollectionID = "SMS00001", Name = "All Systems", LimitToCollectionID = "", Rules = Array.Empty<object>() },
            new
            {
                CollectionID = "MCM00010", Name = "Workstations", LimitToCollectionID = "SMS00001", Comment = "c", RefreshType = 2, Schedule = SevenDays,
                Windows = 2, Deployments = 3, Members = Members(3), Rules = new[] { QueryRule("WS", WsQuery), DirectRule("16777220", "DC01") }
            },
            new { CollectionID = "MCM00011", Name = "Servers", LimitToCollectionID = "SMS00001", Rules = Array.Empty<object>() },
            new { CollectionID = "MCM00014", Name = "Empty", LimitToCollectionID = "SMS00001", Comment = "", RefreshType = 2, Schedule = SevenDays, Rules = Array.Empty<object>() }
        };
        collections.AddRange(extra);
        var site = new StubSitePowerShell(State(new { Reject = "xx" }, collections.ToArray()));
        site.AddQuery("MCM00001", "Laptops", "select * from SMS_R_System where Name like 'LT%'", "old");
        return site;
    }

    private static CollectionService Service(StubSitePowerShell site, RecordingLog? log = null) => new(site, log, () => "t1");

    private static CollectionChange Change(CollectionChangeKind kind, string id = "", string name = "") => new(kind, id, name, null, null, null, null, null, null, []);

    private static CollectionRule Q(string name, string expression) => new(CollectionRuleKind.Query, name, expression, "", "", "");

    private static CollectionRow NewRow(string name, string refresh = "Periodic", RefreshInterval? interval = null) =>
        new("", name, "", "SMS00001", "", 0, false, refresh, "made here", 0, 0, 0, 0, "", interval);

    private static async Task<CollectionOutcome> Apply(CollectionService service, CollectionChange change)
    {
        var outcomes = await service.ApplyAsync([change], new CollectionServiceTests.Recorder<CollectionOutcome>(), CancellationToken.None);
        return outcomes.Single();
    }

    private static async Task<CollectionOutcome> PrepareAndApply(CollectionService service, CollectionChange request) =>
        await Apply(service, await service.PrepareAsync(request, CancellationToken.None));

    private static string Text(JsonObject node, string name) => (string?)node[name] ?? "";

    // ---- Create, copy, properties ----

    [Fact]
    public async Task CreateWritesTheRulesAndTheIntervalAndUndoRemovesTheCollection()
    {
        var site = Site();
        var service = Service(site);
        var outcome = await PrepareAndApply(service, Change(CollectionChangeKind.Create) with
        {
            AfterRow = NewRow("New One", interval: new RefreshInterval(4, "Hours")),
            Rules = [Q("Win [x64]", WsQuery), new CollectionRule(CollectionRuleKind.Include, "", "", "", "MCM00011", "")]
        });

        Assert.True(outcome.Succeeded, outcome.Message);
        var made = site.AllCollections.Single(c => Text(c, "Name") == "New One");
        Assert.Equal(("made here", 2, "SMS00001"), (Text(made, "Comment"), (int)made["RefreshType"]!, Text(made, "LimitToCollectionID")));
        Assert.Contains("New-CMSchedule:Hours:4", site.Log);
        Assert.Equal(new RefreshInterval(4, "Hours"), CollectionService.IntervalOf(Text(made, "Schedule")));
        Assert.Equal([("Win [x64]", WsQuery)], site.QueryRules(Text(made, "CollectionID")));

        var undo = await Apply(service, outcome.UndoChange!);
        Assert.True(undo.Succeeded, undo.Message);
        Assert.False(site.HasCollection(Text(made, "CollectionID")));
    }

    [Fact]
    public async Task APeriodicCreateWithoutAnIntervalUsesSevenDaysAndThePlanSaysSo()
    {
        var site = Site();
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.Create) with { AfterRow = NewRow("Weekly") }, CancellationToken.None);
        Assert.Contains(prepared.Impact, l => l.Contains("every 7 days") && l.Contains("default"));

        var outcome = await Apply(service, prepared);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("New-CMSchedule:Days:7", site.Log);
    }

    [Fact]
    public async Task CopyMakesTheCopyWithItsRulesAndUndoRemovesIt()
    {
        var site = Site();
        var service = Service(site);
        var outcome = await PrepareAndApply(service, Change(CollectionChangeKind.Copy, "MCM00010", "Workstations") with { AfterRow = NewRow("Workstations copy") });

        Assert.True(outcome.Succeeded, outcome.Message);
        var copy = site.AllCollections.Single(c => Text(c, "Name") == "Workstations copy");
        Assert.Equal(Text(copy, "CollectionID"), outcome.UndoChange!.CollectionId);
        Assert.Single(site.QueryRules(Text(copy, "CollectionID")));
        Assert.True((await Apply(service, outcome.UndoChange)).Succeeded);
        Assert.False(site.HasCollection(Text(copy, "CollectionID")));
    }

    [Fact]
    public async Task SetPropertiesSetsTheIntervalAndUndoPutsTheExactOldScheduleBack()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.GetCollectionAsync("MCM00014", CancellationToken.None))!;
        Assert.Equal(new RefreshInterval(7, "Days"), current.RefreshInterval);

        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.SetProperties, "MCM00014") with
        {
            AfterRow = current with { Comment = "new", RefreshInterval = new RefreshInterval(2, "Days") }
        }, CancellationToken.None);
        Assert.Contains(prepared.Impact, l => l.Contains("every 7 days") && l.Contains("every 2 days"));

        var outcome = await Apply(service, prepared);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(new RefreshInterval(2, "Days"), CollectionService.IntervalOf(Text(site.CollectionState("MCM00014"), "Schedule")));
        Assert.Equal("new", Text(site.CollectionState("MCM00014"), "Comment"));

        var undo = await Apply(service, outcome.UndoChange!);
        Assert.True(undo.Succeeded, undo.Message);
        Assert.Equal((SevenDays, ""), (Text(site.CollectionState("MCM00014"), "Schedule"), Text(site.CollectionState("MCM00014"), "Comment")));
    }

    [Fact]
    public async Task SetPropertiesWithoutAnIntervalKeepsTheSchedule()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.GetCollectionAsync("MCM00014", CancellationToken.None))!;
        var outcome = await PrepareAndApply(service, Change(CollectionChangeKind.SetProperties, "MCM00014") with { AfterRow = current with { Comment = "x", RefreshInterval = null } });

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(SevenDays, Text(site.CollectionState("MCM00014"), "Schedule"));
        Assert.Contains(site.Log, l => l.StartsWith("Set-Collection:", StringComparison.Ordinal) && !l.Contains("RefreshSchedule"));
    }

    // ---- Rules ----

    [Fact]
    public async Task QueryDirectAndIncludeRulesAreAddedAndRemovedWithTheirUndo()
    {
        var site = Site();
        var service = Service(site);

        var query = await PrepareAndApply(service, Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = Q("New rule", PcQuery) });
        Assert.True(query.Succeeded, query.Message);
        var direct = await PrepareAndApply(service, Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = new CollectionRule(CollectionRuleKind.Direct, "", "", "16777221", "", "") });
        Assert.True(direct.Succeeded, direct.Message);
        var include = await PrepareAndApply(service, Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = new CollectionRule(CollectionRuleKind.Include, "", "", "", "MCM00011", "") });
        Assert.True(include.Succeeded, include.Message);
        Assert.Equal(3, site.CollectionState("MCM00014")["Rules"]!.AsArray().Count);

        foreach (var undo in new[] { include.UndoChange!, direct.UndoChange!, query.UndoChange! })
        {
            var result = await Apply(service, undo);
            Assert.True(result.Succeeded, result.Message);
        }
        Assert.Empty(site.CollectionState("MCM00014")["Rules"]!.AsArray());
    }

    [Fact]
    public async Task AConsoleRuleWithBracketsIsRemovedAndUndoAddsItAgain()
    {
        var site = Site(new { CollectionID = "MCM00020", Name = "Win", LimitToCollectionID = "SMS00001", Rules = new[] { QueryRule("Win [x64]", WsQuery), QueryRule("Win x", PcQuery) } });
        var service = Service(site);

        var removed = await PrepareAndApply(service, Change(CollectionChangeKind.RemoveRule, "MCM00020") with { RuleBefore = Q("Win [x64]", WsQuery) });
        Assert.True(removed.Succeeded, removed.Message);
        Assert.Equal([("Win x", PcQuery)], site.QueryRules("MCM00020"));

        var undo = await Apply(service, removed.UndoChange!);
        Assert.True(undo.Succeeded, undo.Message);
        Assert.Contains(("Win [x64]", WsQuery), site.QueryRules("MCM00020"));
    }

    [Fact]
    public async Task AConsoleCollectionWithABracketRuleIsRemovedAndUndoCreatesItAgain()
    {
        var site = Site(new { CollectionID = "MCM00020", Name = "Win", LimitToCollectionID = "SMS00001", Comment = "keep", RefreshType = 6, Schedule = "0001200000101000", Rules = new[] { QueryRule("Win [x64]", WsQuery) } });
        var service = Service(site);

        var removed = await PrepareAndApply(service, Change(CollectionChangeKind.Remove, "MCM00020"));
        Assert.True(removed.Succeeded, removed.Message);
        Assert.False(site.HasCollection("MCM00020"));

        var undo = await Apply(service, removed.UndoChange!);
        Assert.True(undo.Succeeded, undo.Message);
        var again = site.AllCollections.Single(c => Text(c, "Name") == "Win");
        Assert.Equal(("keep", 6, "0001200000101000"), (Text(again, "Comment"), (int)again["RefreshType"]!, Text(again, "Schedule")));
        Assert.Equal([("Win [x64]", WsQuery)], site.QueryRules(Text(again, "CollectionID")));
    }

    [Fact]
    public async Task ARuleNameWithAsteriskDoesNotCountAsADuplicateOfAnotherRule()
    {
        var site = Site(new { CollectionID = "MCM00020", Name = "Stars", LimitToCollectionID = "SMS00001", Rules = new[] { QueryRule("A*B", WsQuery), QueryRule("AxB", PcQuery) } });
        var outcome = await PrepareAndApply(Service(site), Change(CollectionChangeKind.RemoveRule, "MCM00020") with { RuleBefore = Q("A*B", WsQuery) });

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal([("AxB", PcQuery)], site.QueryRules("MCM00020"));
    }

    [Fact]
    public async Task ARenamingUpdateWhoseRemoveDoesNothingIsNotReportedAsDone()
    {
        var site = Site();
        site.Fail("ignore-remove:WS");
        var outcome = await PrepareAndApply(Service(site), Change(CollectionChangeKind.UpdateRule, "MCM00010") with { RuleBefore = Q("WS", WsQuery), RuleAfter = Q("WS 2", PcQuery) });

        Assert.False(outcome.Succeeded);
        Assert.Contains("Both rules exist now", outcome.Message);
        Assert.Equal(CollectionChangeKind.RemoveRule, outcome.UndoChange!.Kind);
        Assert.Equal("WS 2", outcome.UndoChange.RuleBefore!.RuleName);
    }

    [Fact]
    public async Task ASameNameUpdateThatLeavesTheTemporaryRuleIsPartialAndItsUndoRemovesThatRuleToo()
    {
        var site = Site();
        var suffix = 0;
        var service = new CollectionService(site, null, () => "t" + ++suffix);
        site.Fail("ignore-remove:WS (update t1)");
        var outcome = await PrepareAndApply(service, Change(CollectionChangeKind.UpdateRule, "MCM00010") with { RuleBefore = Q("WS", WsQuery), RuleAfter = Q("WS", PcQuery) });

        Assert.False(outcome.Succeeded);
        Assert.Contains("WS (update t1)", outcome.Message);
        Assert.Equal([("WS (update t1)", PcQuery), ("WS", PcQuery)], site.QueryRules("MCM00010").OrderByDescending(r => r.Name.Length).ToList());

        var undo = await Apply(service, outcome.UndoChange!);
        Assert.True(undo.Succeeded, undo.Message);
        Assert.Equal([("WS", WsQuery)], site.QueryRules("MCM00010"));
        Assert.True(site.Counts.Where(c => c.StartsWith("MCM00010=", StringComparison.Ordinal)).All(c => int.Parse(c[9..], System.Globalization.CultureInfo.InvariantCulture) >= 1));
    }

    [Fact]
    public async Task EvaluateRunsTheUpdate()
    {
        var site = Site();
        var outcome = await PrepareAndApply(Service(site), Change(CollectionChangeKind.Evaluate, "MCM00010"));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("Invoke-CMCollectionUpdate", site.Log);
    }

    // ---- Remove ----

    [Fact]
    public async Task RemoveRunsWhenNothingChangedAndUndoCreatesTheCollectionAgain()
    {
        var site = Site();
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.Remove, "MCM00010"), CancellationToken.None);
        Assert.Equal(new CollectionRemoveCounts(3, 3, 2), prepared.RemoveCounts);

        var outcome = await Apply(service, prepared);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False(site.HasCollection("MCM00010"));
        var undo = await Apply(service, outcome.UndoChange!);
        Assert.True(undo.Succeeded, undo.Message);
        var again = site.AllCollections.Single(c => Text(c, "Name") == "Workstations");
        Assert.Equal(("c", SevenDays), (Text(again, "Comment"), Text(again, "Schedule")));
    }

    [Theory]
    [InlineData("Deployments", 4)]
    [InlineData("Windows", 3)]
    [InlineData("Members", 4)]
    [InlineData("Comment", "changed")]
    [InlineData("LimitToCollectionID", "MCM00011")]
    [InlineData("RefreshType", 6)]
    [InlineData("Schedule", "0001200000101000")]
    public async Task RemoveIsRefusedWhenWhatGoesWithTheCollectionChangedAfterThePlan(string field, object value)
    {
        var site = Site();
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.Remove, "MCM00010"), CancellationToken.None);
        var state = site.CollectionState("MCM00010");
        state[field] = field == "Members" ? JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Members((int)value))) : JsonValue.Create(value);

        var outcome = await Apply(service, prepared);

        Assert.False(outcome.Succeeded);
        Assert.StartsWith(CollectionService.SkippedPrefix, outcome.Message);
        Assert.Contains("changed after the plan was shown", outcome.Message);
        Assert.True(site.HasCollection("MCM00010"));
        Assert.DoesNotContain("Remove-Collection", site.Log);
    }

    // ---- Saved queries ----

    [Fact]
    public async Task SavedQueriesAreSavedUpdatedAndDeletedWithUndo()
    {
        var site = Site();
        var service = Service(site);

        var saved = await PrepareAndApply(service, Change(CollectionChangeKind.SaveQuery) with { QueryAfter = new SavedQuery("", "Desktops", "select * from SMS_R_System", "", "c") });
        Assert.True(saved.Succeeded, saved.Message);
        Assert.True((await Apply(service, saved.UndoChange!)).Succeeded);
        Assert.DoesNotContain(site.QueriesState, q => (string?)q!["Name"] == "Desktops");

        var laptops = (await service.ListSavedQueriesAsync(CancellationToken.None)).Single(q => q.Name == "Laptops");
        var deleted = await PrepareAndApply(service, Change(CollectionChangeKind.DeleteQuery) with { QueryBefore = laptops });
        Assert.True(deleted.Succeeded, deleted.Message);
        var again = await Apply(service, deleted.UndoChange!);
        Assert.True(again.Succeeded, again.Message);
        Assert.Contains(site.QueriesState, q => (string?)q!["Name"] == "Laptops" && (string?)q["Comments"] == "old");
    }

    [Fact]
    public async Task UndoOfAnUpdateThatAddedALimitClearsTheLimitAgain()
    {
        var site = Site();
        var service = Service(site);
        var laptops = (await service.ListSavedQueriesAsync(CancellationToken.None)).Single(q => q.Name == "Laptops");
        var outcome = await PrepareAndApply(service, Change(CollectionChangeKind.UpdateQuery) with { QueryBefore = laptops, QueryAfter = laptops with { LimitToCollectionId = "SMS00001" } });
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("SMS00001", (string?)site.QueriesState.Single(q => (string?)q!["QueryID"] == "MCM00001")!["LimitToCollectionID"]);

        var undo = await Apply(service, outcome.UndoChange!);
        Assert.True(undo.Succeeded, undo.Message);
        Assert.Equal("", (string?)site.QueriesState.Single(q => (string?)q!["QueryID"] == "MCM00001")!["LimitToCollectionID"]);
    }

    // ---- Uncertain writes ----

    [Fact]
    public async Task ACreateThatReturnsNoIdIsReadBackAndKeepsItsUndo()
    {
        var site = Site();
        site.SetOption("CreateReturnsNothing", true);
        var outcome = await PrepareAndApply(Service(site), Change(CollectionChangeKind.Create) with { AfterRow = NewRow("No id") });

        var made = site.AllCollections.Single(c => Text(c, "Name") == "No id");
        Assert.Equal(CollectionChangeKind.Remove, outcome.UndoChange?.Kind);
        Assert.Equal(Text(made, "CollectionID"), outcome.UndoChange!.CollectionId);
    }

    [Fact]
    public async Task ACreateWhoseReadBackFailsInTheScriptIsReadBackByIdAndKeepsItsUndo()
    {
        var site = Site();
        site.SetOption("HideNew", true);
        var outcome = await PrepareAndApply(Service(site), Change(CollectionChangeKind.Create) with { AfterRow = NewRow("Hidden") });

        var made = site.AllCollections.Single(c => Text(c, "Name") == "Hidden");
        Assert.Equal(CollectionChangeKind.Remove, outcome.UndoChange?.Kind);
        Assert.Equal(Text(made, "CollectionID"), outcome.UndoChange!.CollectionId);
    }

    [Fact]
    public async Task ACollectionAnotherAdminCreatedUnderTheSameNameIsNotTakenAsThisCreate()
    {
        var site = Site();
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.Create) with { AfterRow = NewRow("Race") }, CancellationToken.None);
        site.Fail("create:Race");
        site.AfterRun = (script, p) =>
        {
            if (script != "WriteCollection") return null;
            site.AddCollection(new JsonObject { ["CollectionID"] = "MCM00099", ["Name"] = "Race", ["LimitToCollectionID"] = "MCM00011", ["Comment"] = "theirs", ["Rules"] = new JsonArray() });
            return new ConfigMgrSessionException(ConfigMgrFailure.ProcessDied, "The session ended.");
        };

        var outcome = await Apply(service, prepared);
        site.AfterRun = null;

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
        Assert.Contains("not known", outcome.Message);
        Assert.Contains("Race", outcome.Message);
    }

    [Fact]
    public async Task ASavedQueryAnotherAdminSavedUnderTheSameNameIsNotTakenAsThisSave()
    {
        var site = Site();
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.SaveQuery) with { QueryAfter = new SavedQuery("", "Race", "select * from SMS_R_System", "", "") }, CancellationToken.None);
        site.Fail("newquery:Race");
        site.AfterRun = (script, _) =>
        {
            if (script != "WriteCollection") return null;
            site.AddQuery("MCM00077", "Race", "select Name from SMS_R_System", "theirs");
            return new ConfigMgrSessionException(ConfigMgrFailure.ProcessDied, "The session ended.");
        };

        var outcome = await Apply(service, prepared);
        site.AfterRun = null;

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
        Assert.Contains("not known", outcome.Message);
    }

    // ---- Reads ----

    [Fact]
    public async Task TheDetailPagesMembersInNameOrderWithTheTotalFromTheProvider()
    {
        var site = Site(new { CollectionID = "MCM00030", Name = "Big", LimitToCollectionID = "SMS00001", Members = Members(450), Rules = Array.Empty<object>() });
        var service = Service(site);

        var page = await service.GetDetailAsync("MCM00030", 2, CancellationToken.None);

        Assert.Equal(450, page.MemberTotal);
        Assert.Equal(450 - 2 * CollectionService.MemberPageSize, page.Members.Count);
        Assert.Equal("PC0400", page.Members[0].Name);
    }

    [Fact]
    public async Task ValidateAndPreviewStopReadingAtTheCountLimit()
    {
        var site = Site();
        site.SetOption("ExtraRows", 60);
        var service = new CollectionService(site, null, () => "t1") { MatchCountLimit = 20 };

        var validate = await service.ValidateWqlAsync("select * from SMS_R_System", CancellationToken.None);
        Assert.True(validate.IsValid);
        Assert.Equal(20, validate.MatchCount);
        Assert.Contains("20 or more", validate.Message);
        Assert.True(site.Log.Count(l => l == "Row") <= 20, string.Join(",", site.Log));

        site.Log.Clear();
        var preview = await service.PreviewWqlAsync("select Name, ResourceID from SMS_R_System", 5, CancellationToken.None);
        Assert.Equal((20, 5), (preview.MatchCount, preview.FirstRows.Count));
        Assert.Contains("20 or more", preview.Message);
        Assert.True(site.Log.Count(l => l == "Row") <= 20);
    }
}
