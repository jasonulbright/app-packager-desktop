using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class PublishPreflightTests
{
    [Theory]
    [InlineData(DeploymentTargets.Mecm, "ConfigMgr")]
    [InlineData(DeploymentTargets.MecmAndIntune, "ConfigMgr|Intune")]
    [InlineData(DeploymentTargets.IntuneOnly, "Intune")]
    [InlineData(DeploymentTargets.MecmAndWsus, "ConfigMgr|WSUS")]
    [InlineData(DeploymentTargets.WsusOnly, "WSUS")]
    [InlineData(null, "ConfigMgr")]
    public void EachTargetThatTheRunPublishesToHasItsSentence(string? target, string expected) =>
        Assert.Equal(expected, string.Join("|", PublishPreflight.Lines(target).Select(l => l[..l.IndexOf(':')])));

    [Fact]
    public void TheSentencesNameWhatThePublishDoesNotDo()
    {
        Assert.Contains("does not supersede", PublishPreflight.ConfigMgrSentence);
        Assert.Contains("test collection that Settings name", PublishPreflight.ConfigMgrSentence);
        Assert.Contains("does not create or change assignments", PublishPreflight.IntuneSentence);
        Assert.Contains("already assigned", PublishPreflight.IntuneSentence);
        Assert.Contains("no first install", PublishPreflight.WsusSentence);
        Assert.Contains("approval group in Settings", PublishPreflight.WsusSentence);
        Assert.DoesNotContain("does not approve", PublishPreflight.WsusSentence);
    }

}
