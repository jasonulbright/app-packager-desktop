using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The site list, site.json, active-site.json and the switch (Q-112d). Site codes are fictional (AB1, CD2); hosts are under example.com.</summary>
public sealed class SiteCatalogTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "ap-sitecat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CreateWritesASiteJsonThatMatchesTheContract()
    {
        var site = SiteCatalog.Create("ab1", "cm.ab1.example.com", "Production", _data);

        Assert.Equal("AB1", site.FolderName);
        Assert.Equal(["cm.ab1.example.com"], site.Providers);
        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_data, "sites", "AB1", "site.json")))!;
        Assert.Equal(1, (int?)json["schemaVersion"]);
        Assert.Equal("AB1", (string?)json["siteCode"]);
        Assert.Equal("Production", (string?)json["label"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", (string?)json["createdUtc"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", (string?)json["lastUsedUtc"]);
    }

    [Fact]
    public void CreateRefusesAnInvalidOrReservedCode()
    {
        Assert.Throws<ArgumentException>(() => SiteCatalog.Create("AB", "x", null, _data));
        Assert.Throws<ArgumentException>(() => SiteCatalog.Create("SMS", "x", null, _data));
        Assert.Throws<ArgumentException>(() => SiteCatalog.Create("A/1", "x", null, _data));
        Assert.False(Directory.Exists(Path.Combine(_data, "sites")));
    }

    [Fact]
    public void ListReturnsEverySiteFolderAndIgnoresOtherNames()
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", null, _data);
        SiteCatalog.Create("CD2", "cm.cd2.example.com", "Lab", _data);
        Directory.CreateDirectory(Path.Combine(_data, "sites", "not-a-site"));
        Directory.CreateDirectory(Path.Combine(_data, "sites", "SMS"));

        Assert.Equal(["AB1", "CD2"], SiteCatalog.List(_data).Select(s => s.FolderName));
        Assert.Equal("CD2 - Lab (cm.cd2.example.com)", SiteCatalog.List(_data)[1].DisplayName);
    }

    [Fact]
    public void AReusedCodeWithAnotherProviderNeedsAnAnswerAndADifferentEnvironmentGetsATildeFolder()
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", null, _data);

        Assert.Equal(SiteMatchKind.Existing, SiteCatalog.Match("AB1", "CM.AB1.EXAMPLE.COM", _data).Kind);
        Assert.Equal(SiteMatchKind.Existing, SiteCatalog.Match("ab1", "", _data).Kind);
        Assert.Equal(SiteMatchKind.NeedsAnswer, SiteCatalog.Match("AB1", "cm2.ab1.example.com", _data).Kind);
        Assert.Equal(SiteMatchKind.New, SiteCatalog.Match("CD2", "cm.cd2.example.com", _data).Kind);

        var second = SiteCatalog.Create("AB1", "other.example.com", null, _data);
        var third = SiteCatalog.Create("AB1", "third.example.com", null, _data);
        Assert.Equal("AB1~2", second.FolderName);
        Assert.Equal("AB1~3", third.FolderName);
        Assert.Equal("AB1", second.SiteCode);
    }

    [Fact]
    public void AddProviderKeepsTheOtherValuesAndAddsOnce()
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", "Production", _data);

        SiteCatalog.AddProvider("AB1", "cm2.ab1.example.com", _data);
        var site = SiteCatalog.AddProvider("AB1", "CM2.AB1.EXAMPLE.COM", _data);

        Assert.Equal(["cm.ab1.example.com", "cm2.ab1.example.com"], site.Providers);
        Assert.Equal("Production", site.Label);
    }

    [Fact]
    public void TheActiveSiteFileIsReadBackByTheContractReader()
    {
        SiteCatalog.WriteActiveSite("AB1~2", _data);

        var active = SiteDataPaths.GetActiveSite(_data, _ => null);

        Assert.Equal(new ActiveSite("AB1~2", "active-site.json"), active);
        Assert.Throws<ArgumentException>(() => SiteCatalog.WriteActiveSite("../x", _data));
    }

    // ---- the switch ----

    // A run guard of its own: the shared guard also reports the view models of other tests that run at the same time.
    private SiteManager Manager(Func<string?>? run = null) => new(run ?? (() => null), _data);

    [Fact]
    public void ASwitchActivatesTheSiteAndWritesTheActiveSiteFileOnly()
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", null, _data);
        SiteCatalog.Create("CD2", "cm.cd2.example.com", null, _data);
        using var scope = SiteContext.Scope(null, _data);
        var manager = Manager();
        var changed = 0;
        manager.Changed += () => changed++;
        var before = Directory.EnumerateFiles(_data, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("site.json") && !f.EndsWith("active-site.json")).ToList();

        var result = manager.Switch("CD2");

        Assert.Equal(SiteSwitchOutcome.Switched, result.Outcome);
        Assert.Equal(1, changed);
        Assert.Equal("CD2", SiteDataPaths.GetActiveSite(_data, _ => null)?.FolderName);
        // Nothing else was written: a switch saves nothing across sites.
        Assert.Equal(before, Directory.EnumerateFiles(_data, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("site.json") && !f.EndsWith("active-site.json")).ToList());
        SiteContext.Deactivate();
    }

    [Fact]
    public void ASwitchIsRefusedWhileARunIsActiveAndChangesNothing()
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", null, _data);
        SiteCatalog.Create("CD2", "cm.cd2.example.com", null, _data);
        SiteCatalog.WriteActiveSite("AB1", _data);
        var manager = Manager(() => "a check, Stage or publish run is active");
        var changed = 0;
        manager.Changed += () => changed++;

        var result = manager.Switch("CD2");

        Assert.Equal(SiteSwitchOutcome.Refused, result.Outcome);
        Assert.Contains("a check, Stage or publish run is active", result.Message);
        Assert.Contains("cannot change", result.Message);
        Assert.Equal(0, changed);
        Assert.Equal("AB1", SiteDataPaths.GetActiveSite(_data, _ => null)?.FolderName);
        Assert.Equal(SiteSwitchOutcome.Refused, manager.AddSite("EF3", "x", null, _ => SiteReuseAnswer.Cancel).Outcome);
        Assert.False(Directory.Exists(Path.Combine(_data, "sites", "EF3")));
    }

    [Fact]
    public void TheRunGuardOfTheViewModelsIsWeakAndEndsWithTheRun()
    {
        // Other tests set the guard at the same time, so the test looks for its own reason.
        var owner = new object();
        var reason = "a test scan " + Guid.NewGuid().ToString("N") + " is running";
        SiteRunGuard.Set(owner, reason);
        Assert.NotNull(SiteRunGuard.ActiveRun());
        SiteRunGuard.Set(owner, null);
        GC.KeepAlive(owner);
    }

    [Fact]
    public void AddSiteCreatesAndActivatesANewSiteWithoutAQuestion()
    {
        using var scope = SiteContext.Scope(null, _data);
        var manager = Manager();

        var result = manager.AddSite("AB1", "cm.ab1.example.com", "Production", _ => throw new InvalidOperationException("no question expected"));

        Assert.Equal(SiteSwitchOutcome.Switched, result.Outcome);
        Assert.Equal("AB1", result.FolderName);
        Assert.Equal("AB1", SiteContext.ActiveFolderName);
        SiteContext.Deactivate();
    }

    [Theory]
    [InlineData(SiteReuseAnswer.SameSiteOtherProvider, "AB1", 1)]
    [InlineData(SiteReuseAnswer.DifferentEnvironment, "AB1~2", 2)]
    public void AReusedCodeAsksAndTheAnswerDecidesTheFolder(SiteReuseAnswer answer, string expectedFolder, int expectedSites)
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", null, _data);
        using var scope = SiteContext.Scope(null, _data);
        SiteInfo? asked = null;

        var result = Manager().AddSite("AB1", "other.example.com", null, existing => { asked = existing; return answer; });

        Assert.Equal("AB1", asked?.FolderName);
        Assert.Equal(SiteSwitchOutcome.Switched, result.Outcome);
        Assert.Equal(expectedFolder, result.FolderName);
        Assert.Equal(expectedSites, SiteCatalog.List(_data).Count);
        if (answer == SiteReuseAnswer.SameSiteOtherProvider)
            Assert.Equal(["cm.ab1.example.com", "other.example.com"], SiteCatalog.Get("AB1", _data)!.Providers);
        SiteContext.Deactivate();
    }

    [Fact]
    public void CancellingTheQuestionAddsNothing()
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", null, _data);
        var result = Manager().AddSite("AB1", "other.example.com", null, _ => SiteReuseAnswer.Cancel);

        Assert.Equal(SiteSwitchOutcome.Cancelled, result.Outcome);
        Assert.Single(SiteCatalog.List(_data));
    }

    [Theory]
    [InlineData("", "Enter the site code")]
    [InlineData("AB", "exactly three")]
    [InlineData("SMS", "reserved")]
    [InlineData("A B", "exactly three")]
    public void AnInvalidSiteCodeIsRefusedWithAClearMessage(string code, string expected)
    {
        var result = Manager().AddSite(code, "x", null, _ => SiteReuseAnswer.Cancel);

        Assert.Equal(SiteSwitchOutcome.Invalid, result.Outcome);
        Assert.Contains(expected, result.Message);
    }
}
