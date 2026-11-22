using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The settings store of the per-site layout (Q-112d) through the real SettingsService: global and site values, the switch,
/// and secrets that stay global. Each test has its own data root. Site codes are fictional (AB1, CD2); hosts are under example.com.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsServiceSiteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-settings-site-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public SettingsServiceSiteTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable, SiteDataPaths.AppSiteVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        Environment.SetEnvironmentVariable(SiteDataPaths.AppSiteVariable, null);
        Directory.CreateDirectory(AppDataPaths.Root);
    }

    public void Dispose()
    {
        SiteContext.Deactivate();
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string SiteFile(string folder) => Path.Combine(SiteDataPaths.GetSiteFolder(folder), "settings.json");

    private static AppSettings FirstSave(SettingsService service)
    {
        var s = service.Load();
        s.SiteCode = "AB1";
        s.ProviderMachine = "cm.ab1.example.com";
        s.FileShareRoot = @"\\files.ab1.example.com\content$";
        s.DiscoverySqlServer = "sql.ab1.example.com";
        s.CompanyName = "Example Corp";
        s.GitHubTokenProtected = "FAKE_PROTECTED_TOKEN";
        service.SaveOperatorEdit(s);
        return s;
    }

    [Fact]
    public void TheFirstSaveOfASiteCodeStartsThePerSiteLayout()
    {
        using var scope = SiteContext.Scope(null);
        var service = new SettingsService();

        FirstSave(service);

        Assert.Equal("AB1", SiteContext.ActiveFolderName);
        var site = (JsonObject)JsonNode.Parse(File.ReadAllText(SiteFile("AB1")))!;
        Assert.Equal("sql.ab1.example.com", (string?)site["DiscoverySqlServer"]);
        Assert.Equal(@"\\files.ab1.example.com\content$", (string?)site["FileShareRoot"]);
        Assert.Null(site["CompanyName"]);
        var loaded = new SettingsService().Load();
        Assert.Equal("AB1", loaded.SiteCode);
        Assert.Equal("sql.ab1.example.com", loaded.DiscoverySqlServer);
        Assert.Equal("Example Corp", loaded.CompanyName);
    }

    [Fact]
    public void SecretsAndTokensAreWrittenToTheGlobalFileOnly()
    {
        using var scope = SiteContext.Scope(null);
        var service = new SettingsService();
        FirstSave(service);

        Assert.Contains("FAKE_PROTECTED_TOKEN", File.ReadAllText(SettingsService.SettingsPath));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppDataPaths.Root, "sites"), "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("FAKE_PROTECTED_TOKEN", File.ReadAllText(file));
    }

    [Fact]
    public void SwitchingShowsTheOtherSitesDataAndNothingOfTheFirst()
    {
        using var scope = SiteContext.Scope(null);
        var service = new SettingsService();
        FirstSave(service);
        var manager = new SiteManager(null);

        var added = manager.AddSite("CD2", "cm.cd2.example.com", "Lab", _ => SiteReuseAnswer.Cancel);

        Assert.Equal(SiteSwitchOutcome.Switched, added.Outcome);
        var underCd2 = service.Load();
        Assert.Equal("CD2", underCd2.SiteCode);
        Assert.Equal("cm.cd2.example.com", underCd2.ProviderMachine);
        Assert.NotEqual("sql.ab1.example.com", underCd2.DiscoverySqlServer);
        Assert.NotEqual(@"\\files.ab1.example.com\content$", underCd2.FileShareRoot);
        // Global values stay visible.
        Assert.Equal("Example Corp", underCd2.CompanyName);
        Assert.Equal("FAKE_PROTECTED_TOKEN", underCd2.GitHubTokenProtected);

        underCd2.DiscoverySqlServer = "sql.cd2.example.com";
        service.SaveOperatorEdit(underCd2);

        // Back to AB1: its values return, and the values of CD2 are not there.
        Assert.Equal(SiteSwitchOutcome.Switched, manager.Switch("AB1").Outcome);
        var backOnAb1 = service.Load();
        Assert.Equal("AB1", backOnAb1.SiteCode);
        Assert.Equal("sql.ab1.example.com", backOnAb1.DiscoverySqlServer);
        Assert.Equal(@"\\files.ab1.example.com\content$", backOnAb1.FileShareRoot);
        Assert.Equal("cm.ab1.example.com", backOnAb1.ProviderMachine);
        Assert.DoesNotContain("cd2", File.ReadAllText(SiteFile("AB1")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sql.cd2.example.com", File.ReadAllText(SiteFile("CD2")));
    }

    [Fact]
    public void ASaveWritesOnlyTheActiveSiteFolder()
    {
        using var scope = SiteContext.Scope(null);
        var service = new SettingsService();
        FirstSave(service);
        var manager = new SiteManager(null);
        manager.AddSite("CD2", "cm.cd2.example.com", null, _ => SiteReuseAnswer.Cancel);
        var ab1Before = File.ReadAllText(SiteFile("AB1"));
        var ab1Stamp = File.GetLastWriteTimeUtc(SiteFile("AB1"));

        var s = service.Load();
        s.FileShareRoot = @"\\files.cd2.example.com\content$";
        service.Save(s);

        Assert.Equal(ab1Before, File.ReadAllText(SiteFile("AB1")));
        Assert.Equal(ab1Stamp, File.GetLastWriteTimeUtc(SiteFile("AB1")));
        Assert.Contains("files.cd2.example.com", File.ReadAllText(SiteFile("CD2")));
    }

    [Fact]
    public void ASaveCannotChangeTheSiteCodeOfTheActiveSite()
    {
        using var scope = SiteContext.Scope(null);
        var service = new SettingsService();
        FirstSave(service);

        var s = service.Load();
        s.SiteCode = "ZZ9";
        service.Save(s);

        Assert.Equal("AB1", service.Load().SiteCode);
        Assert.False(Directory.Exists(Path.Combine(AppDataPaths.Root, "sites", "ZZ9")));
    }

    [Fact]
    public void AnEarlierVersionsGlobalFileKeepsItsSiteValuesAfterTheFirstSave()
    {
        using var scope = SiteContext.Scope(null);
        // Copy and keep: a single-site settings.json is read, the site starts from the migration, and the old keys stay in the global file.
        File.WriteAllText(SettingsService.SettingsPath, """
        { "SiteCode": "AB1", "SiteCodeSaved": true, "ProviderMachine": "cm.ab1.example.com", "DiscoverySqlServer": "sql.ab1.example.com", "FirstRunCompleted": true }
        """);
        SiteStartup.Initialize();
        var service = new SettingsService();

        var s = service.Load();
        Assert.Equal("sql.ab1.example.com", s.DiscoverySqlServer);
        s.CompanyName = "Example Corp";
        service.Save(s);

        var global = (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!;
        Assert.Equal("sql.ab1.example.com", (string?)global["DiscoverySqlServer"]);
        Assert.Equal("Example Corp", (string?)global["CompanyName"]);
    }

    [Fact]
    public void WithNoSiteActiveTheSingleSiteFileIsWrittenAsBefore()
    {
        using var scope = SiteContext.Scope(null);
        var service = new SettingsService();
        var s = service.Load();
        s.DiscoverySqlServer = "sql.single.example.com";

        service.Save(s);

        Assert.Null(SiteContext.ActiveFolderName);
        Assert.Contains("sql.single.example.com", File.ReadAllText(SettingsService.SettingsPath));
        Assert.False(Directory.Exists(Path.Combine(AppDataPaths.Root, "sites")));
    }
}
