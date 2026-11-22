using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Hash harness of the single-site migration (Q-112c). Each test seeds an old-layout profile in a temp folder, runs
/// the migration and compares the SHA-256 of every file before and after. Site codes are fictional (AB1, CD2).
/// </summary>
public sealed class SiteDataMigrationTests : IDisposable
{
    private const string SecretGitHub = "ghp_FAKE_github_token_value_1234567890";
    private const string SecretIntune = "FAKE_intune_client_secret_value";
    private const string SecretFeed = "FAKE_feed_password_value";
    private const string SecretNvd = "FAKE_nvd_api_key_value";
    private static readonly Func<DateTime> FixedNow = () => new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ap-sitemig-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_temp, "AppPackager");
    private string LegacyRoot => Path.Combine(_temp, "SRLPackr");
    private string DataRoot => Path.Combine(_temp, "AppPackagerData");

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private SiteMigrationOptions Options(Action<string, string>? hook = null) =>
        new() { Root = Root, LegacyRoot = LegacyRoot, DataRoot = DataRoot, Hook = hook, UtcNow = FixedNow };

    private void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>An old-layout profile as APD wrote it before the site layout: settings, history, per-site stores, global stores, secrets.</summary>
    private void SeedLegacyProfile(string siteCode = "AB1", bool saved = true)
    {
        var settings = $$"""
        {
          "GitHubTokenProtected": "{{SecretGitHub}}",
          "SiteCode": "{{siteCode}}",
          "SiteCodeSaved": {{(saved ? "true" : "false")}},
          "ProviderMachine": "cm01.example.com",
          "FileShareRoot": "\\\\files.example.com\\content$",
          "DiscoverySqlServer": "sql01.example.com",
          "DiscoverySqlDatabase": "CM_{{siteCode}}",
          "ContentDistribution": { "DistributionPointGroup": "All DPs", "TestCollection": "Test Devices" },
          "DistributionOverrides": { "7zip": { "DistributionPointGroup": "Pilot DPs" } },
          "OneClick": {
            "Action": "Report",
            "Destinations": { "7zip": { "ConfigMgr": true, "Intune": false } },
            "DefaultDestinations": { "ConfigMgr": true },
            "Cadence": "Weekly"
          },
          "InstallerSettingValues": { "7zip": { "Silent": "/S" } },
          "AppStates": {
            "7zip": {
              "LastChecked": "2026-11-20T10:00:00Z",
              "LatestVersion": "24.09",
              "CurrentVersion": "24.08",
              "Published": { "ConfigMgr": { "Version": "24.08" } },
              "NotSupported": {}
            }
          },
          "Intune": { "TenantId": "tenant.example.com", "ClientId": "client-1", "ClientSecretProtected": "{{SecretIntune}}" },
          "Wsus": { "Server": "wsus01.example.com", "Port": 8530 },
          "SiteHygiene": { "Areas": ["Collections"], "PauseSeconds": 5 },
          "Theme": "Dark"
        }
        """;
        Write(Root, "settings.json", settings);
        Write(Root, "monitor-config.json", $$"""{ "SiteCode": "{{siteCode}}", "NvdApiKey": "{{SecretNvd}}" }""");
        Write(Root, "FeedCredentials.json", $$"""{ "feed": { "password": "{{SecretFeed}}" } }""");
        Write(Root, "condition-templates.json", "{\"templates\":[{\"name\":\"Mine\"}]}");
        Write(Root, @"Deployments\Rings\plan-1.json", "{\"name\":\"Pilot\",\"collection\":\"Pilot Devices\"}");
        Write(Root, @"Deployments\Rings\runs\run-001.json", "{\"run\":1,\"status\":\"Done\"}");
        Write(Root, @"Deployments\deployment-audit.jsonl", "{\"op\":\"deploy\",\"site\":\"AB1\"}\n{\"op\":\"deploy2\"}\n");
        Write(Root, @"Deployments\Templates\mine.json", "{\"user\":\"template\"}");
        Write(Root, @"Deployments\Reports\r1.html", "<html>report</html>");
        Write(Root, @"SiteHygiene\suppressions.json", "{\"suppressed\":[\"OBJ-1\"]}");
        Write(Root, @"SiteHygiene\last-scan.json", "{\"scanned\":\"2026-11-20\"}");
        Write(Root, @"SiteHygiene\metrics-history.csv", "date,count\n2026-11-20,3\n");
        Write(Root, @"SiteHygiene\site-hygiene-audit.jsonl", "{\"a\":1}\n");
        Write(Root, @"DpContent\Reports\dp-1.csv", "dp,state\nDP1,ok\n");
        Write(Root, @"DpContent\dp-content-audit.jsonl", "{\"a\":2}\n");
        Write(Root, @"PivotsScripts\Sessions\s1.json", "{\"SiteCode\":\"AB1\",\"Provider\":\"cm01.example.com\"}");
        Write(Root, @"PivotsScripts\pivots-scripts-audit.jsonl", "{\"a\":3}\n");
        Write(Root, @"MaintenanceWindows\Reports\mw-1.csv", "mw\n");
        Write(Root, @"Collections\Reports\col-1.csv", "c\n");
        Write(Root, @"Collections\Templates\q1.json", "{\"query\":\"user template\"}");
        Write(Root, @"Vulnerability\cache.json", "{\"cve\":[]}");
        Write(Root, @"Workbench\definitions\mine.json", "{\"user\":\"definition\"}");
        Write(LegacyRoot, "CheckConfigMgrResult.csv", "App,Version,ConfigMgrVersion,Source\n7zip,24.08,24.08,CM\n");
        Write(LegacyRoot, "CheckConfigMgrResult.json", "[{\"App\":\"7zip\"}]");
        Write(LegacyRoot, @"Logs\apd-2026-11-20.log", "log line site AB1\n");
        var db = Path.Combine(LegacyRoot, "history.db");
        using (var c = new SQLiteConnection($"Data Source={db};Version=3;Pooling=False;"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE SiteOperations(id INTEGER PRIMARY KEY, op TEXT); INSERT INTO SiteOperations(op) VALUES ('stage'),('publish');"
                + "CREATE TABLE environment_inventory(environment_key TEXT, app TEXT); INSERT INTO environment_inventory VALUES ('AB1|cm01.example.com','7zip');";
            cmd.ExecuteNonQuery();
        }
        SQLiteConnection.ClearAllPools();
    }

    private static string Sha(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>Relative path to hash, for every file under a folder. Empty when the folder is absent.</summary>
    private static SortedDictionary<string, string> Snapshot(string folder)
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return map;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(folder, f)] = Sha(f);
        return map;
    }

    private SortedDictionary<string, string> Profile()
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Snapshot(Root)) map["Root\\" + k] = v;
        foreach (var (k, v) in Snapshot(LegacyRoot)) map["Legacy\\" + k] = v;
        return map;
    }

    private static Dictionary<string, DateTime> Times(string folder) =>
        !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Dry_run_lists_every_item_with_a_hash_and_writes_nothing()
    {
        SeedLegacyProfile();
        var before = Profile();
        var report = SiteDataMigration.Plan(Options());
        Assert.Equal("DryRun", report.Outcome);
        Assert.Equal("AB1", report.SiteCode);
        Assert.False(Directory.Exists(DataRoot));
        Assert.Equal(before, Profile());
        foreach (var i in report.Items.Where(i => i.Status == SiteMigrationStatus.Planned))
        {
            Assert.Equal("site", i.Scope);
            Assert.False(string.IsNullOrEmpty(i.Source));
            Assert.False(string.IsNullOrEmpty(i.Destination));
            Assert.Equal(64, i.Sha256.Length);
        }
        var text = report.ToText();
        Assert.Contains("deployment-audit.jsonl", text);
        Assert.Contains("history.db", text);
    }

    [Fact]
    public void Run_copies_every_site_file_with_an_equal_hash_and_leaves_the_old_profile_unchanged()
    {
        SeedLegacyProfile();
        var before = Profile();
        var report = SiteDataMigration.Run(Options());
        Assert.Equal("Migrated", report.Outcome);
        Assert.Equal(before, Profile());   // every old file: same path, same SHA-256, nothing added, nothing removed

        var site = Path.Combine(DataRoot, "sites", "AB1");
        foreach (var i in report.Items.Where(i => i.Status == SiteMigrationStatus.Copied && i.Id is not ("site.json" or "settings" or "app-state")))
        {
            Assert.Equal(Sha(i.Source), Sha(i.Destination));
            Assert.Equal(Sha(i.Source), i.Sha256, ignoreCase: true);
            Assert.StartsWith(site, i.Destination, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(Sha(Path.Combine(LegacyRoot, "history.db")), Sha(Path.Combine(site, "results", "history.db")));
        Assert.Equal(Sha(Path.Combine(Root, @"Deployments\deployment-audit.jsonl")), Sha(Path.Combine(site, @"deployments\audit.jsonl")));
        Assert.Equal(Sha(Path.Combine(Root, @"Deployments\Rings\runs\run-001.json")), Sha(Path.Combine(site, @"deployments\rings\runs\run-001.json")));
        Assert.Equal(Sha(Path.Combine(Root, @"SiteHygiene\suppressions.json")), Sha(Path.Combine(site, @"hygiene\suppressions.json")));
        Assert.Equal(Sha(Path.Combine(Root, @"PivotsScripts\Sessions\s1.json")), Sha(Path.Combine(site, @"pivots\sessions\s1.json")));
        Assert.Equal(Sha(Path.Combine(LegacyRoot, "CheckConfigMgrResult.csv")), Sha(Path.Combine(site, @"results\CheckConfigMgrResult.csv")));
        Assert.True(File.Exists(Path.Combine(site, "site.json")));
        Assert.True(File.Exists(Path.Combine(site, SiteDataMigration.MarkerFileName)));

        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(site, "settings.json")));
        Assert.Equal("cm01.example.com", settings.RootElement.GetProperty("ProviderMachine").GetString());
        Assert.Equal("AB1", settings.RootElement.GetProperty("CveMonitorSiteCode").GetString());
        Assert.Equal("Pilot DPs", settings.RootElement.GetProperty("DistributionOverrides").GetProperty("7zip").GetProperty("DistributionPointGroup").GetString());
        Assert.False(settings.RootElement.TryGetProperty("Theme", out _));    // global
        Assert.False(settings.RootElement.TryGetProperty("Intune", out _));   // global and holds a secret
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(site, @"state\app-state.json")));
        var app = state.RootElement.GetProperty("7zip");
        Assert.Equal("24.08", app.GetProperty("CurrentVersion").GetString());
        Assert.False(app.TryGetProperty("LatestVersion", out _));             // global
        using var siteJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(site, "site.json")));
        Assert.Equal("legacy-single-site", siteJson.RootElement.GetProperty("migratedFrom").GetString());
        Assert.Equal("cm01.example.com", siteJson.RootElement.GetProperty("providers")[0].GetString());
    }

    [Fact]
    public void Global_items_stay_and_no_secret_reaches_a_site_folder()
    {
        SeedLegacyProfile();
        var report = SiteDataMigration.Run(Options());
        var site = Path.Combine(DataRoot, "sites");
        var all = string.Concat(Directory.EnumerateFiles(site, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("history.db")).Select(File.ReadAllText));
        foreach (var secret in new[] { SecretGitHub, SecretIntune, SecretFeed, SecretNvd })
            Assert.DoesNotContain(secret, all);
        Assert.Contains(report.Items, i => i.Id == "apd.github-token" && i.Status == SiteMigrationStatus.SecretNotCopied);
        Assert.Contains(report.Items, i => i.Id == "apd.ui" && i.Status == SiteMigrationStatus.GlobalStays);
        // Global stores are not copied into the site folder.
        Assert.False(Directory.Exists(Path.Combine(site, "AB1", "Workbench")));
        Assert.False(File.Exists(Path.Combine(site, "AB1", "condition-templates.json")));
        Assert.False(File.Exists(Path.Combine(site, "AB1", @"collections\templates\q1.json")));
        Assert.False(Directory.Exists(Path.Combine(site, "AB1", "logs")));
    }

    [Fact]
    public void A_second_run_changes_nothing_and_says_so()
    {
        SeedLegacyProfile();
        SiteDataMigration.Run(Options());
        var tree = Snapshot(DataRoot);
        var times = Times(DataRoot);
        var again = SiteDataMigration.Run(Options());
        Assert.Equal("NothingChanged", again.Outcome);
        Assert.True(again.ChangedNothing);
        Assert.Equal(0, again.Count(SiteMigrationStatus.Copied));
        Assert.True(again.Count(SiteMigrationStatus.AlreadyDone) > 10);
        Assert.Equal(tree, Snapshot(DataRoot));
        Assert.Equal(times, Times(DataRoot));   // not even a rewrite
    }

    [Fact]
    public void A_crash_after_any_item_resumes_to_the_same_result_as_a_clean_run()
    {
        SeedLegacyProfile();
        SiteDataMigration.Run(Options());
        var clean = Snapshot(DataRoot);
        var copied = SiteDataMigration.Run(Options()).Count(SiteMigrationStatus.AlreadyDone);
        Directory.Delete(DataRoot, recursive: true);
        var before = Profile();

        for (var crashAt = 1; crashAt < copied; crashAt++)
        {
            var n = 0;
            Assert.Throws<SimulatedCrash>(() => SiteDataMigration.Run(Options((stage, _) => { if (stage == "item-done" && ++n == crashAt) throw new SimulatedCrash(); })));
            Assert.Equal(before, Profile());
            var resumed = SiteDataMigration.Run(Options());
            Assert.DoesNotContain(resumed.Items, i => i.Status is SiteMigrationStatus.Conflict or SiteMigrationStatus.Failed);
            Assert.Equal(clean, Snapshot(DataRoot));
            Assert.Equal(before, Profile());
            Assert.Equal("NothingChanged", SiteDataMigration.Run(Options()).Outcome);
            Directory.Delete(DataRoot, recursive: true);
        }
    }

    [Fact]
    public void A_crash_in_the_middle_of_a_copy_leaves_no_partial_file_at_the_destination()
    {
        SeedLegacyProfile();
        var before = Profile();
        string? crashed = null;
        Assert.Throws<SimulatedCrash>(() => SiteDataMigration.Run(Options((stage, dest) =>
        {
            if (stage == "temp-written" && dest.EndsWith("last-scan.json")) { crashed = dest; throw new SimulatedCrash(); }
        })));
        Assert.NotNull(crashed);
        Assert.False(File.Exists(crashed));                      // only the .migrating file exists
        Assert.True(File.Exists(crashed + ".migrating"));
        Assert.Equal(before, Profile());
        var resumed = SiteDataMigration.Run(Options());
        Assert.DoesNotContain(resumed.Items, i => i.Status is SiteMigrationStatus.Conflict or SiteMigrationStatus.Failed);
        Assert.False(File.Exists(crashed + ".migrating"));
        Assert.Equal(Sha(Path.Combine(Root, @"SiteHygiene\last-scan.json")), Sha(crashed!));
    }

    [Fact]
    public void A_destination_with_different_content_is_kept_and_reported_and_the_rest_still_copies()
    {
        SeedLegacyProfile();
        var dest = Path.Combine(DataRoot, @"sites\AB1\hygiene\suppressions.json");
        Write(DataRoot, @"sites\AB1\hygiene\suppressions.json", "{\"suppressed\":[\"SOMETHING-ELSE\"]}");
        var existing = Sha(dest);
        var before = Profile();
        var report = SiteDataMigration.Run(Options());
        Assert.Equal(existing, Sha(dest));
        Assert.Equal(before, Profile());
        var conflict = Assert.Single(report.Items, i => i.Status == SiteMigrationStatus.Conflict);
        Assert.EndsWith("suppressions.json", conflict.Destination);
        Assert.Equal("MigratedWithConflicts", report.Outcome);
        Assert.True(File.Exists(Path.Combine(DataRoot, @"sites\AB1\hygiene\last-scan.json")));
        var again = SiteDataMigration.Run(Options());
        Assert.Equal("NothingChangedWithConflicts", again.Outcome);
        Assert.Equal(existing, Sha(dest));
    }

    [Fact]
    public void A_source_that_changes_after_the_copy_is_reported_and_the_copy_is_not_overwritten()
    {
        SeedLegacyProfile();
        SiteDataMigration.Run(Options());
        var dest = Path.Combine(DataRoot, @"sites\AB1\hygiene\last-scan.json");
        var copyHash = Sha(dest);
        Write(Root, @"SiteHygiene\last-scan.json", "{\"scanned\":\"2026-11-21\"}");
        var again = SiteDataMigration.Run(Options());
        Assert.Contains(again.Items, i => i.Status == SiteMigrationStatus.SourceChanged && i.Destination == dest);
        Assert.Equal(copyHash, Sha(dest));
    }

    [Fact]
    public void A_copy_that_does_not_match_its_source_stops_the_run_and_the_old_files_stay()
    {
        SeedLegacyProfile();
        var before = Profile();
        var report = SiteDataMigration.Run(Options((stage, dest) =>
        {
            if (stage == "temp-written" && dest.EndsWith("last-scan.json")) File.AppendAllText(dest + ".migrating", "corrupt");
        }));
        Assert.Equal("Failed", report.Outcome);
        var failed = Assert.Single(report.Items, i => i.Status == SiteMigrationStatus.Failed);
        Assert.EndsWith("last-scan.json", failed.Destination);
        Assert.False(File.Exists(failed.Destination));
        Assert.False(File.Exists(failed.Destination + ".migrating"));
        Assert.Equal(before, Profile());
    }

    [Theory]
    [InlineData("never-saved")]
    [InlineData("invalid-code")]
    [InlineData("reserved-code")]
    [InlineData("no-settings-file")]
    public void No_site_code_means_nothing_is_migrated_and_the_report_says_why(string kind)
    {
        SeedLegacyProfile(siteCode: kind == "invalid-code" ? "TOOLONG" : kind == "reserved-code" ? "PRN" : "AB1", saved: kind != "never-saved");
        if (kind == "no-settings-file") File.Delete(Path.Combine(Root, "settings.json"));
        var before = Profile();
        foreach (var report in new[] { SiteDataMigration.Plan(Options()), SiteDataMigration.Run(Options()) })
        {
            Assert.Equal("NothingMigrated", report.Outcome);
            Assert.Contains("No site code is configured", report.Reason);
            Assert.Empty(report.Items);
            Assert.Null(report.SiteCode);
        }
        Assert.False(Directory.Exists(DataRoot));
        Assert.Equal(before, Profile());
    }

    [Fact]
    public void A_database_in_use_is_blocked_not_copied()
    {
        SeedLegacyProfile();
        File.WriteAllBytes(Path.Combine(LegacyRoot, "history.db-wal"), [1, 2, 3]);
        var report = SiteDataMigration.Run(Options());
        Assert.Contains(report.Items, i => i.Status == SiteMigrationStatus.Blocked && i.Source.EndsWith("history.db"));
        Assert.False(File.Exists(Path.Combine(DataRoot, @"sites\AB1\results\history.db")));
        Assert.Equal("MigratedWithConflicts", report.Outcome);
    }

    [Fact]
    public void A_lower_case_site_code_gives_the_upper_case_folder()
    {
        SeedLegacyProfile(siteCode: "ab1");
        var report = SiteDataMigration.Run(Options());
        Assert.Equal("AB1", report.SiteCode);
        Assert.True(Directory.Exists(Path.Combine(DataRoot, "sites", "AB1")));
    }

    [Fact]
    public void A_profile_with_only_some_stores_migrates_what_exists()
    {
        Write(Root, "settings.json", "{\"SiteCode\":\"CD2\",\"SiteCodeSaved\":true,\"ProviderMachine\":\"\"}");
        var before = Profile();
        var report = SiteDataMigration.Run(Options());
        Assert.Equal("Migrated", report.Outcome);
        Assert.Equal(before, Profile());
        Assert.True(File.Exists(Path.Combine(DataRoot, @"sites\CD2\settings.json")));
        Assert.False(Directory.Exists(Path.Combine(DataRoot, @"sites\CD2\hygiene")));
        Assert.Equal("NothingChanged", SiteDataMigration.Run(Options()).Outcome);
    }


    [Fact]
    public void A_settings_file_written_by_another_app_is_merged_and_its_keys_are_kept()
    {
        SeedLegacyProfile();
        Write(DataRoot, @"sites\AB1\settings.json", "{\"SiteCode\":\"AB1\",\"ProviderMachineName\":\"cm01.example.com\",\"FromOtherApp\":{\"a\":1}}");
        var report = SiteDataMigration.Run(Options());
        Assert.Equal("Migrated", report.Outcome);
        using var merged = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataRoot, @"sites\AB1\settings.json")));
        Assert.Equal(1, merged.RootElement.GetProperty("FromOtherApp").GetProperty("a").GetInt32());
        Assert.Equal("cm01.example.com", merged.RootElement.GetProperty("ProviderMachine").GetString());
        Assert.Equal("AB1", merged.RootElement.GetProperty("SiteCode").GetString());
        var tree = Snapshot(DataRoot);
        Assert.Equal("NothingChanged", SiteDataMigration.Run(Options()).Outcome);
        Assert.Equal(tree, Snapshot(DataRoot));
    }

    [Fact]
    public void A_settings_key_with_another_value_is_a_conflict_and_the_file_is_not_written()
    {
        SeedLegacyProfile();
        var dest = Path.Combine(DataRoot, @"sites\AB1\settings.json");
        Write(DataRoot, @"sites\AB1\settings.json", "{\"FileShareRoot\":\"other.example.com-share\"}");
        var existing = Sha(dest);
        var report = SiteDataMigration.Run(Options());
        Assert.Equal(existing, Sha(dest));
        var conflict = Assert.Single(report.Items, i => i.Status == SiteMigrationStatus.Conflict);
        Assert.Contains("FileShareRoot", conflict.Detail);
    }

    [Fact]
    public void Every_apd_entry_of_the_scope_manifest_is_accounted_for_with_the_right_scope()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteData", "scope-manifest.json")));
        var site = SiteDataMigration.SiteGroups.Values.SelectMany(v => v).ToHashSet();
        var global = SiteDataMigration.GlobalIds.ToHashSet();
        var secret = SiteDataMigration.SecretIds.ToHashSet();
        var seen = 0;
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;
            if (!id.StartsWith("apd.", StringComparison.Ordinal)) continue;
            seen++;
            var scope = e.GetProperty("scope").GetString();
            Assert.True(scope switch { "site" => site.Contains(id), "global" => global.Contains(id), "secret" => secret.Contains(id), _ => false }, $"{id} ({scope}) is not handled by the migration with that scope");
        }
        Assert.Equal(site.Count + global.Count + secret.Count, seen);
    }

    private sealed class SimulatedCrash : Exception;
}
