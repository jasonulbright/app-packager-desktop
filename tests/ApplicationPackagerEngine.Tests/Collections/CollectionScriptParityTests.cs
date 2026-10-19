using System.Text.Json;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.Collections.StubSitePowerShell;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// The service with its real embedded scripts over a stub site in Windows PowerShell, compared
/// with the validation, preview and rule-update functions of the two source modules on the same
/// stub site. A module that is not on this computer is replaced by its recorded behavior, and
/// the tests assert that the comparison ran.
/// </summary>
public sealed class CollectionScriptParityTests
{
    private const string FixedModule = @"C:\projects\collection-and-compliance-manager\Module\CCMCommon.psd1";
    private const string OlderModule = @"C:\projects\collection-manager\Module\CollectionManagerCommon.psd1";

    private const string Old = "select * from SMS_R_System where Name like 'WS%'";
    private const string New = "select * from SMS_R_System where Name like 'PC%'";


    private static StubSitePowerShell Site(params string[] fail) => new(State(new { Reject = "xx", Fail = fail },
        Collection("MCM00010", "Workstations", "SMS00001", QueryRule("WS", Old), QueryRule("Other", "select * from SMS_R_System")),
        Collection("SMS00001", "All Systems", ""),
        Collection("MCM00012", "Pilot", "MCM00010")));

    private static CollectionChange Update(string newName, string expression) =>
        new(CollectionChangeKind.UpdateRule, "MCM00010", "Workstations", null, null,
            new CollectionRule(CollectionRuleKind.Query, "WS", Old, "", "", ""), new CollectionRule(CollectionRuleKind.Query, newName, expression, "", "", ""), null, null, []);

    private static int MinimumCount(IEnumerable<string> counts) =>
        counts.Where(c => c.StartsWith("MCM00010=", StringComparison.Ordinal)).Select(c => int.Parse(c["MCM00010=".Length..])).DefaultIfEmpty(2).Min();

    private static async Task<(CollectionOutcome Outcome, StubSitePowerShell Site)> ServiceUpdate(string newName, string expression, params string[] fail)
    {
        var site = Site(fail);
        var service = new CollectionService(site, null, () => "t1");
        var outcomes = await service.ApplyAsync([Update(newName, expression)], new CollectionServiceTests.Recorder<CollectionOutcome>(), CancellationToken.None);
        return (outcomes.Single(), site);
    }

    // ---- The service scripts on the stub site ----

    [Theory]
    [InlineData("WS", new string[0], true, "WS", New)]
    [InlineData("WS 2", new string[0], true, "WS 2", New)]
    [InlineData("WS", new[] { "add:WS (update t1)" }, false, "WS", Old)]
    [InlineData("WS", new[] { "remove:WS" }, false, "WS", Old)]
    [InlineData("WS 2", new[] { "add:WS 2" }, false, "WS", Old)]
    [InlineData("WS 2", new[] { "remove:WS" }, false, "WS", Old)]
    public async Task TheUpdateScriptNeverDropsBelowTheRuleCountItStartedWith(string newName, string[] fail, bool succeeds, string keptName, string keptExpression)
    {
        if (!WindowsPowerShellPresent) return;
        var (outcome, site) = await ServiceUpdate(newName, New, fail);

        Assert.Equal(succeeds, outcome.Succeeded);
        Assert.Contains(site.QueryRules("MCM00010"), r => r.Name == keptName && r.Expression == keptExpression);
        Assert.True(MinimumCount(site.Counts) >= 2, string.Join(",", site.Counts));
        Assert.DoesNotContain("SMS Provider reported", outcome.Message);
    }

    [Fact]
    public async Task AnUpdateTheProviderRejectsLeavesTheOldRuleAndSaysSo()
    {
        if (!WindowsPowerShellPresent) return;
        var (outcome, site) = await ServiceUpdate("WS", "select * from SMS_R_Systemxx");

        Assert.False(outcome.Succeeded);
        Assert.Contains("is not changed", outcome.Message);
        Assert.Equal([("WS", Old), ("Other", "select * from SMS_R_System")], site.QueryRules("MCM00010"));
    }

    [Fact]
    public async Task TheRemoveScriptRefusesALimitingCollectionWithoutAWrite()
    {
        if (!WindowsPowerShellPresent) return;
        var site = Site();
        var service = new CollectionService(site);
        var error = await Assert.ThrowsAsync<CollectionServiceException>(() =>
            service.PrepareAsync(new CollectionChange(CollectionChangeKind.Remove, "MCM00010", "", null, null, null, null, null, null, []), CancellationToken.None));
        Assert.Contains("Pilot", error.Message);
        Assert.DoesNotContain("Remove-Collection", site.Log);
    }

    [Fact]
    public async Task TheReadScriptCountsRulesAndPagesMembersUnderStrictMode()
    {
        if (!WindowsPowerShellPresent) return;
        var service = new CollectionService(Site());
        var rows = await service.ListCollectionsAsync(CancellationToken.None);
        Assert.Equal(2, rows.Single(r => r.CollectionId == "MCM00010").QueryRuleCount);
        Assert.True(rows.Single(r => r.CollectionId == "SMS00001").IsBuiltIn);
        var detail = await service.GetDetailAsync("MCM00010", 0, CancellationToken.None);
        Assert.Equal(["WS", "Other"], detail.Rules.Select(r => r.RuleName).ToArray());
    }

    // ---- Validation and preview against the modules ----

    private static readonly string[] ComparedExpressions =
    [
        "select * from SMS_R_System",
        "select Name, ResourceID from SMS_R_System where Name like '%;%'",
        "select * from SMS_R_Systemxx",
        "select Name from SMS_R_System where Name = 'xx'"
    ];

    [Fact]
    public async Task ValidationMatchesBothModulesForEverySelectQuery()
    {
        if (!WindowsPowerShellPresent) return;
        var compared = 0;
        foreach (var module in new[] { FixedModule, OlderModule }.Where(File.Exists))
        {
            foreach (var expression in ComparedExpressions)
            {
                var moduleResult = Site().RunModule(module, "Test-WqlQuery", new { QueryExpression = expression }).Output.Single();
                var service = await new CollectionService(Site()).ValidateWqlAsync(expression, CancellationToken.None);
                Assert.Equal(moduleResult.GetProperty("IsValid").GetBoolean(), service.IsValid);
                Assert.Equal(moduleResult.GetProperty("ResultCount").GetInt32(), service.MatchCount);
                compared++;
            }
        }
        if (compared == 0) RecordedValidation();
        else Assert.Equal(8, compared);
    }

    /// <summary>The modules' Test-WqlQuery on the stub site, recorded: valid with 2 matches, or invalid with 0.</summary>
    private static void RecordedValidation()
    {
        var recorded = new (string Expression, bool Valid, int Count)[] { (ComparedExpressions[0], true, 2), (ComparedExpressions[2], false, 0) };
        var ran = 0;
        foreach (var (expression, valid, count) in recorded)
        {
            var service = new CollectionService(Site()).ValidateWqlAsync(expression, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal((valid, count), (service.IsValid, service.MatchCount));
            ran++;
        }
        Assert.Equal(2, ran);
    }

    [Fact]
    public async Task ADifferenceByDesignTheModulesSendAnyTextToTheProviderAndTheServiceRefusesIt()
    {
        if (!WindowsPowerShellPresent) return;
        var site = Site();
        var service = await new CollectionService(site).ValidateWqlAsync("delete from SMS_R_System", CancellationToken.None);
        Assert.False(service.IsValid);
        Assert.Empty(site.Log);

        if (!File.Exists(FixedModule)) return;
        var moduleSite = Site();
        var module = moduleSite.RunModule(FixedModule, "Test-WqlQuery", new { QueryExpression = "delete from SMS_R_System" });
        Assert.Contains("Invoke-CMWmiQuery", module.Log);
    }

    [Fact]
    public async Task PreviewMatchesTheCountAndFirstRowsOfTheFixedModuleAndTheOlderModuleHidesErrors()
    {
        if (!WindowsPowerShellPresent) return;
        var service = await new CollectionService(Site()).PreviewWqlAsync("select Name, ResourceID from SMS_R_System", 1, CancellationToken.None);
        Assert.Equal(2, service.MatchCount);
        Assert.Single(service.FirstRows);
        var invalid = await new CollectionService(Site()).PreviewWqlAsync("select * from SMS_R_Systemxx", 1, CancellationToken.None);
        Assert.False(invalid.IsValid);

        if (File.Exists(FixedModule))
        {
            var fixedResult = Site().RunModule(FixedModule, "Invoke-WqlPreview", new { QueryExpression = "select Name, ResourceID from SMS_R_System", MaxResults = 1 }).Output.Single();
            Assert.Equal(service.MatchCount, fixedResult.GetProperty("TotalCount").GetInt32());
            var first = fixedResult.GetProperty("Results");
            var firstName = (first.ValueKind == JsonValueKind.Array ? first[0] : first).GetProperty("Name").GetString();
            Assert.Equal(firstName, service.FirstRows[0][service.Columns.ToList().IndexOf("Name")]);
            var fixedInvalid = Site().RunModule(FixedModule, "Invoke-WqlPreview", new { QueryExpression = "select * from SMS_R_Systemxx" }).Output.Single();
            Assert.False(fixedInvalid.GetProperty("IsValid").GetBoolean());
        }
        if (File.Exists(OlderModule))
        {
            var olderInvalid = Site().RunModule(OlderModule, "Invoke-WqlPreview", new { QueryExpression = "select * from SMS_R_Systemxx" }).Output.Single();
            Assert.Equal(0, olderInvalid.GetProperty("TotalCount").GetInt32());
            Assert.False(olderInvalid.TryGetProperty("IsValid", out _));
        }
    }

    // ---- Rule update against the modules ----

    /// <summary>
    /// Update-QueryRule of each module on the stub site, recorded: the older module removes the
    /// old rule first and does not restore it; the fixed module removes first and adds the old
    /// rule back when the new one fails. Both drop to one query rule during the update.
    /// </summary>
    internal static readonly (string Module, string[] Fail, int Minimum, bool OldKept)[] RecordedModuleUpdates =
    [
        ("older", [], 1, false),
        ("older", ["add:WS"], 1, false),
        ("fixed", [], 1, false),
        ("fixed", ["add:WS"], 1, true),
        ("fixed", ["add:WS", "add:WS"], 1, false)
    ];

    [Fact]
    public async Task TheRuleUpdateKeepsEveryRuleWhereTheModulesDropOne()
    {
        if (!WindowsPowerShellPresent) return;
        var compared = 0;
        foreach (var (name, path) in new[] { ("older", OlderModule), ("fixed", FixedModule) })
        {
            if (!File.Exists(path)) continue;
            foreach (var fail in new[] { Array.Empty<string>(), ["add:WS"], ["add:WS", "add:WS"] })
            {
                var recorded = RecordedModuleUpdates.FirstOrDefault(r => r.Module == name && r.Fail.SequenceEqual(fail));
                if (recorded.Module is null) continue;
                var moduleSite = Site(fail);
                moduleSite.RunModule(path, "Update-QueryRule", new { CollectionId = "MCM00010", OldRuleName = "WS", NewRuleName = "WS", NewQueryExpression = New });
                Assert.Equal(recorded.Minimum, MinimumCount(moduleSite.Counts));
                var expectedOld = fail.Length == 0 ? false : recorded.OldKept;
                Assert.Equal(expectedOld, moduleSite.QueryRules("MCM00010").Contains(("WS", Old)));
                if (fail.Length == 0) Assert.Contains(("WS", New), moduleSite.QueryRules("MCM00010"));

                var (outcome, serviceSite) = await ServiceUpdate("WS", New, fail.Select(f => f == "add:WS" ? "add:WS (update t1)" : f).ToArray());
                Assert.Equal(fail.Length == 0, outcome.Succeeded);
                Assert.True(MinimumCount(serviceSite.Counts) >= 2);
                Assert.Equal(fail.Length == 0 ? ("WS", New) : ("WS", Old), serviceSite.QueryRules("MCM00010").First(r => r.Name == "WS"));
                compared++;
            }
        }
        if (compared == 0)
        {
            foreach (var (_, fail, minimum, _) in RecordedModuleUpdates)
                Assert.True(minimum < 2);
            var (outcome, site) = await ServiceUpdate("WS", New);
            Assert.True(outcome.Succeeded);
            Assert.True(MinimumCount(site.Counts) >= 2);
            compared = RecordedModuleUpdates.Length;
        }
        Assert.True(compared >= 4, $"Only {compared} updates were compared.");
    }
}
