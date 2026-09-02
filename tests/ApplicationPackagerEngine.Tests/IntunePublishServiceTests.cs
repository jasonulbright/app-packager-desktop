using System.Net;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

internal sealed record RecordedRequest(HttpMethod Method, string Uri, string Body);

/// <summary>Records every request and answers from a scripted URI matcher.</summary>
internal sealed class FakeGraphHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;
    internal List<RecordedRequest> Requests { get; } = [];

    internal FakeGraphHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body));
        return _responder(request, body);
    }

    internal static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    internal static HttpResponseMessage Empty() => new(HttpStatusCode.OK) { Content = new StringContent("") };
}

public sealed class IntunePublishServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-intune-publish-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static IntunePublishOptions Options() => new()
    {
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = "secret",
        PollInterval = TimeSpan.Zero,
        CommitPollInterval = TimeSpan.Zero,
        PollTimeoutSec = 30
    };

    private static StageManifest Manifest(string appName = "Contoso App") => new()
    {
        AppName = appName,
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

    /// <summary>Scripted Graph and blob endpoints for a successful publish.</summary>
    private static FakeGraphHandler BuildHandler(string existingAppsJson)
    {
        return new FakeGraphHandler((request, _) =>
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
            {
                // First GET hands back the storage URI; later GETs report the commit finished.
                return FakeGraphHandler.Json(
                    """{"id":"file-1","uploadState":"commitFileSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1"}""");
            }

            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch)
                return FakeGraphHandler.Json("""{"id":"app-100"}""");

            return FakeGraphHandler.Json("{}");
        });
    }

    private static readonly string NoApps = """{"value":[]}""";
    private static readonly string ExistingApp =
        """{"value":[{"id":"app-existing","@odata.type":"#microsoft.graph.win32LobApp"}]}""";

    [Fact]
    public async Task Publish_WhenAppMissing_CreatesApp()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        var appId = await svc.PublishAsync(package, Manifest(), Options());

        Assert.Equal("app-100", appId);
        var creates = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"))
            .ToList();
        Assert.Single(creates);
        Assert.Contains("#microsoft.graph.win32LobApp", creates[0].Body);
        Assert.Contains("\"runAsAccount\":\"system\"", creates[0].Body);
        Assert.Contains("\"minimumSupportedWindowsRelease\":\"Windows10_21H2\"", creates[0].Body);
        Assert.Contains("\"returnCode\":1707", creates[0].Body);
        Assert.Contains("\"installCommandLine\":\"install.bat\"", creates[0].Body);
    }

    [Fact]
    public async Task Publish_WithStagedIcon_SetsLargeIconMimeContent()
    {
        var package = IntuneWinFixture.Create(_dir);
        var iconBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(package))!, "app-icon.png"), iconBytes);
        var manifest = Manifest();
        manifest.Icon = "app-icon.png";
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, manifest, Options());

        var create = handler.Requests.First(r =>
            r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        var largeIcon = JsonNode.Parse(create.Body)!["largeIcon"]!;
        Assert.Equal("#microsoft.graph.mimeContent", (string?)largeIcon["@odata.type"]);
        Assert.Equal("image/png", (string?)largeIcon["type"]);
        Assert.Equal(Convert.ToBase64String(iconBytes), (string?)largeIcon["value"]);
    }

    [Fact]
    public async Task Publish_WithMissingIconFile_LeavesLargeIconUnset()
    {
        var package = IntuneWinFixture.Create(_dir);
        var manifest = Manifest();
        manifest.Icon = "app-icon.png";
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, manifest, Options());

        var create = handler.Requests.First(r =>
            r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        Assert.DoesNotContain("largeIcon", create.Body);
    }

    [Theory]
    [InlineData("app-icon.ico", "image/x-icon")]
    [InlineData("app-icon.png", "image/png")]
    [InlineData("app-icon.jpg", "image/jpeg")]
    [InlineData("app-icon.jpeg", "image/jpeg")]
    public void BuildIconMimeContent_MapsTheExtensionToAContentType(string fileName, string expected)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Equal(expected, (string?)IntunePublishService.BuildIconMimeContent(path)!["type"]);
    }

    [Fact]
    public void BuildIconMimeContent_ReturnsNullForAMissingOrEmptyFile()
    {
        Directory.CreateDirectory(_dir);
        var empty = Path.Combine(_dir, "empty.png");
        File.WriteAllBytes(empty, []);

        Assert.Null(IntunePublishService.BuildIconMimeContent(empty));
        Assert.Null(IntunePublishService.BuildIconMimeContent(Path.Combine(_dir, "absent.png")));
    }

    [Fact]
    public async Task Publish_WhenAppExists_PatchesAndDoesNotCreate()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingApp);
        using var svc = new IntunePublishService(handler);

        var appId = await svc.PublishAsync(package, Manifest(), Options());

        Assert.Equal("app-existing", appId);
        Assert.DoesNotContain(handler.Requests,
            r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        Assert.Contains(handler.Requests,
            r => r.Method == HttpMethod.Patch && r.Uri.EndsWith("/deviceAppManagement/mobileApps/app-existing"));
    }

    [Fact]
    public async Task Publish_NonWin32Match_IsIgnoredAndAppIsCreated()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler("""{"value":[{"id":"app-msi","@odata.type":"#microsoft.graph.windowsMobileMSI"}]}""");
        using var svc = new IntunePublishService(handler);

        var appId = await svc.PublishAsync(package, Manifest(), Options());

        Assert.Equal("app-100", appId);
    }

    [Fact]
    public async Task Publish_EscapesSingleQuotesInDisplayNameFilter()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest("Contoso's App"), Options());

        var filterRequest = handler.Requests.First(r => r.Uri.Contains("filter="));
        Assert.Contains("Contoso''s%20App", Uri.UnescapeDataString(filterRequest.Uri).Replace(" ", "%20"));
    }

    [Fact]
    public async Task Publish_CommitCarriesFileEncryptionInfo()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        var commit = handler.Requests.Single(r => r.Uri.EndsWith("/commit"));
        var body = JsonNode.Parse(commit.Body)!["fileEncryptionInfo"]!;
        Assert.Equal("ENC-KEY", (string?)body["encryptionKey"]);
        Assert.Equal("MAC-KEY", (string?)body["macKey"]);
        Assert.Equal("IV", (string?)body["initializationVector"]);
        Assert.Equal("MAC", (string?)body["mac"]);
        Assert.Equal("ProfileVersion1", (string?)body["profileIdentifier"]);
        Assert.Equal("DIGEST", (string?)body["fileDigest"]);
        Assert.Equal("SHA256", (string?)body["fileDigestAlgorithm"]);
    }

    [Fact]
    public async Task Publish_PatchesCommittedContentVersionLast()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        var patch = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        Assert.EndsWith("/deviceAppManagement/mobileApps/app-100", patch.Uri);
        Assert.Equal("7", (string?)JsonNode.Parse(patch.Body)!["committedContentVersion"]);
    }

    [Fact]
    public async Task Publish_UploadsBlocksThenBlockList()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        var blobPuts = handler.Requests.Where(r => r.Uri.Contains("blob.core.windows.net")).ToList();
        Assert.Equal(2, blobPuts.Count);
        Assert.Contains("comp=block&blockid=", blobPuts[0].Uri);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("block-000000")),
            Uri.UnescapeDataString(blobPuts[0].Uri));
        Assert.EndsWith("comp=blocklist", blobPuts[1].Uri);
        Assert.Contains("<BlockList><Latest>", blobPuts[1].Body);
    }

    [Fact]
    public async Task Publish_FileEntryUsesMetadataSizeAndEncryptedSize()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        var fileRequest = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/files"));
        var body = JsonNode.Parse(fileRequest.Body)!;
        Assert.Equal("IntunePackage.intunewin", (string?)body["name"]);
        Assert.Equal(4242, (long?)body["size"]);
        Assert.Equal(IntuneWinFixture.PayloadText.Length, (long?)body["sizeEncrypted"]);
        Assert.False((bool?)body["isDependency"]);
        Assert.Null(body["manifest"]);
    }

    [Fact]
    public async Task Publish_CommandOverrides_ReplaceDefaults()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        var manifest = Manifest();
        manifest.CommandOverrides = new StageCommandOverrides { Install = "setup.exe /S", Uninstall = "setup.exe /U" };
        await svc.PublishAsync(package, manifest, Options());

        var create = handler.Requests.First(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        var body = JsonNode.Parse(create.Body)!;
        Assert.Equal("setup.exe /S", (string?)body["installCommandLine"]);
        Assert.Equal("setup.exe /U", (string?)body["uninstallCommandLine"]);
    }

    [Fact]
    public async Task Publish_UploadStateFailed_Throws()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = new FakeGraphHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/oauth2/v2.0/token")) return FakeGraphHandler.Json("""{"access_token":"t"}""");
            if (uri.Contains("filter=")) return FakeGraphHandler.Json(NoApps);
            if (uri.EndsWith("/contentVersions")) return FakeGraphHandler.Json("""{"id":"7"}""");
            if (uri.EndsWith("/files") && request.Method == HttpMethod.Post) return FakeGraphHandler.Json("""{"id":"file-1"}""");
            if (uri.Contains("/files/file-1") && request.Method == HttpMethod.Get)
                return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"azureStorageUriRequestFailed"}""");
            return FakeGraphHandler.Json("""{"id":"app-100"}""");
        });
        using var svc = new IntunePublishService(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PublishAsync(package, Manifest(), Options()));
        Assert.Contains("Azure storage URI request failed", ex.Message);
    }

    [Fact]
    public async Task GetToken_WithoutAccessToken_Throws()
    {
        var handler = new FakeGraphHandler((_, _) => FakeGraphHandler.Json("""{"token_type":"Bearer"}"""));
        using var svc = new IntunePublishService(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetTokenAsync(Options()));
        Assert.Contains("no access_token", ex.Message);
    }
}
