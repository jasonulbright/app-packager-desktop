using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A run answers for every target the operator selected. An exit code derived from the
/// MECM result alone reports an Intune-only build or publish failure as a success, and
/// reports a created MECM application with a failed Intune publish as fully packaged.
/// The composition here is driven with the real deployment target strings.
/// </summary>
public class PackagingOutcomeTests
{
    private static PackagingExitOutcome Compose(
        string target, bool mecmSucceeded, string mecmError, IntuneRunOutcome intune,
        bool mecmAlreadyExists = false) =>
        PackagingOutcomeComposer.Compose(
            PackagingOutcomeComposer.MecmStatus(
                !DeploymentTargets.SkipsMecm(target), mecmSucceeded, mecmAlreadyExists),
            mecmError,
            DeploymentTargets.PublishesToIntune(target),
            intune);

    private static IntuneRunOutcome Published() =>
        IntuneRunOutcome.Published("built Contoso.intunewin", "published (app id app-100)");

    [Fact]
    public void IntuneOnlyPublishSucceeds()
    {
        var outcome = Compose(DeploymentTargets.IntuneOnly, true, "", Published());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("", outcome.StdErr);
    }

    [Fact]
    public void MecmOnlySucceedsWithoutAnIntuneStep()
    {
        var outcome = Compose(DeploymentTargets.Mecm, true, "", IntuneRunOutcome.NotRun);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("", outcome.StdErr);
    }

    /// <summary>A failed optional package build never fails an MECM-only run.</summary>
    [Fact]
    public void MecmOnlyIsUnaffectedByAFailedOptionalPackageBuild()
    {
        var outcome = Compose(
            DeploymentTargets.Mecm, true, "", IntuneRunOutcome.BuildFailed("build failed: tool missing"));

        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public void BothTargetsSucceeding() =>
        Assert.Equal(0, Compose(DeploymentTargets.MecmAndIntune, true, "", Published()).ExitCode);

    [Fact]
    public void AnIntuneOnlyBuildFailureFailsTheRun()
    {
        var outcome = Compose(
            DeploymentTargets.IntuneOnly, true, "",
            IntuneRunOutcome.BuildFailed("build failed: tool download blocked"));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("Intune package build failed", outcome.StdErr);
        Assert.Contains("tool download blocked", outcome.StdErr);
        Assert.DoesNotContain("ConfigMgr application created", outcome.StdErr);
    }

    [Fact]
    public void AnIntuneOnlyPublishFailureFailsTheRun()
    {
        var outcome = Compose(
            DeploymentTargets.IntuneOnly, true, "",
            IntuneRunOutcome.PublishFailed("built Contoso.intunewin", "publish failed: 403 Forbidden"));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("Intune publish failed", outcome.StdErr);
        Assert.Contains("403 Forbidden", outcome.StdErr);
    }

    /// <summary>A variant-split app cannot publish, so an Intune run that wanted it did not succeed.</summary>
    [Fact]
    public void AnUnsupportedVariantSplitFailsTheRun()
    {
        var outcome = Compose(
            DeploymentTargets.IntuneOnly, true, "",
            IntuneRunOutcome.PublishRefused("built Contoso.intunewin",
                "variant-split app; publish skipped (single-payload apps only)."));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("Intune publish did not run", outcome.StdErr);
        Assert.Contains("variant-split app", outcome.StdErr);
    }

    [Fact]
    public void MissingCredentialsFailTheRunRatherThanPassSilently()
    {
        var outcome = Compose(
            DeploymentTargets.IntuneOnly, true, "",
            IntuneRunOutcome.PublishRefused("built Contoso.intunewin",
                "Intune credentials not configured; set Tenant ID, Client ID, and Client Secret in Settings."));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("credentials not configured", outcome.StdErr);
    }

    /// <summary>
    /// The MECM application exists and stays reported, so an operator is not sent looking
    /// for something already on the site, but the run still fails.
    /// </summary>
    [Fact]
    public void MecmSuccessWithAnIntunePublishFailureReportsBoth()
    {
        var outcome = Compose(
            DeploymentTargets.MecmAndIntune, true, "",
            IntuneRunOutcome.PublishFailed("built Contoso.intunewin", "publish failed: 429 Too Many Requests"));

        Assert.Equal(1, outcome.ExitCode);
        Assert.StartsWith("ConfigMgr application created or updated.", outcome.StdErr);
        Assert.Contains("Intune publish failed", outcome.StdErr);
        Assert.Contains("429", outcome.StdErr);
    }

    [Fact]
    public void MecmSuccessWithAnIntuneBuildFailureReportsBoth()
    {
        var outcome = Compose(
            DeploymentTargets.MecmAndIntune, true, "",
            IntuneRunOutcome.BuildFailed("build failed: staged content missing"));

        Assert.Equal(1, outcome.ExitCode);
        Assert.StartsWith("ConfigMgr application created or updated.", outcome.StdErr);
        Assert.Contains("Intune package build failed", outcome.StdErr);
    }

    [Fact]
    public void AMecmFailureFailsTheRunAndKeepsItsOwnError()
    {
        var outcome = Compose(
            DeploymentTargets.MecmAndIntune, false, "Content share not accessible: \\\\server\\share",
            IntuneRunOutcome.NotRun);

        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("Content share not accessible: \\\\server\\share", outcome.StdErr);
    }

    [Fact]
    public void AMecmFailureWithNoMessageStillReportsSomething()
    {
        var outcome = Compose(DeploymentTargets.Mecm, false, "", IntuneRunOutcome.NotRun);

        Assert.Equal(1, outcome.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(outcome.StdErr));
    }

    /// <summary>
    /// A requested Intune step that never ran is not a success. Passing it because it is
    /// absent from the failure set is how a skipped step reads as a published app.
    /// </summary>
    [Theory]
    [InlineData("IntuneOnly")]
    [InlineData("MECMAndIntune")]
    public void ARequestedIntuneStepThatNeverRanFailsTheRun(string target)
    {
        var outcome = Compose(target, true, "", IntuneRunOutcome.NotRun);

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("Intune publish was not attempted", outcome.StdErr);
    }

    [Theory]
    [InlineData(IntuneStepStatus.NotRun)]
    [InlineData(IntuneStepStatus.BuildFailed)]
    [InlineData(IntuneStepStatus.PublishFailed)]
    [InlineData(IntuneStepStatus.PublishRefused)]
    public void OnlyPublishedSatisfiesARequestedIntuneTarget(IntuneStepStatus status)
    {
        var outcome = Compose(
            DeploymentTargets.IntuneOnly, true, "", new IntuneRunOutcome(status, "b", "p"));

        Assert.Equal(1, outcome.ExitCode);
    }

    /// <summary>
    /// Skip finds a title already on the site and succeeds without creating anything, so
    /// the partial-success line must not claim an application was created.
    /// </summary>
    [Fact]
    public void AnExistingMecmApplicationIsNotReportedAsCreated()
    {
        var outcome = Compose(
            DeploymentTargets.MecmAndIntune, true, "",
            IntuneRunOutcome.PublishFailed("built Contoso.intunewin", "publish failed: 500"),
            mecmAlreadyExists: true);

        Assert.Equal(1, outcome.ExitCode);
        Assert.DoesNotContain("ConfigMgr application created", outcome.StdErr);
        Assert.StartsWith("Existing ConfigMgr application kept.", outcome.StdErr);
        Assert.Contains("Intune publish failed", outcome.StdErr);
    }

    /// <summary>An Intune-only run has no MECM step to describe.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnUnrequestedMecmStepHasNoStatusOfItsOwn(bool succeeded, bool alreadyExists) =>
        Assert.Equal(MecmStepStatus.NotRequested,
            PackagingOutcomeComposer.MecmStatus(false, succeeded, alreadyExists));

    [Theory]
    [InlineData(true, false, MecmStepStatus.Created)]
    [InlineData(true, true, MecmStepStatus.AlreadyExisted)]
    [InlineData(false, false, MecmStepStatus.Failed)]
    [InlineData(false, true, MecmStepStatus.Failed)]
    public void TheMecmStatusFollowsTheTargetResult(
        bool succeeded, bool alreadyExists, MecmStepStatus expected) =>
        Assert.Equal(expected, PackagingOutcomeComposer.MecmStatus(true, succeeded, alreadyExists));

    /// <summary>A published app whose note happens to contain "failed" is still a success.</summary>
    [Fact]
    public void TheDecisionReadsTheStatusRatherThanTheNoteText()
    {
        var outcome = Compose(
            DeploymentTargets.IntuneOnly, true, "",
            IntuneRunOutcome.Published("built failed-retry-2.intunewin", "published (app id failed-over-1)"));

        Assert.Equal(0, outcome.ExitCode);
    }

    /// <summary>Only a published app counts; "never ran" is not a quiet success.</summary>
    [Theory]
    [InlineData(IntuneStepStatus.Published, true)]
    [InlineData(IntuneStepStatus.NotRun, false)]
    [InlineData(IntuneStepStatus.BuildFailed, false)]
    [InlineData(IntuneStepStatus.PublishFailed, false)]
    [InlineData(IntuneStepStatus.PublishRefused, false)]
    public void OnlyAPublishedOutcomeSucceeds(IntuneStepStatus status, bool succeeded) =>
        Assert.Equal(succeeded, new IntuneRunOutcome(status, "b", "p").Succeeded);

    /// <summary>A build failure explains itself through the build note, not the empty publish note.</summary>
    [Fact]
    public void TheFailureNoteComesFromTheStepThatFailed()
    {
        Assert.Equal("build failed: x", IntuneRunOutcome.BuildFailed("build failed: x").FailureNote);
        Assert.Equal("publish failed: y",
            IntuneRunOutcome.PublishFailed("built z", "publish failed: y").FailureNote);
    }

    /// <summary>
    /// Cancellation is not an outcome status: it propagates out of the publish instead, so
    /// the composition never sees it and the run does not report a verdict.
    /// </summary>
    [Fact]
    public async Task ACancelledPublishThrowsRatherThanReturningAnOutcome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packr-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var package = IntuneWinFixture.Create(dir);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            // A fake transport, so a cancelled run cannot reach a real endpoint either way.
            var handler = new FakeGraphHandler((_, _) => FakeGraphHandler.Json("{}"));
            using var svc = new IntunePublishService(handler);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                svc.PublishAsync(package, new StageManifest(), new IntunePublishOptions
                {
                    TenantId = "t",
                    ClientId = "c",
                    ClientSecret = "s"
                }, null, cts.Token));

            // Whatever the run reached went through the fake transport, never a real endpoint.
            Assert.All(handler.Requests, r => Assert.DoesNotContain("graph.microsoft.com/v1.0", r.Uri));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoOutcomeStatusRepresentsCancellation() =>
        Assert.DoesNotContain(Enum.GetNames<IntuneStepStatus>(),
            n => n.Contains("Cancel", StringComparison.OrdinalIgnoreCase));
}
