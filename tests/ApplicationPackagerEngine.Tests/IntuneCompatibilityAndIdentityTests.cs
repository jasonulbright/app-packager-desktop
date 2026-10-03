using System.Net;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Intune compatibility findings are decided before any Graph call and a blocking
/// finding refuses the publish. The existing app is matched by the identity tag in
/// notes first, then by display name, and ambiguity on either path refuses the publish.
/// </summary>
public sealed class IntuneCompatibilityAndIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-intune-compat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static StageManifest Manifest() => new()
    {
        AppName = "Contoso App",
        Publisher = "Contoso",
        SoftwareVersion = "1.0",
        SetupFile = "install.bat",
        Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Contoso", ExpectedValue = "1.0", Operator = "GreaterEquals" }
    };

    private static IntunePublishOptions Options() => new()
    {
        TenantId = "t", ClientId = "c", ClientSecret = "s",
        PollInterval = TimeSpan.Zero, CommitPollInterval = TimeSpan.Zero, PollTimeoutSec = 30
    };

    private static string[] Codes(StageManifest m, IReadOnlyList<StageRequirement>? extra = null) =>
        IntuneCompatibilityFindings.Build(m, extra).Select(f => $"{f.Severity}:{f.Code}").ToArray();

    // ── Findings ────────────────────────────────────────────────

    [Fact]
    public void APlainManifestCarriesNoFinding() => Assert.Empty(Codes(Manifest()));

    [Fact]
    public void VariantsAndAnInteractiveInstallBlock()
    {
        var m = Manifest();
        m.DeploymentTypes = [new StageDeploymentType { NameSuffix = "x64" }];
        m.RequireUserInteraction = true;
        Assert.Equal(["Blocking:MultipleDeploymentTypes", "Blocking:InteractiveInstall"], Codes(m));
    }

    [Fact]
    public void UntranslatedRequirementsAndAMissingArchitectureNeedReview()
    {
        var m = Manifest();
        m.Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "x64" }];
        m.Architecture = "";
        var findings = IntuneCompatibilityFindings.Build(m, [new StageRequirement { ConditionId = "vpn-connected", Value = true }]);
        Assert.Equal(["Review:RequirementsNotTranslated", "Review:ArchitectureUnresolved"], findings.Select(f => $"{f.Severity}:{f.Code}"));
        Assert.Contains("carries 2 ConfigMgr requirement rule(s)", findings[0].Message);
    }

    [Fact]
    public void AnUnmappedArchitectureAndAnUnmappableDetectionBlock()
    {
        var m = Manifest();
        m.Architecture = "ia64";
        m.Detection = new StageDetection { Type = "Compound", Connector = "Or", Clauses = [new() { Type = "RegistryKey", RegistryKeyRelative = "A" }, new() { Type = "RegistryKey", RegistryKeyRelative = "B" }] };
        Assert.Equal(["Blocking:DetectionNotMappable", "Blocking:ArchitectureUnsupported"], Codes(m));
    }

    [Fact]
    public void RecordedTimingAndAPackageSetupFileAreInformational()
    {
        var m = Manifest();
        m.SetupFile = null;
        m.Timing = new StageTiming { EstimatedMinutes = 10, MaximumMinutes = 60 };
        Assert.Equal(["Info:TimingNotApplied", "Info:SetupFileFromPackage"], Codes(m));
    }

    [Theory]
    [InlineData("x64", "x64")]
    [InlineData("AMD64", "x64")]
    [InlineData("32-bit", "x86")]
    [InlineData("ARM64", "arm64")]
    [InlineData("aarch64", "arm64")]
    [InlineData("ia64", "")]
    public void TheArchitectureMapsOntoAGraphValue(string input, string expected) =>
        Assert.Equal(expected, IntuneCompatibilityFindings.AllowedArchitecture(input));

    [Fact]
    public async Task ABlockingFindingRefusesBeforeAnyGraphCall()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = new FakeGraphHandler((_, _) => FakeGraphHandler.Json("{}"));
        using var svc = new IntunePublishService(handler);
        var m = Manifest();
        m.RequireUserInteraction = true;
        var log = new List<string>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishAsync(package, m, Options(), log.Add));

        Assert.StartsWith("This application cannot be published to Intune: InteractiveInstall: ", ex.Message);
        Assert.Contains(log, l => l.StartsWith("Intune compatibility         : [Blocking] InteractiveInstall - ", StringComparison.Ordinal));
        Assert.Empty(handler.Requests);
    }

    // ── Identity tag ────────────────────────────────────────────

    [Fact]
    public void TheIdentityTagNamesTheApplicationAndProfile()
    {
        var m = Manifest();
        m.ApplicationId = "catalog:contoso";
        m.ProfileId = "pilot01";
        Assert.Equal("AppPackager:catalog:contoso/pilot01", IntunePublishService.IdentityTag(m));

        var legacy = Manifest();
        legacy.AppName = "Contoso App (x64) 1.0";
        legacy.ProfileId = "";
        Assert.Equal("AppPackager:legacy:Contoso-App--x64--1.0/default", IntunePublishService.IdentityTag(legacy));
    }

    private static FakeGraphHandler Graph(Func<string, string?> list)
    {
        return new FakeGraphHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (uri.Contains("/oauth2/v2.0/token")) return FakeGraphHandler.Json("""{"access_token":"t"}""");
            if (uri.Contains("blob.core.windows.net")) return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") };
            if (uri.Contains("mobileApps?$filter=")) return FakeGraphHandler.Json(list(uri) ?? """{"value":[]}""");
            if (uri.EndsWith("/contentVersions") && request.Method == HttpMethod.Post) return FakeGraphHandler.Json("""{"id":"7"}""");
            if (uri.EndsWith("/files") && request.Method == HttpMethod.Post) return FakeGraphHandler.Json("""{"id":"file-1"}""");
            if (uri.EndsWith("/commit")) return FakeGraphHandler.Empty();
            if (uri.Contains("/files/file-1")) return FakeGraphHandler.Json("""{"uploadState":"commitFileSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1"}""");
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch) return FakeGraphHandler.Json("""{"id":"app-new"}""");
            return FakeGraphHandler.Json("{}");
        });
    }

    [Fact]
    public async Task ANewAppCarriesTheTagInNotes()
    {
        var handler = Graph(_ => null);
        using var svc = new IntunePublishService(handler);
        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        var create = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        Assert.Contains("\"notes\":\"AppPackager:legacy:Contoso-App/default\"", create.Body);
        Assert.Contains("\"deviceRestartBehavior\":\"basedOnReturnCode\"", create.Body);
    }

    [Fact]
    public async Task ARenamedAppIsFoundByItsTagOnALaterPage()
    {
        const string tag = "AppPackager:legacy:Contoso-App/default";
        var handler = Graph(uri =>
            uri.Contains("skiptoken=2")
                ? $$"""{"value":[{"id":"app-tagged","@odata.type":"#microsoft.graph.win32LobApp","displayName":"Renamed","notes":"{{tag}}"}]}"""
                : uri.Contains("isof(")
                    ? """{"value":[{"id":"other","@odata.type":"#microsoft.graph.win32LobApp","notes":""}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps?$filter=isof('microsoft.graph.win32LobApp')&$skiptoken=2"}"""
                    : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Patch && r.Uri.EndsWith("/mobileApps/app-tagged"));
        Assert.DoesNotContain(handler.Requests, r => Uri.UnescapeDataString(r.Uri).Contains("displayName eq"));
    }

    [Fact]
    public async Task TwoTaggedAppsRefuseThePublish()
    {
        const string tag = "AppPackager:legacy:Contoso-App/default";
        var handler = Graph(uri => uri.Contains("isof(")
            ? $$"""{"value":[{"id":"a","@odata.type":"#microsoft.graph.win32LobApp","notes":"{{tag}}"},{"id":"b","@odata.type":"#microsoft.graph.win32LobApp","notes":"{{tag}}"}]}"""
            : null);
        using var svc = new IntunePublishService(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));
        Assert.Equal($"Multiple Intune apps carry the identity tag '{tag}' (ids: a, b); resolve the duplicates before publishing.", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method != HttpMethod.Get && !r.Uri.Contains("oauth2"));
    }

    [Fact]
    public async Task TwoUntaggedAppsWithTheNameRefuseThePublish()
    {
        var handler = Graph(uri => uri.Contains("displayName eq")
            ? """{"value":[{"id":"a","@odata.type":"#microsoft.graph.win32LobApp"},{"id":"b","@odata.type":"#microsoft.graph.win32LobApp"}]}"""
            : null);
        using var svc = new IntunePublishService(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));
        Assert.Equal("Multiple Intune apps are named 'Contoso App' and none carries this application's identity tag (ids: a, b); rename or tag the intended app before publishing.", ex.Message);
    }

    [Fact]
    public async Task OneUntaggedAppWithTheNameIsUpdated()
    {
        var handler = Graph(uri => uri.Contains("displayName eq")
            ? """{"value":[{"id":"named","@odata.type":"#microsoft.graph.win32LobApp"}]}"""
            : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Patch && r.Uri.EndsWith("/mobileApps/named"));
    }

    [Theory]
    [InlineData(null, "basedOnReturnCode")]
    [InlineData("BasedOnExitCode", "basedOnReturnCode")]
    [InlineData("NoAction", "allow")]
    [InlineData("ProgramReboot", "allow")]
    [InlineData("ForceReboot", "force")]
    public void ThePostExecutionBehaviorMapsOntoTheRestartBehavior(string? behavior, string expected) =>
        Assert.Equal(expected, IntunePublishService.DeviceRestartBehavior(behavior));
}
