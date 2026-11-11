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
    private static FakeGraphHandler BuildHandler(string existingAppsJson,
        Func<HttpRequestMessage, HttpResponseMessage?>? intercept = null)
    {
        return new FakeGraphHandler((request, _) =>
        {
            if (intercept?.Invoke(request) is { } intercepted) return intercepted;
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

    [Theory]
    [InlineData("Windows11_23H2", "Windows11_23H2")]
    [InlineData("windows11_24h2", "Windows11_24H2")]
    [InlineData("", "Windows10_21H2")]
    [InlineData("not a release\"", "Windows10_21H2")]
    public async Task Publish_SendsTheMinimumWindowsReleaseOfTheOptions(string release, string sent)
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.MinimumSupportedWindowsRelease = release;

        await svc.PublishAsync(package, Manifest(), options);

        var create = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        Assert.Contains($"\"minimumSupportedWindowsRelease\":\"{sent}\"", create.Body);
    }

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
    public async Task Publish_PerUserManifest_RunsAsUserWithCurrentUserKeyPath()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);
        var manifest = Manifest();
        manifest.InstallContext = "User";
        manifest.Detection = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"Software\Vendor\App",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.0.0",
            Operator = "GreaterEquals",
            Hive = "CurrentUser"
        };

        await svc.PublishAsync(package, manifest, Options());

        var create = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        Assert.Contains("\"runAsAccount\":\"user\"", create.Body);
        Assert.Contains("HKEY_CURRENT_USER\\\\Software\\\\Vendor\\\\App", create.Body);
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

    [Theory]
    [InlineData("upload")]
    [InlineData("commit")]
    [InlineData("cancel")]
    public async Task FailedReplacementLeavesExistingAppMetadataAndContentVersionAlone(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = BuildHandler(ExistingApp, request =>
        {
            if (request.RequestUri!.Host.EndsWith("blob.core.windows.net", StringComparison.Ordinal))
            {
                if (failure == "upload") return new HttpResponseMessage(HttpStatusCode.BadGateway);
                if (failure == "cancel")
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
            }
            if (failure == "commit" && request.RequestUri.AbsolutePath.EndsWith("/commit", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            return null;
        });
        using var service = new IntunePublishService(handler);

        var package = IntuneWinFixture.Create(_dir);
        if (failure == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PublishAsync(
                package, Manifest(), Options(), ct: cancellation.Token));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => service.PublishAsync(
                package, Manifest(), Options(), ct: cancellation.Token));

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task ReplacementActivatesMetadataAndContentTogetherAfterCommitValidation()
    {
        var handler = BuildHandler(ExistingApp);
        using var service = new IntunePublishService(handler);

        await service.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        var patch = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Same(handler.Requests[^1], patch);
        Assert.Equal(HttpMethod.Get, handler.Requests[^2].Method);
        Assert.EndsWith("/files/file-1", handler.Requests[^2].Uri);
        var body = JsonNode.Parse(patch.Body)!;
        Assert.Equal("7", (string?)body["committedContentVersion"]);
        Assert.Equal("install.bat", (string?)body["installCommandLine"]);
        Assert.NotNull(body["rules"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NameFallbackExaminesEveryPageBeforeAdoptingAnApp(bool duplicate)
    {
        var handler = new FakeGraphHandler((request, _) =>
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            if (uri.Contains("isof(")) return FakeGraphHandler.Json(NoApps);
            if (uri.Contains("$skiptoken=")) return FakeGraphHandler.Json(
                """{"value":[{"id":"second","@odata.type":"#microsoft.graph.win32LobApp"}]}""");
            return FakeGraphHandler.Json(new JsonObject
            {
                ["value"] = duplicate ? JsonNode.Parse(ExistingApp)!["value"]!.DeepClone() : new JsonArray(),
                ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps?$skiptoken=next"
            }.ToJsonString());
        });
        using var service = new IntunePublishService(handler);

        if (duplicate)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.FindExistingAppAsync(
                "https://graph.microsoft.com/v1.0", "token", "AppPackager:test/default", "Contoso"));
        else
            Assert.Equal("second", (string?)(await service.FindExistingAppAsync(
                "https://graph.microsoft.com/v1.0", "token", "AppPackager:test/default", "Contoso"))?["id"]);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task NameFallbackEncodesTheEntireFilterAsOneQueryValue()
    {
        const string name = "Contoso's R&D #1 + 100%";
        var handler = new FakeGraphHandler((_, _) => FakeGraphHandler.Json(NoApps));
        using var service = new IntunePublishService(handler);

        await service.FindExistingAppAsync("https://graph.microsoft.com/v1.0", "token", "tag", name);

        var uri = new Uri(handler.Requests[^1].Uri);
        Assert.Empty(uri.Fragment);
        Assert.DoesNotContain("&", uri.Query);
        Assert.Equal("?$filter=displayName eq 'Contoso''s R&D #1 + 100%'", Uri.UnescapeDataString(uri.Query));
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

        var filterRequest = handler.Requests.First(r => Uri.UnescapeDataString(r.Uri).Contains("displayName eq"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobFailureDoesNotDiscloseTheUploadCredential(bool failBlockList)
    {
        const string sas = "https://x.blob.core.windows.net/c/b?sv=1&sig=private-upload-credential";
        Directory.CreateDirectory(_dir);
        var payload = Path.Combine(_dir, "payload.bin");
        File.WriteAllText(payload, "encrypted payload");
        var handler = new FakeGraphHandler((request, _) =>
            !failBlockList || request.RequestUri!.Query.Contains("comp=blocklist")
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Failed request: " + sas) }
                : FakeGraphHandler.Empty());
        using var service = new IntunePublishService(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.UploadBlockBlobAsync(sas, payload));

        Assert.Contains("403", error.Message);
        Assert.Contains("https://x.blob.core.windows.net/c/b", error.Message);
        Assert.DoesNotContain("private-upload-credential", error.ToString());
        Assert.DoesNotContain("sig=", error.ToString());
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
