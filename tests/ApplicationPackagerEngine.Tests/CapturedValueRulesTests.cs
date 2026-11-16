using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A captured value goes to another host only as a short version (validation rules 7 and 8 of the vendor import).</summary>
public sealed class CapturedValueRulesTests
{
    private static AppDefinition Def(string sourceUrl, string downloadUrl)
    {
        var d = new AppDefinition
        {
            Id = "contoso-agent", Vendor = "Contoso", Application = "Contoso Agent", CMName = "Contoso Agent", Architecture = "x64",
            VendorUrl = "https://contoso.example/", InstallerType = "MSI", DownloadUrl = downloadUrl, InstallArgs = "/qn",
            Detection = new DetectionConfig { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\Contoso" }
        };
        d.VersionScraper.Url = sourceUrl;
        return d;
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3-beta1")]
    [InlineData("2026.11.0+build5")]
    public void AShortVersionIsAccepted(string value) => Assert.True(CapturedValueRules.IsShortVersion(value));

    [Theory]
    [InlineData("")]
    [InlineData("v1.2")]
    [InlineData("1.2/../../x")]
    [InlineData("1.2?x=y")]
    [InlineData("evil.example/1.2")]
    [InlineData("1.2.3-abcdefghijklmnopq")]
    [InlineData("1.2\n")]
    public void AnythingElseIsNotAShortVersion(string value) => Assert.False(CapturedValueRules.IsShortVersion(value));

    [Fact]
    public void AVersionFromOneHostAndADownloadOnAnotherKeepsWorkingAndAnyOtherCapturedValueIsFound()
    {
        // A version from the GitHub API and a download from github.com.
        var api = Def("https://api.github.com/repos/contoso/agent/releases/latest", "https://github.com/contoso/agent/releases/download/v{version}/agent.msi");
        Assert.Empty(CapturedValueRules.CrossHostCaptures(api));
        Assert.True(CapturedValueRules.VersionCrossesHosts(api));

        var capture = Def("https://api.github.com/repos/contoso/agent/releases/latest", "https://downloads.example/{capture1}/agent.msi");
        Assert.Equal("{capture1}", Assert.Single(CapturedValueRules.CrossHostCaptures(capture)).Placeholder);

        var sameHost = Def("https://contoso.example/releases", "https://contoso.example/{capture1}/agent.msi");
        Assert.Empty(CapturedValueRules.CrossHostCaptures(sameHost));
        Assert.False(CapturedValueRules.VersionCrossesHosts(sameHost));
    }

    [Fact]
    public void ADefinitionThatPutsACaptureOnAnotherHostIsBlockedAndTheOtherDefinitionsImport()
    {
        string Json(AppDefinition d) => JsonSerializer.Serialize(d);
        var bad = Def("https://api.contoso.example/latest", "https://cdn.example/{capture1}/agent.msi");
        var good = Def("https://api.contoso.example/latest", "https://cdn.example/agent-{version}.msi");
        good.Id = "contoso-good";
        good.Application = "Contoso Good";
        good.CMName = "Contoso Good";
        var document = "{\"FeedFormat\":1,\"Vendor\":\"V\",\"FeedId\":\"vendor-feed\",\"Updated\":\"2026-11-12T08:00:00Z\",\"Definitions\":[" + Json(bad) + "," + Json(good) + "]}";

        var feed = FeedValidator.Validate(Encoding.UTF8.GetBytes(document));

        Assert.Contains(feed.Definitions.Single(d => d.Id == "contoso-agent").Findings, f => f.Code == "CapturedValueCrossHost");
        Assert.DoesNotContain(feed.Definitions.Single(d => d.Id == "contoso-good").Findings, f => f.Code == "CapturedValueCrossHost");
    }
}
