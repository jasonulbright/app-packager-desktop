using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Which publish buttons are available, and what their tooltips say, for each combination of configured systems.</summary>
public sealed class PublishActionStateTests
{
    private static PublishActionState State(bool console = true, string siteCode = "MCM", bool intune = false, string wsusServer = "",
        string[]? inUse = null, string tenant = "", string client = "", bool secret = false)
    {
        var systems = inUse ?? ["ConfigMgr", "Intune", "Wsus"];
        return PublishActionState.For(new PublishReadiness(
            new SystemsInUse { ConfigMgr = systems.Contains("ConfigMgr"), Intune = systems.Contains("Intune"), Wsus = systems.Contains("Wsus") },
            console, siteCode, @"\\server\share",
            intune ? "tenant" : tenant, intune ? "client" : client, intune || secret, wsusServer));
    }

    [Fact]
    public void ConfigMgrOnlyEnablesCheckConfigMgrAndPublishToConfigMgr()
    {
        var s = State();
        Assert.True(s.CheckMecmEnabled && s.MecmEnabled);
        Assert.False(s.IntuneEnabled);
        Assert.False(s.WsusEnabled || s.WsusUpdatesEnabled);
        Assert.Contains("WSUS server", s.WsusToolTip);
        Assert.Contains("WSUS server", s.WsusUpdatesToolTip);
        Assert.Contains("Tenant ID", s.IntuneToolTip);
        Assert.Contains("Client Secret", s.IntuneToolTip);
    }

    [Fact]
    public void NoConsoleDisablesBothConfigMgrButtonsAndKeepsWsus()
    {
        var s = State(console: false, wsusServer: "wsus01");
        Assert.False(s.CheckMecmEnabled || s.MecmEnabled);
        Assert.Contains("console", s.MecmToolTip);
        Assert.True(s.WsusEnabled && s.WsusUpdatesEnabled);
    }

    [Fact]
    public void NoSiteCodeDisablesPublishToConfigMgrAndNamesIt()
    {
        var s = State(siteCode: "");
        Assert.False(s.MecmEnabled);
        Assert.Contains("site code", s.MecmToolTip);
    }

    [Fact]
    public void AllSystemsEnableEveryButton()
    {
        var s = State(intune: true, wsusServer: "wsus01");
        Assert.True(s.CheckMecmEnabled && s.MecmEnabled && s.IntuneEnabled && s.WsusEnabled && s.WsusUpdatesEnabled);
        Assert.Contains("older version", s.WsusToolTip);
        Assert.Equal("Build a ConfigMgr Application and Deployment Type for every checked app", s.MecmToolTip);
    }

    [Fact]
    public void ATenantWithoutClientAndSecretKeepsIntuneDisabled() =>
        Assert.False(State(tenant: "tenant").IntuneEnabled);

    [Fact]
    public void ConfigMgrNotInUseDisablesItsButtonsDespiteConsoleAndSiteCode()
    {
        var s = State(wsusServer: "wsus01", inUse: ["Wsus"]);
        Assert.False(s.CheckMecmEnabled || s.MecmEnabled);
        Assert.Contains("not in use", s.MecmToolTip);
        Assert.Contains("Systems in use", s.MecmToolTip);
        Assert.True(s.WsusEnabled && s.WsusUpdatesEnabled);
        Assert.True(s.SkipMecmPreflight);
    }

    [Fact]
    public void WsusNotInUseDisablesItsButtonsDespiteAServer()
    {
        var s = State(intune: true, wsusServer: "wsus01", inUse: ["ConfigMgr", "Intune"]);
        Assert.False(s.WsusEnabled || s.WsusUpdatesEnabled);
        Assert.StartsWith("Publish to WSUS: WSUS is not in use", s.WsusToolTip);
        Assert.True(s.MecmEnabled && s.IntuneEnabled);
    }

    [Fact]
    public void ADisabledButtonIsReEnabledOnceItsSettingIsSaved()
    {
        Assert.False(State(inUse: ["Wsus"]).WsusEnabled);
        Assert.True(State(inUse: ["Wsus"], wsusServer: "wsus01").WsusEnabled);
    }

    [Fact]
    public void TheSiteGatesApplyOnlyToAConfigMgrRun()
    {
        var noSite = new PublishReadiness(new SystemsInUse { ConfigMgr = false, Intune = true, Wsus = true }, false, "", "", "t", "c", true, "wsus01");
        Assert.Null(PublishActionState.RunGate(PublishRunTargets.IntuneOnly, noSite));
        Assert.Null(PublishActionState.RunGate(PublishRunTargets.WsusOnly, noSite));
        Assert.Contains("ConfigMgr Console", PublishActionState.RunGate(PublishRunTargets.ConfigMgr, noSite));
        Assert.Contains("SiteCode", PublishActionState.RunGate(PublishRunTargets.ConfigMgr, noSite with { ConsoleFound = true }));
        Assert.Contains("File Share Root", PublishActionState.RunGate(PublishRunTargets.ConfigMgr, noSite with { ConsoleFound = true, SiteCode = "MCM" }));
        Assert.Contains("WSUS server", PublishActionState.RunGate(PublishRunTargets.WsusOnly, noSite with { WsusServerName = "" }));
        Assert.Contains("Intune credentials", PublishActionState.RunGate(PublishRunTargets.IntuneOnly, noSite with { HasIntuneClientSecret = false }));
    }
}
