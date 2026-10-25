using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.Compliance.StubComplianceSite;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>
/// A setting change that remediates on a configuration item in a remediating baseline deployment
/// needs the second confirmation; the plan names those deployments from the live state.
/// </summary>
public sealed class ComplianceRemediatingSettingTests
{
    private const string CiId = "16780001";

    private static StubComplianceSite Site(bool remediating = true) => new(NewState(
        items: [Ci(CiId, "Lab CI", 4, RegistrySetting("RegSetting_a", "EnableMulticast"), RegistrySetting("RegSetting_b", "SmbSigning", remediate: true))],
        baselines: [CiBaseline("16780002", "Lab Baseline", "", CiId), CiBaseline("16780003", "Audit Baseline", "", CiId)],
        assignments:
        [
            Deployment(16777270, "16780002", "MCM00081", enforcement: remediating),
            Deployment(16777271, "16780003", "MCM00080")
        ]));

    private static ComplianceService Service(StubComplianceSite site) => ComplianceDeploymentTests.Service(site);

    private static async Task<ComplianceOutcome> Apply(ComplianceService service, ComplianceChange change) =>
        (await service.ApplyAsync([change], new ComplianceDeploymentTests.Recorder<ComplianceOutcome>(), CancellationToken.None)).Single();

    private static ComplianceChange Change(ComplianceChangeKind kind, string ciId, CiSetting? before, CiSetting? after) =>
        new(kind, null, new Dictionary<string, string>(), [], null, null, null, ciId, before, after, [], []);

    private static async Task<CiSetting> Setting(ComplianceService service, string name, string ciId = CiId) =>
        (await service.ListSettingsAsync(ciId, CancellationToken.None)).Single(s => s.Name == name);

    private static JsonObject Stored(StubComplianceSite site, string name) =>
        ((JsonArray)site.Item(CiId)!["Settings"]!).Select(s => s!.AsObject()).Single(s => (string)s["Name"]! == name);

    private static CiSetting NewRegistry(bool remediate) =>
        new("", "LmLevel", "Registry", "Integer", "", "5", "lab", "LocalMachine", @"SYSTEM\CurrentControlSet\Control\Lsa", "LmCompatibilityLevel", true,
            RuleName: "LmLevel rule", Operator: "IsEquals", Severity: "Critical", RemediateDword: true, Remediate: remediate);

    [Fact]
    public async Task TurningRemediationOnNamesTheRemediatingDeploymentAndIsSkippedWithoutTheSecondConfirmation()
    {
        var site = Site();
        var service = Service(site);
        var before = await Setting(service, "EnableMulticast");

        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, CiId, before, before with { Remediate = true }), CancellationToken.None);

        Assert.True(plan.InRemediatingDeployment);
        Assert.Contains(plan.Impact, l => l.Contains("Lab Baseline (16780002)", StringComparison.Ordinal) && l.Contains("CCM Test 2 (MCM00081)", StringComparison.Ordinal)
                                          && l.Contains("3 members now", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Impact, l => l.Contains("Audit Baseline", StringComparison.Ordinal));
        Assert.Contains(ComplianceService.SettingRemediationLine, plan.Impact);

        var skipped = await Apply(service, plan);

        Assert.False(skipped.Succeeded);
        Assert.StartsWith(ComplianceService.SkippedPrefix, skipped.Message, StringComparison.Ordinal);
        Assert.Contains("second confirmation", skipped.Message, StringComparison.Ordinal);
        Assert.False((bool)Stored(site, "EnableMulticast")["Remediate"]!);

        var done = await Apply(service, plan with { RemediationConfirmed = true });

        Assert.True(done.Succeeded, done.Message);
        Assert.True((bool)Stored(site, "EnableMulticast")["Remediate"]!);
    }

    [Fact]
    public async Task AnEditOfASettingThatRemediatesIsSkippedWithoutTheSecondConfirmation()
    {
        var site = Site();
        var service = Service(site);
        var before = await Setting(service, "SmbSigning");

        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, CiId, before, before with { Description = "changed" }), CancellationToken.None);
        var skipped = await Apply(service, plan);

        Assert.True(plan.InRemediatingDeployment);
        Assert.StartsWith(ComplianceService.SkippedPrefix, skipped.Message, StringComparison.Ordinal);
        Assert.Equal("", (string?)Stored(site, "SmbSigning")["Description"] ?? "");
    }

    [Fact]
    public async Task AnAddThatRemediatesIsSkippedWithoutTheSecondConfirmation()
    {
        var site = Site();
        var service = Service(site);

        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.AddSetting, CiId, null, NewRegistry(remediate: true)), CancellationToken.None);
        var skipped = await Apply(service, plan);

        Assert.True(plan.InRemediatingDeployment);
        Assert.StartsWith(ComplianceService.SkippedPrefix, skipped.Message, StringComparison.Ordinal);
        Assert.Equal(2, ((JsonArray)site.Item(CiId)!["Settings"]!).Count);
    }

    [Fact]
    public async Task WithoutARemediatingDeploymentTheChangeNeedsNoSecondConfirmation()
    {
        var site = Site(remediating: false);
        var service = Service(site);
        var before = await Setting(service, "EnableMulticast");

        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, CiId, before, before with { Remediate = true }), CancellationToken.None);

        Assert.False(plan.InRemediatingDeployment);
        Assert.Contains(ComplianceService.NoRemediatingDeploymentLine, plan.Impact);
        Assert.DoesNotContain(ComplianceService.SettingRemediationLine, plan.Impact);
        var done = await Apply(service, plan);
        Assert.True(done.Succeeded, done.Message);
    }

    [Fact]
    public async Task ARemoveAndAChangeThatDoesNotRemediateAreNotHeldBack()
    {
        var site = Site();
        var service = Service(site);
        var multicast = await Setting(service, "EnableMulticast");

        var edit = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, CiId, multicast, multicast with { Description = "changed" }), CancellationToken.None);
        Assert.True(edit.InRemediatingDeployment);
        Assert.DoesNotContain(ComplianceService.SettingRemediationLine, edit.Impact);
        var edited = await Apply(service, edit);
        Assert.True(edited.Succeeded, edited.Message);

        var latest = (string)site.Item(CiId)!["Id"]!;
        var signing = await Setting(service, "SmbSigning", latest);
        var remove = await service.PrepareAsync(Change(ComplianceChangeKind.RemoveSetting, latest, signing, null), CancellationToken.None);
        Assert.True(remove.InRemediatingDeployment);
        var removed = await Apply(service, remove);
        Assert.True(removed.Succeeded, removed.Message);
    }

    [Fact]
    public async Task TheBaselineThatHoldsAnOlderRevisionOfTheItemCounts()
    {
        var site = Site();
        var service = Service(site);
        var multicast = await Setting(service, "EnableMulticast");
        var edited = await Apply(service, await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, CiId, multicast, multicast with { Description = "changed" }), CancellationToken.None));
        Assert.True(edited.Succeeded, edited.Message);
        var latest = (string)site.Item(CiId)!["Id"]!;
        Assert.NotEqual(CiId, latest);

        var before = await Setting(service, "EnableMulticast", latest);
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, latest, before, before with { Remediate = true }), CancellationToken.None);

        Assert.True(plan.InRemediatingDeployment);
        Assert.Contains(plan.Impact, l => l.Contains("CCM Test 2 (MCM00081)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeploymentThatStartsToRemediateAfterThePlanStopsTheUnconfirmedChange()
    {
        var site = Site(remediating: false);
        var service = Service(site);
        var before = await Setting(service, "EnableMulticast");
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.EditSetting, CiId, before, before with { Remediate = true }), CancellationToken.None);
        Assert.False(plan.InRemediatingDeployment);

        site.Assignment(16777270)!["Enforcement"] = true;
        var skipped = await Apply(service, plan);

        Assert.StartsWith(ComplianceService.SkippedPrefix, skipped.Message, StringComparison.Ordinal);
        Assert.Contains("second confirmation", skipped.Message, StringComparison.Ordinal);
        Assert.False((bool)Stored(site, "EnableMulticast")["Remediate"]!);
    }
}
