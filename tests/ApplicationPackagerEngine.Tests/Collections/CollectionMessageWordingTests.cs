using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using static ApplicationPackagerEngine.Tests.Collections.FakeCollectionSession;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// The texts the Collections service and page generate, from every result status of the write
/// script for every change kind, the refusals of a plan, and the page's message builders, read
/// for a word written twice in a row and for a number that does not agree with its verb.
/// </summary>
public sealed partial class CollectionMessageWordingTests
{
    [GeneratedRegex(@"\b([A-Za-z]+)\s+\1\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoubledWord();

    [GeneratedRegex(@"\b1 (collection|rule|query|device|member) (include|exclude|use|have|are|were)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SingularWithPluralVerb();

    private static void AssertWording(IEnumerable<(string Source, string Text)> texts)
    {
        var problems = texts.Where(t => t.Text is not null).Where(t => DoubledWord().IsMatch(t.Text) || SingularWithPluralVerb().IsMatch(t.Text))
            .Select(t => $"{t.Source}: {t.Text}").Distinct().ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Answers every write with one result status, and every read with nothing.</summary>
    private sealed class StatusSession(string status) : IConfigMgrSession
    {
        public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            if (script.Name != "WriteCollection") return Task.FromResult(new ConfigMgrCommandResult([], []));
            var collection = new
            {
                Kind = "Collection", CollectionId = "MCM00020", Name = "Pilot", ObjectPath = "", LimitToCollectionId = "SMS00001", LimitToCollectionName = "All Systems",
                MemberCount = 0, RefreshType = 2, RefreshSchedule = "0001200000100038", Comment = "", WindowCount = 0, DirectRuleCount = 0, QueryRuleCount = 1, IncludeRuleCount = 0, ExcludeRuleCount = 0
            };
            var rule = new { Kind = "Rule", RuleKind = "Query", RuleName = "Ring", Expression = CollectionServiceTests.WsQuery, ResourceId = "", IncludedCollectionId = "" };
            var query = new { Kind = "SavedQuery", QueryId = "MCM00001", Name = "Laptops", Expression = CollectionServiceTests.WsQuery, TargetClassName = "SMS_R_System", Comment = "", LimitToCollectionId = "" };
            var result = new
            {
                Kind = "Result", Status = status, Detail = "0", Steps = new[] { "add-final", "remove-old" }, Collection = collection, Rules = new[] { rule },
                Query = query, References = new[] { "MCM00030" }, CreatedId = "MCM00020"
            };
            return Task.FromResult(new ConfigMgrCommandResult([JsonSerializer.SerializeToElement(result)], []));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IReadOnlyList<CollectionChange> EveryKind()
    {
        var row = new CollectionRow("MCM00020", "Pilot", "", "SMS00001", "All Systems", 0, false, "Periodic", "", 0, 1, 0, 0, "0001200000100038");
        var rule = new CollectionRule(CollectionRuleKind.Query, "Ring", CollectionServiceTests.WsQuery, "", "", "");
        var newRule = rule with { RuleName = "Ring 2" };
        var include = new CollectionRule(CollectionRuleKind.Include, "Servers", "", "", "MCM00011", "");
        var query = new SavedQuery("MCM00001", "Laptops", CollectionServiceTests.WsQuery, "SMS_R_System", "");
        CollectionChange Make(CollectionChangeKind kind) => new(kind, "MCM00020", "Pilot", row, row with { Comment = "new" }, null, null, null, null, []);
        return
        [
            Make(CollectionChangeKind.Create) with { CollectionId = "", BeforeRow = null, AfterRow = row with { CollectionId = "" }, Rules = [rule] },
            Make(CollectionChangeKind.Copy) with { AfterRow = row with { Name = "Pilot copy" } },
            Make(CollectionChangeKind.SetProperties),
            Make(CollectionChangeKind.AddRule) with { RuleAfter = rule },
            Make(CollectionChangeKind.AddRule) with { RuleAfter = include },
            Make(CollectionChangeKind.ApplyTemplate) with { RuleAfter = rule },
            Make(CollectionChangeKind.UpdateRule) with { RuleBefore = rule, RuleAfter = newRule },
            Make(CollectionChangeKind.RemoveRule) with { RuleBefore = rule },
            Make(CollectionChangeKind.RemoveRule) with { RuleBefore = include },
            Make(CollectionChangeKind.Evaluate),
            Make(CollectionChangeKind.Remove) with { AfterRow = null, Rules = [rule], RemoveCounts = new CollectionRemoveCounts(0, 0, 0) },
            Make(CollectionChangeKind.SaveQuery) with { CollectionId = "", QueryAfter = query with { QueryId = "" } },
            Make(CollectionChangeKind.UpdateQuery) with { CollectionId = "", QueryBefore = query, QueryAfter = query with { Comment = "c" } },
            Make(CollectionChangeKind.DeleteQuery) with { CollectionId = "", QueryBefore = query }
        ];
    }

    private static IReadOnlyList<string> ScriptStatuses()
    {
        using var stream = typeof(CollectionService).Assembly.GetManifestResourceStream(CollectionService.WriteResource)!;
        var text = new StreamReader(stream).ReadToEnd();
        var statuses = Regex.Matches(text, @"Write-Result '([a-z-]+)'").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("changed", statuses);
        Assert.Contains("schedule-mismatch", statuses);
        return [.. statuses, "unknown-status"];
    }

    [Fact]
    public async Task EveryOutcomeOfEveryWriteStatusReadsWithoutADoubledWord()
    {
        var texts = new List<(string, string)>();
        foreach (var status in ScriptStatuses())
        {
            var service = new CollectionService(new StatusSession(status));
            foreach (var change in EveryKind())
            {
                var outcome = Assert.Single(await service.ApplyAsync([change], new CollectionServiceTests.Recorder<CollectionOutcome>(), CancellationToken.None));
                texts.Add(($"{change.Kind}/{status}", outcome.Message));
                foreach (var line in outcome.UndoChange?.Impact ?? []) texts.Add(($"{change.Kind}/{status} undo", line));
            }
        }
        Assert.Contains(texts, t => t.Item2.Contains("changed after the plan was shown", StringComparison.Ordinal));
        AssertWording(texts);
    }

    [Fact]
    public async Task ThePlanLinesAndRefusalsOfThePreparedChangesReadWithoutADoubledWord()
    {
        var site = CollectionServiceTests.Site();
        site.Add("MCM00015", "Ring 2", "SMS00001", IncludeRule("MCM00014", "Empty"));
        site.Add("MCM00016", "Ring 3", "SMS00001", ExcludeRule("MCM00014", "Empty"));
        var service = CollectionServiceTests.Service(site);
        var texts = new List<(string, string)>();
        async Task Collect(string source, CollectionChange request)
        {
            try
            {
                var prepared = await service.PrepareAsync(request, CancellationToken.None);
                texts.AddRange(prepared.Impact.Select(l => (source, l)));
            }
            catch (CollectionServiceException ex) { texts.Add((source, ex.Message)); }
        }
        await Collect("remove limiting", CollectionServiceTests.Change(CollectionChangeKind.Remove, "MCM00010"));
        await Collect("remove included once", CollectionServiceTests.Change(CollectionChangeKind.Remove, "MCM00011"));
        await Collect("remove included twice", CollectionServiceTests.Change(CollectionChangeKind.Remove, "MCM00014"));
        await Collect("remove", CollectionServiceTests.Change(CollectionChangeKind.Remove, "MCM00012"));
        await Collect("evaluate", CollectionServiceTests.Change(CollectionChangeKind.Evaluate, "MCM00012"));
        await Collect("include", CollectionServiceTests.Change(CollectionChangeKind.AddRule, "MCM00012") with { RuleAfter = new CollectionRule(CollectionRuleKind.Include, "", "", "", "MCM00011", "") });
        await Collect("exclude", CollectionServiceTests.Change(CollectionChangeKind.AddRule, "MCM00012") with { RuleAfter = new CollectionRule(CollectionRuleKind.Exclude, "", "", "", "MCM00011", "") });
        await Collect("rule", CollectionServiceTests.Change(CollectionChangeKind.AddRule, "MCM00012") with { RuleAfter = new CollectionRule(CollectionRuleKind.Query, "Collection rule", CollectionServiceTests.WsQuery, "", "", "") });
        await Collect("rule taken", CollectionServiceTests.Change(CollectionChangeKind.AddRule, "MCM00010") with { RuleAfter = new CollectionRule(CollectionRuleKind.Query, "WS", CollectionServiceTests.WsQuery, "", "", "") });
        await Collect("remove rule", CollectionServiceTests.Change(CollectionChangeKind.RemoveRule, "MCM00010") with { RuleBefore = new CollectionRule(CollectionRuleKind.Query, "WS", CollectionServiceTests.WsQuery, "", "", "") });
        await Collect("remove missing rule", CollectionServiceTests.Change(CollectionChangeKind.RemoveRule, "MCM00012") with { RuleBefore = new CollectionRule(CollectionRuleKind.Query, "WS", CollectionServiceTests.WsQuery, "", "", "") });

        Assert.Contains(texts, t => t.Item1 == "remove included once" && t.Item2.StartsWith("1 collection includes or excludes collection Servers (MCM00011)", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Item1 == "remove included twice" && t.Item2.StartsWith("2 collections include or exclude collection Empty (MCM00014)", StringComparison.Ordinal));
        AssertWording(texts);
    }

    [Fact]
    public async Task TheRemovePlanFromTheRealServiceStatesTheUndoNoteOnce()
    {
        if (!StubSitePowerShell.WindowsPowerShellPresent) return;
        var site = new StubSitePowerShell(StubSitePowerShell.State(new { Reject = "xx" },
            new { CollectionID = "SMS00001", Name = "All Systems", LimitToCollectionID = "", Rules = Array.Empty<object>() },
            new { CollectionID = "MCM00020", Name = "Pilot", LimitToCollectionID = "SMS00001", Windows = 1, Deployments = 1, Rules = Array.Empty<object>() }));
        var page = await RealServicePage.OpenAsync(site, "MCM00020");
        page.Harness.Plans.Confirm = false;

        await page.ViewModel.RemoveCollectionCommand.ExecuteAsync(null);

        var detail = Assert.Single(page.Harness.Plans.Shown[^1].Items).Detail!;
        Assert.Single(Regex.Matches(detail, @"\bUndo (creates|recreates)\b"));
        Assert.Single(Regex.Matches(detail, "new ID"));
        Assert.Single(Regex.Matches(detail, "are not restored"));
        Assert.Contains("Deployments to the collection: 1.", detail);
        AssertWording([("remove plan", detail)]);
    }

    /// <summary>Every public static text and text builder of the page, called with plain names and with counts of 1 and 2.</summary>
    [Fact]
    public void ThePageTextsReadWithoutADoubledWord()
    {
        var texts = new List<(string, string)>();
        foreach (var type in new[] { typeof(CollectionComplianceViewModel), typeof(CollectionsText), typeof(CollectionWriter), typeof(CollectionEditorViewModel), typeof(WqlParameters) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(string)))
                texts.Add(($"{type.Name}.{field.Name}", (string)field.GetValue(null)!));
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(string) && !m.IsSpecialName))
            {
                var parameters = method.GetParameters();
                if (!parameters.All(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(int))) continue;
                foreach (var n in new[] { 1, 2 })
                {
                    var arguments = parameters.Select(p => p.ParameterType == typeof(int) ? (object)n : p.Name switch
                    {
                        "noun" => "collection",
                        "id" => "MCM00020",
                        _ => "Pilot"
                    }).ToArray();
                    try { texts.Add(($"{type.Name}.{method.Name}({n})", (string)method.Invoke(null, arguments)!)); }
                    catch (TargetInvocationException) { }
                }
            }
        }
        Assert.True(texts.Count > 40, $"Only {texts.Count} texts were read.");
        AssertWording(texts);
    }
}
