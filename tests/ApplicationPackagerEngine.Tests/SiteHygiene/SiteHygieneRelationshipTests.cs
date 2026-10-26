using ApplicationPackagerEngine.Services;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>SUP, DEP, REL and APP-04 from the relation classes, the inventory and the tree.</summary>
public sealed class SiteHygieneRelationshipTests
{
    private static readonly string[] Rel = ["FromApplicationCIID", "FromDeploymentTypeCIID", "ToApplicationCIID", "ToDeploymentTypeCIID", "RelationType"];
    private static readonly string[] AppCols = ["CI_ID", "ModelID", "ModelName", "LocalizedDisplayName", "IsDeployed", "IsExpired", "IsSuperseded", "IsSuperseding", "IsEnabled", "HasContent", "Manufacturer", "SoftwareVersion", "DateCreated", "NumberOfDeploymentTypes", "CIVersion"];
    private static readonly string[] DtCols = ["CI_ID", "ModelName", "AppModelName", "LocalizedDisplayName", "ContentId", "IsLatest"];

    private static string[] App(int ci, string name, bool enabled = true, bool expired = false, bool content = true, string maker = "Contoso") =>
        [ci.ToString(), (ci + 1000).ToString(), "ScopeId_X/Application_" + name, name, "False", expired ? "True" : "False", "False", "False", enabled ? "True" : "False", content ? "True" : "False", maker, "1.0", "2026-01-01T00:00:00", "1", "1"];

    [Fact]
    public void TheDependencyChecksFindWhatTheSourceFindsOnCm01()
    {
        var (findings, checks) = SiteHygieneParity.Native("AppRelationships");
        foreach (var id in new[] { "SUP-01", "SUP-02", "SUP-03", "SUP-04", "DEP-01", "DEP-02", "DEP-03", "DEP-04", "DEP-05", "REL-01" })
        {
            Assert.Equal(HygieneCheckStates.Ran, checks.Single(c => c.CheckId == id).State);
            SiteHygieneParity.SameAsSource(findings, id, s => s.ObjectName, f => f.ObjectName);
        }
        Assert.Equal(2, findings.Count(f => f.CheckId == "DEP-05"));
        Assert.Equal(HygieneCheckStates.NotSelected, checks.Single(c => c.CheckId == "APP-04").State);
        Assert.Contains("probe is off", checks.Single(c => c.CheckId == "APP-04").Reason);
    }

    [Fact]
    public async Task TheContentPathCheckWithTheProbeFindsWhatTheSourceFindsFromCm01()
    {
        using var h = new SiteHygieneHarness();
        var probed = new List<string>();
        var service = new SiteHygieneService(h.Site, h.Audit, utcNow: () => h.Clock, probe: (path, timeout, ct) =>
        {
            probed.Add(path);
            return Task.FromResult(path.Contains("CmcoCanary$", StringComparison.OrdinalIgnoreCase) ? HygieneProbe.Missing : HygieneProbe.Ok);
        });
        var result = await service.ScanAsync(new HygieneScanOptions(new HashSet<string> { "AppRelationships" }, TimeSpan.Zero, true), new Progress<string>(), CancellationToken.None);
        Assert.Equal(HygieneCheckStates.Ran, result.Checks.Single(c => c.CheckId == "APP-04").State);
        SiteHygieneParity.SameAsSource(result.Findings, "APP-04", s => s.ObjectName, f => f.ObjectName);
        Assert.Equal(probed.Count, probed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(TimeSpan.FromSeconds(3), SiteHygieneService.ProbeTimeout);
    }

    [Fact]
    public async Task ContentProbesUseTheProviderAndKnownDistributionPointHostsOnly()
    {
        using var h = new SiteHygieneHarness();
        var classes = h.Site.State["Classes"]!.AsObject();
        classes["SMS_DistributionPointInfo"]!.AsArray().Add(new JsonObject
        {
            ["NALPath"] = "NAL-2",
            ["Name"] = "DP01.contoso.com"
        });
        AddContentSource(classes, "Content_TrustedDp", @"\\DP01.contoso.com\Content$\Apps\");
        AddContentSource(classes, "Content_UnknownHost", @"\\external.example\Content$\Apps\");
        AddContentSource(classes, "Content_ForwardSlashHost", "//external-two.example/Content$/Apps/");
        var probed = new List<string>();
        var service = new SiteHygieneService(h.Site, h.Audit, utcNow: () => h.Clock, connection: () => h.Connection, probe: (path, timeout, ct) =>
        {
            probed.Add(path);
            return Task.FromResult(HygieneProbe.Ok);
        });

        var result = await service.ScanAsync(new HygieneScanOptions(new HashSet<string> { "AppRelationships" }, TimeSpan.Zero, true), new Progress<string>(), CancellationToken.None);

        Assert.Contains(@"\\DP01.contoso.com\Content$\Apps\", probed);
        Assert.DoesNotContain(@"\\external.example\Content$\Apps\", probed);
        Assert.DoesNotContain("//external-two.example/Content$/Apps/", probed);
        var check = Assert.Single(result.Checks, c => c.CheckId == "APP-04");
        Assert.Equal(HygieneCheckStates.NotRun, check.State);
        Assert.Contains("could not be probed safely", check.Reason);
    }

    [Theory]
    [InlineData(@"\\CM01.contoso.com\Content$\Apps\", true)]
    [InlineData(@"\\CM01\Content$\Apps\", true)]
    [InlineData(@"\\DP01.contoso.com\Content$\Apps\", true)]
    [InlineData(@"\\CM01.attacker.example\Content$\Apps\", false)]
    [InlineData(@"\\?\UNC\CM01.contoso.com\Content$\Apps\", false)]
    [InlineData(@"\\.\PIPE\spoolss", false)]
    [InlineData("//attacker.example/Content$/Apps/", false)]
    public void ProbePathsMustBeLocalOrUseAnAllowlistedUncHost(string path, bool safe)
    {
        var allowedHosts = new HashSet<string>(["CM01.contoso.com", "DP01.contoso.com"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(safe, SiteHygieneService.IsSafeProbePath(path, allowedHosts));
    }

    private static void AddContentSource(JsonObject classes, string contentId, string path)
    {
        classes["SMS_Content"]!.AsArray().Add(new JsonObject { ["ContentUniqueID"] = contentId, ["ContentSource"] = path });
        classes["SMS_DeploymentType"]!.AsArray().Add(new JsonObject
        {
            ["CI_ID"] = "999999",
            ["ModelName"] = "ScopeId_Test/DeploymentType_" + contentId,
            ["AppModelName"] = "ScopeId_Test/Application_Test",
            ["LocalizedDisplayName"] = contentId,
            ["ContentId"] = contentId,
            ["IsLatest"] = true
        });
    }

    [Fact]
    public void MoreContentPathsThanTheCapMakeTheCheckNotRun()
    {
        var read = SiteHygieneParity.EmptySite();
        var probes = Enumerable.Range(0, 5).ToDictionary(i => $"\\\\s\\p{i}", i => i < 4 ? HygieneProbe.Missing : HygieneProbe.NotProbed);
        var context = new HygieneContext(read, new HygieneThresholds(), SiteHygieneFixtures.CaptureLocal, "MCM") { ProbeResults = probes };
        var (findings, checks) = HygieneEngine.Run(context, new HashSet<string> { "AppRelationships" }, true);
        Assert.Equal(HygieneCheckStates.NotRun, checks.Single(c => c.CheckId == "APP-04").State);
        Assert.DoesNotContain(findings, f => f.CheckId == "APP-04");
    }

    [Fact]
    public void BrokenLoopingRetiredAndDisabledRelationsAreFound()
    {
        var read = SiteHygieneParity.EmptySite()
            .Put("Applications", AppCols, App(1, "A"), App(2, "B"), App(3, "Retired", expired: true), App(4, "Off", enabled: false), App(5, "NoMaker", maker: ""))
            .Put("DeploymentTypes", DtCols, ["11", "ScopeId_X/DeploymentType_A", "ScopeId_X/Application_A", "A DT", "", "True"])
            .Put("AppRelations", Rel,
                ["1", "11", "2", "21", "15"], ["2", "21", "1", "11", "15"],
                ["1", "11", "999", "991", "15"],
                ["1", "11", "3", "31", "10"],
                ["5", "51", "4", "41", "10"],
                ["4", "41", "1", "11", "15"]);
        var (findings, _) = SiteHygieneParity.Native(read, "AppRelationships");
        Assert.Equal(2, findings.Count(f => f.CheckId == "SUP-02"));
        var orphan = Assert.Single(findings, f => f.CheckId == "SUP-01");
        Assert.Equal("ScopeId_X/Application_A|CI_ID 999", orphan.ObjectKey);
        Assert.Contains("'A DT'", orphan.Evidence);
        Assert.Single(findings, f => f.CheckId == "DEP-04");
        Assert.Single(findings, f => f.CheckId == "DEP-03");
        Assert.Single(findings, f => f.CheckId == "SUP-03");
        Assert.Equal("ScopeId_X/Application_NoMaker", Assert.Single(findings, f => f.CheckId == "REL-01").ObjectKey);
        Assert.All(findings, f => Assert.Equal(HygieneFixKind.None, f.FixKind));
    }

    [Fact]
    public async Task TheInventoryIsOneScriptRunAndListsEveryEdgeWithItsHealth()
    {
        using var h = new SiteHygieneHarness();
        var inventory = await h.Service.LoadRelationsAsync(CancellationToken.None);
        Assert.Single(h.Site.Runs);
        Assert.Equal(5, h.Site.Queries());
        Assert.False(inventory.Truncated);
        Assert.Equal(3, inventory.Edges.Count);
        var sup = Assert.Single(inventory.Edges, e => e.Kind == RelationKinds.Supersedes);
        Assert.Equal(("SHL-App-New", "SHL-App-Old", RelationHealth.Healthy), (sup.FromName, sup.ToName, sup.Health));
        Assert.Equal("SHL-App-New DT", sup.FromDeploymentType);
        Assert.All(inventory.Edges.Where(e => e.Kind == RelationKinds.DependsOn), e =>
        {
            Assert.Equal(RelationHealth.MissingContent, e.Health);
            Assert.Equal("Required", e.DependencyState);
        });
        Assert.Equal(["SHL-App-New"], HygieneRelationTree.Roots(inventory, RelationKinds.Supersedes).Select(k => inventory.Apps[k].Name));
        Assert.Equal("SHL-App-Old", Assert.Single(HygieneRelationTree.Children(inventory, RelationKinds.Supersedes, sup.FromKey)).ToName);
        Assert.Equal(("✓", "Healthy"), HygieneRelationTree.Health(inventory, sup.ToKey, true));
    }

    [Fact]
    public void APureLoopStillHasRootsAndTheChainDepthStopsAtTheLoop()
    {
        var read = SiteHygieneParity.EmptySite()
            .Put("Applications", AppCols, App(1, "A"), App(2, "B"), App(3, "C"))
            .Put("AppRelations", Rel, ["1", "11", "2", "21", "15"], ["2", "21", "3", "31", "15"], ["3", "31", "1", "11", "15"]);
        var inventory = HygieneChecks.Inventory(read);
        Assert.All(inventory.Edges, e => Assert.Equal(RelationHealth.Circular, e.Health));
        Assert.Equal(3, HygieneRelationTree.Roots(inventory, RelationKinds.Supersedes).Count);
        Assert.All(inventory.Edges, e => Assert.InRange(e.ChainDepth, 0, 3));
    }
}
