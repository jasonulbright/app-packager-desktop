using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// After a ConfigMgr application is created, the run may distribute its content to a DP group and deploy it to a test
/// collection. If a step fails, the application exists but clients cannot install it, so the run must not report success.
/// </summary>
public sealed class ConfigMgrContentStepOutcomeTests
{
    private const string Failure = "Content distribution to 'All DPs' failed: access denied";

    private static PackagingExitOutcome Compose(string target, IntuneRunOutcome intune, string? contentStepFailure, bool mecmAlreadyExists = false) =>
        PackagingOutcomeComposer.Compose(
            PackagingOutcomeComposer.MecmStatus(!DeploymentTargets.SkipsMecm(target), true, mecmAlreadyExists),
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

    [Fact]
    public void AnEmptyReasonStillGivesAMessage()
    {
        var text = MecmTarget.ContentStepFailureText("distribution", "All DPs", "");

        Assert.Equal("Content distribution to 'All DPs' failed: the provider gave no reason.", text);
    }
}