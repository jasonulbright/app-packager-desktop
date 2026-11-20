using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

public class PublishAllStateTests
{
    [Fact]
    public void NoSystemInUseDisablesPublishAll()
    {
        var (enabled, tip, target) = PublishAllState.For(new SystemsInUse { ConfigMgr = false, Intune = false, Wsus = false });
        Assert.False(enabled);
        Assert.Equal("", target);
        Assert.Contains("no system is in use", tip);
    }

    [Theory]
    [InlineData(true, false, false, PublishRunTargets.ConfigMgr)]
    [InlineData(false, true, false, PublishRunTargets.IntuneOnly)]
    [InlineData(false, false, true, PublishRunTargets.WsusOnly)]
    [InlineData(true, true, false, PublishRunTargets.ConfigMgrAndIntune)]
    [InlineData(true, false, true, PublishRunTargets.ConfigMgrAndWsus)]
    [InlineData(false, true, true, PublishRunTargets.IntuneAndWsus)]
    [InlineData(true, true, true, PublishRunTargets.ConfigMgrAndIntuneAndWsus)]
    public void PublishAllTargetsEveryEnabledSystem(bool configMgr, bool intune, bool wsus, string expected)
    {
        var (enabled, tip, target) = PublishAllState.For(new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus });
        Assert.True(enabled);
        Assert.Equal(expected, target);
        Assert.Contains(new DestinationSet(configMgr, wsus, intune).Describe(), tip);
    }
}
