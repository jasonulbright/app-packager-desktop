using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The split of settings.json into the global file and the site files (Q-112d): the settings store of the per-site layout.</summary>
public sealed class SiteSettingsSplitTests
{
    private const string Full = """
    {
      "CompanyName": "Example Corp",
      "Theme": "Dark",
      "GitHubTokenProtected": "FAKE_PROTECTED_TOKEN",
      "SiteCode": "AB1",
      "SiteCodeSaved": true,
      "ProviderMachine": "cm.ab1.example.com",
      "FileShareRoot": "\\\\files.ab1.example.com\\content$",
      "DiscoverySqlServer": "sql.ab1.example.com",
      "DiscoverySqlDatabase": "CM_AB1",
      "ContentDistribution": { "DistributionPointGroup": "AB1 DPs" },
      "Wsus": { "ServerName": "wsus.ab1.example.com" },
      "Intune": { "TenantId": "tenant-one", "ClientSecretProtected": "FAKE_PROTECTED_SECRET" },
      "OneClick": { "Cadence": "Weekly", "Destinations": { "7zip": { "ConfigMgr": true } }, "DefaultDestinations": { "ConfigMgr": true } },
      "InstallerSettingValues": { "7zip": { "Silent": "/S" } },
      "AppStates": { "7zip": { "LatestVersion": "24.09", "CurrentVersion": "24.08", "Published": { "ConfigMgr": { "Version": "24.08" } } } }
    }
    """;

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void ASaveSendsTheSiteKeysToTheSiteFileAndTheRestToTheGlobalFile()
    {
        var split = SiteSettingsSplit.Split(Parse(Full), null, null);

        Assert.Equal("sql.ab1.example.com", (string?)split.Site["DiscoverySqlServer"]);
        Assert.Equal("AB1 DPs", (string?)split.Site["ContentDistribution"]!["DistributionPointGroup"]);
        Assert.NotNull(split.Site["OneClick"]!["Destinations"]);
        Assert.Null(split.Site["Theme"]);
        Assert.Null(split.Site["InstallerSettingValues"]);

        Assert.Equal("Dark", (string?)split.Global["Theme"]);
        Assert.NotNull(split.Global["InstallerSettingValues"]);
        Assert.Null(split.Global["DiscoverySqlServer"]);
        Assert.Null(split.Global["ContentDistribution"]);
        Assert.Null(split.Global["OneClick"]!["Destinations"]);
        Assert.Equal("Weekly", (string?)split.Global["OneClick"]!["Cadence"]);
    }

    [Fact]
    public void TheAppStateSplitsIntoTheSiteFileAndTheGlobalLatestVersion()
    {
        var split = SiteSettingsSplit.Split(Parse(Full), null, null);

        Assert.Equal("24.08", (string?)split.AppState!["7zip"]!["CurrentVersion"]);
        Assert.NotNull(split.AppState["7zip"]!["Published"]);
        Assert.Null(split.AppState["7zip"]!["LatestVersion"]);
        Assert.Equal("24.09", (string?)split.Global["AppStates"]!["7zip"]!["LatestVersion"]);
        Assert.Null(split.Global["AppStates"]!["7zip"]!["CurrentVersion"]);
    }

    [Fact]
    public void SecretsAndTokensStayGlobalAndNeverReachASiteFile()
    {
        var split = SiteSettingsSplit.Split(Parse(Full), null, null);

        Assert.Equal("FAKE_PROTECTED_TOKEN", (string?)split.Global["GitHubTokenProtected"]);
        Assert.Equal("FAKE_PROTECTED_SECRET", (string?)split.Global["Intune"]!["ClientSecretProtected"]);
        var site = split.Site.ToJsonString() + (split.AppState?.ToJsonString() ?? "");
        Assert.DoesNotContain("FAKE_PROTECTED", site);
        Assert.DoesNotMatch(new Regex("token|secret|password|apikey|credential|protected", RegexOptions.IgnoreCase), site);
    }

    [Fact]
    public void NoSiteKeyNameLooksLikeASecret()
    {
        var secret = new Regex("token|secret|password|passwd|apikey|api_key|credential|protected|webhook", RegexOptions.IgnoreCase);
        foreach (var key in SiteSettingsSplit.SiteKeys.Concat(SiteSettingsSplit.OneClickSiteKeys).Concat(SiteSettingsSplit.AppStateSiteKeys))
            Assert.DoesNotMatch(secret, key);
    }

    [Fact]
    public void TheOverlayShowsOnlyTheValuesOfTheActiveSite()
    {
        // The global file still holds the values that the single-site version wrote (AB1). Under CD2 none of them is visible.
        var global = Parse(Full);
        var siteCd2 = Parse("""{ "SiteCode": "CD2", "ProviderMachine": "cm.cd2.example.com", "DiscoverySqlServer": "sql.cd2.example.com" }""");

        var overlaid = SiteSettingsSplit.Overlay(global, siteCd2, null, "CD2", "cm.cd2.example.com");

        Assert.Equal("CD2", (string?)overlaid["SiteCode"]);
        Assert.Equal("sql.cd2.example.com", (string?)overlaid["DiscoverySqlServer"]);
        Assert.Null(overlaid["FileShareRoot"]);
        Assert.Null(overlaid["ContentDistribution"]);
        Assert.Null(overlaid["Wsus"]);
        Assert.Null(overlaid["OneClick"]!["Destinations"]);
        Assert.Null(overlaid["AppStates"]!["7zip"]!["CurrentVersion"]);
        Assert.Null(overlaid["AppStates"]!["7zip"]!["Published"]);
        // Global values stay.
        Assert.Equal("Dark", (string?)overlaid["Theme"]);
        Assert.Equal("24.09", (string?)overlaid["AppStates"]!["7zip"]!["LatestVersion"]);
        Assert.Equal("Weekly", (string?)overlaid["OneClick"]!["Cadence"]);
        Assert.DoesNotContain("ab1", overlaid.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheOverlayReadsBackWhatTheSplitWrote()
    {
        var full = Parse(Full);
        var split = SiteSettingsSplit.Split(full, null, null);

        var again = SiteSettingsSplit.Overlay(split.Global, split.Site, split.AppState, "AB1", null);

        foreach (var key in SiteSettingsSplit.SiteKeys)
            Assert.Equal(full[key]?.ToJsonString(), again[key]?.ToJsonString());
        Assert.Equal(full["OneClick"]!.ToJsonString().Length, again["OneClick"]!.ToJsonString().Length);
        Assert.Equal("24.08", (string?)again["AppStates"]!["7zip"]!["CurrentVersion"]);
    }

    [Fact]
    public void AnEarlierVersionsSiteKeysStayInTheGlobalFileAsTheyAre()
    {
        // Copy and keep: the global file that an earlier version wrote keeps its site values, so a downgrade still finds them.
        var existingGlobal = Parse(Full);
        var edited = Parse(Full);
        edited["DiscoverySqlServer"] = "sql.changed.example.com";
        edited["OneClick"]!["Destinations"] = new JsonObject();

        var split = SiteSettingsSplit.Split(edited, existingGlobal, null);

        Assert.Equal("sql.ab1.example.com", (string?)split.Global["DiscoverySqlServer"]);
        Assert.Equal("sql.changed.example.com", (string?)split.Site["DiscoverySqlServer"]);
        Assert.NotNull(split.Global["OneClick"]!["Destinations"]!["7zip"]);
    }

    [Fact]
    public void KeysOfTheSiteFileThatTheModelDoesNotKnowSurviveASave()
    {
        var existingSite = Parse("""{ "CveMonitorSiteCode": "AB1", "DiscoverySqlServer": "old" }""");

        var split = SiteSettingsSplit.Split(Parse(Full), null, existingSite);

        Assert.Equal("AB1", (string?)split.Site["CveMonitorSiteCode"]);
        Assert.Equal("sql.ab1.example.com", (string?)split.Site["DiscoverySqlServer"]);
    }

    [Fact]
    public void ANewSiteStartsWithTheProviderOfItsSiteJson()
    {
        var overlaid = SiteSettingsSplit.Overlay(Parse(Full), null, null, "CD2", "cm.cd2.example.com");

        Assert.Equal("CD2", (string?)overlaid["SiteCode"]);
        Assert.Equal("cm.cd2.example.com", (string?)overlaid["ProviderMachine"]);
    }

    [Fact]
    public void TheKeyListsEqualTheMigrationAndTheScopeManifest()
    {
        // The migration copies exactly the keys that the split reads from a site file; the scope manifest names the same keys.
        var migrated = typeof(SiteDataMigration).GetMethod("ExtractSiteSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var settings = new JsonObject();
        foreach (var key in SiteSettingsSplit.SiteKeys) settings[key] = "x";
        settings["OneClick"] = new JsonObject { ["Destinations"] = new JsonObject { ["a"] = 1 }, ["DefaultDestinations"] = new JsonObject { ["b"] = 2 }, ["Cadence"] = "Weekly" };
        object?[] args = [settings, Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid().ToString("N") + ".json"), null];
        var extracted = (JsonObject)migrated.Invoke(null, args)!;
        Assert.Equal(SiteSettingsSplit.SiteKeys.Append("OneClick").Order(), extracted.Select(p => p.Key).Order());
        Assert.Equal(SiteSettingsSplit.OneClickSiteKeys.Order(), ((JsonObject)extracted["OneClick"]!).Select(p => p.Key).Order());

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteData", "scope-manifest.json")));
        var siteRoots = doc.RootElement.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("app").GetString() == "apd" && e.GetProperty("scope").GetString() == "site" && e.GetProperty("location").GetString() == "settings.json")
            .Select(e => e.GetProperty("key").GetString()!.Split('.', ' ')[0])
            .ToHashSet();
        // The manifest names the hygiene options in prose ("Site Hygiene options"); their keys are SiteHygiene and SiteHygieneLive.
        var known = SiteSettingsSplit.SiteKeys.Concat(["OneClick", "AppStates", "Site"]).ToHashSet();
        Assert.True(siteRoots.IsSubsetOf(known), "Manifest site keys the split does not handle: " + string.Join(", ", siteRoots.Except(known)));
    }
}
