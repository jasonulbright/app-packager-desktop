using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// After a ConfigMgr application is created, the run may distribute its content to a DP group and deploy it to a test
/// collection. If a step fails, the application exists but clients cannot install it, so the run must not report success.
/// </summary>
public sealed class ConfigMgrContentStepOutcomeTests
{
    private const string Failure = "Content distribution to 'All DPs' failed: access denied";

    private static PackagingExitOutcome Compose(string target, IntuneRunOutcome intune, string? contentStepFailure) =>
        PackagingOutcomeComposer.Compose(
            PackagingOutcomeComposer.MecmStatus(!DeploymentTargets.SkipsMecm(target), true, false),
            "",
            DeploymentTargets.PublishesToIntune(target),
            intune,
            contentStepFailure);

    [Fact]
    public void AFailedContentStepFailsAConfigMgrOnlyRunAndKeepsTheReason()
    {
        var outcome = Compose(DeploymentTargets.Mecm, IntuneRunOutcome.NotRun, Failure);

        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("ConfigMgr application created, but a step after it failed: " + Failure, outcome.StdErr);
    }

    [Fact]
    public void NoFailureKeepsTheRunSuccessful()
    {
        var outcome = Compose(DeploymentTargets.Mecm, IntuneRunOutcome.NotRun, null);
        var blank = Compose(DeploymentTargets.Mecm, IntuneRunOutcome.NotRun, "  ");

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(0, blank.ExitCode);
    }

    [Fact]
    public void ARunThatPublishedToIntuneIsStillFailedByTheContentStep()
    {
        var published = IntuneRunOutcome.Published("built Contoso.intunewin", "published (app id app-100)");

        var outcome = Compose(DeploymentTargets.MecmAndIntune, published, Failure);

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(Failure, outcome.StdErr);
    }

    [Fact]
    public void AFailedIntunePublishAndAFailedContentStepAreBothReported()
    {
        var outcome = Compose(DeploymentTargets.MecmAndIntune,
            IntuneRunOutcome.PublishFailed("built Contoso.intunewin", "publish failed: 500"), Failure);

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("Intune publish failed", outcome.StdErr);
        Assert.Contains(Failure, outcome.StdErr);
    }

    [Fact]
    public void AnIntuneOnlyRunHasNoConfigMgrStepToFail()
    {
        var published = IntuneRunOutcome.Published("built Contoso.intunewin", "published (app id app-100)");

        var outcome = Compose(DeploymentTargets.IntuneOnly, published, Failure);

        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public void ADistributionThatFailsIsDescribedWithTheGroupAndTheReason()
    {
        var text = MecmTarget.ContentStepFailureText("distribution", "All DPs", "  access denied\r\n");

        Assert.Equal("Content distribution to 'All DPs' failed: access denied", text);
    }

    [Fact]
    public void ATestDeploymentThatFailsIsDescribedWithTheCollectionAndTheReason()
    {
        var text = MecmTarget.ContentStepFailureText("test deployment", "Pilot", "collection not found");

        Assert.Equal("Test deployment to 'Pilot' failed: collection not found", text);
    }

    [Fact]
    public void OnlyTheFirstLineOfALongProviderTextIsTheReason()
    {
        var provider = "Distribution point group 'No Such Group' was not found.\r\nAt line:12 char:5\r\n+ throw ...";

        var text = MecmTarget.ContentStepFailureText("distribution", "No Such Group", provider);

        Assert.Equal("Content distribution to 'No Such Group' failed: Distribution point group 'No Such Group' was not found.", text);
    }

    [Fact]
    public void AVeryLongReasonIsCut()
    {
        var text = MecmTarget.ContentStepFailureText("distribution", "All DPs", new string('x', 1000));

        Assert.Equal("Content distribution to 'All DPs' failed: " + new string('x', 300) + "...", text);
    }

    // ---- the steps, in their order ----------------------------------------------------------------------------------

    private static Task<MecmTarget.ContentStepResult> Step(List<string> calls, string name, MecmTarget.ContentStepResult result)
    {
        calls.Add(name);
        return Task.FromResult(result);
    }

    [Fact]
    public async Task ADistributionThatFailsSkipsTheTestDeploymentAndReturnsItsSentence()
    {
        var calls = new List<string>();

        var failure = await MecmTarget.RunContentStepsAsync(
            () => Step(calls, "distribute", MecmTarget.ContentStepResult.Failed("Content distribution to 'All DPs' failed: denied")),
            () => Step(calls, "deploy", MecmTarget.ContentStepResult.Ok));

        Assert.Equal("Content distribution to 'All DPs' failed: denied", failure);
        Assert.Equal(["distribute"], calls);
    }

    [Fact]
    public async Task ATestDeploymentThatFailsAfterADistributionThatWorkedReturnsItsSentence()
    {
        var calls = new List<string>();

        var failure = await MecmTarget.RunContentStepsAsync(
            () => Step(calls, "distribute", MecmTarget.ContentStepResult.Ok),
            () => Step(calls, "deploy", MecmTarget.ContentStepResult.Failed("Test deployment to 'Pilot' failed: no collection")));

        Assert.Equal("Test deployment to 'Pilot' failed: no collection", failure);
        Assert.Equal(["distribute", "deploy"], calls);
    }

    [Fact]
    public async Task BothStepsThatWorkReturnNoFailure()
    {
        var calls = new List<string>();

        var failure = await MecmTarget.RunContentStepsAsync(
            () => Step(calls, "distribute", MecmTarget.ContentStepResult.Ok),
            () => Step(calls, "deploy", MecmTarget.ContentStepResult.Ok));

        Assert.Null(failure);
        Assert.Equal(["distribute", "deploy"], calls);
    }

    [Fact]
    public async Task WithoutATestDeploymentOnlyTheDistributionRuns()
    {
        var calls = new List<string>();

        var failure = await MecmTarget.RunContentStepsAsync(
            () => Step(calls, "distribute", MecmTarget.ContentStepResult.Ok), null);

        Assert.Null(failure);
        Assert.Equal(["distribute"], calls);
    }

    // ---- what the inventory records ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, false, null, InventoryOutcome.Succeeded)]
    [InlineData(true, true, false, "Content distribution to 'All DPs' failed: denied", InventoryOutcome.Partial)]
    [InlineData(true, true, false, "  ", InventoryOutcome.Succeeded)]
    [InlineData(true, true, true, null, InventoryOutcome.AlreadyPresent)]
    [InlineData(true, false, false, null, InventoryOutcome.Failed)]
    public void TheConfigMgrDestinationIsPartialWhenTheApplicationExistsButAContentStepFailed(
        bool requested, bool succeeded, bool alreadyExists, string? failure, InventoryOutcome expected) =>
        Assert.Equal(expected, PackagingOutcomeComposer.ConfigMgrInventoryOutcome(requested, succeeded, alreadyExists, failure));

    [Fact]
    public void AnIntuneOnlyRunHasNoConfigMgrOutcome() =>
        Assert.Null(PackagingOutcomeComposer.ConfigMgrInventoryOutcome(false, true, false, "anything"));

    [Fact]
    public void AConfigMgrOnlyRunWithAPartialDestinationIsSummarizedAsFailed() =>
        Assert.Equal(InventoryOutcome.Failed, PackagingOutcomeComposer.SummarizeDestinations([InventoryOutcome.Partial]));

    [Fact]
    public void APublishedIntuneAppWithAPartialConfigMgrDestinationIsSummarizedAsPartial() =>
        Assert.Equal(InventoryOutcome.Partial,
            PackagingOutcomeComposer.SummarizeDestinations([InventoryOutcome.Partial, InventoryOutcome.Succeeded]));

    [Theory]
    [InlineData(InventoryOutcome.Succeeded, InventoryOutcome.Succeeded, InventoryOutcome.Succeeded)]
    [InlineData(InventoryOutcome.AlreadyPresent, InventoryOutcome.Succeeded, InventoryOutcome.Succeeded)]
    [InlineData(InventoryOutcome.Succeeded, InventoryOutcome.Failed, InventoryOutcome.Partial)]
    [InlineData(InventoryOutcome.Succeeded, InventoryOutcome.Refused, InventoryOutcome.Partial)]
    [InlineData(InventoryOutcome.Refused, InventoryOutcome.Failed, InventoryOutcome.Refused)]
    [InlineData(InventoryOutcome.Skipped, InventoryOutcome.Skipped, InventoryOutcome.Skipped)]
    [InlineData(InventoryOutcome.Failed, InventoryOutcome.Failed, InventoryOutcome.Failed)]
    public void TheDestinationSummaryKeepsItsEarlierRules(InventoryOutcome first, InventoryOutcome second, InventoryOutcome expected) =>
        Assert.Equal(expected, PackagingOutcomeComposer.SummarizeDestinations([first, second]));

    [Fact]
    public void AnEmptyReasonStillGivesAMessage()
    {
        var text = MecmTarget.ContentStepFailureText("distribution", "All DPs", "");

        Assert.Equal("Content distribution to 'All DPs' failed: the provider gave no reason.", text);
    }
}