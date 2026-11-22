using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Contract tests for the per-site data layout (Q-112b). The fixtures in Fixtures\SiteData are the files of the
/// app-packager-suite repository (Tests\Fixtures\SiteData and docs\schema). Both repositories pin the SHA-256 of
/// every file, line endings normalized, so a change of one copy fails the test of the other until both change.
/// The same cases drive SuiteCommon in PowerShell. Schema validation runs in the suite repository.
/// </summary>
public sealed class SiteDataPathsTests
{
    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteData", name);

    private static JsonElement Load(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Fx(name)));
        return doc.RootElement.Clone();
    }

    private static string NormalizedHash(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static readonly Dictionary<string, string> Pins = new()
    {
        ["scope-manifest.json"] = "b6ef8da676de41bdd067f390baa586bdacd2dcf85284d5f05170d13fed8f3307",
        ["site-cases.json"] = "e4084aa3c3da1774046bd75d5cab1dd97157b9dfa4b6f0c2849e12b29d7096ca",
        ["site.json"] = "33d8b80f2f64057407b1907efb66cd28f3fded7151397ccf5f35be68150215ca",
        ["active-site.json"] = "1a82f657c57f72a8f21ef2445dab9225a3a91e9c2e748d3802dca40ed7e30500",
        ["site.schema.json"] = "9a4283e4ee0bd3b0c0dfd4133c78f9765e058adc12d967e0a6a3a8db285f2007",
        ["active-site.schema.json"] = "a26607a3a9e7b32c765e986d9144161c1beb3712a952452ae0f69e78b576b902",
        ["scope-manifest.schema.json"] = "1d90288bed5aaca55b471c530ea3974803cbdca02edb3041e1592434e5c0788a",
    };

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string? NullableString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Fact]
    public void Fixtures_and_schemas_match_the_pinned_hashes_shared_with_the_suite_repository()
    {
        foreach (var (name, hash) in Pins)
            Assert.True(hash == NormalizedHash(Fx(name)), $"{name} differs from the app-packager-suite copy.");
    }

    [Fact]
    public void Reserved_codes_match_the_fixture()
    {
        Assert.Equal(Strings(Load("site-cases.json").GetProperty("reservedSiteCodes")), SiteDataPaths.ReservedSiteCodes);
    }

    [Fact]
    public void Site_codes_follow_the_cases()
    {
        foreach (var c in Load("site-cases.json").GetProperty("siteCodes").EnumerateArray())
        {
            var value = c.GetProperty("value").GetString();
            Assert.True(c.GetProperty("valid").GetBoolean() == SiteDataPaths.IsValidSiteCode(value), $"site code '{value}'");
        }
    }

    [Fact]
    public void Folder_names_follow_the_cases()
    {
        foreach (var c in Load("site-cases.json").GetProperty("folderNames").EnumerateArray())
        {
            var value = c.GetProperty("value").GetString();
            var valid = c.GetProperty("valid").GetBoolean();
            Assert.True(valid == SiteDataPaths.TryParseFolderName(value, out var parsed), $"folder name '{value}'");
            Assert.Equal(valid, SiteDataPaths.IsValidFolderName(value));
            if (!valid)
            {
                Assert.Null(parsed);
                continue;
            }
            Assert.Equal(c.GetProperty("siteCode").GetString(), parsed!.SiteCode);
            Assert.Equal(c.GetProperty("ordinal").GetInt32(), parsed.Ordinal);
        }
    }

    [Fact]
    public void Next_free_folder_name_follows_the_cases()
    {
        foreach (var c in Load("site-cases.json").GetProperty("nextFolderName").EnumerateArray())
        {
            var code = c.GetProperty("siteCode").GetString()!;
            var existing = Strings(c.GetProperty("existing"));
            Assert.Equal(c.GetProperty("expected").GetString(), SiteDataPaths.NextFreeFolderName(code, existing));
        }
        Assert.Throws<ArgumentException>(() => SiteDataPaths.NextFreeFolderName("AUX"));
    }

    [Fact]
    public void Tool_names_follow_the_cases()
    {
        foreach (var c in Load("site-cases.json").GetProperty("toolNames").EnumerateArray())
        {
            var value = c.GetProperty("value").GetString();
            Assert.True(c.GetProperty("valid").GetBoolean() == SiteDataPaths.IsValidPathSegment(value), $"tool '{value}'");
        }
    }

    [Fact]
    public void Site_folders_follow_the_relative_path_cases_and_create_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "SiteDataPathsTests-" + Guid.NewGuid().ToString("N"));
        foreach (var c in Load("site-cases.json").GetProperty("relativePaths").EnumerateArray())
        {
            var code = c.GetProperty("siteCode").GetString()!;
            var tool = NullableString(c, "tool");
            var area = NullableString(c, "area");
            var expected = NullableString(c, "expected");
            if (expected is null)
                Assert.Throws<ArgumentException>(() => SiteDataPaths.GetSiteFolder(code, tool, area, root));
            else
                Assert.Equal(Path.Combine(root, expected), SiteDataPaths.GetSiteFolder(code, tool, area, root));
        }
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Tool_and_area_are_exclusive()
    {
        Assert.Throws<ArgumentException>(() => SiteDataPaths.GetSiteFolder("AB1", "a", "b", "C:\\x"));
    }

    [Fact]
    public void Data_root_defaults_to_AppPackagerData_and_scopes_an_override_to_the_user()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPackagerData"),
            SiteDataPaths.ResolveDataRoot(null));
        var shared = Path.Combine(Path.GetTempPath(), "SiteDataShared");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var scoped = Path.Combine(shared, "Users", sid);
        Assert.Equal(scoped, SiteDataPaths.ResolveDataRoot(shared));
        Assert.Equal(scoped, SiteDataPaths.ResolveDataRoot(scoped));
    }

    [Fact]
    public void Data_root_refuses_Program_Files()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Throws<UnauthorizedAccessException>(() => SiteDataPaths.ResolveDataRoot(Path.Combine(pf, "x")));
    }

    [Fact]
    public void Active_site_reads_the_shared_fixture_and_prefers_the_environment()
    {
        var root = Path.Combine(Path.GetTempPath(), "SiteDataActive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(SiteDataPaths.GetActiveSite(root, _ => null));
            File.Copy(Fx("active-site.json"), Path.Combine(root, "active-site.json"));
            var fromFile = SiteDataPaths.GetActiveSite(root, _ => null);
            Assert.Equal(new ActiveSite("AB1", "active-site.json"), fromFile);

            string? Env(string n) => n == "APP_PACKAGER_SITE" ? "cd2" : null;
            Assert.Equal(new ActiveSite("CD2", "APP_PACKAGER_SITE"), SiteDataPaths.GetActiveSite(root, Env));
            string? Both(string n) => n == "SUITE_CM_SITECODE" ? "EF3~2" : "cd2";
            Assert.Equal(new ActiveSite("EF3~2", "SUITE_CM_SITECODE"), SiteDataPaths.GetActiveSite(root, Both));
            Assert.Equal(fromFile, SiteDataPaths.GetActiveSite(root, n => n == "SUITE_CM_SITECODE" ? "..\\x" : null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"siteCode\":\"AB1\",\"changedUtc\":\"2026-11-22T10:30:00Z\"}")]
    [InlineData("{\"schemaVersion\":1,\"siteCode\":\"AUX\",\"changedUtc\":\"2026-11-22T10:30:00Z\"}")]
    [InlineData("{\"schemaVersion\":1,\"siteCode\":5}")]
    [InlineData("[1]")]
    [InlineData("{not json")]
    public void Active_site_ignores_an_unusable_file(string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "SiteDataBad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "active-site.json"), content);
            Assert.Null(SiteDataPaths.GetActiveSite(root, _ => null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Scope_manifest_rules_hold()
    {
        var entries = Load("scope-manifest.json").GetProperty("entries").EnumerateArray().ToList();
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Count, entries.Select(e => e.GetProperty("id").GetString()).Distinct().Count());
        foreach (var e in entries)
        {
            var id = e.GetProperty("id").GetString();
            var scope = e.GetProperty("scope").GetString();
            var import = e.GetProperty("import").GetString();
            var target = NullableString(e, "target");
            if (scope == "secret") Assert.True(import == "never" && target is null, $"secret {id}");
            else if (scope == "site") Assert.False(string.IsNullOrEmpty(target), $"site entry {id} needs a target");
            else Assert.True(target is null, $"global entry {id} has no target");
        }
        foreach (var id in new[] { "apd.site-code", "apd.provider", "apd.check-cm-result", "apd.inventory", "apd.deployments", "apd.hygiene-stores", "suite.pivots-sessions" })
            Assert.Equal("never", entries.Single(e => e.GetProperty("id").GetString() == id).GetProperty("import").GetString());
    }

    [Fact]
    public void Site_and_active_site_fixtures_have_the_required_fields()
    {
        var site = Load("site.json");
        Assert.Equal(1, site.GetProperty("schemaVersion").GetInt32());
        Assert.True(SiteDataPaths.IsValidSiteCode(site.GetProperty("siteCode").GetString()));
        Assert.Equal(JsonValueKind.Array, site.GetProperty("providers").ValueKind);
        Assert.True(DateTime.TryParse(site.GetProperty("createdUtc").GetString(), out _));
        Assert.True(DateTime.TryParse(site.GetProperty("lastUsedUtc").GetString(), out _));
        var active = Load("active-site.json");
        Assert.Equal(1, active.GetProperty("schemaVersion").GetInt32());
        Assert.True(SiteDataPaths.IsValidFolderName(active.GetProperty("siteCode").GetString()));
    }
}
