using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// One Click rebuilds when the vendor version, the profile revision or the signing-policy
/// digest differs from the newest sealed build, and skips only when all three match.
/// </summary>
public sealed class OneClickFreshnessTests : IDisposable
{
    private const string App = "catalog:package-contoso";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-freshness-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Seal(string version, int revision, string digest, string buildId = "20261003-120000-aaaaaaaa")
    {
        var stage = Path.Combine(_root, "stage", buildId);
        Directory.CreateDirectory(stage);
        BuildRecords.Write(_root, stage, new StageManifest
        {
            ApplicationId = App,
            BuildId = buildId,
            ProfileId = ApplicationIds.DefaultProfileId,
            ProfileRevision = revision,
            SoftwareVersion = version,
            ScriptSigning = new JsonObject { ["PolicyDigest"] = digest }
        });
        return digest;
    }

    private static readonly string Digest = new SigningPolicy().PolicyDigest;

    [Fact]
    public void NoSealedBuildIsStale() =>
        Assert.False(BuildRecords.IsCurrent(_root, App, "default", "1.0", 0, Digest));

    [Fact]
    public void AllThreeMatchingIsCurrent()
    {
        Seal("1.2.3.0", 4, Digest);
        Assert.True(BuildRecords.IsCurrent(_root, App, "default", "1.2.3", 4, Digest));
    }

    [Theory]
    [InlineData("1.2.4", 4, false)]
    [InlineData("1.2.3", 5, false)]
    [InlineData("1.2.3", 4, true)]
    public void AnyDifferenceIsStale(string version, int revision, bool changePolicy)
    {
        Seal("1.2.3", 4, Digest);
        var digest = changePolicy ? new SigningPolicy { SignDetection = true }.PolicyDigest : Digest;
        Assert.False(BuildRecords.IsCurrent(_root, App, "default", version, revision, digest));
    }

    [Fact]
    public void TheNewestBuildDecides()
    {
        Seal("1.0", 1, Digest, "20261001-120000-aaaaaaaa");
        Seal("2.0", 1, Digest, "20261002-120000-bbbbbbbb");
        Assert.True(BuildRecords.IsCurrent(_root, App, "default", "2.0", 1, Digest));
        Assert.False(BuildRecords.IsCurrent(_root, App, "default", "1.0", 1, Digest));
    }

    [Fact]
    public void AnUnreadableIdentityLeavesTheDecisionToTheCaller() =>
        Assert.Null(BuildRecords.IsCurrent(_root, "not an id", "default", "1.0", 0, Digest));

    [Theory]
    [InlineData(" mcm ", "CM01.contoso.com ", "MCM", "cm01.contoso.com", true)]
    [InlineData("MCM", null, "MCM", "", true)]
    [InlineData("MCM", "cm01", "PS1", "cm01", false)]
    public void TheSiteIdentityIgnoresWhitespaceAndCase(string siteA, string? providerA, string siteB, string providerB, bool same) =>
        Assert.Equal(same, SiteIdentity.Key(siteA, providerA) == SiteIdentity.Key(siteB, providerB));

    [Fact]
    public void TheGlobalRemoveVersionReachesAPs1ChildWithoutAProfileMode()
    {
        var profile = WorkbenchProfile.CreateDefault("catalog:package-x");
        Assert.Equal("NoVersion", PackagerChildBridge.BuildLegacyVariables(profile, removeVersionFromTitle: true)[PackagerChildBridge.TitleModeVariable]);
        Assert.Equal("IncludeVersion", PackagerChildBridge.BuildLegacyVariables(profile, includeVersionInTitle: true, removeVersionFromTitle: true)[PackagerChildBridge.TitleModeVariable]);
        Assert.False(PackagerChildBridge.BuildLegacyVariables(profile).ContainsKey(PackagerChildBridge.TitleModeVariable));
    }
}
