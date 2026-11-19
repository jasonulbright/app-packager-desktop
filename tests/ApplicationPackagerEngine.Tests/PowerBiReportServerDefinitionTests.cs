using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Power BI Desktop (optimized for Power BI Report Server): the version comes from the change log of Power BI Report
/// Server on Microsoft Learn, the installer from the Download Center page of Power BI Report Server, the staged installer
/// must report the version of the change log, and the Add or Remove Programs entry of the bundle is the detection.
/// The definition is separate from Power BI Desktop (powerbidesktop.json): both can be installed on one computer.
/// </summary>
public sealed class PowerBiReportServerDefinitionTests : IDisposable
{
    private const string Id = "powerbidesktopreportserver.json";
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-pbirs-" + Guid.NewGuid().ToString("N"));

    public PowerBiReportServerDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string ChangeLog = """
        <h3 id="power-bi-report-server">Power BI Report Server</h3>
        <ul><li><p><em>Version: 1.27.9818.31285 (build 15.0.1122.106), Released: TBD</em></p></li></ul>
        <h3 id="power-bi-desktop-optimized-for-power-bi-report-server">Power BI Desktop (optimized for Power BI Report Server)</h3>
        <ul>
        <li><p><em>Version: 2.158.1602.0 (September 2026), Released: October 26, 2026</em></p></li>
        <li><p><em>Version: 2.158.1177.0 (September 2026), Released: September 29, 2026</em></p></li>
        </ul>
        <h2 id="may-2026">May 2026</h2>
        <h3 id="power-bi-desktop-optimized-for-power-bi-report-server-1">Power BI Desktop (optimized for Power BI Report Server)</h3>
        <ul><li><p><em>Version: 2.154.956.0 (May 2026), Released: May 21, 2026</em></p></li></ul>
        """;

    private const string DownloadPage =
        """{"downloadFile":[{"isPrimary":"False","name":"PBIDesktopSetupRS_x64.exe","url":"https://download.microsoft.com/download/2/d/a/2da81ea3/PBIDesktopSetupRS_x64.exe","size":"701293560"},{"name":"PowerBIReportServer.exe","url":"https://download.microsoft.com/download/2/d/a/2da81ea3/PowerBIReportServer.exe"}]}""";

    [Fact]
    public void TheVersionHopTakesTheNewestDesktopVersionAndNotTheReportServerVersion()
    {
        var hop = DefinitionFixture.Load(Id).VersionScraper!.Hops![0];
        var match = Regex.Match(ChangeLog, hop.Pattern);
        Assert.True(match.Success);
        Assert.Equal("2.158.1602.0", match.Groups["version"].Value);
    }

    [Fact]
    public void TheVersionHopNeverTakesTheReportServerVersionWhenTheDesktopListIsEmpty()
    {
        var hop = DefinitionFixture.Load(Id).VersionScraper!.Hops![0];
        const string page = "<h3>Power BI Desktop (optimized for Power BI Report Server)</h3><p>No release.</p><h3>Power BI Report Server</h3><ul><li><p><em>Version: 1.27.9818.31285 (build 15.0.1122.106)</em></p></li></ul>";
        Assert.False(Regex.Match(page, hop.Pattern).Success);
    }
    [Fact]
    public void TheDownloadHopTakesTheDesktopInstallerAndNotTheReportServerSetup()
    {
        var def = DefinitionFixture.Load(Id);
        var hop = def.VersionScraper!.Hops![1];
        Assert.Equal("https://www.microsoft.com/en-us/download/details.aspx?id=105945", hop.Url);
        var match = Regex.Match(DownloadPage, hop.Pattern);
        Assert.True(match.Success);
        Assert.EndsWith("/PBIDesktopSetupRS_x64.exe", match.Groups["url"].Value);
        Assert.Equal("CHAINED", def.VersionScraper.DownloadUrlQuery);
        Assert.Equal("ChainedHttpRegex", def.VersionScraper.Type);
        Assert.True(string.IsNullOrEmpty(def.DownloadUrl));
    }

    [Fact]
    public void TheBundleEntryInTheThirtyTwoBitViewIsTheDetectionAndTheKeyComesFromTheInstallerHeader()
    {
        var def = DefinitionFixture.Load(Id);
        var path = Path.Combine(_dir, "PBIDesktopSetupRS_x64.exe");
        var id = Guid.Parse("72B00526-F00D-4CB9-A561-7E8F4806A9BE");
        File.WriteAllBytes(path, ArpDetectionKeyAssertionTests.BurnBundle(id));

        var tokens = StagingService.BuildStageTokens(def, path, "2.158.1602.0", null, new Dictionary<string, string>());
        var det = StagingService.BuildDetection(def, null, "2.158.1602.0", tokens: tokens);

        Assert.Equal("RegistryKeyValue", det.Type);
        Assert.Equal(Uninstall + "{72B00526-F00D-4CB9-A561-7E8F4806A9BE}", det.RegistryKeyRelative, ignoreCase: true);
        Assert.Equal("DisplayVersion", det.ValueName);
        Assert.Equal("GreaterEquals", det.Operator);
        Assert.Equal("2.158.1602.0", det.ExpectedValue);
        Assert.False(det.Is64Bit);
    }

    [Fact]
    public void StagingStopsWhenTheInstallerReportsAnotherVersionThanTheChangeLog()
    {
        var def = DefinitionFixture.Load(Id);
        Assert.Equal("{version}", def.ExpectedPayloadVersion);
        var path = Path.Combine(_dir, "PBIDesktopSetupRS_x64.exe");
        File.WriteAllBytes(path, ArpDetectionKeyAssertionTests.BurnBundle(Guid.NewGuid()));

        var ex = Assert.Throws<InvalidOperationException>(() => StagingService.VerifyPayloadVersion(def, path, null, false, "2.158.1602.0"));
        Assert.Contains("2.158.1602.0", ex.Message);

        // An installer that reports the version passes: the framework library of this test run stands in for it.
        var stand = typeof(object).Assembly.Location;
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(stand).FileVersion!;
        StagingService.VerifyPayloadVersion(def, stand, null, false, version);
    }

    [Fact]
    public void ItNeedsAMicrosoftSignatureBecauseTheVendorPublishesNoDigest()
    {
        var def = DefinitionFixture.Load(Id);
        Assert.True(def.RequireValidSignature);
        Assert.False(def.RequirePayloadHash);
        Assert.True(string.IsNullOrEmpty(def.PayloadSha256Url));
        Assert.Equal("-quiet -norestart ACCEPT_EULA=1 DISABLE_UPDATE_NOTIFICATION=1", def.InstallArgs);
        Assert.Equal("-uninstall -quiet -norestart", def.UninstallArgs);
    }

    [Fact]
    public void ItHasNoCpeBecauseNvdHasNoEntryForThisEdition()
    {
        var def = DefinitionFixture.Load(Id);
        Assert.True(string.IsNullOrEmpty(def.Cpe));
        Assert.Contains(def.StageNotes!, n => n.Contains("NVD has no CPE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Microsoft Power BI Desktop (September 2026) (x64)", true)]
    [InlineData("Microsoft Power BI Desktop (May 2027) (x64)", true)]
    [InlineData("Microsoft Power BI Desktop (x64)", false)]
    [InlineData("Microsoft PowerBI Desktop (x64)", false)]
    [InlineData("Microsoft PowerBI Desktop (x64) (September 2026)", false)]
    public void TheAddOrRemoveProgramsPatternMatchesThisEditionOnly(string name, bool expected)
    {
        var def = DefinitionFixture.Load(Id);
        Assert.Equal("x64", def.ArpArch);
        Assert.True(ArpWildcard.TryCreate(def.ArpName!, out var regex));
        Assert.Equal(expected, regex!.IsMatch(name));
    }

    [Fact]
    public void ItIsAnotherDefinitionThanPowerBiDesktopWithItsOwnIdAndName()
    {
        var other = DefinitionFixture.Load("powerbidesktop.json");
        var def = DefinitionFixture.Load(Id);
        Assert.NotEqual(other.Id, def.Id);
        Assert.NotEqual(other.CMName, def.CMName);
        Assert.NotEqual(other.ArpName, def.ArpName);
        Assert.Equal("EXE", def.InstallerType);
        Assert.Equal("WixBurn", def.InstallerFramework);
        Assert.Equal("External", def.IconSource);
        Assert.Contains("PBIDesktop", def.RunningProcess!);
    }

    [Theory]
    [InlineData("Microsoft PowerBI Desktop (x64)", true)]
    [InlineData("Microsoft PowerBI Desktop (x64) (September 2026)", false)]
    [InlineData("Microsoft Power BI Desktop (September 2026) (x64)", false)]
    public void PowerBiDesktopKeepsToItsOwnAddOrRemoveProgramsEntry(string name, bool expected)
    {
        // The entries below were read on a lab computer with both editions installed.
        var def = DefinitionFixture.Load("powerbidesktop.json");
        Assert.True(ArpWildcard.TryCreate(def.ArpName!, out var regex));
        Assert.Equal(expected, regex!.IsMatch(name));
    }

    [Theory]
    [InlineData("Microsoft Power BI Desktop (x64)", true)]
    [InlineData("Microsoft Power BI Desktop (September 2026) (x64)", false)]
    public void ThePowerBiDesktopUninstallCommandLeavesTheReportServerEditionAlone(string displayName, bool uninstalled)
    {
        // With both editions installed the filter found two Windows Installer entries and the command passed both product codes to msiexec.
        var command = DefinitionFixture.Load("powerbidesktop.json").UninstallCommand!;
        var like = Regex.Match(command, "DisplayName -like \"(?<p>[^\"]+)\"");
        var notMatch = Regex.Match(command, "DisplayName -notmatch \"(?<p>[^\"]+)\"");
        Assert.True(like.Success && notMatch.Success, command);
        var selected = ArpWildcard.TryCreate(like.Groups["p"].Value, out var wildcard) && wildcard!.IsMatch(displayName)
                       && !Regex.IsMatch(displayName, notMatch.Groups["p"].Value);
        Assert.Equal(uninstalled, selected);
    }
}
