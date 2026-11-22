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
        ["site-cases.json"] = "bef79e14b8a5628b6758f105dba609105a8529a4820d627b751203b2fa0c3e45",
        ["site.json"] = "d06e0b9cd62833749a1ee85c324b29f33ac475c29ffe85e798b67a9e38307afe",
        ["active-site.json"] = "668f50612694c88f6ef1c7dacf8c6e3d9b5b08ff5e71c8f3c77d5a60826202ee",
        ["site.schema.json"] = "677036729cf4f1752025a85ce9802cf607340b7e7c4b96ee13a06450d48658c3",
        ["active-site.schema.json"] = "2d1c07be15d6794f47df54be5e83e4c7ce38afe27a59e138b084f55ea678ef0c",
        ["scope-manifest.schema.json"] = "8e9ed900f7146eda3a1bcced89c6a7b54c5ece292ce9c7b7bbeb91e2e995e345",
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
        Assert.Throws<ArgumentException>(() => SiteDataPaths.GetSiteFolder("PCM", "a", "b", "C:\\x"));
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
            Assert.Equal(new ActiveSite("PCM", "active-site.json"), fromFile);

            string? Env(string n) => n == "APP_PACKAGER_SITE" ? "dcm" : null;
            Assert.Equal(new ActiveSite("DCM", "APP_PACKAGER_SITE"), SiteDataPaths.GetActiveSite(root, Env));
            string? Both(string n) => n == "SUITE_CM_SITECODE" ? "TCM~2" : "dcm";
            Assert.Equal(new ActiveSite("TCM~2", "SUITE_CM_SITECODE"), SiteDataPaths.GetActiveSite(root, Both));
            Assert.Equal(fromFile, SiteDataPaths.GetActiveSite(root, n => n == "SUITE_CM_SITECODE" ? "..\\x" : null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"siteCode\":\"PCM\",\"changedUtc\":\"2026-11-22T10:30:00Z\"}")]
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
