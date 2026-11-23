using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The first start of the new version (Q-112d, B4 and B6): migration once (copy, verify, keep the old files), then the active site.
/// An integration test over a seeded old-layout profile in a temp folder. Site codes are fictional (AB1, CD2); hosts are under example.com.
/// </summary>
public sealed class SiteStartupTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ap-sitestart-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_temp, "AppPackager");
    private string LegacyRoot => Path.Combine(_temp, "SRLPackr");
    private string Data => Path.Combine(_temp, "AppPackagerData");

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Seed(string relative, string content, string? root = null)
    {
        var path = Path.Combine(root ?? Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private void SeedLegacyProfile(string code = "AB1", bool saved = true)
    {
        Seed("settings.json", $$"""
        {
          "GitHubTokenProtected": "FAKE_TOKEN_VALUE",
          "SiteCode": "{{code}}", "SiteCodeSaved": {{(saved ? "true" : "false")}},
          "ProviderMachine": "cm.ab1.example.com",
          "FileShareRoot": "\\\\files.ab1.example.com\\content$",
          "DiscoverySqlServer": "sql.ab1.example.com",
          "ContentDistribution": { "DistributionPointGroup": "AB1 DPs" },
          "Theme": "Dark"
        }
        """);
        Seed(@"Deployments\Rings\plan-one.json", """{ "name": "plan one" }""");
        Seed(@"Deployments\deployment-audit.jsonl", "{\"line\":1}\n");
        Seed(@"SiteHygiene\suppressions.json", """{ "items": [] }""");
        Seed(@"DpContent\Reports\report.csv", "a,b\n1,2\n");
        Seed("CheckConfigMgrResult.csv", "Application,Status\n7zip,Current\n", LegacyRoot);
        Seed("history.db", "not a real database but a file to copy", LegacyRoot);
        Seed(@"Workbench\definitions\mine.json", "{}");
        Seed("condition-templates.json", "[]");
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private Dictionary<string, string> OldFiles() =>
        Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(Data, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f, Sha);

    private SiteStartupResult Start(Func<string, string?>? env = null) =>
        SiteStartup.Initialize(Root, LegacyRoot, Data, env ?? (_ => null));

    [Fact]
    public void TheFirstStartCopiesVerifiesKeepsTheOldFilesAndActivatesTheSite()
    {
        SeedLegacyProfile();
        var before = OldFiles();
        using var scope = SiteContext.Scope(null, Data);

        var result = Start();

        Assert.Equal("AB1", result.ActiveFolder);
        Assert.Equal("settings", result.Source);
        Assert.Equal("Migrated", result.Migration?.Outcome);
        Assert.Equal("AB1", SiteContext.ActiveFolderName);
        // Old files: not one byte changed, not one deleted.
        Assert.Equal(before, OldFiles());
        // New files exist with the hash of their source.
        var site = Path.Combine(Data, "sites", "AB1");
        Assert.Equal(Sha(Path.Combine(Root, "Deployments", "Rings", "plan-one.json")), Sha(Path.Combine(site, "deployments", "rings", "plan-one.json")));
        Assert.Equal(Sha(Path.Combine(Root, "SiteHygiene", "suppressions.json")), Sha(Path.Combine(site, "hygiene", "suppressions.json")));
        Assert.Equal(Sha(Path.Combine(Root, "DpContent", "Reports", "report.csv")), Sha(Path.Combine(site, "dpcontent", "Reports", "report.csv")));
        Assert.Equal(Sha(Path.Combine(LegacyRoot, "CheckConfigMgrResult.csv")), Sha(Path.Combine(site, "results", "CheckConfigMgrResult.csv")));
        Assert.Equal(Sha(Path.Combine(LegacyRoot, "history.db")), Sha(Path.Combine(site, "results", "history.db")));
        // The stores now read the site folder.
        Assert.Equal(Path.Combine(site, "deployments", "rings"), RingPlanStore.DefaultFolder());
        Assert.True(File.Exists(Path.Combine(RingPlanStore.DefaultFolder(), "plan-one.json")));
        // active-site.json is written for tools that start on their own.
        Assert.Equal("AB1", SiteDataPaths.GetActiveSite(Data, _ => null)?.FolderName);
        Assert.Equal(["cm.ab1.example.com"], SiteCatalog.Get("AB1", Data)!.Providers);
    }

    [Fact]
    public void NoSecretOrTokenIsCopiedIntoTheSiteFolder()
    {
        SeedLegacyProfile();
        using var scope = SiteContext.Scope(null, Data);

        Start();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Data, "sites"), "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("FAKE_TOKEN_VALUE", File.ReadAllText(file));
        var siteSettings = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Data, "sites", "AB1", "settings.json")))!;
        Assert.Null(siteSettings["GitHubTokenProtected"]);
        Assert.Null(siteSettings["Theme"]);
        Assert.Equal("sql.ab1.example.com", (string?)siteSettings["DiscoverySqlServer"]);
    }

    [Fact]
    public void ASecondStartMigratesNothingAndChangesNoFile()
    {
        SeedLegacyProfile();
        using var scope = SiteContext.Scope(null, Data);
        Start();
        var after = Directory.EnumerateFiles(Data, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => (Sha(f), File.GetLastWriteTimeUtc(f)));

        var second = Start();

        Assert.Null(second.Migration);
        Assert.Equal("AB1", second.ActiveFolder);
        Assert.Equal(after.Keys.Order(), Directory.EnumerateFiles(Data, "*", SearchOption.AllDirectories).Order());
        foreach (var (file, state) in after)
            if (!file.EndsWith("active-site.json") && !file.EndsWith("site.json")) Assert.Equal(state.Item1, Sha(file));
    }

    [Fact]
    public void AfterASwitchTheMirroredSiteCodeDoesNotCopyTheSingleSiteDataIntoTheNewSite()
    {
        SeedLegacyProfile();
        using var scope = SiteContext.Scope(null, Data);
        Start();
        SiteCatalog.Create("CD2", "cm.cd2.example.com", null, Data);
        SiteCatalog.WriteActiveSite("CD2", Data);
        // After a save under CD2 the global file mirrors the code of CD2 as the last selection.
        Seed("settings.json", "{ \"SiteCode\": \"CD2\", \"SiteCodeSaved\": true, \"ProviderMachine\": \"cm.cd2.example.com\", \"FileShareRoot\": \"stale\" }");

        var second = Start();

        Assert.Equal("CD2", second.ActiveFolder);
        Assert.Null(second.Migration);
        Assert.False(Directory.Exists(Path.Combine(Data, "sites", "CD2", "hygiene")));
        Assert.False(File.Exists(Path.Combine(Data, "sites", "CD2", "results", "history.db")));
        Assert.False(File.Exists(Path.Combine(Data, "sites", "CD2", "settings.json")));
    }

    [Fact]
    public void WithNoSavedSiteCodeNothingIsMigratedAndTheSingleSiteLayoutStays()
    {
        SeedLegacyProfile(saved: false);
        var before = OldFiles();
        using var scope = SiteContext.Scope(null, Data);

        var result = Start();

        Assert.Null(result.ActiveFolder);
        Assert.Equal("none", result.Source);
        Assert.Null(SiteContext.ActiveFolderName);
        Assert.False(Directory.Exists(Path.Combine(Data, "sites")));
        Assert.Equal(before, OldFiles());
        Assert.Equal(Path.Combine(AppDataPaths.Root, "Deployments", "Rings"), RingPlanStore.DefaultFolder());
    }

    [Fact]
    public void ALauncherSiteWinsOverActiveSiteJsonAndActiveSiteJsonWinsOverSettings()
    {
        SeedLegacyProfile();
        using var scope = SiteContext.Scope(null, Data);
        Start();
        SiteCatalog.Create("CD2", "cm.cd2.example.com", null, Data);
        SiteCatalog.WriteActiveSite("CD2", Data);

        Assert.Equal("CD2", Start().ActiveFolder);
        Assert.Equal("CD2", SiteContext.ActiveFolderName);

        SiteCatalog.Create("EF3", "cm.ef3.example.com", null, Data);
        var fromEnvironment = Start(name => name == "SUITE_CM_SITECODE" ? "EF3" : null);
        Assert.Equal("EF3", fromEnvironment.ActiveFolder);
        Assert.Equal("SUITE_CM_SITECODE", fromEnvironment.Source);
        // The launcher value is not written back into active-site.json.
        Assert.Equal("CD2", SiteDataPaths.GetActiveSite(Data, _ => null)?.FolderName);
    }

    [Fact]
    public void ALauncherSiteWithNoFolderYetGetsOneWithTheLauncherProvider()
    {
        SeedLegacyProfile();
        using var scope = SiteContext.Scope(null, Data);

        var result = Start(name => name switch { "SUITE_CM_SITECODE" => "CD2", "SUITE_CM_PROVIDER" => "cm.cd2.example.com", _ => null });

        Assert.Equal("CD2", result.ActiveFolder);
        Assert.Equal(["cm.cd2.example.com"], SiteCatalog.Get("CD2", Data)!.Providers);
    }

    [Fact]
    public void AnInvalidLauncherValueIsSkipped()
    {
        SeedLegacyProfile();
        using var scope = SiteContext.Scope(null, Data);

        var result = Start(name => name == "SUITE_CM_SITECODE" ? "../x" : null);

        Assert.Equal("AB1", result.ActiveFolder);
        Assert.Equal("settings", result.Source);
    }

    [Fact]
    public void AConflictKeepsBothCopiesAndTellsTheUser()
    {
        SeedLegacyProfile();
        // A copy with other content exists already (for example the PowerShell app wrote the same file).
        Seed(@"sites\AB1\results\CheckConfigMgrResult.csv", "different content\n", Data);
        using var scope = SiteContext.Scope(null, Data);

        var result = Start();

        Assert.Equal("AB1", result.ActiveFolder);
        Assert.Contains("Both copies are kept", result.Notice);
        Assert.Equal("different content\n", File.ReadAllText(Path.Combine(Data, "sites", "AB1", "results", "CheckConfigMgrResult.csv")));
        Assert.Equal("Application,Status\n7zip,Current\n", File.ReadAllText(Path.Combine(LegacyRoot, "CheckConfigMgrResult.csv")).Replace("\r\n", "\n"));
    }

    [Fact]
    public void AFailedMigrationLeavesTheSingleSiteLayoutInUse()
    {
        SeedLegacyProfile();
        // The first destination file is held open without sharing, so the copy cannot finish: the run fails and the old files stay live.
        var blocked = Path.Combine(Data, "sites", "AB1", "settings.json.migrating");
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
        using var hold = new FileStream(blocked, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var scope = SiteContext.Scope(null, Data);

        var result = Start();

        Assert.Equal("Failed", result.Migration?.Outcome);
        Assert.Null(result.ActiveFolder);
        Assert.Null(SiteContext.ActiveFolderName);
        Assert.Contains("stays in use", result.Notice);
        Assert.Equal(Path.Combine(AppDataPaths.Root, "Deployments", "Rings"), RingPlanStore.DefaultFolder());
    }
}
