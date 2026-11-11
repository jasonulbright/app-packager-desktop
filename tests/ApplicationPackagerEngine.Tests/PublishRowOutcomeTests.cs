using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

public class PublishRowOutcomeTests
{
    private static readonly WsusStepResult WsusOk = new(WsusStepStatus.Published, "published x", [], []);
    private static readonly WsusStepResult WsusAgain = new(WsusStepStatus.AlreadyPublished, "already on the server x", [], []);
    private static readonly WsusStepResult WsusWarned = new(WsusStepStatus.Published, "published x; approval for P failed: no", [], ["approval for P failed: no"]);
    private static readonly WsusStepResult WsusRefused = new(WsusStepStatus.Refused, "cannot", ["InstallerTypeUnsupported", "CustomInstall"], []);
    private static readonly WsusStepResult WsusFailed = new(WsusStepStatus.Failed, "rejected", [], []);
    private static readonly WsusStepResult WsusAgainWarned = new(WsusStepStatus.AlreadyPublished, "already on the server x; approval for P failed: no", [], ["approval for P failed: no"]);

    [Theory]
    [InlineData("ok", PublishRowOutcomeKind.Success, "Published (updates only)", true, "This update applies only where the detection finds an older version of the application, so WSUS cannot give a first install of it.")]
    [InlineData("again", PublishRowOutcomeKind.Success, "Already on the server (updates only)", true, "This update applies only where the detection finds an older version of the application, so WSUS cannot give a first install of it.")]
    [InlineData("againWarned", PublishRowOutcomeKind.Success, "Already on the server (updates only), warning", true, "already on the server x; approval for P failed: no")]
    [InlineData("warned", PublishRowOutcomeKind.Success, "Published (updates only), warning", true, "published x; approval for P failed: no")]
    [InlineData("refused", PublishRowOutcomeKind.NotSupported, "not supported: InstallerTypeUnsupported,CustomInstall", false, "cannot")]
    [InlineData("failed", PublishRowOutcomeKind.Failed, "Publish error", false, "rejected")]
    [InlineData("none", PublishRowOutcomeKind.Failed, "Publish error", false, "")]
    public void AWsusRunSucceedsOnlyWhenItsPublishSucceeds(string wsus, PublishRowOutcomeKind kind, string status, bool published, string detail)
    {
        var outcome = PublishRowOutcome.For(PublishRunTargets.WsusOnly, 0, wsus: Pick(wsus));
        Assert.Equal(new PublishRowOutcome(kind, status, published, detail), outcome);
    }

    [Fact]
    public void AWsusRefusalRowUsesTheResultCellWording()
    {
        Assert.Equal(WsusRefused.NotSupportedReason, PublishRowOutcome.For(PublishRunTargets.WsusOnly, 0, wsus: WsusRefused).Status);
    }

    [Theory]
    [InlineData("none", "Packaged")]
    [InlineData("ok", "Packaged")]
    [InlineData("warned", "Packaged, WSUS warning")]
    [InlineData("refused", "Packaged, WSUS not supported")]
    [InlineData("failed", "Packaged, WSUS failed")]
    public void AConfigMgrRunCarriesTheWsusResultAsASuffix(string wsus, string status)
    {
        Assert.Equal(new PublishRowOutcome(PublishRowOutcomeKind.Success, status, false),
            PublishRowOutcome.For(PublishRunTargets.ConfigMgrAndWsus, 0, wsus: Pick(wsus)));
    }

    [Fact]
    public void AnIntuneRunSucceedsOnlyWhenTheAppWasPublished()
    {
        Assert.Equal(new PublishRowOutcome(PublishRowOutcomeKind.Success, "Published", true),
            PublishRowOutcome.For(PublishRunTargets.IntuneOnly, 0, intune: IntuneRunOutcome.Published("built", "published")));
        Assert.Equal(new PublishRowOutcome(PublishRowOutcomeKind.Failed, "Publish error", false),
            PublishRowOutcome.For(PublishRunTargets.IntuneOnly, 0, intune: IntuneRunOutcome.PublishFailed("built", "denied")));
        Assert.Equal(PublishRowOutcomeKind.Failed, PublishRowOutcome.For(PublishRunTargets.IntuneOnly, 0).Kind);
    }

    [Fact]
    public void AFailedRunIsAPublishErrorWithoutASiteAndAPackageErrorWithOne()
    {
        Assert.Equal("Publish error", PublishRowOutcome.For(PublishRunTargets.WsusOnly, 1).Status);
        Assert.Equal("Publish error", PublishRowOutcome.For(PublishRunTargets.IntuneOnly, 1).Status);
        Assert.Equal("Package error", PublishRowOutcome.For(PublishRunTargets.ConfigMgr, 1).Status);
        Assert.Equal(PublishRowOutcomeKind.Failed, PublishRowOutcome.For(PublishRunTargets.ConfigMgrAndWsus, 2, wsus: WsusOk).Kind);
    }

    [Theory]
    [InlineData(PackageRunEnd.Skipped, "none", "Skipped")]
    [InlineData(PackageRunEnd.Canceled, "refused", "Canceled, WSUS not supported")]
    public void ASkippedOrCanceledRunKeepsTheWsusSuffix(PackageRunEnd end, string wsus, string status)
    {
        Assert.Equal(new PublishRowOutcome(PublishRowOutcomeKind.Skipped, status, false),
            PublishRowOutcome.For(PublishRunTargets.ConfigMgrAndWsus, 0, end, Pick(wsus)));
    }

    [Fact]
    public void ARefusalCellShowsOnlyTheCodes()
    {
        Assert.Equal("not supported: InstallerTypeUnsupported,CustomInstall", WsusRefused.NotSupportedReason);
        Assert.Equal("", WsusOk.NotSupportedReason);
        Assert.True(WsusAgain.Ok);
        Assert.False(WsusRefused.Ok);
    }

    [Fact]
    public void OnlyTheSiteFreeTargetsSkipTheSite()
    {
        Assert.Equal([PublishRunTargets.IntuneOnly, PublishRunTargets.WsusOnly], PublishRunTargets.Names.Where(PublishRunTargets.SkipsSite));
        Assert.Equal([PublishRunTargets.ConfigMgrAndWsus, PublishRunTargets.WsusOnly], PublishRunTargets.Names.Where(PublishRunTargets.PublishesToWsus));
    }

    private static WsusStepResult? Pick(string name) => name switch
    {
        "ok" => WsusOk,
        "again" => WsusAgain,
        "againWarned" => WsusAgainWarned,
        "warned" => WsusWarned,
        "refused" => WsusRefused,
        "failed" => WsusFailed,
        _ => null
    };
}
