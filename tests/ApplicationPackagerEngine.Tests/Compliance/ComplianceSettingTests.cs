using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.Compliance.StubComplianceSite;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>Setting changes through the real embedded scripts on the stub site.</summary>
public sealed class ComplianceSettingTests
{
    private static StubComplianceSite Site(params object[] settings) => new(NewState(
        items: [Ci("16780001", "Lab CI", 4, settings.Length > 0 ? settings : [RegistrySetting("RegSetting_a", "EnableMulticast"), ScriptSetting("ScriptSetting_b", "Probe")])],
        baselines: [CiBaseline("16780002", "Lab Baseline", "", "16780001")],
        assignments: [Deployment(16777270, "16780002", "MCM00080")]));

    private static ComplianceService Service(StubComplianceSite site) => ComplianceDeploymentTests.Service(site);

    private static async Task<ComplianceOutcome> Apply(ComplianceService service, ComplianceChange change) =>
        (await service.ApplyAsync([change], new ComplianceDeploymentTests.Recorder<ComplianceOutcome>(), CancellationToken.None)).Single();

    private static ComplianceChange Change(ComplianceChangeKind kind, string ciId, CiSetting? before, CiSetting? after) =>
        new(kind, null, new Dictionary<string, string>(), [], null, null, null, ciId, before, after, [], []);

    private static CiSetting NewRegistry(string name = "LmLevel", string type = "Integer", string value = "5") =>
        new("", name, "Registry", type, "", value, "lab", "LocalMachine", @"SYSTEM\CurrentControlSet\Control\Lsa", "LmCompatibilityLevel", true,
            RuleName: name + " rule", Operator: type == "StringArray" ? "AllOf" : "IsEquals", Severity: "Critical", RemediateDword: type == "Integer");

    private static JsonArray Settings(StubComplianceSite site, string ciId) => (JsonArray)site.Item(ciId)!["Settings"]!;

    [Fact]
    public async Task SettingsReadWithTheirRuleAndThePlanStatesTheRevisionAndTheDeployedBaseline()
    {
        var site = Site();
        var service = Service(site);

        var settings = await service.ListSettingsAsync("16780001", CancellationToken.None);
        var registry = settings.Single(s => s.Kind == "Registry");
        Assert.Equal(("Integer", "1", "IsEquals", "LocalMachine", true, true), (registry.DataType, registry.ExpectedValue, registry.Operator, registry.Hive, registry.IsEditable, registry.RemediateDword));
        Assert.Equal(@"LocalMachine\SOFTWARE\Contoso\EnableMulticast", registry.Detail);
        var script = settings.Single(s => s.Kind == "Script");
        Assert.Equal(("Boolean", "True", "PowerShell"), (script.DataType, script.ExpectedValue, script.DiscoveryLanguage));

        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.AddSetting, "16780001", null, NewRegistry()), CancellationToken.None);

        Assert.Equal("4", plan.Values[ComplianceService.RevisionKey]);
        Assert.Contains(plan.Impact, l => l.Contains("revision 4 becomes 5", StringComparison.Ordinal));
        Assert.Contains(plan.Impact, l => l.Contains("deployed baseline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddReadsTheSettingBackAndUndoRemovesItFromTheCiUnderItsNewId()
    {
        var site = Site();
        var service = Service(site);
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.AddSetting, "16780001", null, NewRegistry()), CancellationToken.None);

        var outcome = await Apply(service, plan);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(3, Settings(site, "16780001").Count);
        Assert.Equal(ComplianceChangeKind.RemoveSetting, outcome.UndoChange!.Kind);
        Assert.NotEqual("16780001", outcome.UndoChange.CiId);
        var undone = await Apply(service, outcome.UndoChange);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal(2, Settings(site, "16780001").Count);
    }

    [Fact]
    public async Task AnEditOfTheKeyKeepsTheSettingIdAndUndoPutsTheOldKeyBack()
    {
        var site = Site();
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", CancellationToken.None)).Single(s => s.Kind == "Registry");
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, "16780001", before, before with { KeyName = @"SOFTWARE\Other", Name = "Renamed" }), CancellationToken.None);
        Assert.Equal("InPlace", plan.Values[ComplianceService.EditModeKey]);

        var outcome = await Apply(service, plan);

        Assert.True(outcome.Succeeded, outcome.Message);
        var stored = Settings(site, "16780001").Single(s => (string)s!["SettingId"]! == "RegSetting_a")!;
        Assert.Equal(("Renamed", @"SOFTWARE\Other"), ((string)stored["Name"]!, (string)stored["KeyName"]!));
        var undone = await Apply(service, outcome.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        stored = Settings(site, "16780001").Single(s => (string)s!["SettingId"]! == "RegSetting_a")!;
        Assert.Equal(("EnableMulticast", @"SOFTWARE\Contoso"), ((string)stored["Name"]!, (string)stored["KeyName"]!));
    }

    [Fact]
    public async Task ADataTypeChangeAddsTheNewSettingFirstAndUndoRestoresTheOldType()
    {
        var site = Site();
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", CancellationToken.None)).Single(s => s.Kind == "Registry");
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, "16780001", before,
            before with { DataType = "String", ExpectedValue = "zero", RemediateDword = false }), CancellationToken.None);
        Assert.Equal("Replace", plan.Values[ComplianceService.EditModeKey]);
        Assert.Contains(plan.Impact, l => l.Contains("new ID", StringComparison.Ordinal));
        Assert.Contains(plan.Impact, l => l == "Data type: Integer -> String");

        var outcome = await Apply(service, plan);

        Assert.True(outcome.Succeeded, outcome.Message);
        var stored = Settings(site, "16780001").Single(s => (string)s!["Name"]! == "EnableMulticast")!;
        Assert.Equal("String", (string)stored["DataType"]!);
        Assert.NotEqual("RegSetting_a", (string)stored["SettingId"]!);
        Assert.True(site.Log.FindIndex(l => l.StartsWith("Add-Setting:Registry:EnableMulticast (edit t1)", StringComparison.Ordinal))
                    < site.Log.IndexOf("Remove-CMComplianceSetting:EnableMulticast"));

        var undone = await Apply(service, outcome.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        stored = Settings(site, "16780001").Single(s => (string)s!["Name"]! == "EnableMulticast")!;
        Assert.Equal(("Integer", "1"), ((string)stored["DataType"]!, (string)((JsonArray)stored["Values"]!)[0]!));
        Assert.Equal(2, Settings(site, "16780001").Count);
    }

    [Fact]
    public async Task AReplaceWhoseRemoveFailsKeepsTheOldSettingAndUndoRemovesTheNewOne()
    {
        var site = Site();
        site.Fail("removesetting:EnableMulticast");
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", CancellationToken.None)).Single(s => s.Kind == "Registry");
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, "16780001", before, before with { ExpectedValue = "2" }), CancellationToken.None);

        var outcome = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.Contains("Both exist", outcome.Message);
        Assert.Equal(3, Settings(site, "16780001").Count);
        var undone = await Apply(service, outcome.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal(["EnableMulticast", "Probe"], Settings(site, "16780001").Select(s => (string)s!["Name"]!).Order());
    }

    [Fact]
    public async Task AReplaceWhoseRenameFailsKeepsTheNewValuesAndUndoPutsTheOldSettingBack()
    {
        var site = Site();
        site.Fail("rename:EnableMulticast (edit t1)");
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", CancellationToken.None)).Single(s => s.Kind == "Registry");
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, "16780001", before, before with { ExpectedValue = "2" }), CancellationToken.None);

        var outcome = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.Contains("temporary name", outcome.Message);
        var undone = await Apply(service, outcome.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        var stored = Settings(site, "16780001").Single(s => (string)s!["Name"]! == "EnableMulticast")!;
        Assert.Equal("1", (string)((JsonArray)stored["Values"]!)[0]!);
    }

    [Fact]
    public async Task RemoveCapturesTheSettingAndUndoAddsItAgainWithEqualValues()
    {
        var site = Site();
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", CancellationToken.None)).Single(s => s.Kind == "Script");
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.RemoveSetting, "16780001", before, null), CancellationToken.None);

        var outcome = await Apply(service, plan);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Single(Settings(site, "16780001"));

        var undone = await Apply(service, outcome.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        var again = (await service.ListSettingsAsync("16780001", CancellationToken.None)).Single(s => s.Kind == "Script");
        Assert.True(SettingRules.Same(again, before with { SettingId = again.SettingId }));
        Assert.NotEqual(before.SettingId, again.SettingId);
    }

    [Fact]
    public async Task ASettingConfigMgrDoesNotStoreFailsWithoutChangingAnything()
    {
        var site = Site();
        site.SetOption("DropAdds", true);
        var service = Service(site);
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.AddSetting, "16780001", null, NewRegistry()), CancellationToken.None);

        var outcome = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not store it", outcome.Message);
        Assert.Null(outcome.UndoChange);
    }

    [Theory]
    [InlineData("Registry", "Boolean", "True", false, "script settings only")]
    [InlineData("Script", "StringArray", "a", false, "registry settings only")]
    [InlineData("Registry", "DateTime", "2026-01-02T03:04:05", true, "does not store a DateTime setting with remediation")]
    [InlineData("Registry", "Integer", "five", false, "whole number")]
    [InlineData("Registry", "Version", "1.x", false, "version")]
    [InlineData("Registry", "StringArray", "a", false, "All of")]
    public void DataTypesConfigMgrCannotStoreAreRefusedWithAReason(string kind, string type, string value, bool remediate, string reason)
    {
        var setting = kind == "Registry"
            ? NewRegistry(type: type, value: value) with { Remediate = remediate, Operator = "IsEquals" }
            : new CiSetting("", "S", "Script", type, "", value, DiscoveryLanguage: "PowerShell", DiscoveryScript: "1", RuleName: "R", Severity: "Warning");

        Assert.Contains(reason, SettingRules.Check(setting));
    }

    [Fact]
    public async Task ADuplicateSettingNameIsRefusedBecauseTheCmdletWouldRemoveBoth()
    {
        var site = Site(RegistrySetting("RegSetting_a", "Twin"), RegistrySetting("RegSetting_b", "Twin"));
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", CancellationToken.None)).First();

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() =>
            service.PrepareAsync(Change(ComplianceChangeKind.RemoveSetting, "16780001", before, null), CancellationToken.None));

        Assert.Contains("More than one setting", error.Message);
    }

    [Fact]
    public async Task ARevisionMadeElsewhereSinceThePlanSkipsTheWrite()
    {
        var site = Site();
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", CancellationToken.None)).Single(s => s.Kind == "Registry");
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.RemoveSetting, "16780001", before, null), CancellationToken.None);
        site.Item("16780001")!["Version"] = 9;

        var outcome = await Apply(service, plan);

        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.Equal(2, Settings(site, "16780001").Count);
    }

    [Fact]
    public async Task ASettingChangedSinceItWasReadIsRefusedInThePlan()
    {
        var site = Site();
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", CancellationToken.None)).Single(s => s.Kind == "Registry");
        ((JsonArray)Settings(site, "16780001")[0]!["Values"]!)[0] = "9";

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() =>
            service.PrepareAsync(Change(ComplianceChangeKind.RemoveSetting, "16780001", before, null), CancellationToken.None));

        Assert.Contains("changed since it was read", error.Message);
    }

    [Fact]
    public async Task ProviderErrorTextNeverReachesTheMessage()
    {
        var site = Site();
        site.Fail("add:LmLevel");
        var service = Service(site);
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.AddSetting, "16780001", null, NewRegistry()), CancellationToken.None);

        var outcome = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("SECRET-DETAIL", outcome.Message);
        Assert.DoesNotContain("SMS Provider", outcome.Message);
    }
}
