using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class ProductSigningCheckTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-dhsigning-" + Guid.NewGuid().ToString("N"));
    private readonly ApplicationInventoryService _inventory;
    private readonly SigningPolicy _policy = new() { RequireDeployment = true };
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

    public ProductSigningCheckTests()
    {
        Directory.CreateDirectory(_folder);
        _inventory = new ApplicationInventoryService(Path.Combine(_folder, "history.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private ProductSigningCheck Check(string site = "MCM") => new(_inventory, () => new ConfigMgrConnection(site, "cm01.contoso.com"), () => _policy);

    private void Publish(string name, string? deploymentStatus, string build = "build-1", string publishedBuild = "build-1", string? ciId = null)
    {
        var key = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        var identity = new InventoryApplicationIdentity("uid-" + name, "catalog:" + key, "default", 1);
        var manifest = Path.Combine(_folder, key + "-manifest.json");
        if (deploymentStatus is not null)
            File.WriteAllText(manifest, $$"""{ "ScriptSigning": { "Deployment": { "Status": "{{deploymentStatus}}", "Files": [] } } }""");
        _inventory.RecordStagedBuild(new InventoryStagedBuild(identity, "1.0", build, "build.json", manifest, At));
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("uid-" + name, identity, InventoryEnvironmentIdentity.ConfigMgr("MCM", "cm01.contoso.com"),
            "Publish", "1.0", InventoryOutcome.Succeeded, name, At, publishedBuild, ConfigMgrCiId: ciId));
    }

    private const string Model = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E/Application_47899247-5d1c-426d-80cb-cf82c3313247";

    private InventoryDestinationState Row(string name) =>
        _inventory.GetDestinationState("uid-" + name, InventoryEnvironmentIdentity.ConfigMgr("MCM", "cm01.contoso.com"), "Publish")!;

    [Fact]
    public void AnApplicationRenamedAfterThePublishIsFoundByARevisionCiIdAndLinkedToItsModel()
    {
        Publish("Tool", SigningStatus.NotRequested, ciId: "16780662");
        var renamed = new SiteApplication("Tool (renamed)", "16780665", Model, ["16780662", "16780665"]);

        Assert.True(Check().IsPackaged(renamed));
        var refusal = Check().RequiredDeploymentRefusal(renamed);

        Assert.Contains("This product packaged application Tool (renamed) (published as Tool)", refusal);
        Assert.Equal(Model, Row("Tool").ConfigMgrModelName);
        Assert.Equal("16780665", Row("Tool").ConfigMgrCiId);
        Assert.NotNull(Check().RequiredDeploymentRefusal(renamed with { Name = "Tool (renamed twice)", CiId = "16780670", RevisionCiIds = ["16780670"] }));
    }

    [Fact]
    public void AListChecksEveryApplicationAgainstTheSamePublicationRows()
    {
        Publish("Tool", SigningStatus.NotRequested, ciId: "16780662");
        var renamed = new SiteApplication("Tool (renamed)", "16780665", Model, ["16780662", "16780665"]);
        var other = new SiteApplication("Other", "16790000", "ScopeId_X/Application_other", ["16790000"]);

        Assert.Equal([true, false], Check().ArePackaged([renamed, other]));
        Assert.Equal(Model, Row("Tool").ConfigMgrModelName);
    }

    [Fact]
    public void AListLinksARecordByItsUniqueNameAndARenameAfterThatIsStillCaught()
    {
        Publish("Tool", SigningStatus.NotRequested);
        Publish("Twin", SigningStatus.NotRequested);
        Check().Link([new SiteApplication("Tool", "16780662", Model, ["16780662"]), new SiteApplication("Twin", "1", "M1", ["1"]), new SiteApplication("Twin", "2", "M2", ["2"])]);

        Assert.Equal(Model, Row("Tool").ConfigMgrModelName);
        Assert.Null(Row("Twin").ConfigMgrModelName);
        Assert.NotNull(Check().RequiredDeploymentRefusal(new SiteApplication("Renamed", "16780665", Model, ["16780662", "16780665"])));
    }

    [Fact]
    public void ALinkedRecordDoesNotMatchAnotherApplicationThatTookItsName()
    {
        Publish("Tool", SigningStatus.NotRequested, ciId: "16780662");
        var other = new SiteApplication("Tool", "16790000", "ScopeId_X/Application_other", ["16790000"]);

        Assert.False(Check().IsPackaged(other));
        Assert.Null(Check().RequiredDeploymentRefusal(other));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARenameInTheConsoleBeforeValidateIsCaughtWhenTheRecordHoldsTheCiId(bool recordHasCiId)
    {
        Publish("7-Zip (x64 edition) - 26.03", SigningStatus.NotRequested, ciId: recordHasCiId ? "16779607" : null);
        var state = StubDeploymentSite.Lab();
        var app = state["Applications"]![0]!.AsObject();
        state["Revisions"] = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonNode.Parse(app.ToJsonString()));
        app["CI_ID"] = 16779700;
        app["LocalizedDisplayName"] = "7-Zip renamed";
        using var h = new DeploymentHarness(state, Check());
        var renamed = DeploymentHarness.App with { Id = "16779700", Name = "7-Zip renamed" };

        var prepared = await h.PrepareAsync(DeploymentHarness.Create(renamed, DeploymentHarness.Options(DeploymentObjectType.Application, "Required"), required: true));

        var signing = prepared.Checks.Single(c => c.Name == "Signing");
        Assert.Equal(recordHasCiId ? "Fail" : "Pass", signing.Result);
        if (recordHasCiId)
        {
            Assert.Contains("This product packaged application 7-Zip renamed (published as 7-Zip (x64 edition) - 26.03)", signing.Message);
            Assert.False(string.IsNullOrEmpty(Row("7-Zip (x64 edition) - 26.03").ConfigMgrModelName));
            Assert.StartsWith("Skipped: ", (await h.ApplyAsync(prepared with { Checks = prepared.Checks.Select(c => c with { Result = "Pass" }).ToList() })).Single().Message);
            Assert.Equal(0, h.CreateCalls);
        }
    }

    [Fact]
    public void AnOlderRecordWithoutACiIdRenamedBeforeAnyLookupIsAGap()
    {
        // Known gap: the record holds only the published name, so nothing ties it to the renamed application.
        Publish("Tool", SigningStatus.NotRequested);

        var renamed = new SiteApplication("Tool (renamed)", "16780665", Model, ["16780662", "16780665"]);

        Assert.False(Check().IsPackaged(renamed));
        Assert.Null(Check().RequiredDeploymentRefusal(renamed));
    }

    [Fact]
    public void AnApplicationWhoseIdentityWasNotReadIsRefusedWhenRecordsHoldIdentities()
    {
        Publish("Tool", SigningStatus.NotRequested, ciId: "16780662");

        Assert.Contains("cannot tell whether it packaged the application", Check().RequiredDeploymentRefusal(new SiteApplication("Other", "16790000", "", [])));
        Assert.Null(Check().RequiredDeploymentRefusal(new SiteApplication("Other", "16790000", "ScopeId_X/Application_other", ["16790000"])));
    }

    [Fact]
    public void ARequiredDeploymentOfAnUnsignedPackagedApplicationIsRefused()
    {
        Publish("Tool", SigningStatus.NotRequested);

        Assert.Contains("Tool", Check().PackagedApplications());
        Assert.Contains("A Required deployment is refused", Check().RequiredDeploymentRefusal("tool"));
    }

    [Theory]
    [InlineData(SigningStatus.SignedAndVerified)]
    [InlineData(SigningStatus.ExistingSignatureValid)]
    public void ASignedBuildIsAllowed(string status)
    {
        Publish("Tool", status);
        Assert.Null(Check().RequiredDeploymentRefusal("Tool"));
    }

    [Fact]
    public void TheRuleDoesNotApplyWithoutThePolicyOrForAnotherApplicationOrSite()
    {
        Publish("Tool", SigningStatus.NotRequested);

        Assert.Null(Check().RequiredDeploymentRefusal("Other"));
        Assert.Empty(Check("XYZ").PackagedApplications());
        Assert.Null(Check("XYZ").RequiredDeploymentRefusal("Tool"));
        _policy.RequireDeployment = false;
        Assert.Null(Check().RequiredDeploymentRefusal("Tool"));
    }

    [Fact]
    public void AMissingManifestOrABuildOtherThanThePublishedOneIsRefusedUnderThePolicy()
    {
        Publish("NoManifest", null);
        Publish("Rebuilt", SigningStatus.SignedAndVerified, build: "build-2", publishedBuild: "build-1");

        Assert.NotNull(Check().RequiredDeploymentRefusal("NoManifest"));
        Assert.NotNull(Check().RequiredDeploymentRefusal("Rebuilt"));
    }
}
