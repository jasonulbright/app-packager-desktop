using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>Plan details keep each line of the service and each registry path on a line of its own.</summary>
public sealed class CompliancePlanTextTests
{
    private static string[] Lines(string? text) => (text ?? "").Split(Environment.NewLine);

    [Fact]
    public void EveryImpactLineOfACatalogDeployStaysOnItsOwnLine()
    {
        var harness = new ComplianceHarness();
        string[] impact =
        [
            "Parameters: SiteCode LAB, CollectionName Servers.",
            "Mode: REMEDIATE. Clients change these registry values to the catalog values at each evaluation:",
            @"HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters (SMB1)",
            @"HKLM\SYSTEM\CurrentControlSet\Services\mrxsmb10 (Start)"
        ];
        var change = ComplianceRequests.Deploy(harness.Autorun, new Dictionary<string, string>(), ["LAB00010"], true) with { Impact = impact };
        var (plan, _) = ComplianceCatalogViewModel.BuildDeployPlan(harness.Autorun, change, harness.Collections.Collections);
        Assert.Equal(impact, Lines(plan!.Plan.Items[0].Detail));
    }

    [Fact]
    public void EachRegistryPathOfARemediatingDeployHasItsOwnLine()
    {
        var harness = new ComplianceHarness();
        var entry = harness.Autorun with
        {
            RegistrySurface = [new RegistrySurfaceItem("HKLM", @"SOFTWARE\Policies\A", ["One"]), new RegistrySurfaceItem("HKLM", @"SOFTWARE\Policies\B", ["Two", "Three"])]
        };
        var change = ComplianceRequests.Deploy(entry, new Dictionary<string, string>(), ["LAB00010"], true);
        var (plan, _) = ComplianceCatalogViewModel.BuildDeployPlan(entry, change, harness.Collections.Collections);
        var lines = Lines(plan!.Plan.Items[0].Detail);
        Assert.Contains(@"HKLM\SOFTWARE\Policies\A (One)", lines);
        Assert.Contains(@"HKLM\SOFTWARE\Policies\B (Two, Three)", lines);
        Assert.Contains("Deploys to Servers (LAB00010): 3 members now.", lines);
    }

    [Fact]
    public void TheLiveCompliancePlanAndUndoLinesStayOnTheirOwnLines()
    {
        var harness = new ComplianceHarness();
        var deployment = harness.SmbOnServers;
        var change = ComplianceRequests.SetRemediation(deployment, true) with { Impact = ["First line.", "Second line."] };
        var detail = ComplianceLiveViewModel.PlanDetail("Turns remediation on.", change, []);
        Assert.Equal(["Turns remediation on.", "First line.", "Second line."], Lines(detail));
    }
}
