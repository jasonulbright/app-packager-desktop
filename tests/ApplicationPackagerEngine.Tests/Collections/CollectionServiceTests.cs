using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using static ApplicationPackagerEngine.Tests.Collections.FakeCollectionSession;

namespace ApplicationPackagerEngine.Tests.Collections;

public sealed class CollectionServiceTests
{
    internal const string WsQuery = "select * from SMS_R_System where Name like 'WS%'";

    internal sealed class Recorder<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) { lock (Items) Items.Add(value); }
    }

    internal static FakeCollectionSession Site()
    {
        var site = new FakeCollectionSession();
        site.Add("SMS00001", "All Systems", "", QueryRule("All Systems", "select * from sms_r_system"));
        var ws = site.Add("MCM00010", "Workstations", "SMS00001", QueryRule("WS", WsQuery), DirectRule("16777220", "DC01"));
        ws.Members.Add(("16777221", "CLIENT01", true, true, false));
        ws.Members.Add(("16777220", "DC01", true, false, true));
        ws.Members.Add(("16777300", "OLD01", false, false, false));
        ws.Windows = 2;
        ws.Deployments = 3;
        ws.Path = "/Clients/Windows";
        site.Add("MCM00011", "Servers", "SMS00001");
        site.Add("MCM00012", "Pilot", "MCM00010");
        site.Add("MCM00013", "Ring 1", "SMS00001", IncludeRule("MCM00011", "Servers"));
        site.Add("MCM00014", "Empty", "SMS00001");
        site.Queries.Add(new Query { Id = "SMS001", Name = "All Systems", Expression = "select * from sms_r_system" });
        site.Queries.Add(new Query { Id = "MCM00001", Name = "Laptops", Expression = "select * from SMS_R_System where Name like 'LT%'", Comment = "old" });
        return site;
    }

    internal static CollectionService Service(FakeCollectionSession site, RecordingLog? log = null) => new(site, log, () => "t1");

    internal static CollectionChange Change(CollectionChangeKind kind, string id = "", string name = "") =>
        new(kind, id, name, null, null, null, null, null, null, []);

    internal static async Task<(IReadOnlyList<CollectionOutcome> Outcomes, List<CollectionOutcome> Reported)> Apply(CollectionService service, params CollectionChange[] changes)
    {
        var recorder = new Recorder<CollectionOutcome>();
        var outcomes = await service.ApplyAsync(changes, recorder, CancellationToken.None);
        return (outcomes, recorder.Items);
    }

    private static async Task<CollectionOutcome> PrepareAndApply(CollectionService service, CollectionChange request)
    {
        var prepared = await service.PrepareAsync(request, CancellationToken.None);
        var (outcomes, reported) = await Apply(service, prepared);
        Assert.Single(reported);
        return outcomes.Single();
    }

    // ---- Reads ----

    [Fact]
    public async Task TheListHasEveryDeviceCollectionWithItsCountsAndBuiltInFlag()
    {
        var site = Site();
        var rows = await Service(site).ListCollectionsAsync(CancellationToken.None);

        Assert.Equal(6, rows.Count);
        var ws = rows.Single(r => r.CollectionId == "MCM00010");
        Assert.Equal(("Workstations", @"Clients\Windows", 3, "Periodic", 1, 1, 0, 0), (ws.Name, ws.FolderPath, ws.MemberCount, ws.RefreshType, ws.DirectRuleCount, ws.QueryRuleCount, ws.IncludeRuleCount, ws.ExcludeRuleCount));
        Assert.Equal("0001200000100038", ws.RefreshSchedule);
        Assert.True(rows.Single(r => r.CollectionId == "SMS00001").IsBuiltIn);
        Assert.False(ws.IsBuiltIn);
        Assert.Single(site.Calls);
    }

    [Fact]
    public async Task OpenByIdReadsOneCollectionAndReturnsNullWhenItIsMissing()
    {
        var site = Site();
        var service = Service(site);

        Assert.Equal("Servers", (await service.GetCollectionAsync(" mcm00011 ", CancellationToken.None))!.Name);
        Assert.Null(await service.GetCollectionAsync("MCM00099", CancellationToken.None));
        Assert.All(site.Calls, c => Assert.Equal("Get", c.Operation));
        var error = await Assert.ThrowsAsync<CollectionServiceException>(() => service.GetCollectionAsync("x' OR 1=1", CancellationToken.None));
        Assert.Contains("collection ID", error.Message);
        Assert.Equal(2, site.Calls.Count);
    }

    [Fact]
    public async Task TheDetailHasTheRulesAndOnePageOfMembers()
    {
        var site = Site();
        for (var i = 0; i < 450; i++) site.Find("MCM00014")!.Members.Add(($"{20000000 + i}", $"PC{i:D4}", true, true, false));
        var service = Service(site);

        var detail = await service.GetDetailAsync("MCM00010", 0, CancellationToken.None);
        Assert.Equal(2, detail.Rules.Count);
        Assert.Contains(detail.Rules, r => r is { Kind: CollectionRuleKind.Query, RuleName: "WS", Expression: WsQuery });
        Assert.Contains(detail.Rules, r => r is { Kind: CollectionRuleKind.Direct, ResourceId: "16777220", Detail: "Resource 16777220" });
        Assert.Equal(["Client, active", "Client, inactive", "No client"], detail.Members.Select(m => m.ClientState).ToArray());
        Assert.True(detail.Members.Single(m => m.Name == "DC01").IsDirect);

        var page = await service.GetDetailAsync("MCM00014", 2, CancellationToken.None);
        Assert.Equal(450, page.MemberTotal);
        Assert.Equal(450 - 2 * CollectionService.MemberPageSize, page.Members.Count);
        Assert.Equal("PC0400", page.Members[0].Name);

        await Assert.ThrowsAsync<CollectionServiceException>(() => service.GetDetailAsync("MCM00099", 0, CancellationToken.None));
    }

    [Fact]
    public async Task ANonSelectExpressionNeverReachesTheProvider()
    {
        var site = Site();
        var service = Service(site);

        var validate = await service.ValidateWqlAsync("select * from SMS_R_System; delete from x", CancellationToken.None);
        var preview = await service.PreviewWqlAsync("associators of {SMS_R_System.ResourceId=1}", 10, CancellationToken.None);

        Assert.False(validate.IsValid);
        Assert.False(preview.IsValid);
        Assert.Empty(site.Calls);
    }

    [Fact]
    public async Task AQueryTheProviderRejectsGivesAPlainMessageAndTheRawTextGoesOnlyToTheLog()
    {
        var site = Site();
        var log = new RecordingLog();
        var result = await Service(site, log).ValidateWqlAsync("select * from SMS_R_Systemxx", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.DoesNotContain("Exception", result.Message);
        Assert.DoesNotContain("secret", result.Message);
        Assert.Contains(log.Lines, l => l.Contains("ManagementException"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("SMS_R_Systemxx"));
    }

    [Fact]
    public async Task ValidateReportsTheMatchCount()
    {
        var result = await Service(Site()).ValidateWqlAsync("select * from SMS_R_System", CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal(3, result.MatchCount);
        Assert.Contains("3 resources", result.Message);
    }

    [Fact]
    public async Task PreviewIsCappedCancellableAndKeepsTheMatchCount()
    {
        var site = Site();
        using var cts = new CancellationTokenSource();
        var result = await Service(site).PreviewWqlAsync("select Name, ResourceId from SMS_R_System", 2, cts.Token);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.MatchCount);
        Assert.Equal(["Name", "ResourceId"], result.Columns);
        Assert.Equal(2, result.FirstRows.Count);
        Assert.Equal(["CM01", "16777219"], result.FirstRows[0]);
        Assert.Contains("first 2", result.Message);
        Assert.True(site.Calls.Single().Cancellable);
        Assert.Equal(2, site.Calls.Single().Parameters["MaxRows"]);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(site).PreviewWqlAsync("select * from SMS_R_System", 5, cts.Token));
    }

    [Fact]
    public async Task SavedQueriesAreListedByName()
    {
        var queries = await Service(Site()).ListSavedQueriesAsync(CancellationToken.None);
        Assert.Equal(["All Systems", "Laptops"], queries.Select(q => q.Name).ToArray());
        Assert.Equal("old", queries[1].Comment);
    }

    [Fact]
    public async Task AccessDeniedOnAReadNamesTheRightsTheRoleNeeds()
    {
        var site = Site();
        site.Throw = _ => new ConfigMgrSessionException(ConfigMgrFailure.AccessDenied, "ConfigMgr denied access.");
        var error = await Assert.ThrowsAsync<CollectionServiceException>(() => Service(site).ListCollectionsAsync(CancellationToken.None));
        Assert.Contains("collections and queries", error.Message);
    }

    [Fact]
    public async Task AScriptFailureOnAReadGivesAPlainMessage()
    {
        var site = Site();
        var log = new RecordingLog();
        site.Throw = _ => new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, "System.Management.ManagementException at line 12 in C:\\x\\y.ps1");
        var error = await Assert.ThrowsAsync<CollectionServiceException>(() => Service(site, log).ListCollectionsAsync(CancellationToken.None));
        Assert.DoesNotContain("Exception", error.Message);
        Assert.Contains(log.Lines, l => l.Contains("ManagementException"));
    }

    // ---- Create, copy, properties ----

    private static CollectionRow NewRow(string name, string limit = "SMS00001", string refresh = "Periodic") =>
        new("", name, "", limit, "", 0, false, refresh, "a comment", 0, 0, 0, 0);

    [Fact]
    public async Task CreateMakesTheCollectionWithItsRulesAndUndoRemovesIt()
    {
        var site = Site();
        var service = Service(site);
        var request = Change(CollectionChangeKind.Create) with
        {
            AfterRow = NewRow("New One"),
            Rules = [new CollectionRule(CollectionRuleKind.Query, "Q1", WsQuery, "", "", ""), new CollectionRule(CollectionRuleKind.Include, "", "", "", "mcm00011", "")]
        };

        var prepared = await service.PrepareAsync(request, CancellationToken.None);
        Assert.Contains(prepared.Impact, l => l.Contains("Creates collection New One") && l.Contains("All Systems"));
        Assert.Contains(prepared.Impact, l => l.Contains("Undo removes the new collection"));
        Assert.Empty(site.Writes);

        var (outcomes, _) = await Apply(service, prepared);
        var outcome = outcomes.Single();
        Assert.True(outcome.Succeeded, outcome.Message);
        var made = site.Collections.Single(c => c.Name == "New One");
        Assert.Equal(2, made.Rules.Count);
        Assert.Equal("a comment", made.Comment);
        Assert.Equal(CollectionChangeKind.Remove, outcome.UndoChange!.Kind);
        Assert.Equal(made.Id, outcome.UndoChange.CollectionId);

        var (undo, _) = await Apply(service, outcome.UndoChange);
        Assert.True(undo.Single().Succeeded, undo.Single().Message);
        Assert.Null(site.Find(made.Id));
    }

    [Fact]
    public async Task CreateIsRefusedForATakenNameOrAMissingLimitingCollection()
    {
        var service = Service(Site());
        var taken = await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.Create) with { AfterRow = NewRow("servers") }, CancellationToken.None));
        Assert.Contains("exists already", taken.Message);
        var missing = await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.Create) with { AfterRow = NewRow("X", "MCM00099") }, CancellationToken.None));
        Assert.Contains("does not exist", missing.Message);
        await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.Create) with { AfterRow = NewRow("X", refresh: "Hourly") }, CancellationToken.None));
        await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.Create) with { AfterRow = NewRow("  ") }, CancellationToken.None));
    }

    [Fact]
    public async Task ACreateWhoseRuleFailsReportsTheMissingRuleAndKeepsItsUndo()
    {
        var site = Site();
        site.RuleCmdletFails = (kind, name) => kind == "Query" && name == "Bad";
        var outcome = await PrepareAndApply(Service(site), Change(CollectionChangeKind.Create) with
        {
            AfterRow = NewRow("Half"), Rules = [new CollectionRule(CollectionRuleKind.Query, "Bad", WsQuery, "", "", "")]
        });

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not add 1 rule: query rule Bad", outcome.Message);
        Assert.Equal(CollectionChangeKind.Remove, outcome.UndoChange!.Kind);
    }

    [Fact]
    public async Task CopyMakesANewCollectionAndUndoRemovesTheCopy()
    {
        var site = Site();
        var service = Service(site);
        var outcome = await PrepareAndApply(service, Change(CollectionChangeKind.Copy, "MCM00010", "Workstations") with { AfterRow = NewRow("Workstations copy") });

        Assert.True(outcome.Succeeded, outcome.Message);
        var copy = site.Collections.Single(c => c.Name == "Workstations copy");
        Assert.Equal(2, copy.Rules.Count);
        Assert.Equal(copy.Id, outcome.UndoChange!.CollectionId);
        Assert.Equal(CollectionChangeKind.Remove, outcome.UndoChange.Kind);
    }

    [Fact]
    public async Task SetPropertiesShowsOldAndNewValuesAndUndoSetsThemBack()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.GetCollectionAsync("MCM00014", CancellationToken.None))!;
        var request = Change(CollectionChangeKind.SetProperties, "MCM00014", "Empty") with
        {
            AfterRow = current with { Name = "Renamed", Comment = "new", LimitingCollectionId = "MCM00011", RefreshType = "Manual" }
        };

        var prepared = await service.PrepareAsync(request, CancellationToken.None);
        Assert.Contains("Name: Empty -> Renamed", prepared.Impact);
        Assert.Contains("Comment: (empty) -> new", prepared.Impact);
        Assert.Contains(prepared.Impact, l => l.StartsWith("Limiting collection:", StringComparison.Ordinal) && l.Contains("MCM00011"));
        Assert.Contains(prepared.Impact, l => l.StartsWith("Refresh:", StringComparison.Ordinal));

        var outcome = (await Apply(service, prepared)).Outcomes.Single();
        Assert.True(outcome.Succeeded, outcome.Message);
        var c = site.Find("MCM00014")!;
        Assert.Equal(("Renamed", "new", "MCM00011", 1), (c.Name, c.Comment, c.LimitId, c.RefreshType));

        var undo = (await Apply(service, outcome.UndoChange!)).Outcomes.Single();
        Assert.True(undo.Succeeded, undo.Message);
        Assert.Equal(("Empty", "", "SMS00001", 2), (c.Name, c.Comment, c.LimitId, c.RefreshType));
    }

    [Fact]
    public async Task SetPropertiesRefusesNoChangeSelfLimitingAndBuiltIn()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.GetCollectionAsync("MCM00014", CancellationToken.None))!;
        await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.SetProperties, "MCM00014") with { AfterRow = current }, CancellationToken.None));
        await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.SetProperties, "MCM00014") with { AfterRow = current with { LimitingCollectionId = "MCM00014" } }, CancellationToken.None));
        var builtIn = await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.SetProperties, "SMS00001") with { AfterRow = current }, CancellationToken.None));
        Assert.Contains("built-in", builtIn.Message);
    }

    [Fact]
    public async Task AChangeMadeBetweenThePlanAndTheWriteIsRefusedWithAPlainMessage()
    {
        var site = Site();
        var service = Service(site);
        var current = (await service.GetCollectionAsync("MCM00014", CancellationToken.None))!;
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.SetProperties, "MCM00014") with { AfterRow = current with { Comment = "mine" } }, CancellationToken.None);
        site.Find("MCM00014")!.Comment = "another admin";

        var outcome = (await Apply(service, prepared)).Outcomes.Single();
        Assert.False(outcome.Succeeded);
        Assert.Contains("changed after the plan was shown", outcome.Message);
        Assert.Equal("another admin", site.Find("MCM00014")!.Comment);
        Assert.Null(outcome.UndoChange);
    }

    // ---- Rules ----

    private static CollectionRule Q(string name, string expression) => new(CollectionRuleKind.Query, name, expression, "", "", "");

    [Fact]
    public async Task AQueryRuleAddShowsTheWqlKeepsThePreviewCountAndUndoRemovesTheRule()
    {
        var site = Site();
        site.StoreExpression = e => e.Replace("select *", "select SMS_R_SYSTEM.ResourceID", StringComparison.Ordinal);
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.AddRule, "MCM00014") with
        {
            RuleAfter = Q("New rule", "select * from SMS_R_System"), Impact = ["The last preview matched 3 resources."]
        }, CancellationToken.None);
        Assert.Equal("The last preview matched 3 resources.", prepared.Impact[0]);
        Assert.Contains("WQL: select * from SMS_R_System", prepared.Impact);
        Assert.DoesNotContain(site.Calls, c => c.Operation is "Preview" or "Validate");

        var outcome = (await Apply(service, prepared)).Outcomes.Single();
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("select SMS_R_SYSTEM.ResourceID from SMS_R_System", outcome.UndoChange!.RuleBefore!.Expression);
        var undo = (await Apply(service, outcome.UndoChange)).Outcomes.Single();
        Assert.True(undo.Succeeded, undo.Message);
        Assert.Empty(site.Find("MCM00014")!.Rules);
    }

    [Fact]
    public async Task DirectIncludeAndExcludeRulesAreAddedAndRemovedWithTheirUndo()
    {
        var site = Site();
        var service = Service(site);

        var direct = await PrepareAndApply(service, Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = new CollectionRule(CollectionRuleKind.Direct, "", "", "16777221", "", "") });
        Assert.True(direct.Succeeded, direct.Message);
        Assert.Contains("CLIENT01", direct.Message);

        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = new CollectionRule(CollectionRuleKind.Exclude, "", "", "", "MCM00011", "") }, CancellationToken.None);
        Assert.Contains(prepared.Impact, l => l.Contains("changes membership evaluation"));
        Assert.Contains(prepared.Impact, l => l.Contains("Servers (MCM00011)") && l.Contains("Empty (MCM00014)"));
        var exclude = (await Apply(service, prepared)).Outcomes.Single();
        Assert.True(exclude.Succeeded, exclude.Message);

        var remove = await PrepareAndApply(service, Change(CollectionChangeKind.RemoveRule, "MCM00014") with { RuleBefore = exclude.UndoChange!.RuleBefore });
        Assert.True(remove.Succeeded, remove.Message);
        Assert.Equal(CollectionChangeKind.AddRule, remove.UndoChange!.Kind);
        Assert.DoesNotContain(site.Find("MCM00014")!.Rules, r => r.Kind == "Exclude");

        await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = new CollectionRule(CollectionRuleKind.Include, "", "", "", "MCM00014", "") }, CancellationToken.None));
        await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = new CollectionRule(CollectionRuleKind.Direct, "", "", "99", "", "") }, CancellationToken.None));
    }

    [Fact]
    public async Task RuleRemoveIsRefusedWhenTheRuleChangedOrTheCollectionIsBuiltIn()
    {
        var site = Site();
        var service = Service(site);
        var changed = await Assert.ThrowsAsync<CollectionServiceException>(() =>
            service.PrepareAsync(Change(CollectionChangeKind.RemoveRule, "MCM00010") with { RuleBefore = Q("WS", "select other") }, CancellationToken.None));
        Assert.Contains("changed since it was read", changed.Message);
        await Assert.ThrowsAsync<CollectionServiceException>(() =>
            service.PrepareAsync(Change(CollectionChangeKind.RemoveRule, "SMS00001") with { RuleBefore = Q("All Systems", "select * from sms_r_system") }, CancellationToken.None));

        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.RemoveRule, "MCM00010") with { RuleBefore = Q("WS", WsQuery) }, CancellationToken.None);
        site.Find("MCM00010")!.Rules.Single(r => r.Kind == "Query").Expression = "select changed";
        var outcome = (await Apply(service, prepared)).Outcomes.Single();
        Assert.False(outcome.Succeeded);
        Assert.Contains("changed after the plan was shown", outcome.Message);
        Assert.Single(site.Find("MCM00010")!.Rules, r => r.Kind == "Query");
    }

    [Fact]
    public async Task ARuleNameWithWildcardsIsRefusedBeforeItReachesConfigMgr()
    {
        var site = Site();
        var error = await Assert.ThrowsAsync<CollectionServiceException>(() =>
            Service(site).PrepareAsync(Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = Q("W*", WsQuery) }, CancellationToken.None));
        Assert.Contains("* ? [ or ]", error.Message);
        Assert.Empty(site.Calls);
    }

    [Theory]
    [InlineData("WS")]
    [InlineData("WS renamed")]
    public async Task ARuleUpdateNeverLeavesTheCollectionWithFewerRules(string newName)
    {
        var site = Site();
        var service = Service(site);
        var outcome = await PrepareAndApply(service, Change(CollectionChangeKind.UpdateRule, "MCM00010") with
        {
            RuleBefore = Q("WS", WsQuery), RuleAfter = Q(newName, "select * from SMS_R_System where Name like 'PC%'")
        });

        Assert.True(outcome.Succeeded, outcome.Message);
        var rule = site.Find("MCM00010")!.Rules.Single(r => r.Kind == "Query");
        Assert.Equal((newName, "select * from SMS_R_System where Name like 'PC%'"), (rule.Name, rule.Expression));
        Assert.True(site.QueryRuleCounts["MCM00010"].Min() >= 1);
        Assert.Equal(CollectionChangeKind.UpdateRule, outcome.UndoChange!.Kind);
        Assert.Equal(WsQuery, outcome.UndoChange.RuleAfter!.Expression);

        var undo = (await Apply(service, outcome.UndoChange)).Outcomes.Single();
        Assert.True(undo.Succeeded, undo.Message);
        Assert.Equal(("WS", WsQuery), (site.Find("MCM00010")!.Rules.Single(r => r.Kind == "Query").Name, site.Find("MCM00010")!.Rules.Single(r => r.Kind == "Query").Expression));
    }

    [Theory]
    [InlineData("add-temporary", true, false)]
    [InlineData("remove-old", true, true)]
    [InlineData("add-final", false, true)]
    [InlineData("remove-temporary", false, true)]
    public async Task ASameNameRuleUpdateThatFailsAtAStepSaysWhichRulesExist(string step, bool oldStays, bool hasUndo)
    {
        var site = Site();
        const string temp = "WS (update t1)";
        site.RuleCmdletFails = step switch
        {
            "add-temporary" => (kind, name) => kind == "Query" && name == temp,
            "remove-old" => (kind, name) => kind == "remove" && name == "WS",
            "add-final" => (kind, name) => kind == "Query" && name == "WS",
            _ => (kind, name) => kind == "remove" && name == temp
        };
        var outcome = await PrepareAndApply(Service(site), Change(CollectionChangeKind.UpdateRule, "MCM00010") with
        {
            RuleBefore = Q("WS", WsQuery), RuleAfter = Q("WS", "select * from SMS_R_System where Name like 'PC%'")
        });

        Assert.False(outcome.Succeeded);
        var rules = site.Find("MCM00010")!.Rules.Where(r => r.Kind == "Query").ToList();
        Assert.Equal(oldStays, rules.Any(r => r.Name == "WS" && r.Expression == WsQuery));
        Assert.True(site.QueryRuleCounts.GetValueOrDefault("MCM00010", [1]).Min() >= 1);
        Assert.Equal(hasUndo, outcome.UndoChange is not null);
        if (hasUndo) Assert.Contains("Query rules on Workstations (MCM00010) now:", outcome.Message);
        Assert.DoesNotContain("SMS Provider reported", outcome.Message);
    }

    [Fact]
    public async Task ARenamingRuleUpdateWhoseAddFailsLeavesTheOldRule()
    {
        var site = Site();
        site.RuleCmdletFails = (kind, name) => kind == "Query" && name == "New";
        var outcome = await PrepareAndApply(Service(site), Change(CollectionChangeKind.UpdateRule, "MCM00010") with
        {
            RuleBefore = Q("WS", WsQuery), RuleAfter = Q("New", "select * from SMS_R_System")
        });

        Assert.False(outcome.Succeeded);
        Assert.Contains("is not changed", outcome.Message);
        Assert.Null(outcome.UndoChange);
        Assert.Single(site.Find("MCM00010")!.Rules, r => r.Kind == "Query" && r.Name == "WS");
    }

    [Fact]
    public async Task ARenamingRuleUpdateWhoseRemoveFailsSaysBothRulesExist()
    {
        var site = Site();
        site.RuleCmdletFails = (kind, name) => kind == "remove" && name == "WS";
        var outcome = await PrepareAndApply(Service(site), Change(CollectionChangeKind.UpdateRule, "MCM00010") with
        {
            RuleBefore = Q("WS", WsQuery), RuleAfter = Q("New", "select * from SMS_R_System")
        });

        Assert.False(outcome.Succeeded);
        Assert.Contains("Both rules exist now", outcome.Message);
        Assert.Equal(CollectionChangeKind.RemoveRule, outcome.UndoChange!.Kind);
        Assert.Equal("New", outcome.UndoChange.RuleBefore!.RuleName);
    }

    [Fact]
    public async Task EvaluateHasNoUndoAndSaysItStartsWorkOnTheSiteServer()
    {
        var site = Site();
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.Evaluate, "SMS00001"), CancellationToken.None);
        Assert.Contains(prepared.Impact, l => l.Contains("site server"));
        Assert.Contains("Undo: not applicable.", prepared.Impact);
        var outcome = (await Apply(service, prepared)).Outcomes.Single();
        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
    }

    // ---- Remove ----

    [Fact]
    public async Task RemoveShowsWhatGoesWithTheCollectionAndUndoCreatesItAgainWithItsRules()
    {
        var site = Site();
        site.Find("MCM00012")!.LimitId = "SMS00001";
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.Remove, "MCM00010", "Workstations"), CancellationToken.None);

        Assert.Contains("Members now: 3.", prepared.Impact);
        Assert.Contains(prepared.Impact, l => l.StartsWith("Deployments to the collection: 3.", StringComparison.Ordinal));
        Assert.Contains(prepared.Impact, l => l.StartsWith("Maintenance windows on the collection: 2.", StringComparison.Ordinal));
        Assert.Contains(prepared.Impact, l => l.Contains("new ID") && l.Contains("not restored"));
        Assert.Equal(2, prepared.Rules!.Count);

        var outcome = (await Apply(service, prepared)).Outcomes.Single();
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Null(site.Find("MCM00010"));
        Assert.Contains("new ID", outcome.Message);

        var undo = (await Apply(service, outcome.UndoChange!)).Outcomes.Single();
        Assert.True(undo.Succeeded, undo.Message);
        var again = site.Collections.Single(c => c.Name == "Workstations");
        Assert.NotEqual("MCM00010", again.Id);
        Assert.Equal(2, again.Rules.Count);
        Assert.Equal(0, again.Windows);
    }

    [Fact]
    public async Task RemoveIsRefusedForALimitingCollectionAnIncludedCollectionAndABuiltInCollection()
    {
        var site = Site();
        var service = Service(site);
        var limiting = await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.Remove, "MCM00010"), CancellationToken.None));
        Assert.Contains("limiting collection of 1 collection (Pilot (MCM00012))", limiting.Message);
        var included = await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.Remove, "MCM00011"), CancellationToken.None));
        Assert.Contains("Ring 1 (MCM00013)", included.Message);
        var builtIn = await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.Remove, "SMS00001"), CancellationToken.None));
        Assert.Contains("built-in", builtIn.Message);
        Assert.Empty(site.Writes);
    }

    [Fact]
    public async Task ARemoveIsRefusedWhenTheRulesChangedAfterThePlan()
    {
        var site = Site();
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.Remove, "MCM00014"), CancellationToken.None);
        site.Find("MCM00014")!.Rules.Add(Q2("Late", "select 1"));
        var outcome = (await Apply(service, prepared)).Outcomes.Single();
        Assert.False(outcome.Succeeded);
        Assert.NotNull(site.Find("MCM00014"));
    }

    private static Rule Q2(string name, string expression) => QueryRule(name, expression);

    // ---- Saved queries ----

    [Fact]
    public async Task SavedQueriesAreSavedUpdatedAndDeletedWithUndo()
    {
        var site = Site();
        var service = Service(site);

        var saved = await PrepareAndApply(service, Change(CollectionChangeKind.SaveQuery) with { QueryAfter = new SavedQuery("", "Desktops", "select * from SMS_R_System", "", "c") });
        Assert.True(saved.Succeeded, saved.Message);
        Assert.Equal(CollectionChangeKind.DeleteQuery, saved.UndoChange!.Kind);
        Assert.Equal("SMS_R_System", site.Queries.Single(q => q.Name == "Desktops").Target);

        var laptops = (await service.ListSavedQueriesAsync(CancellationToken.None)).Single(q => q.Name == "Laptops");
        var update = await service.PrepareAsync(Change(CollectionChangeKind.UpdateQuery) with { QueryBefore = laptops, QueryAfter = laptops with { Expression = "select * from SMS_R_System where Name like 'NB%'", Comment = "" } }, CancellationToken.None);
        Assert.Contains("Old WQL: select * from SMS_R_System where Name like 'LT%'", update.Impact);
        Assert.Contains("New WQL: select * from SMS_R_System where Name like 'NB%'", update.Impact);
        var updated = (await Apply(service, update)).Outcomes.Single();
        Assert.True(updated.Succeeded, updated.Message);
        var undoUpdate = (await Apply(service, updated.UndoChange!)).Outcomes.Single();
        Assert.True(undoUpdate.Succeeded, undoUpdate.Message);
        Assert.Equal(("select * from SMS_R_System where Name like 'LT%'", "old"), (site.Queries.Single(q => q.Name == "Laptops").Expression, site.Queries.Single(q => q.Name == "Laptops").Comment));

        var deleted = await PrepareAndApply(service, Change(CollectionChangeKind.DeleteQuery) with { QueryBefore = laptops });
        Assert.True(deleted.Succeeded, deleted.Message);
        Assert.Contains("new query ID", deleted.Message);
        var undoDelete = (await Apply(service, deleted.UndoChange!)).Outcomes.Single();
        Assert.True(undoDelete.Succeeded, undoDelete.Message);
        Assert.NotEqual("MCM00001", site.Queries.Single(q => q.Name == "Laptops").Id);
    }

    [Fact]
    public async Task BuiltInSavedQueriesAndTakenNamesAreRefused()
    {
        var site = Site();
        var service = Service(site);
        var all = (await service.ListSavedQueriesAsync(CancellationToken.None)).Single(q => q.QueryId == "SMS001");
        await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.DeleteQuery) with { QueryBefore = all }, CancellationToken.None));
        var taken = await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.SaveQuery) with { QueryAfter = new SavedQuery("", "laptops", "select * from SMS_R_System", "", "") }, CancellationToken.None));
        Assert.Contains("exists already", taken.Message);
        await Assert.ThrowsAsync<CollectionServiceException>(() => service.PrepareAsync(Change(CollectionChangeKind.SaveQuery) with { QueryAfter = new SavedQuery("", "X", "drop table", "", "") }, CancellationToken.None));
    }

    // ---- Apply template, cancel, partial writes ----

    [Fact]
    public async Task ApplyTemplateSkipsACollectionThatHasTheRuleNameAndReportsEachCollection()
    {
        var site = Site();
        site.Find("MCM00011")!.Rules.Add(QueryRule("Template rule", "select 1"));
        site.RuleCmdletFails = (kind, name) => false;
        var service = Service(site);
        var rule = Q("Template rule", "select * from SMS_R_System where Client = 1");
        var prepared = new List<CollectionChange>();
        foreach (var id in new[] { "MCM00011", "MCM00013", "MCM00014" })
            prepared.Add(await service.PrepareAsync(Change(CollectionChangeKind.ApplyTemplate, id) with { RuleAfter = rule }, CancellationToken.None));
        Assert.Contains(prepared[0].Impact, l => l.StartsWith(CollectionService.SkippedPrefix, StringComparison.Ordinal));

        site.Throw = c => c.Script == "WriteCollection" && Equals(c.Parameters.GetValueOrDefault("CollectionId"), "MCM00013")
            ? new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, "raw provider text")
            : null;
        var (outcomes, reported) = await Apply(service, prepared.ToArray());

        Assert.Equal(3, reported.Count);
        Assert.StartsWith(CollectionService.SkippedPrefix, outcomes[0].Message);
        Assert.False(outcomes[1].Succeeded);
        Assert.DoesNotContain("raw provider text", outcomes[1].Message);
        Assert.True(outcomes[2].Succeeded, outcomes[2].Message);
        Assert.Equal(CollectionChangeKind.RemoveRule, outcomes[2].UndoChange!.Kind);
        Assert.Single(site.Find("MCM00011")!.Rules);
    }

    [Fact]
    public async Task CancelStopsBetweenChangesAndReportsTheRestAsSkipped()
    {
        var site = Site();
        var service = Service(site);
        using var cts = new CancellationTokenSource();
        var changes = new List<CollectionChange>();
        foreach (var id in new[] { "MCM00011", "MCM00013", "MCM00014" })
            changes.Add(await service.PrepareAsync(Change(CollectionChangeKind.ApplyTemplate, id) with { RuleAfter = Q("T", "select * from SMS_R_System") }, CancellationToken.None));
        site.AfterWrite = _ => { cts.Cancel(); return null; };
        var recorder = new Recorder<CollectionOutcome>();

        var outcomes = await service.ApplyAsync(changes, recorder, cts.Token);

        Assert.True(outcomes[0].Succeeded);
        Assert.All(outcomes.Skip(1), o => Assert.StartsWith(CollectionService.SkippedPrefix, o.Message));
        Assert.Equal(3, recorder.Items.Count);
        Assert.Single(site.Writes);
    }

    [Fact]
    public async Task AFailureAfterTheWriteReadsTheStateBackAndKeepsTheUndo()
    {
        var site = Site();
        var service = Service(site);
        site.AfterWrite = _ => new ConfigMgrSessionException(ConfigMgrFailure.ProcessDied, "The ConfigMgr session ended.");

        var create = await PrepareAndApply(service, Change(CollectionChangeKind.Create) with { AfterRow = NewRow("Lost reply") });
        Assert.False(create.Succeeded);
        Assert.Contains("created collection Lost reply", create.Message);
        Assert.Equal(CollectionChangeKind.Remove, create.UndoChange!.Kind);

        var addRule = await PrepareAndApply(service, Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = Q("R", "select * from SMS_R_System") });
        Assert.False(addRule.Succeeded);
        Assert.Equal(CollectionChangeKind.RemoveRule, addRule.UndoChange!.Kind);

        var remove = await PrepareAndApply(service, Change(CollectionChangeKind.Remove, "MCM00012"));
        Assert.False(remove.Succeeded);
        Assert.Equal(CollectionChangeKind.Create, remove.UndoChange!.Kind);

        var save = await PrepareAndApply(service, Change(CollectionChangeKind.SaveQuery) with { QueryAfter = new SavedQuery("", "Q lost", "select * from SMS_R_System", "", "") });
        Assert.False(save.Succeeded);
        Assert.Equal(CollectionChangeKind.DeleteQuery, save.UndoChange!.Kind);
    }

    [Fact]
    public async Task AFailureBeforeAnyWriteKeepsThePlainFailureWithoutUndo()
    {
        var site = Site();
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.Create) with { AfterRow = NewRow("Never") }, CancellationToken.None);
        site.Throw = c => c.Script == "WriteCollection" ? new ConfigMgrSessionException(ConfigMgrFailure.Timeout, "The command did not finish in time.") : null;

        var outcome = (await Apply(service, prepared)).Outcomes.Single();
        Assert.False(outcome.Succeeded);
        Assert.Contains("is not known", outcome.Message);
        Assert.Null(outcome.UndoChange);
    }

    [Fact]
    public async Task AccessDeniedOnAWriteNamesTheRights()
    {
        var site = Site();
        var service = Service(site);
        var prepared = await service.PrepareAsync(Change(CollectionChangeKind.Evaluate, "MCM00014"), CancellationToken.None);
        site.Throw = c => c.Script == "WriteCollection" ? new ConfigMgrSessionException(ConfigMgrFailure.AccessDenied, "denied") : null;
        var outcome = (await Apply(service, prepared)).Outcomes.Single();
        Assert.Contains(CollectionService.AccessHint, outcome.Message);
    }

    [Fact]
    public async Task AnUnexpectedErrorBecomesAFailedOutcomeAndTheRunGoesOn()
    {
        var site = Site();
        var service = Service(site);
        var first = await service.PrepareAsync(Change(CollectionChangeKind.Evaluate, "MCM00014"), CancellationToken.None);
        var second = await service.PrepareAsync(Change(CollectionChangeKind.Evaluate, "MCM00011"), CancellationToken.None);
        var calls = 0;
        site.Throw = c => c.Script == "WriteCollection" && calls++ == 0 ? new InvalidOperationException("raw .NET text") : null;

        var (outcomes, reported) = await Apply(service, first, second);
        Assert.False(outcomes[0].Succeeded);
        Assert.DoesNotContain("raw .NET text", outcomes[0].Message);
        Assert.True(outcomes[1].Succeeded);
        Assert.Equal(2, reported.Count);
    }

    [Fact]
    public async Task AnUnpreparedChangeIsNotWritten()
    {
        var site = Site();
        var (outcomes, _) = await Apply(Service(site),
            Change(CollectionChangeKind.Remove, "MCM00014"),
            Change(CollectionChangeKind.SetProperties, "MCM00014"),
            Change(CollectionChangeKind.RemoveRule, "MCM00014"),
            Change(CollectionChangeKind.Remove, "SMS00001") with { BeforeRow = NewRow("x"), Rules = [] });
        Assert.All(outcomes, o => Assert.False(o.Succeeded));
        Assert.Empty(site.Writes);
    }

    [Fact]
    public async Task TheLogHoldsCountsAndOutcomesButNoWqlNamesOrValues()
    {
        var site = Site();
        var log = new RecordingLog();
        var service = Service(site, log);
        await service.ListCollectionsAsync(CancellationToken.None);
        await service.PreviewWqlAsync(WsQuery, 5, CancellationToken.None);
        await PrepareAndApply(service, Change(CollectionChangeKind.AddRule, "MCM00014") with { RuleAfter = Q("Secret rule", WsQuery) });

        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains("WS%") || l.Contains("Secret rule") || l.Contains("Workstations") || l.Contains("Empty"));
    }
}
