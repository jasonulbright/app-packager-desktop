using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using static ApplicationPackagerEngine.Tests.Wsus.WsusTestSupport;

namespace ApplicationPackagerEngine.Tests.Wsus;

public class WsusSettingsTests
{
    [Fact]
    public void FillsEveryDefaultFromAnEmptyInput()
    {
        var s = new WsusSettings().Normalize();
        Assert.Equal("", s.ServerName);
        Assert.Equal(8530, s.PortNumber);
        Assert.False(s.UseSsl);
        Assert.Null(s.PackageType);
        Assert.Equal("Updates", s.Classification);
        Assert.Equal("", s.ApprovalGroup);
        Assert.False(s.DeclineSuperseded);
    }

    [Fact]
    public void DefaultsThePortTo8531WhenSslIsOnAndNoPortIsGiven()
    {
        Assert.Equal(8531, new WsusSettings { ServerName = "wsus01", UseSsl = true, PortNumber = 0 }.Normalize().PortNumber);
        Assert.Equal(8530, new WsusSettings { ServerName = "wsus01", PortNumber = 70000 }.Normalize().PortNumber);
    }

    [Fact]
    public void KeepsAnExplicitPortAndTrimsTheServerName()
    {
        var s = new WsusSettings { ServerName = " wsus01.contoso.com ", PortNumber = 443, UseSsl = true, ApprovalGroup = " Pilot " }.Normalize();
        Assert.Equal("wsus01.contoso.com", s.ServerName);
        Assert.Equal(443, s.PortNumber);
        Assert.Equal("Pilot", s.ApprovalGroup);
    }

    [Fact]
    public void RefusesAServerNameThatIsNotAHostName()
    {
        Assert.Contains("not a host name", Assert.Throws<WsusSettingsException>(() => new WsusSettings { ServerName = "wsus01;calc" }.Normalize()).Message);
    }

    [Fact]
    public void RefusesAnUnknownClassification()
    {
        Assert.Contains("classification", Assert.Throws<WsusSettingsException>(() => new WsusSettings { Classification = "Drivers" }.Normalize()).Message);
    }

    [Fact]
    public void RefusesTheRetiredPackageTypeApplicationAndAnyTypeOtherThanUpdate()
    {
        var application = Assert.Throws<WsusSettingsException>(() => new WsusSettings { PackageType = "Application" }.Normalize()).Message;
        Assert.Contains("installed products only", application);
        Assert.EndsWith("Remove the PackageType setting.", application);
        Assert.Contains("package type", Assert.Throws<WsusSettingsException>(() => new WsusSettings { PackageType = "Bundle" }.Normalize()).Message);
        new WsusSettings { PackageType = "Update" }.Normalize();
    }

    [Fact]
    public void AStoredFileKeepsItsValidValuesAndFallsBackForTheRest()
    {
        var stored = WsusSettings.FromStored(new WsusSettings
        {
            ServerName = " wsus01.contoso.com ", PortNumber = 70000, UseSsl = true, Classification = "Drivers",
            ApprovalGroup = " Pilot ", DeclineSuperseded = true, PackageType = "Application"
        });
        Assert.Equal("wsus01.contoso.com", stored.ServerName);
        Assert.Equal(8530, stored.PortNumber);
        Assert.True(stored.UseSsl);
        Assert.Equal("Updates", stored.Classification);
        Assert.Equal("Pilot", stored.ApprovalGroup);
        Assert.True(stored.DeclineSuperseded);
        Assert.Null(stored.PackageType);
        Assert.Equal("SecurityUpdates", WsusSettings.FromStored(new WsusSettings { Classification = "SecurityUpdates" }).Classification);
        Assert.Equal(8531, WsusSettings.FromStored(new WsusSettings { PortNumber = 8531 }).PortNumber);
        Assert.Equal(new WsusSettings().PortNumber, WsusSettings.FromStored(null).PortNumber);
    }

    [Fact]
    public void OffersEveryClassification()
    {
        Assert.Equal(["Updates", "SecurityUpdates", "CriticalUpdates", "FeaturePacks", "ServicePacks", "Tools", "UpdateRollups"], WsusClassifications.Names);
    }
}

public class SystemsInUseTests
{
    [Fact]
    public void AFileWithoutTheSettingInfersIntuneFromATenantAndWsusFromAServer()
    {
        var none = SystemsInUse.Resolve(null, "", "");
        Assert.True(none.ConfigMgr);
        Assert.False(none.Intune);
        Assert.False(none.Wsus);
        var both = SystemsInUse.Resolve(null, "tenant-guid", "wsus01");
        Assert.True(both.ConfigMgr);
        Assert.True(both.Intune);
        Assert.True(both.Wsus);
        Assert.False(SystemsInUse.Resolve(null, "  ", null).Intune);
    }

    [Fact]
    public void AStoredSettingWinsOverTheInference()
    {
        var stored = SystemsInUse.Resolve(new SystemsInUse { ConfigMgr = false, Intune = false, Wsus = true }, "tenant-guid", "");
        Assert.False(stored.ConfigMgr);
        Assert.False(stored.Intune);
        Assert.True(stored.Wsus);
    }
}

public class WsusIdentityTests
{
    [Fact]
    public void RoundTripsTheIdentityAndVersionThroughAnUpdateDescription()
    {
        var tag = WsusIdentity.IdentityTag(Manifest());
        Assert.Equal("AppPackager:catalog:package-contosotool/default", tag);
        var description = "Contoso Tool 5.4.2\r\n\r\n" + WsusIdentity.IdentityLine(tag, "Update", "2026.08.2-200") + "\r\n";
        var parsed = WsusIdentity.ParseIdentity(description)!;
        Assert.Equal(tag, parsed.IdentityTag);
        Assert.Equal("Update", parsed.PackageType);
        Assert.Equal("2026.08.2-200", parsed.Version);
    }

    [Fact]
    public void ReadsAnIdentityLineThatCarriesNoVersion()
    {
        var parsed = WsusIdentity.ParseIdentity("x\r\n" + WsusIdentity.IdentityLine("AppPackager:catalog:package-a/default", "Application"))!;
        Assert.Equal("Application", parsed.PackageType);
        Assert.Equal("", parsed.Version);
    }

    [Fact]
    public void DerivesALegacyKeyFromThePublisherAndTheTitleForAManifestWithoutWorkbenchIds()
    {
        Assert.Equal("AppPackager:legacy:Contoso:Contoso-Tool-5.4/default",
            WsusIdentity.IdentityTag(Manifest(m => { m.ApplicationId = ""; m.ProfileId = ""; })));
    }

    [Fact]
    public void KeepsTwoPublishersApartWhenTheirLegacyTitlesMatch()
    {
        var a = WsusIdentity.IdentityTag(Manifest(m => { m.ApplicationId = ""; m.AppName = "Contoso Helper"; m.Publisher = "Acme"; m.SoftwareVersion = "1.0"; }));
        var b = WsusIdentity.IdentityTag(Manifest(m => { m.ApplicationId = ""; m.AppName = "Contoso Helper"; m.Publisher = "Northwind"; m.SoftwareVersion = "1.0"; }));
        Assert.NotEqual(a, b);
        Assert.Equal(a, WsusIdentity.ParseIdentity(WsusIdentity.IdentityLine(a, "Update", "1.0"))!.IdentityTag);
    }

    [Fact]
    public void KeepsOneLegacyIdentityAcrossTheReleasesOfOneMajorWhenTheTitleCarriesTheVersion()
    {
        var a = WsusIdentity.IdentityTag(Manifest(m => { m.ApplicationId = ""; m.AppName = "7-Zip 26.03 (x64 edition)"; m.SoftwareVersion = "26.03"; }));
        var b = WsusIdentity.IdentityTag(Manifest(m => { m.ApplicationId = ""; m.AppName = "7-Zip 26.04 (x64 edition)"; m.SoftwareVersion = "26.04"; }));
        Assert.Equal("AppPackager:legacy:Contoso:7-Zip-26-x64-edition/default", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void KeepsSideBySideMajorsApartWhenTheTitleDiffersOnlyByTheVersion()
    {
        string Tag(string name, string version) => WsusIdentity.IdentityTag(Manifest(m =>
        {
            m.ApplicationId = ""; m.AppName = name; m.Publisher = "Microsoft Corporation"; m.SoftwareVersion = version;
        }));
        var net8 = Tag("Microsoft .NET 8.0.31 - Windows Server Hosting", "8.0.31");
        Assert.Equal("AppPackager:legacy:Microsoft-Corporation:Microsoft-.NET-8-Windows-Server-Hosting/default", net8);
        Assert.Equal(net8, Tag("Microsoft .NET 8.0.30 - Windows Server Hosting", "8.0.30"));
        Assert.NotEqual(net8, Tag("Microsoft .NET 10.0.12 - Windows Server Hosting", "10.0.12"));
    }

    [Fact]
    public void ReturnsNothingForADescriptionWithoutAnIdentityLine()
    {
        Assert.Null(WsusIdentity.ParseIdentity("Published by another tool"));
        Assert.Null(WsusIdentity.ParseIdentity(""));
    }

    [Fact]
    public void ExtractsBareIdsAndCatalogLinksOnceEachInInputOrder()
    {
        const string text = "12345678-90ab-cdef-1234-567890abcdef\r\n" +
                            "https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=0e3c9a1b-2f4d-4c5e-8a6b-7d8e9f0a1b2c\r\n" +
                            "12345678-90AB-CDEF-1234-567890ABCDEF, 0e3c9a1b-2f4d-4c5e-8a6b-7d8e9f0a1b2c";
        Assert.Equal([Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"), Guid.Parse("0e3c9a1b-2f4d-4c5e-8a6b-7d8e9f0a1b2c")], WsusIdentity.CatalogIds(text));
    }

    [Fact]
    public void ReturnsNothingForTextWithoutAnUpdateId()
    {
        Assert.Empty(WsusIdentity.CatalogIds("KB5034441"));
        Assert.Empty(WsusIdentity.CatalogIds(""));
    }

    [Theory]
    [InlineData("5.4.2", "5.4.3", -1)]
    [InlineData("5.10", "5.9", 1)]
    [InlineData("5.4", "5.4.0", 0)]
    [InlineData("2026.08.2-200", "2026.08.2-199", 1)]
    [InlineData("24.08", "24.8", 0)]
    [InlineData("1.0.100000000000000000000", "1.0.99999999999999999999", 1)]
    public void OrdersVersionsByTheirNumericRuns(string left, string right, int expected)
    {
        Assert.Equal(expected, WsusIdentity.CompareVersion(left, right));
    }

    [Fact]
    public void CannotCompareAVersionWithoutDigits()
    {
        Assert.Null(WsusIdentity.CompareVersion("latest", "5.4"));
        Assert.Null(WsusIdentity.CompareVersion("", "5.4"));
    }

    [Fact]
    public void KeepsALongTitleUnder80CharactersWithTheVersionAtTheEnd()
    {
        var title = WsusIdentity.Title(Manifest(m => m.AppName = "Contoso Enterprise Remote Management and Monitoring Agent for Windows Workstations (x64)"));
        Assert.True(title.Length < 80);
        Assert.EndsWith("... 5.4.2", title);
        Assert.Equal("Contoso Tool 5.4 5.4.2", WsusIdentity.Title(Manifest()));
        Assert.Equal("Contoso Tool 5.4.2", WsusIdentity.Title(Manifest(m => m.AppName = "Contoso Tool 5.4.2")));
    }

    [Fact]
    public void NamesTheVersionOnceInTheDescriptionWhenTheTitleAlreadyCarriesIt()
    {
        var description = WsusIdentity.Description(Manifest(m => { m.AppName = "Contoso Tool 5.4.2"; m.BuildId = "b1"; }));
        Assert.StartsWith("Contoso Tool 5.4.2 from Contoso, published by AppPackager from build b1.\r\n\r\n", description);
        Assert.EndsWith("; type Update; version 5.4.2", description);
    }

    [Fact]
    public void KeepsPropertyAssignmentsAndDropsMsiexecSwitchesAndLogFiles()
    {
        Assert.Equal("TRANSFORMS=custom.mst ALLUSERS=1 PROP=\"a b\"",
            WsusMsiCommandLine.Convert("/qn TRANSFORMS=custom.mst /l*v \"C:\\log dir\\x.log\" ALLUSERS=1 PROP=\"a b\""));
    }

    [Fact]
    public void TurnsNorestartIntoRebootReallySuppressOnce()
    {
        Assert.Equal("REBOOT=ReallySuppress", WsusMsiCommandLine.Convert("/qn /norestart"));
    }

    [Fact]
    public void KeepsAnExplicitRebootPropertyInsteadOfAddingOne()
    {
        Assert.Equal("REBOOT=Force", WsusMsiCommandLine.Convert("/qn /norestart REBOOT=Force"));
    }

    [Fact]
    public void ReturnsAnEmptyStringForSwitchesOnly()
    {
        Assert.Equal("", WsusMsiCommandLine.Convert("/qn"));
    }

    [Theory]
    [InlineData("5.4.2", "5.4.2.0")]
    [InlineData(" 24.08 ", "24.8.0.0")]
    [InlineData("65535.0", "65535.0.0.0")]
    [InlineData("65536", null)]
    [InlineData("1.2.3.4.5", null)]
    [InlineData("2026.08.2-200", null)]
    [InlineData("", null)]
    public void PadsAVersionToFourNumericParts(string version, string? expected)
    {
        Assert.Equal(expected, WsusIdentity.VersionString(version));
    }
}
