using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Import of settings from another site (Q-112e). Real files in a temp data root: two sites with fictional codes (AB1 the target, CD2 the source)
/// and hosts under example.com. The tests check what is copied, what never is, that the source stays byte for byte, that a conflict keeps the target
/// value by default, that every write is verified by its SHA-256, that a second run changes nothing, and that a run that stopped can run again.
/// </summary>
public sealed class SiteSettingsImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-siteimport-" + Guid.NewGuid().ToString("N"));

    public SiteSettingsImportTests()
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", "Target", _root);
        SiteCatalog.Create("CD2", "cm.cd2.example.com", "Source", _root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string SiteFile(string site, string name) => Path.Combine(SiteDataPaths.GetSiteFolder(site, dataRoot: _root), name);

    private string Write(string site, string name, string json)
    {
        var path = SiteFile(site, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private const string SourceSettings = """
    {
      "SiteCode": "CD2",
      "ProviderMachine": "cm.cd2.example.com",
      "DiscoverySqlServer": "sql.cd2.example.com",
      "DiscoverySqlDatabase": "CM_CD2",
      "FileShareRoot": "\\\\files.cd2.example.com\\content$",
      "ContentDistribution": { "AutoDistribute": true, "DpGroupName": "CD2 DPs", "TestCollectionName": "CD2 Test" },
      "DistributionOverrides": { "7zip": { "DpGroupName": "CD2 Special" } },
      "Wsus": { "ServerName": "wsus.cd2.example.com", "UseSsl": true },
      "SiteHygiene": { "PauseDays": 3 },
      "OneClick": { "Destinations": { "7zip": { "ConfigMgr": true } }, "DefaultDestinations": { "ConfigMgr": true } }
    }
    """;

    private static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static Dictionary<string, string> TreeHash(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => f, HashOf);

    private SiteImportRequest Request(IReadOnlyCollection<string>? ids = null, IReadOnlyDictionary<string, SiteImportChoice>? choices = null, Action<string, string>? hook = null) =>
        new("CD2", "AB1", ids, choices, _root, () => new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc), hook);

    private JsonObject TargetSettings() => SiteSettingsSplit.ReadObject(SiteFile("AB1", "settings.json")) ?? new JsonObject();

    // ---- the items come from the scope manifest ----

    private static JsonElement Manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteData", "scope-manifest.json")));
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, (string Scope, string Import, string Key)> ManifestEntries() =>
        Manifest().GetProperty("entries").EnumerateArray().ToDictionary(
            e => e.GetProperty("id").GetString()!,
            e => (e.GetProperty("scope").GetString()!, e.GetProperty("import").GetString()!, e.GetProperty("key").GetString()!));

    [Fact]
    public void EveryItemIsASiteScopedEntryOfTheScopeManifestAndOnlyEnvironmentValuesAreOptIn()
    {
        var manifest = ManifestEntries();
        foreach (var item in SiteSettingsImport.Items)
        {
            Assert.True(manifest.TryGetValue(item.Id, out var entry), $"{item.Id} is not in the scope manifest.");
            Assert.Equal("site", entry.Scope);
            Assert.NotEqual("secret", entry.Scope);
            // The manifest default 'on' is on here; 'off' and the three environment values that the manifest marks 'never' are off by default.
            if (item.EnvironmentValue) Assert.False(item.DefaultOn, $"{item.Id} is an environment value and must be off by default.");
            if (entry.Import == "on") Assert.True(item.DefaultOn, $"{item.Id} is 'on' in the manifest.");
            if (entry.Import == "off") Assert.False(item.DefaultOn, $"{item.Id} is 'off' in the manifest.");
        }
        // The only 'never' entries that the user may tick are the values that name a server of one environment.
        var optInNever = SiteSettingsImport.Items.Where(i => manifest[i.Id].Import == "never").Select(i => i.Id).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "apd.provider", "apd.sql-database", "apd.sql-server" }, optInNever);
        Assert.All(SiteSettingsImport.Items.Where(i => optInNever.Contains(i.Id)), i => Assert.True(i.EnvironmentValue));
    }

    [Fact]
    public void EveryKeyIsASiteKeyOfTheSettingsSplit()
    {
        foreach (var item in SiteSettingsImport.Items)
            foreach (var key in item.Keys)
            {
                var parts = key.Split('.');
                if (parts.Length == 1) Assert.Contains(parts[0], SiteSettingsSplit.SiteKeys);
                else
                {
                    Assert.Equal("OneClick", parts[0]);
                    Assert.Contains(parts[1], SiteSettingsSplit.OneClickSiteKeys);
                }
                Assert.DoesNotContain("SiteCode", key);
                Assert.Matches("^[A-Za-z.]+$", key);
                Assert.False(SiteDataMigration.SecretName.IsMatch(key), $"{key} looks like a secret name.");
            }
    }

    [Fact]
    public void NeverCopiedAreSiteRecordsAndSecretsOfTheManifestAndNoItemIsAmongThem()
    {
        var manifest = ManifestEntries();
        foreach (var never in SiteSettingsImport.NeverCopied)
        {
            Assert.True(manifest.TryGetValue(never.ManifestId, out var entry), $"{never.ManifestId} is not in the scope manifest.");
            Assert.Contains(entry.Scope, new[] { "site", "secret" });
            Assert.Equal("never", entry.Import);
            Assert.DoesNotContain(SiteSettingsImport.Items, i => i.Id == never.ManifestId);
        }
        foreach (var id in SiteDataMigration.SecretIds) Assert.Contains(SiteSettingsImport.NeverCopied, n => n.ManifestId == id);
        Assert.Contains(SiteSettingsImport.NeverCopied, n => n.ManifestId == "apd.site-code");
    }

    // ---- preview ----

    [Fact]
    public void ThePreviewWritesNothingAndTheDefaultsExcludeEnvironmentValues()
    {
        Write("CD2", "settings.json", SourceSettings);
        var before = TreeHash(_root);
        var report = SiteSettingsImport.Plan(Request());
        Assert.Equal(before, TreeHash(_root));
        Assert.True(report.DryRun);
        Assert.Equal(SiteImportReport.Done, report.Outcome);
        Assert.Equal(SiteImportOutcome.Copy, report.Rows.Single(r => r.Id == "apd.content-distribution").Outcome);
        foreach (var id in new[] { "apd.share-root", "apd.wsus", "apd.sql-server", "apd.sql-database", "apd.provider" })
        {
            var row = report.Rows.Single(r => r.Id == id);
            Assert.Equal(SiteImportOutcome.Skipped, row.Outcome);
            Assert.False(row.Selected);
            Assert.Equal(SiteImportState.New, row.State);
        }
        Assert.Contains("CD2 DPs", report.Rows.Single(r => r.Id == "apd.content-distribution").SourceText);
        Assert.Contains("cm.cd2.example.com", report.Rows.Single(r => r.Id == "apd.provider").SourceText);
    }

    // ---- copy ----

    [Fact]
    public void ADefaultImportCopiesTheNamesAndOptionsOnlyAndLeavesTheSourceUnchanged()
    {
        var source = Write("CD2", "settings.json", SourceSettings);
        Write("CD2", "state\\app-state.json", """{ "7zip": { "CurrentVersion": "9.9" } }""");
        Write("CD2", "results\\CheckConfigMgrResult.csv", "a,b\n1,2\n");
        Write("AB1", "settings.json", """{ "SiteCode": "AB1", "ProviderMachine": "cm.ab1.example.com", "Theme": "x", "OneClick": { "Cadence": "Daily" } }""");
        var sourceFolderBefore = TreeHash(SiteDataPaths.GetSiteFolder("CD2", dataRoot: _root));

        var report = SiteSettingsImport.Run(Request());

        Assert.Equal(SiteImportReport.Done, report.Outcome);
        Assert.Equal(sourceFolderBefore, TreeHash(SiteDataPaths.GetSiteFolder("CD2", dataRoot: _root)));
        var t = TargetSettings();
        Assert.Equal("CD2 DPs", t["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        Assert.Equal("CD2 Special", t["DistributionOverrides"]!["7zip"]!["DpGroupName"]!.GetValue<string>());
        Assert.Equal(3, t["SiteHygiene"]!["PauseDays"]!.GetValue<int>());
        Assert.True(t["OneClick"]!["Destinations"]!["7zip"]!["ConfigMgr"]!.GetValue<bool>());
        // The target keeps its own identity and unrelated values, including the sibling key of OneClick.
        Assert.Equal("AB1", t["SiteCode"]!.GetValue<string>());
        Assert.Equal("cm.ab1.example.com", t["ProviderMachine"]!.GetValue<string>());
        Assert.Equal("x", t["Theme"]!.GetValue<string>());
        Assert.Equal("Daily", t["OneClick"]!["Cadence"]!.GetValue<string>());
        // Environment values were not ticked: nothing of them arrived.
        foreach (var key in new[] { "FileShareRoot", "Wsus", "DiscoverySqlServer", "DiscoverySqlDatabase" }) Assert.Null(t[key]);
        // No record of the source site arrived.
        Assert.False(File.Exists(SiteFile("AB1", "results\\CheckConfigMgrResult.csv")));
        Assert.False(File.Exists(SiteFile("AB1", "state\\app-state.json")));
        Assert.Equal(HashOf(source), report.SourceSha256);
        Assert.Equal(HashOf(SiteFile("AB1", "settings.json")), report.TargetSha256);
    }

    [Fact]
    public void TickedEnvironmentValuesAreCopiedAndTheProviderIsListedInTheTargetSite()
    {
        Write("CD2", "settings.json", SourceSettings);
        var ids = SiteSettingsImport.Items.Select(i => i.Id).ToList();
        var report = SiteSettingsImport.Run(Request(ids));

        Assert.Equal(SiteImportReport.Done, report.Outcome);
        var t = TargetSettings();
        Assert.Equal("sql.cd2.example.com", t["DiscoverySqlServer"]!.GetValue<string>());
        Assert.Equal("CM_CD2", t["DiscoverySqlDatabase"]!.GetValue<string>());
        Assert.Equal("wsus.cd2.example.com", t["Wsus"]!["ServerName"]!.GetValue<string>());
        Assert.Equal("cm.cd2.example.com", t["ProviderMachine"]!.GetValue<string>());
        Assert.Equal("\\\\files.cd2.example.com\\content$", t["FileShareRoot"]!.GetValue<string>());
        Assert.Contains("cm.cd2.example.com", SiteCatalog.Get("AB1", _root)!.Providers);
        // The site code is never copied, even when everything is ticked.
        Assert.Equal("AB1", SiteCatalog.Get("AB1", _root)!.SiteCode);
        Assert.NotEqual("CD2", t["SiteCode"]?.GetValue<string>());
    }

    [Fact]
    public void ASourceWithNoSettingsFileOffersNothingAndWritesNothing()
    {
        var before = TreeHash(_root);
        var report = SiteSettingsImport.Run(Request());
        Assert.Equal(SiteImportReport.NothingToDo, report.Outcome);
        Assert.All(report.Rows, r => Assert.Equal(SiteImportState.NotInSource, r.State));
        Assert.Equal(before, TreeHash(_root));
        Assert.False(File.Exists(SiteFile("AB1", "settings.json")));
    }

    // ---- conflicts ----

    private void SeedConflict()
    {
        Write("CD2", "settings.json", SourceSettings);
        Write("AB1", "settings.json", """{ "SiteCode": "AB1", "ContentDistribution": { "DpGroupName": "AB1 DPs" }, "SiteHygiene": { "PauseDays": 3 } }""");
    }

    [Fact]
    public void AConflictKeepsTheTargetValueByDefaultAndTheRowSaysSo()
    {
        SeedConflict();
        var target = SiteFile("AB1", "settings.json");
        var plan = SiteSettingsImport.Plan(Request());
        var cd = plan.Rows.Single(r => r.Id == "apd.content-distribution");
        Assert.Equal(SiteImportState.Conflict, cd.State);
        Assert.Equal(SiteImportOutcome.Keep, cd.Outcome);
        Assert.Equal(SiteImportState.Same, plan.Rows.Single(r => r.Id == "apd.hygiene-options").State);

        var run = SiteSettingsImport.Run(Request());
        Assert.Equal(SiteImportOutcome.Kept, run.Rows.Single(r => r.Id == "apd.content-distribution").Outcome);
        Assert.Equal(SiteImportOutcome.AlreadyDone, run.Rows.Single(r => r.Id == "apd.hygiene-options").Outcome);
        Assert.Equal("AB1 DPs", TargetSettings()["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "settings.json" + SiteSettingsImport.BackupInfix + "*"));
    }

    [Fact]
    public void AKeptConflictDoesNotStopTheOtherItemsAndAMultiKeyItemIsKeptWhole()
    {
        Write("CD2", "settings.json", SourceSettings);
        // One OneClick key conflicts, the other is new: Keep leaves the whole item as the target has it, so the flags of two sites never mix.
        Write("AB1", "settings.json", """{ "OneClick": { "Destinations": { "7zip": { "ConfigMgr": false } } } }""");
        var keep = SiteSettingsImport.Run(Request());
        Assert.Equal(SiteImportOutcome.Kept, keep.Rows.Single(r => r.Id == "apd.destinations").Outcome);
        Assert.Null(TargetSettings()["OneClick"]!["DefaultDestinations"]);
        Assert.False(TargetSettings()["OneClick"]!["Destinations"]!["7zip"]!["ConfigMgr"]!.GetValue<bool>());
        Assert.NotNull(TargetSettings()["ContentDistribution"]);

        var replace = SiteSettingsImport.Run(Request(choices: new Dictionary<string, SiteImportChoice> { ["apd.destinations"] = SiteImportChoice.Replace }));
        Assert.Equal(SiteImportOutcome.Replaced, replace.Rows.Single(r => r.Id == "apd.destinations").Outcome);
        Assert.True(TargetSettings()["OneClick"]!["DefaultDestinations"]!["ConfigMgr"]!.GetValue<bool>());
        Assert.True(TargetSettings()["OneClick"]!["Destinations"]!["7zip"]!["ConfigMgr"]!.GetValue<bool>());
    }

    [Fact]
    public void ASaveOfTheAppDuringTheRunIsNotOverwritten()
    {
        SeedConflict();
        var target = SiteFile("AB1", "settings.json");
        const string saved = """{ "SiteCode": "AB1", "Theme": "saved-in-between" }""";
        var run = SiteSettingsImport.Run(Request(choices: new Dictionary<string, SiteImportChoice> { ["apd.content-distribution"] = SiteImportChoice.Replace },
            hook: (stage, _) => { if (stage == "temp-written") File.WriteAllText(target, saved); }));
        Assert.Equal(SiteImportReport.FailedOutcome, run.Outcome);
        Assert.True(run.TargetChanged);
        Assert.Equal(saved, File.ReadAllText(target));
        Assert.False(File.Exists(target + ".importing"));
    }

    [Fact]
    public void AFailureAfterTheReplaceIsAWarningAndTheImportStaysDone()
    {
        Write("CD2", "settings.json", SourceSettings);
        // A directory where the log goes: the log cannot be written after the settings are in place.
        Directory.CreateDirectory(SiteFile("AB1", SiteSettingsImport.LogFileName));
        var run = SiteSettingsImport.Run(Request());
        Assert.Equal(SiteImportReport.Done, run.Outcome);
        Assert.True(run.TargetChanged);
        Assert.Contains("could not be written", run.Warning);
        Assert.Equal("CD2 DPs", TargetSettings()["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
    }

    [Fact]
    public void AReplaceKeepsOnlyTheNewestBackups()
    {
        SeedConflict();
        var folder = Path.GetDirectoryName(SiteFile("AB1", "settings.json"))!;
        for (var i = 1; i <= 7; i++) File.WriteAllText(Path.Combine(folder, $"settings.json{SiteSettingsImport.BackupInfix}2026010{i}T000000Z"), "old");
        SiteSettingsImport.Run(Request(choices: new Dictionary<string, SiteImportChoice> { ["apd.content-distribution"] = SiteImportChoice.Replace }));
        var left = Directory.GetFiles(folder, "settings.json" + SiteSettingsImport.BackupInfix + "*");
        Assert.Equal(SiteSettingsImport.MaxBackups, left.Length);
        Assert.Contains(left, f => f.EndsWith("20261123T100000Z", StringComparison.Ordinal));
    }

    [Fact]
    public void AKeptConflictStillAddsTheKeysThatAreMissingInTheOtherItems()
    {
        SeedConflict();
        // The other items have no value in the target: they are copied; the conflicting item is kept.
        var run = SiteSettingsImport.Run(Request());
        Assert.Equal(SiteImportOutcome.Copied, run.Rows.Single(r => r.Id == "apd.distribution-overrides").Outcome);
        Assert.Equal(SiteImportReport.Done, run.Outcome);
        Assert.Equal("AB1 DPs", TargetSettings()["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        Assert.NotNull(TargetSettings()["DistributionOverrides"]);
    }

    [Fact]
    public void ReplaceOverwritesOnlyTheChosenItemAndKeepsAVerifiedBackupOfTheTargetFile()
    {
        SeedConflict();
        var target = SiteFile("AB1", "settings.json");
        var hashBefore = HashOf(target);
        var choices = new Dictionary<string, SiteImportChoice> { ["apd.content-distribution"] = SiteImportChoice.Replace };
        var run = SiteSettingsImport.Run(Request(choices: choices));

        Assert.Equal(SiteImportOutcome.Replaced, run.Rows.Single(r => r.Id == "apd.content-distribution").Outcome);
        Assert.Equal("CD2 DPs", TargetSettings()["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        var backups = Directory.GetFiles(Path.GetDirectoryName(target)!, "settings.json" + SiteSettingsImport.BackupInfix + "*");
        var backup = Assert.Single(backups);
        Assert.Equal(hashBefore, HashOf(backup));
        Assert.Contains("20261123T100000Z", backup);
    }

    // ---- secrets ----

    [Fact]
    public void NoSecretOrTokenIsCopiedWhateverTheSourceHolds()
    {
        Write("CD2", "settings.json", """
        {
          "GitHubTokenProtected": "FAKE_GITHUB", "NvdApiKeyProtected": "FAKE_NVD",
          "Intune": { "ClientSecretProtected": "FAKE_INTUNE" },
          "ContentDistribution": { "DpGroupName": "CD2 DPs", "ApiToken": "FAKE_TOKEN", "Password": "FAKE_PW" },
          "Wsus": { "ServerName": "wsus.cd2.example.com", "WebhookUrl": "https://hooks.example.com/FAKE" },
          "SiteHygiene": { "PauseDays": 3, "ClientSecret": "FAKE_SECRET" }
        }
        """);
        var report = SiteSettingsImport.Run(Request(SiteSettingsImport.Items.Select(i => i.Id).ToList()));
        var written = File.ReadAllText(SiteFile("AB1", "settings.json")) + File.ReadAllText(SiteFile("AB1", SiteSettingsImport.LogFileName));
        foreach (var fake in new[] { "FAKE_GITHUB", "FAKE_NVD", "FAKE_INTUNE", "FAKE_TOKEN", "FAKE_PW", "hooks.example.com", "FAKE_SECRET" })
            Assert.DoesNotContain(fake, written);
        var t = TargetSettings();
        Assert.Equal("CD2 DPs", t["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        Assert.Null(t["ContentDistribution"]!["ApiToken"]);
        Assert.Equal("wsus.cd2.example.com", t["Wsus"]!["ServerName"]!.GetValue<string>());
        Assert.Null(t["GitHubTokenProtected"]);
        Assert.Null(t["Intune"]);
        Assert.Contains(report.NeverCopiedKeys, k => k.Contains("ApiToken"));
        Assert.DoesNotContain(SiteSettingsImport.Items, i => i.Keys.Any(k => k.Contains("Token") || k.Contains("Secret") || k.Contains("Protected")));
    }

    // ---- idempotent and resumable ----

    [Fact]
    public void ASecondRunChangesNothingAndMakesNoNewBackup()
    {
        SeedConflict();
        var choices = new Dictionary<string, SiteImportChoice> { ["apd.content-distribution"] = SiteImportChoice.Replace };
        SiteSettingsImport.Run(Request(choices: choices));
        var after = TreeHash(_root);

        var second = SiteSettingsImport.Run(Request(choices: choices));

        Assert.Equal(SiteImportReport.NothingToDo, second.Outcome);
        Assert.All(second.Rows.Where(r => r.Selected && r.State != SiteImportState.NotInSource), r => Assert.Equal(SiteImportOutcome.AlreadyDone, r.Outcome));
        Assert.Equal(after, TreeHash(_root));
    }

    [Fact]
    public void ARunThatStoppedBeforeTheReplaceLeavesTheTargetAsItWasAndRunsAgain()
    {
        SeedConflict();
        var target = SiteFile("AB1", "settings.json");
        var before = HashOf(target);
        var choices = new Dictionary<string, SiteImportChoice> { ["apd.content-distribution"] = SiteImportChoice.Replace };

        Assert.Throws<InvalidOperationException>(() =>
            SiteSettingsImport.Run(Request(choices: choices, hook: (stage, _) => { if (stage == "temp-written") throw new InvalidOperationException("crash"); })));
        Assert.Equal(before, HashOf(target));
        Assert.True(File.Exists(target + ".importing"));

        var again = SiteSettingsImport.Run(Request(choices: choices));
        Assert.Equal(SiteImportReport.Done, again.Outcome);
        Assert.False(File.Exists(target + ".importing"));
        Assert.Equal("CD2 DPs", TargetSettings()["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
    }

    [Fact]
    public void ARunThatStoppedAfterTheReplaceWritesTheLogOnTheNextRun()
    {
        Write("CD2", "settings.json", SourceSettings);
        var logPath = SiteFile("AB1", SiteSettingsImport.LogFileName);
        // Simulate the crash between the settings replace and the log: run, then delete the log.
        SiteSettingsImport.Run(Request());
        File.Delete(logPath);
        var again = SiteSettingsImport.Run(Request());
        Assert.Equal(SiteImportReport.NothingToDo, again.Outcome);
        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public void TheLogRecordsTheSourceTheDateAndTheHashesOfTheFiles()
    {
        var source = Write("CD2", "settings.json", SourceSettings);
        SiteSettingsImport.Run(Request());
        var log = JsonNode.Parse(File.ReadAllText(SiteFile("AB1", SiteSettingsImport.LogFileName)))!.AsObject();
        Assert.Equal("CD2", log["sourceSite"]!.GetValue<string>());
        Assert.Equal("2026-11-23T10:00:00Z", log["importedUtc"]!.GetValue<string>());
        Assert.Equal(HashOf(source), log["sourceSettingsSha256"]!.GetValue<string>());
        Assert.Equal(HashOf(SiteFile("AB1", "settings.json")), log["targetSettingsSha256"]!.GetValue<string>());
        Assert.Contains(log["items"]!.AsArray(), n => n!["id"]!.GetValue<string>() == "apd.content-distribution" && n["outcome"]!.GetValue<string>() == "Copied");
    }

    // ---- refusals ----

    [Fact]
    public void TheSameSiteAnUnknownSiteAndAnUnreadableFileAreRefusedWithNothingWritten()
    {
        Write("CD2", "settings.json", SourceSettings);
        var before = TreeHash(_root);
        Assert.Equal(SiteImportReport.Refused, SiteSettingsImport.Run(new SiteImportRequest("AB1", "AB1", null, null, _root)).Outcome);
        Assert.Equal(SiteImportReport.Refused, SiteSettingsImport.Run(new SiteImportRequest("EF3", "AB1", null, null, _root)).Outcome);
        Assert.Equal(SiteImportReport.Refused, SiteSettingsImport.Run(new SiteImportRequest("CD2", "EF3", null, null, _root)).Outcome);
        Assert.Equal(SiteImportReport.Refused, SiteSettingsImport.Run(new SiteImportRequest("..\\x", "AB1", null, null, _root)).Outcome);
        Write("AB1", "settings.json", "{ this is not json");
        var damaged = SiteSettingsImport.Run(Request());
        Assert.Equal(SiteImportReport.Refused, damaged.Outcome);
        Assert.Contains("cannot be read", damaged.Reason);
        Assert.Equal("{ this is not json", File.ReadAllText(SiteFile("AB1", "settings.json")));
        File.Delete(SiteFile("AB1", "settings.json"));
        Assert.Equal(before.Count, TreeHash(_root).Count);
    }

    [Fact]
    public void ADamagedSourceFileIsRefusedAndNotChanged()
    {
        Write("CD2", "settings.json", "[1,2");
        var report = SiteSettingsImport.Run(Request());
        Assert.Equal(SiteImportReport.Refused, report.Outcome);
        Assert.Equal("[1,2", File.ReadAllText(SiteFile("CD2", "settings.json")));
        Assert.False(File.Exists(SiteFile("AB1", "settings.json")));
    }
}
