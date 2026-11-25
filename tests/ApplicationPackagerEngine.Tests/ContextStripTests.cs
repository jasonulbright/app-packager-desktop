using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using Wpf.Ui.Appearance;
using Application = System.Windows.Application;
using Size = System.Windows.Size;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Overhaul V1 phase 2 (Q-126): the context strip. The state of each system is a pure function of the settings and the last ConfigMgr test;
/// the strip shows the word and a glyph, never a color alone; no box edits the site; the strip follows a saved setting without a restart.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ContextStripTests
{
    private static PublishReadiness Readiness(bool configMgr, bool intune, bool wsus, bool console = true, bool creds = true, bool server = true) =>
        new(new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }, console, "MCM", "\\\\cm01\\share",
            creds ? "t" : "", creds ? "c" : "", creds, server ? "wsus01" : "");

    [Fact]
    public void ASystemThatIsNotInUseIsOff()
    {
        var state = ContextStripState.From(Readiness(true, false, false), ConfigMgrConnectionState.Unknown, "MCM", "cm01.contoso.com");
        Assert.Equal(SystemState.Off, state.For("Intune").State);
        Assert.Equal("Intune off", state.For("Intune").Text);
        Assert.Equal("WSUS off", state.For("WSUS").Text);
    }

    [Fact]
    public void ASystemInUseWithAMissingSettingIsNotConfiguredAndNamesTheReason()
    {
        var state = ContextStripState.From(Readiness(true, true, true, console: false, creds: false, server: false), ConfigMgrConnectionState.Connected, "MCM", "cm01");
        Assert.Equal(SystemState.NotConfigured, state.For("ConfigMgr").State);
        Assert.Contains("ConfigMgr Console", state.For("ConfigMgr").Reason);
        Assert.Equal(SystemState.NotConfigured, state.For("Intune").State);
        Assert.Contains("Intune credentials", state.For("Intune").Reason);
        Assert.Equal("WSUS not configured", state.For("WSUS").Text);
        Assert.Contains("WSUS server", state.For("WSUS").Reason);
    }

    [Theory]
    [InlineData(ConfigMgrConnectionState.Unknown, SystemState.Ready, "ConfigMgr enabled")]
    [InlineData(ConfigMgrConnectionState.Testing, SystemState.Testing, "ConfigMgr testing")]
    [InlineData(ConfigMgrConnectionState.Connected, SystemState.Connected, "ConfigMgr connected")]
    [InlineData(ConfigMgrConnectionState.Failed, SystemState.Failed, "ConfigMgr connection failed")]
    public void ConfigMgrShowsTheLastConnectionTest(ConfigMgrConnectionState connection, SystemState expected, string text)
    {
        var configMgr = ContextStripState.From(Readiness(true, false, false), connection, "MCM", "cm01").For("ConfigMgr");
        Assert.Equal(expected, configMgr.State);
        Assert.Equal(text, configMgr.Text);
    }

    [Theory]
    [InlineData(ConfigMgrConnectionState.Unknown)]
    [InlineData(ConfigMgrConnectionState.Connected)]
    [InlineData(ConfigMgrConnectionState.Failed)]
    public void IntuneAndWsusNeverReadConnectedBecauseNothingTestsThem(ConfigMgrConnectionState connection)
    {
        var state = ContextStripState.From(Readiness(true, true, true), connection, "MCM", "cm01");
        Assert.Equal(SystemState.Ready, state.For("Intune").State);
        Assert.Equal(SystemState.Ready, state.For("WSUS").State);
        Assert.Equal("Intune enabled", state.For("Intune").Text);
        Assert.DoesNotContain("connected", state.For("WSUS").Text);
    }

    [Fact]
    public void TheNameOfTheStripReadsTheSiteTheHostAndEverySystem()
    {
        var state = ContextStripState.From(Readiness(true, false, false), ConfigMgrConnectionState.Connected, "MCM", "cm01.contoso.com");
        Assert.Equal("Site MCM, cm01.contoso.com, ConfigMgr connected, Intune off, WSUS off", state.AutomationName);
        var none = ContextStripState.From(Readiness(false, false, false), ConfigMgrConnectionState.Unknown, "", "");
        Assert.Equal("Site not set, server not set, ConfigMgr off, Intune off, WSUS off", none.AutomationName);
    }

    [Fact]
    public void EveryStateHasAGlyphAndEveryGlyphIsOneOfFive()
    {
        var glyphs = Enum.GetValues<SystemState>().Select(ContextStripViewModel.GlyphOf).ToList();
        Assert.Equal(6, glyphs.Count);
        Assert.Equal(5, glyphs.Distinct().Count());
    }

    // The service

    private static AppSettings Settings(bool configMgr = true, bool intune = false, bool wsus = false, string site = "MCM", string host = "cm01.contoso.com") => new()
    {
        SiteCode = site, ProviderMachine = host, FileShareRoot = "\\\\cm01\\share",
        Systems = new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }
    };

    [Fact]
    public void TheServiceReadsTheSettingsAgainAfterTheOperatorSavedThem()
    {
        var current = Settings();
        Action? saved = null;
        var service = new SiteStateService(() => current, () => true, handler => saved = handler);
        var raised = 0;
        service.Changed += () => raised++;
        Assert.Equal(SystemState.Off, service.Current.For("WSUS").State);

        current = Settings(wsus: true);
        saved!();

        Assert.Equal(1, raised);
        Assert.Equal(SystemState.NotConfigured, service.Current.For("WSUS").State);
        saved();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void TheServiceFollowsASiteSwitchAndForgetsTheConnectionTestOfTheOtherSite()
    {
        var current = Settings();
        Action? switched = null;
        var service = new SiteStateService(() => current, () => true, null, handler => switched = handler);
        service.SetConfigMgrConnection(ConfigMgrConnectionState.Connected);
        Assert.Equal(SystemState.Connected, service.Current.For("ConfigMgr").State);

        current = Settings(site: "PS1", host: "ps1.contoso.com");
        switched!();

        Assert.Equal("Site PS1", service.Current.SiteText);
        Assert.Equal("ps1.contoso.com", service.Current.HostText);
        Assert.Equal(SystemState.Ready, service.Current.For("ConfigMgr").State);
    }

    [Fact]
    public void ANewHostDropsTheConnectionTestOfTheOldHost()
    {
        var current = Settings();
        Action? saved = null;
        var service = new SiteStateService(() => current, () => true, handler => saved = handler);
        service.SetConfigMgrConnection(ConfigMgrConnectionState.Connected);

        // Turning a system off and on again keeps the test: the host did not change.
        current = Settings(configMgr: false);
        saved!();
        current = Settings();
        saved();
        Assert.Equal(SystemState.Connected, service.Current.For("ConfigMgr").State);

        current = Settings(host: "cm02.contoso.com");
        saved();
        Assert.Equal("cm02.contoso.com", service.Current.HostText);
        Assert.Equal(SystemState.Ready, service.Current.For("ConfigMgr").State);
        Assert.Equal(ConfigMgrConnectionState.Unknown, service.ConfigMgr);
    }

    [Fact]
    public void TheViewModelOfTheStripFollowsASavedSettingWithoutAWindow()
    {
        var current = Settings();
        Action? saved = null;
        var service = new SiteStateService(() => current, () => true, handler => saved = handler);
        var viewModel = new ContextStripViewModel(service);
        Assert.Equal("WSUS off", viewModel.Systems[2].Text);

        current = Settings(wsus: true);
        saved!();

        Assert.Equal("WSUS not configured", viewModel.Systems[2].Text);
        Assert.Equal("Site MCM, cm01.contoso.com, ConfigMgr enabled, Intune off, WSUS not configured", viewModel.AutomationName);
    }

    [Fact]
    public void TheConnectionTestOfThePackagesPageReachesTheStrip()
    {
        var service = new SiteStateService(() => Settings(), () => true);
        var raised = 0;
        service.Changed += () => raised++;
        service.SetConfigMgrConnection(ConfigMgrConnectionState.Failed);
        service.SetConfigMgrConnection(ConfigMgrConnectionState.Failed);
        Assert.Equal(1, raised);
        Assert.Equal("ConfigMgr connection failed", service.Current.For("ConfigMgr").Text);
    }

    [Fact]
    public void AMissingConsoleMakesConfigMgrNotConfigured()
    {
        var service = new SiteStateService(() => Settings(), () => false);
        Assert.Equal(SystemState.NotConfigured, service.Current.For("ConfigMgr").State);
    }

    // The control

    [Fact]
    public void TheStripHasNoBoxThatEditsTheSite()
    {
        var path = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Controls", "ContextStrip.xaml");
        var xaml = File.ReadAllText(path);
        foreach (var forbidden in new[] { "<ui:TextBox", "<TextBox", "<ComboBox", "<ui:PasswordBox", "<PasswordBox", "<ui:NumberBox", "<ui:AutoSuggestBox" })
            Assert.DoesNotContain(forbidden, xaml);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheStripShowsEverySystemWithAGlyphAndAWordAndHasAReadableName(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var service = new SiteStateService(() => Settings(intune: true), () => true);
            service.SetConfigMgrConnection(ConfigMgrConnectionState.Connected);
            var strip = new ContextStrip { DataContext = new ContextStripViewModel(service), Width = 1100 };
            strip.Measure(new Size(1100, 200));
            strip.Arrange(new Rect(0, 0, 1100, strip.DesiredSize.Height));
            strip.UpdateLayout();

            var texts = RenderKit.Descendants(strip).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Site MCM", texts);
            Assert.Contains("cm01.contoso.com", texts);
            Assert.Contains("ConfigMgr connected", texts);
            Assert.Contains("Intune not configured", texts);
            Assert.Contains("WSUS off", texts);
            Assert.Equal(3, RenderKit.Descendants(strip).OfType<Wpf.Ui.Controls.SymbolIcon>().Count(i => i.Symbol != Wpf.Ui.Controls.SymbolRegular.PlugConnected24));

            // The name of the control is read from the same state.
            var peer = UIElementAutomationPeer.CreatePeerForElement(strip);
            Assert.Equal("Site MCM, cm01.contoso.com, ConfigMgr connected, Intune not configured, WSUS off", peer.GetName());
            Assert.True(strip.ActualHeight >= 40 && strip.ActualHeight <= 64, $"The strip is {strip.ActualHeight} high.");

            Assert.NotNull(RenderKit.ById<Wpf.Ui.Controls.Button>(strip, "btnContextConnections"));
        });
    }
}
