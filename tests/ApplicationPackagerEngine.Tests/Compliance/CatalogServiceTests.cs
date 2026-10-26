using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Compliance;

public sealed class CatalogServiceTests
{
    private static readonly string[] ShippedIds =
    [
        "autorun-autoplay-disable", "installer-alwaysinstallelevated-disable", "powershell-scriptblock-logging", "rdp-nla-tls-require",
        "smbv1-disable", "tls-legacy-disable", "uac-remote-restriction", "acrobat-alltoolspane-fix", "do-downloadmode", "specexec-mitigations",
        "anonymous-enumeration-restrict", "llmnr-disable", "lsa-protection-runasppl", "ntlm-lmcompatibilitylevel", "smb-signing-require", "wdigest-disable"
    ];

    [Fact]
    public void All16ShippedEntriesConvertAndValidate()
    {
        var catalog = new CatalogService();

        Assert.Null(catalog.Message);
        Assert.Equal(ShippedIds.Order(), catalog.All().Select(e => e.Id).Order());
        Assert.Equal(7, catalog.All().Count(e => e.Subcategory == "Compliance"));
        Assert.Equal(6, catalog.All().Count(e => e.Subcategory == "Hardening"));
        Assert.Equal(2, catalog.All().Count(e => e.Subcategory == "Configuration"));
        Assert.Equal(1, catalog.All().Count(e => e.Subcategory == "CVE-Remediation"));
    }

    [Fact]
    public void EveryEntryHasItsBuilderAndTheHashOfTheEmbeddedBytes()
    {
        var catalog = new CatalogService();
        foreach (var entry in catalog.All())
        {
            var bytes = catalog.ReadBuilder(entry);
            Assert.NotNull(bytes);
            Assert.Equal(CatalogService.Sha256(bytes!), entry.BuilderSha256);
            Assert.Matches("^[0-9A-F]{64}$", entry.BuilderSha256);
            Assert.StartsWith(CatalogService.BuilderPrefix + "New-", entry.BuilderResource);
        }
        Assert.Equal(32, typeof(CatalogService).Assembly.GetManifestResourceNames().Count(n => n.StartsWith(CatalogService.ResourcePrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void LsaProtectionIsDetectionOnlyAndAcceptsBothValuesWithoutRewritingTheUefiChoice()
    {
        var catalog = new CatalogService();
        var entry = catalog.Find("lsa-protection-runasppl")!;
        var builder = Encoding.UTF8.GetString(catalog.ReadBuilder(entry)!);

        Assert.False(entry.RemediationSupported);
        Assert.Contains("Expected = @('1', '2')", builder);
        Assert.Contains("ExpressionOperator = 'OneOf'; ExpectedValue = $s.Expected", builder);
        Assert.Contains("-EnableEnforcement $false", builder);
        Assert.DoesNotContain("RemediateDword", builder);
        Assert.DoesNotContain("Remediate =", builder);
    }

    [Fact]
    public void SpecExecTakesTwoCollectionsAndProducesTwoPairs()
    {
        var entry = new CatalogService().Find("specexec-mitigations")!;

        Assert.Equal(["HtEnabledCollectionName", "HtDisabledCollectionName"], entry.CollectionParameters);
        Assert.Equal(2, entry.ProducesConfigurationItems.Count);
        Assert.Equal(2, entry.ProducesBaselines.Count);
        Assert.Equal(2, entry.Dependencies.Count);
        Assert.True(entry.RemediationSupported);
        Assert.All(entry.Dependencies, d => Assert.Null(d.CreatedBy));
        Assert.True(entry.RebootRequired);
        Assert.Equal("False", entry.Parameters.Single(p => p.Name == "DetectOnly").Default);
        Assert.Equal("Devices: Hyperthreading Enabled", entry.Parameters.Single(p => p.Name == "HtEnabledCollectionName").Default);
    }

    [Fact]
    public void NoShippedCatalogTextNamesASourceAnAuthorOrALicense()
    {
        var assembly = typeof(CatalogService).Assembly;
        var allowedUrl = new Regex(@"^https://learn\.microsoft\.com/", RegexOptions.CultureInvariant);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(CatalogService.ResourcePrefix, StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!, Encoding.UTF8);
            var text = reader.ReadToEnd();
            foreach (Match url in Regex.Matches(text, @"https?://[^\s""')]+"))
                Assert.True(allowedUrl.IsMatch(url.Value) && name.EndsWith(".json", StringComparison.Ordinal), $"{name} holds the address {url.Value}");
            Assert.DoesNotMatch(new Regex(@"(?i)\b(author|copyright|licen[cs]e|credit|written by|courtesy|\(c\))\b|community\.|github|reddit|stackoverflow|source:"), text);
            Assert.DoesNotMatch(new Regex(@"(?i)MECM\\|Registry\\New-SpecExecBitmask|bitmask tool"), text);
        }
    }

    [Fact]
    public void AnEntryThatFailsTheSchemaIsLeftOutAndNamed()
    {
        var good = File.ReadAllText(EntryPath("llmnr-disable"));
        var bad = good.Replace("\"Version\": \"1.0.0\"", "\"Version\": \"one\"");
        var resources = new Dictionary<string, byte[]>
        {
            [CatalogService.EntryPrefix + "llmnr-disable.json"] = Encoding.UTF8.GetBytes(good),
            [CatalogService.EntryPrefix + "broken.json"] = Encoding.UTF8.GetBytes(bad.Replace("llmnr-disable", "broken")),
            [CatalogService.EntryPrefix + "garbage.json"] = Encoding.UTF8.GetBytes("{ not json"),
            [CatalogService.BuilderPrefix + "New-LLMNRDisableBaseline.ps1"] = Encoding.UTF8.GetBytes("param()")
        };
        var catalog = new CatalogService(null, resources.Keys.ToList(), n => resources.TryGetValue(n, out var b) ? new MemoryStream(b) : null);

        Assert.Equal(["llmnr-disable"], catalog.All().Select(e => e.Id));
        Assert.Contains("broken", catalog.Message);
        Assert.Contains("garbage", catalog.Message);
    }

    [Fact]
    public void AnEntryWhoseBuilderIsMissingOrNamesAPathIsLeftOut()
    {
        var good = File.ReadAllText(EntryPath("llmnr-disable"));
        var resources = new Dictionary<string, byte[]>
        {
            [CatalogService.EntryPrefix + "llmnr-disable.json"] = Encoding.UTF8.GetBytes(good),
            [CatalogService.EntryPrefix + "climb.json"] = Encoding.UTF8.GetBytes(good.Replace("llmnr-disable", "climb").Replace("New-LLMNRDisableBaseline.ps1", "..\\\\..\\\\x.ps1"))
        };
        var catalog = new CatalogService(null, resources.Keys.ToList(), n => resources.TryGetValue(n, out var b) ? new MemoryStream(b) : null);

        Assert.Empty(catalog.All());
        Assert.Contains("climb", catalog.Message);
        Assert.Contains("llmnr-disable", catalog.Message);
    }

    public static TheoryData<string, string, string> SchemaCases => new()
    {
        { "\"Id\": \"llmnr-disable\"", "\"Id\": \"LLMNR_Disable\"", "Id must be kebab-case" },
        { "\"Version\": \"1.0.0\"", "\"Version\": \"1.0\"", "Version must be semver" },
        { "\"RebootRequired\": false", "\"RebootRequired\": \"no\"", "RebootRequired must be a boolean" },
        { "\"RebootRequired\": false,", "\"RebootRequired\": false,\n  \"RemediationSupported\": \"no\",", "RemediationSupported must be a boolean" },
        { "\"TenablePlugins\": []", "\"TenablePlugins\": [\"x\"]", "TenablePlugins must contain only integers" },
        { "\"Type\": \"Switch\"", "\"Type\": \"Bool\"", "Type must be one of: String, Switch, Int, StringArray" },
        { "\"DependsOn\": []", "\"DependsOn\": [ { \"Type\": \"Collection\", \"Name\": \"x\" } ]", "DependsOn[0].CreatedBy key is required" },
        { "\"ConfigurationItems\": [", "\"ConfigurationItems\": [], \"Unused\": [", "Produces.ConfigurationItems must list at least one artifact" },
        { "\"Severity\": \"Warning\",", "", "Missing required key: Severity" }
    };

    [Theory]
    [MemberData(nameof(SchemaCases))]
    public void TheSchemaChecksMatchTheCatalogSchema(string find, string replace, string expected)
    {
        var text = File.ReadAllText(EntryPath("llmnr-disable"));
        Assert.Contains(find, text);
        using var document = JsonDocument.Parse(text.Replace(find, replace));

        var errors = CatalogValidation.Validate(document.RootElement);

        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    internal static string EntryPath(string id) =>
        Path.Combine(RepositoryRoot(), "src", "ApplicationPackagerEngine", "Services", "Compliance", "Catalog", "Entries", id + ".json");

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ApplicationPackagerEngine"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
