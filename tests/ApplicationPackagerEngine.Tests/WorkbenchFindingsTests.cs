using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class WorkbenchFindingsTests
{
    private static readonly WorkbenchInheritedSettings Inherited = WorkbenchInheritedSettings.Build("App", "Vendor", "", 15, 30);

    private static WorkbenchProfile Profile() => WorkbenchProfile.CreateNew("catalog:package-findings", name: "Managed");

    private static IEnumerable<string> Codes(WorkbenchProfile profile) =>
        WorkbenchFindings.Local(profile, Inherited).Select(f => f.Code);

    [Fact]
    public void AnUntouchedProfile_CarriesOnlyTheIntuneTimingNote()
    {
        var findings = WorkbenchFindings.Local(Profile(), Inherited);

        var only = Assert.Single(findings);
        Assert.Equal(new WorkbenchFinding("Info", "TIMING-INTUNE",
            "Intune install timeout is not sent by this publisher; the value is recorded in the definition only."), only);
    }

    [Theory]
    [InlineData(14, true)]
    [InlineData(15, false)]
    [InlineData(720, false)]
    [InlineData(721, true)]
    [InlineData(1440, true)]
    public void AMaximumOutsideWhatTheSiteAccepts_IsAReviewFindingThatBlocksNothing(int maximum, bool expected)
    {
        var profile = Profile();
        profile.Set("Timing.EstimatedMinutes", 10);
        profile.Set("Timing.MaximumMinutes", maximum);

        var findings = WorkbenchFindings.Local(profile, Inherited);

        var finding = findings.SingleOrDefault(f => f.Code == "TIMING-CONFIGMGR-RANGE");
        Assert.Equal(expected, finding is not null);
        if (finding is null) return;
        Assert.Equal("Review", finding.Severity);
        Assert.Equal("ConfigMgr accepts a maximum runtime from 15 to 720 minutes. A ConfigMgr publish with this value stops before it writes anything.", finding.Message);
        Assert.Empty(WorkbenchFindings.BlockingFor(findings, RunTargets.Mecm));
        Assert.Empty(WorkbenchFindings.BlockingFor(findings, RunTargets.IntuneOnly));
    }

    [Fact]
    public void AnEstimateAboveTheInheritedMaximum_IsBlocking()
    {
        var profile = Profile();
        profile.Set("Timing.EstimatedMinutes", 45);

        var finding = WorkbenchFindings.Local(profile, Inherited).First();

        Assert.Equal("Blocking", finding.Severity);
        Assert.Equal("TIMING-ORDER", finding.Code);
        Assert.Equal("Estimated install duration is greater than the maximum runtime.", finding.Message);
    }

    [Fact]
    public void ANonPositiveOrUnreadableDuration_IsARangeFinding()
    {
        var profile = Profile();
        profile.Set("Timing.MaximumMinutes", "abc");

        Assert.Contains("TIMING-RANGE", Codes(profile));
    }

    [Fact]
    public void CustomScripts_GroupedDetection_Requirements_Splits_AndEscapingDestinations_AreReported()
    {
        var profile = Profile();
        profile.Set("Uninstall.Mode", "Custom");
        profile.Set("Detection.Mode", "Custom");
        profile.Set("Detection.Rule", new WorkbenchDetectionRule { Logic = "Or" }.ToJson());
        profile.Set("Requirements.Operations", new JsonArray(WorkbenchRequirementRow.ForTemplate("cpu-arch").ToJson()));
        profile.Set("Variants.Split", WorkbenchFieldMapping.DisplayToVariantSplit("Architecture"));
        profile.Set("SourceFiles", WorkbenchSourceFiles.ToJson([new WorkbenchSourceFile("", "c:\\x", "..\\outside.txt", "", 1, false)]));

        var codes = Codes(profile).ToList();

        Assert.Contains("INSTALL-CUSTOM", codes);
        Assert.Contains("DETECT-INTUNE-COMPOUND", codes);
        Assert.Contains("REQ-INTUNE", codes);
        Assert.Contains("VARIANT-INTUNE", codes);
        Assert.Contains("SOURCE-PATH", codes);

        profile.Set("Detection.IntuneScriptConversion", true);
        Assert.DoesNotContain("DETECT-INTUNE-COMPOUND", Codes(profile));
    }

    [Fact]
    public void AnIntuneBlocker_NeverChangesTheContentOrConfigMgrChip()
    {
        var chips = WorkbenchFindings.Chips([new WorkbenchFinding("Blocking", "VARIANT-INTUNE", "x")], validated: true);

        Assert.Equal(new WorkbenchChipState("Ready", "Ready", "Unsupported"), chips);
        Assert.Equal(WorkbenchChipState.Unvalidated, WorkbenchFindings.Chips([], validated: false));
        Assert.Equal("Needs review", WorkbenchFindings.Chips([new WorkbenchFinding("Review", "REQ-INTUNE", "x")], true).Intune);
        Assert.Equal(new WorkbenchChipState("Needs review", "Needs review", "Ready"),
            WorkbenchFindings.Chips([new WorkbenchFinding("Blocking", "SOURCE-PATH", "x")], true));
    }

    [Fact]
    public void IntuneBlockers_StopOnlyAnIntunePublish()
    {
        WorkbenchFinding[] findings =
        [
            new("Blocking", "VARIANT-INTUNE", "split"),
            new("Review", "INSTALL-CUSTOM", "custom")
        ];

        Assert.Empty(WorkbenchFindings.BlockingFor(findings, RunTargets.Mecm));
        Assert.Single(WorkbenchFindings.BlockingFor(findings, RunTargets.IntuneOnly));
        Assert.Single(WorkbenchFindings.BlockingFor([new("Blocking", "TIMING-ORDER", "t")], RunTargets.Mecm));
    }
}
