using System.Net;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Graph splits Windows architecture across two properties on a Win32 app.
/// applicableArchitectures has no arm64 member, so arm64 travels in
/// allowedArchitectures and Graph forces applicableArchitectures to none whenever
/// allowedArchitectures is non-null. These tests read the request body the publish
/// actually sends rather than the helper that feeds it.
/// </summary>
public sealed class IntuneArchitectureWireTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "packr-arch-wire-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private const string NoApps = """{"value":[]}""";
    private const string ExistingApp =
        """{"value":[{"id":"app-existing","@odata.type":"#microsoft.graph.win32LobApp"}]}""";

    private static IntunePublishOptions Options(string architecture) => new()
    {
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = "secret",
        Architecture = architecture,
        PollInterval = TimeSpan.Zero,
        CommitPollInterval = TimeSpan.Zero,
        PollTimeoutSec = 30
    };

    private static StageManifest Manifest() => new()
    {
        AppName = "Contoso App",
        Publisher = "Contoso Ltd",
        SoftwareVersion = "1.2.3.4",
        Detection = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Contoso",
            ExpectedValue = "1.2.3.4",
            Operator = "GreaterEquals"
        }
    };

    private static FakeGraphHandler BuildHandler(string existingAppsJson) =>
        new((request, _) =>
        {
            var uri = request.RequestUri!.ToString();

            if (uri.Contains("/oauth2/v2.0/token"))
                return FakeGraphHandler.Json("""{"access_token":"fake-token","expires_in":3599}""");

            if (uri.Contains("blob.core.windows.net"))
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") };

            if (uri.Contains("mobileApps?$filter=") || uri.Contains("mobileApps?%24filter="))
                return FakeGraphHandler.Json(existingAppsJson);

            if (uri.EndsWith("/contentVersions") && request.Method == HttpMethod.Post)
                return FakeGraphHandler.Json("""{"id":"7"}""");

            if (uri.EndsWith("/files") && request.Method == HttpMethod.Post)
                return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"azureStorageUriRequestPending"}""");

            if (uri.EndsWith("/commit") && request.Method == HttpMethod.Post)
                return FakeGraphHandler.Empty();

            if (uri.Contains("/files/file-1") && request.Method == HttpMethod.Get)
                return FakeGraphHandler.Json(
                    """{"id":"file-1","uploadState":"commitFileSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1"}""");

            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch)
                return FakeGraphHandler.Json("""{"id":"app-100"}""");

            return FakeGraphHandler.Json("{}");
        });

    private static JsonObject AppBody(FakeGraphHandler handler)
    {
        var write = handler.Requests.First(r =>
            (r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"))
            || r.Method == HttpMethod.Patch);
        return (JsonObject)JsonNode.Parse(write.Body)!;
    }

    private async Task<JsonObject> PublishAndReadBody(string architecture, string existingApps = NoApps)
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(existingApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options(architecture));

        return AppBody(handler);
    }

    // ── The published body ──────────────────────────────────────

    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    public async Task AnIntelArchitectureTravelsInApplicableArchitectures(string architecture)
    {
        var body = await PublishAndReadBody(architecture);

        Assert.Equal(architecture, (string?)body["applicableArchitectures"]);
        // Written as null, not omitted, so a previous arm64 publish cannot linger and
        // force applicableArchitectures back to none.
        Assert.True(body.ContainsKey("allowedArchitectures"));
        Assert.Null(body["allowedArchitectures"]);
    }

    [Fact]
    public async Task Arm64TravelsInAllowedArchitecturesWithApplicableSetToNone()
    {
        var body = await PublishAndReadBody(InstallerArchitectures.Arm64);

        Assert.Equal("arm64", (string?)body["allowedArchitectures"]);
        Assert.Equal("none", (string?)body["applicableArchitectures"]);
    }

    /// <summary>arm64 is never spelled the way the engine spells it internally.</summary>
    [Fact]
    public async Task TheArm64WireValueIsLowercase()
    {
        var body = await PublishAndReadBody(InstallerArchitectures.Arm64);

        Assert.Equal("arm64", (string?)body["allowedArchitectures"]);
        Assert.NotEqual("ARM64", (string?)body["allowedArchitectures"]);
    }

    // ── Updating an existing app between architectures ──────────

    [Fact]
    public async Task UpdatingAnExistingAppToX64ClearsAnyAllowedArchitectures()
    {
        var body = await PublishAndReadBody("x64", ExistingApp);

        Assert.Equal("x64", (string?)body["applicableArchitectures"]);
        Assert.True(body.ContainsKey("allowedArchitectures"));
        Assert.Null(body["allowedArchitectures"]);
    }

    [Fact]
    public async Task UpdatingAnExistingAppToArm64ClearsApplicableArchitectures()
    {
        var body = await PublishAndReadBody(InstallerArchitectures.Arm64, ExistingApp);

        Assert.Equal("none", (string?)body["applicableArchitectures"]);
        Assert.Equal("arm64", (string?)body["allowedArchitectures"]);
    }

    // ── Mapping in isolation ────────────────────────────────────

    [Theory]
    [InlineData("x64", "x64", null)]
    [InlineData("x86", "x86", null)]
    [InlineData("X64", "x64", null)]
    [InlineData("ARM64", "none", "arm64")]
    [InlineData("arm64", "none", "arm64")]
    [InlineData(null, "x64", null)]
    [InlineData("", "x64", null)]
    public void TheMappingSplitsAcrossTheTwoProperties(string? architecture, string applicable, string? allowed)
    {
        var (mappedApplicable, mappedAllowed) = IntunePublishService.GraphArchitectures(architecture);

        Assert.Equal(applicable, mappedApplicable);
        Assert.Equal(allowed, mappedAllowed);
    }

    [Theory]
    [InlineData("amd64")]
    [InlineData("arm")]
    [InlineData("neutral")]
    public void AnUnsupportedArchitectureIsStillRefused(string architecture)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => IntunePublishService.GraphArchitectures(architecture));
        Assert.Contains(architecture, ex.Message);
    }
}
