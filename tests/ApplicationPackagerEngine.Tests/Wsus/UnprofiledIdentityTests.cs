using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// A catalog definition staged without a profile carries its catalog ApplicationId, so
/// every release maps to one WSUS and Intune identity and a newer release supersedes
/// the older one.
/// </summary>
public sealed class UnprofiledIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-unprofiled-id-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task TwoVersionsStagedWithoutAProfileShareOneIdentityAndTheOlderIsSuperseded()
    {
        byte[] Exe() => [(byte)'M', (byte)'Z', .. new byte[64]];
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["widget-25.01.exe"] = Exe(), ["widget-26.03.exe"] = Exe() });
        var def = ProfileStageFixture.Definition(server);
        def.Id = "sevenzip";
        var staging = ProfileStageFixture.Service();

        var older = await staging.StageAsync(def, _root, knownVersion: "25.01");
        var newer = await staging.StageAsync(def, _root, knownVersion: "26.03");
        Assert.True(older.Success, older.ErrorMessage);
        Assert.True(newer.Success, newer.ErrorMessage);
        var olderManifest = new ManifestService().Read(older.ManifestPath);
        var newerManifest = new ManifestService().Read(newer.ManifestPath);

        Assert.Equal("catalog:sevenzip", olderManifest.ApplicationId);
        Assert.Equal("", newerManifest.BuildId);
        var identity = WsusIdentity.IdentityTag(newerManifest);
        Assert.Equal("AppPackager:catalog:sevenzip/default", identity);
        Assert.Equal(identity, WsusIdentity.IdentityTag(olderManifest));
        Assert.Equal(identity, IntunePublishService.IdentityTag(newerManifest));

        var published = new WsusUpdateCandidate(Guid.NewGuid().ToString(), "Widget 25.01", WsusIdentity.Description(olderManifest),
            DateTimeOffset.Now.AddDays(-7), false, "Published");
        var decision = WsusSupersedence.Decide([published], identity, newerManifest.SoftwareVersion);
        Assert.Equal(published.PackageId, Assert.Single(decision.Earlier).PackageId);
    }

    [Fact]
    public void AnUpdateUnderTheFormerVersionedLegacyTagIsSupersededToo()
    {
        var manifest = new StageManifest { AppName = "7-Zip (x64) - 26.03 (x64)", Publisher = "Igor Pavlov", SoftwareVersion = "26.03" };
        var older = new StageManifest { AppName = "7-Zip (x64) - 26.01 (x64)", Publisher = "Igor Pavlov", SoftwareVersion = "26.01" };
        var formerLine = WsusIdentity.IdentityLine("AppPackager:legacy:Igor-Pavlov:7-Zip-x64-26-x64/default", WsusSettings.UpdatePackageType, "26.01");
        var candidate = new WsusUpdateCandidate(Guid.NewGuid().ToString(), "7-Zip", "7-Zip 26.01\r\n\r\n" + formerLine,
            DateTimeOffset.Now.AddDays(-7), false, "Published");

        var decision = WsusSupersedence.Decide([candidate], WsusIdentity.IdentityTag(manifest), "26.03", WsusIdentity.FormerIdentityTags(manifest));

        Assert.Single(decision.Earlier);
        Assert.Equal(WsusIdentity.IdentityTag(older), WsusIdentity.IdentityTag(manifest));
    }
}
