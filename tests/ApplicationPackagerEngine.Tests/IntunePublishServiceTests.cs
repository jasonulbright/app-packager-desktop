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
        PollTimeoutSec = 30,
        RetryBaseDelay = TimeSpan.Zero
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
    public async Task Publish_OfAnAppThatIsAlreadyInIntune_SendsTheNewMinimumWindowsReleaseInTheUpdate()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingApp);
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.MinimumSupportedWindowsRelease = "Windows11_23H2";

        await svc.PublishAsync(package, Manifest(), options);

        var patches = handler.Requests.Where(r => r.Method == HttpMethod.Patch && r.Uri.Contains("app-existing")).ToList();
        Assert.NotEmpty(patches);
        Assert.Contains(patches, p => p.Body.Contains("\"minimumSupportedWindowsRelease\":\"Windows11_23H2\""));
    }

    private static string ExistingAppWithDescription(string description, string displayName = "") =>
        $"{{\"value\":[{{\"id\":\"app-existing\",\"@odata.type\":\"#microsoft.graph.win32LobApp\",\"displayName\":\"{displayName}\",\"description\":\"{description}\"}}]}}";

    [Theory]
    [InlineData("", "Typed in the console", "Typed in the console")]
    [InlineData("  ", "Typed in the console", "Typed in the console")]
    [InlineData("A new comment", "Typed in the console", "A new comment")]
    [InlineData("", "", "Contoso App")]
    [InlineData("", "Contoso App 1.2.2", "Contoso App")]
    public async Task Publish_OfAnExistingApp_KeepsItsDescriptionWhenTheRunHasNoComment(string comment, string existing, string sent)
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingAppWithDescription(existing, "Contoso App 1.2.2"));
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.Description = comment;
        var manifest = Manifest();
        manifest.AppName = "Contoso App";

        await svc.PublishAsync(package, manifest, options);

        var patches = handler.Requests.Where(r => r.Method == HttpMethod.Patch && r.Uri.Contains("app-existing")).ToList();
        Assert.Contains(patches, p => p.Body.Contains($"\"description\":\"{sent}\""));
    }

    // ---- a repeat publish of an unchanged package ----

    private const string IdentityLine = "AppPackager:legacy:Contoso-App/default";
    private const string ContentLine = "AppPackagerContent:DIGEST";

    private static string ExistingAppWith(string notesJsonText, string? committedContentVersion = "3") =>
        "{\"value\":[{\"id\":\"app-existing\",\"@odata.type\":\"#microsoft.graph.win32LobApp\"," +
        (committedContentVersion is null ? "" : $"\"committedContentVersion\":\"{committedContentVersion}\",") +
        $"\"notes\":\"{notesJsonText}\"}}]}}";

    private static bool UploadedContent(FakeGraphHandler handler) =>
        handler.Requests.Any(r => r.Uri.EndsWith("/contentVersions") || r.Uri.Contains("blob.core.windows.net"));

    [Fact]
    public async Task Publish_OfAnUnchangedPackage_SkipsTheUploadAndUpdatesOnlyTheApp()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingAppWith($"{IdentityLine}\\n{ContentLine}"));
        using var svc = new IntunePublishService(handler);
        var log = new List<string>();

        var appId = await svc.PublishAsync(package, Manifest(), Options(), log.Add);

        Assert.Equal("app-existing", appId);
        Assert.False(UploadedContent(handler));
        var patch = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.EndsWith("/deviceAppManagement/mobileApps/app-existing", patch.Uri);
        Assert.DoesNotContain("committedContentVersion", patch.Body);
        Assert.Contains("\"minimumSupportedWindowsRelease\"", patch.Body);
        Assert.Contains($"{IdentityLine}\\n{ContentLine}", patch.Body);
        Assert.Contains(log, l => l.Contains("upload skipped"));
        Assert.Contains(log, l => l.StartsWith("Published to Intune"));
    }

    [Fact]
    public async Task Publish_OfAnUnchangedPackage_StillUpdatesTheMetadataOfTheApp()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingAppWith($"{IdentityLine}\\n{ContentLine}"));
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.MinimumSupportedWindowsRelease = "Windows11_23H2";

        await svc.PublishAsync(package, Manifest(), options);

        var patch = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("\"minimumSupportedWindowsRelease\":\"Windows11_23H2\"", patch.Body);
        Assert.Contains("\"installCommandLine\":\"install.bat\"", patch.Body);
    }

    [Fact]
    public async Task Publish_WhenThePackageDigestDiffers_UploadsAndRecordsTheNewDigest()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingAppWith($"{IdentityLine}\\nAppPackagerContent:OLD-DIGEST"));
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        Assert.True(UploadedContent(handler));
        var patch = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        Assert.Contains("\"committedContentVersion\":\"7\"", patch.Body);
        Assert.Contains($"{IdentityLine}\\n{ContentLine}", patch.Body);
        Assert.DoesNotContain("OLD-DIGEST", patch.Body);
    }

    [Fact]
    public async Task Publish_OfAnAppThatNeverHadContentCommitted_UploadsEvenWhenTheNotesCarryTheDigest()
    {
        // A first publish that stopped after it created the app leaves the digest in the notes and no content.
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingAppWith($"{IdentityLine}\\n{ContentLine}", committedContentVersion: null));
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        Assert.True(UploadedContent(handler));
    }

    [Fact]
    public async Task Publish_OfAnAppThatEarlierReleasesPublished_UploadsOnceAndRecordsTheDigest()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingAppWith(IdentityLine));
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        Assert.True(UploadedContent(handler));
        Assert.Contains(ContentLine, handler.Requests.Last(r => r.Method == HttpMethod.Patch).Body);
    }

    [Fact]
    public async Task Publish_OfANewApp_RecordsTheDigestInTheNotesOfTheCreate()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        var create = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        Assert.Contains($"\"notes\":\"{IdentityLine}\\n{ContentLine}\"", create.Body);
    }

    [Fact]
    public async Task Publish_KeepsTheLinesOfTheOperatorAndReplacesTheOldDigestLine()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingAppWith($"Owner: desktop team\\n{IdentityLine}\\nAppPackagerContent:OLD"));
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        var patch = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        Assert.Contains($"\"notes\":\"Owner: desktop team\\n{IdentityLine}\\n{ContentLine}\"", patch.Body);
    }

    [Fact]
    public async Task Publish_WhenThePackageCarriesNoDigest_AlwaysUploads()
    {
        var package = IntuneWinFixture.Create(_dir, detectionXml: IntuneWinFixture.DetectionXml().Replace("<FileDigest>DIGEST</FileDigest>", "<FileDigest></FileDigest>"));
        var handler = BuildHandler(ExistingAppWith($"{IdentityLine}\\nAppPackagerContent:"));
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        Assert.True(UploadedContent(handler));
        Assert.DoesNotContain("AppPackagerContent:", handler.Requests.Last(r => r.Method == HttpMethod.Patch).Body);
    }

    [Fact]
    public async Task Publish_WhenAnUnchangedPackageUpdateFails_ThePublishFails()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(ExistingAppWith($"{IdentityLine}\\n{ContentLine}"),
            request => request.Method == HttpMethod.Patch ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad") } : null);
        using var svc = new IntunePublishService(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.PublishAsync(package, Manifest(), Options()));
    }

    // ---- the hardware minimums of a converted app ----

    private static IntuneConversionOverrides Conversion(Dictionary<string, int> minimums) => new(
        [new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppFileSystemRule",
            ["ruleType"] = "detection",
            ["path"] = @"C:\Program Files\Contoso",
            ["fileOrFolderName"] = "app.exe",
            ["check32BitOn64System"] = false,
            ["operationType"] = "exists",
            ["operator"] = "notConfigured",
            ["comparisonValue"] = null
        }],
        [new JsonObject { ["returnCode"] = 0, ["type"] = "success" }],
        "basedOnReturnCode", minimums, "x64");

    private async Task<JsonObject> CreatedBodyOfAConvertedApp(Dictionary<string, int> minimums)
    {
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);
        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options(), conversion: Conversion(minimums));
        var create = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        return (JsonObject)JsonNode.Parse(create.Body)!;
    }

    [Fact]
    public async Task Publish_OfAConvertedApp_SendsAllFourHardwareMinimums()
    {
        var body = await CreatedBodyOfAConvertedApp(new()
        {
            ["MinimumMemoryInMB"] = 4096, ["MinimumNumberOfProcessors"] = 2, ["MinimumCpuSpeedInMHz"] = 2400, ["MinimumFreeDiskSpaceInMB"] = 1024
        });

        Assert.Equal(4096, (int?)body["minimumMemoryInMB"]);
        Assert.Equal(2, (int?)body["minimumNumberOfProcessors"]);
        Assert.Equal(2400, (int?)body["minimumCpuSpeedInMHz"]);
        Assert.Equal(1024, (int?)body["minimumFreeDiskSpaceInMB"]);
    }

    [Fact]
    public async Task Publish_OfAConvertedApp_SendsNullForAMinimumThatIsNotSet_SoARepeatPublishClearsAnOldOne()
    {
        var body = await CreatedBodyOfAConvertedApp(new() { ["MinimumMemoryInMB"] = 4096 });

        Assert.Equal(4096, (int?)body["minimumMemoryInMB"]);
        foreach (var property in new[] { "minimumNumberOfProcessors", "minimumCpuSpeedInMHz", "minimumFreeDiskSpaceInMB" })
        {
            Assert.True(body.ContainsKey(property), property + " is sent");
            Assert.Null(body[property]);
        }
    }

    [Fact]
    public async Task Publish_OfAConvertedApp_TheUpdateOfAnExistingAppClearsTheMinimumsThatTheConversionNoLongerHas()
    {
        var handler = BuildHandler(ExistingApp);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options(), conversion: Conversion([]));

        var patch = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = (JsonObject)JsonNode.Parse(patch.Body)!;
        Assert.True(body.ContainsKey("minimumFreeDiskSpaceInMB"));
        Assert.Null(body["minimumFreeDiskSpaceInMB"]);
        Assert.True(body.ContainsKey("minimumCpuSpeedInMHz"));
        Assert.Null(body["minimumCpuSpeedInMHz"]);
    }

    [Fact]
    public async Task Publish_OfAConvertedApp_IgnoresAKeyThatIsNotAHardwareMinimum()
    {
        var body = await CreatedBodyOfAConvertedApp(new() { ["MinimumColor"] = 5 });

        Assert.Null(body["minimumColor"]);
    }

    [Fact]
    public async Task Publish_OfAConvertedApp_RefusesANegativeMinimumBeforeTheFirstGraphCall()
    {
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options(), conversion: Conversion(new() { ["MinimumFreeDiskSpaceInMB"] = -1 })));

        Assert.Contains("MinimumFreeDiskSpaceInMB", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Publish_OfAStageThatIsNotConverted_SendsNoHardwareMinimum()
    {
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);
        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());
        var create = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));

        Assert.DoesNotContain("minimumFreeDiskSpaceInMB", create.Body);
        Assert.DoesNotContain("minimumCpuSpeedInMHz", create.Body);
        Assert.DoesNotContain("minimumMemoryInMB", create.Body);
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

    private static StageManifest ManifestWithRequirements(params StageRequirement[] requirements)
    {
        var m = Manifest();
        m.Requirements = requirements;
        return m;
    }

    private static JsonArray CreatedRules(FakeGraphHandler handler)
    {
        var create = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        return (JsonArray)JsonNode.Parse(create.Body)!["rules"]!;
    }

    [Fact]
    public async Task Publish_SendsTheRequirementRulesAfterTheDetectionRules()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, ManifestWithRequirements(new StageRequirement { ConditionId = "cpu-arch", Value = "x64" }), Options());

        var rules = CreatedRules(handler);
        Assert.Equal(["detection", "requirement"], rules.Select(r => (string?)r!["ruleType"]));
        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", (string?)rules[1]!["@odata.type"]);
    }

    [Fact]
    public async Task Publish_RefusesARequirementWithNoRuleBeforeTheFirstGraphCall()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PublishAsync(package, ManifestWithRequirements(new StageRequirement { ConditionId = "no-such-condition" }), Options()));

        Assert.Contains("no-such-condition", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Publish_LeavesOutARequirementWhoseDropWasAccepted_AndKeepsTheOthers()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);
        var manifest = ManifestWithRequirements(new StageRequirement { ConditionId = "no-such-condition" }, new StageRequirement { ConditionId = "cpu-arch", Value = "x64" });
        var plan = IntuneRequirementPlan.Of(null, null, [IntuneRequirementPlan.DropKey(manifest, "no-such-condition")]);

        await svc.PublishAsync(package, manifest, Options(), requirements: plan);

        var rules = CreatedRules(handler);
        Assert.Equal(2, rules.Count);
        Assert.Equal("APKG - CPU Architecture", (string?)rules[1]!["displayName"]);
    }

    [Fact]
    public async Task Publish_SendsTheRequirementsThatTheRunAppliesBesideTheManifest()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);
        var plan = IntuneRequirementPlan.Of([new StageRequirement { ConditionId = "vpn-connected", Value = true }], null, null);

        await svc.PublishAsync(package, Manifest(), Options(), requirements: plan);

        Assert.Equal(["detection", "requirement"], CreatedRules(handler).Select(r => (string?)r!["ruleType"]));
    }

    [Fact]
    public async Task Publish_StopsBeforeTheFirstGraphCallWhenARequiredRequirementSignatureIsNotProduced()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);
        var policy = new ApplicationPackagerEngine.Models.Signing.SigningPolicy { RequireRequirements = true };
        var plan = IntuneRequirementPlan.Of(null, null, null, policy);

        var ex = await Assert.ThrowsAsync<IntuneRequirementSigningException>(() =>
            svc.PublishAsync(package, ManifestWithRequirements(new StageRequirement { ConditionId = "vpn-connected", Value = true }), Options(), requirements: plan));

        Assert.Contains("signing is required", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Publish_OfAnAppWithoutRequirements_SendsOnlyTheDetectionRules()
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = BuildHandler(NoApps);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(package, Manifest(), Options());

        Assert.Equal(["detection"], CreatedRules(handler).Select(r => (string?)r!["ruleType"]));
    }

    // ---- retries, token renewal and upload address renewal ----

    private static HttpResponseMessage Status(HttpStatusCode code, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent("busy") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static bool IsAppList(HttpRequestMessage r) => r.Method == HttpMethod.Get && r.RequestUri!.ToString().Contains("mobileApps?");
    private static bool IsAppCreate(HttpRequestMessage r) => r.Method == HttpMethod.Post && r.RequestUri!.ToString().EndsWith("/deviceAppManagement/mobileApps");
    private static bool IsBlob(HttpRequestMessage r) => r.RequestUri!.Host.EndsWith("blob.core.windows.net", StringComparison.Ordinal);

    [Fact]
    public async Task Publish_RetriesAThrottledRead_AfterTheWaitOfTheService()
    {
        var throttled = 0;
        var handler = BuildHandler(NoApps, r => IsAppList(r) && throttled++ == 0 ? Status(HttpStatusCode.TooManyRequests, "0") : null);
        using var svc = new IntunePublishService(handler);
        var log = new List<string>();

        var appId = await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options(), log.Add);

        Assert.Equal("app-100", appId);
        Assert.True(throttled >= 2, "the throttled read was sent again");
        Assert.Contains(log, l => l.StartsWith("Intune request retry") && l.Contains("429"));
    }

    [Fact]
    public async Task Publish_DoesNotRepeatTheCreateOfTheAppAfterABusyAnswerThatMayHaveComeFromAFrontEnd()
    {
        var handler = BuildHandler(NoApps, r => IsAppCreate(r) ? Status(HttpStatusCode.ServiceUnavailable) : null);
        using var svc = new IntunePublishService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
    }

    [Fact]
    public async Task Publish_RepeatsTheCreateOfTheAppAfterThrottling()
    {
        var throttled = 0;
        var handler = BuildHandler(NoApps, r => IsAppCreate(r) && throttled++ == 0 ? Status(HttpStatusCode.TooManyRequests, "0") : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps")));
    }

    [Fact]
    public async Task Publish_RepeatsTheContentVersionCreateAfterABusyAnswer()
    {
        var busy = 0;
        var handler = BuildHandler(NoApps, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/contentVersions", StringComparison.Ordinal) && busy++ == 0
            ? Status(HttpStatusCode.ServiceUnavailable) : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/contentVersions")));
    }

    [Fact]
    public async Task Publish_RepeatsTheRenewalRequestAfterAServerError_AndOnlyAcceptsANewAddress()
    {
        var blobAnswers = 0;
        var renewalPosts = 0;
        var polls = 0;
        var renewalRequested = false;
        var handler = BuildHandler(NoApps, r =>
        {
            if (IsBlob(r)) return blobAnswers++ == 0 ? Status(HttpStatusCode.Forbidden) : null;
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/renewUpload", StringComparison.Ordinal))
            {
                renewalRequested = true;
                return renewalPosts++ == 0 ? Status(HttpStatusCode.InternalServerError) : FakeGraphHandler.Json("{}");
            }
            if (renewalRequested && polls < 2 && r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/files/file-1", StringComparison.Ordinal))
            {
                // The first poll still shows the old address with the old state; the second shows the new one.
                return FakeGraphHandler.Json(polls++ == 0
                    ? """{"id":"file-1","uploadState":"azureStorageUriRenewalSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1"}"""
                    : """{"id":"file-1","uploadState":"azureStorageUriRenewalSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=2"}""");
            }
            return null;
        });
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        Assert.Equal(2, renewalPosts);
        Assert.Equal(2, polls);
        Assert.Contains("sv=2", handler.Requests.Last(r => r.Uri.Contains("blob.core.windows.net")).Uri);
    }

    [Fact]
    public async Task Publish_CountsRenewalsOfExpiredAddressesInARowAndNotInTheWholeUpload()
    {
        // The payload has one block and a block list. The address expires at the block and again at the list:
        // two renewals in one upload, with one allowed in a row.
        var blobAnswers = 0;
        var pendingPoll = false;
        var renewals = 0;
        var handler = BuildHandler(NoApps, r =>
        {
            if (IsBlob(r)) { var n = blobAnswers++; return n == 0 || n == 2 ? Status(HttpStatusCode.Forbidden) : null; }
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/renewUpload", StringComparison.Ordinal))
            {
                renewals++;
                pendingPoll = true;
                return FakeGraphHandler.Json("{}");
            }
            if (pendingPoll && r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/files/file-1", StringComparison.Ordinal))
            {
                pendingPoll = false;
                return FakeGraphHandler.Json($$"""{"id":"file-1","uploadState":"azureStorageUriRenewalSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv={{renewals + 1}}"}""");
            }
            return null;
        });
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.MaxStorageUriRenewals = 1;

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), options);

        Assert.Equal(2, renewals);
    }

    [Fact]
    public async Task Publish_DoesNotRetryACreateAfterAFailureThatMayHaveRunIt()
    {
        var handler = BuildHandler(NoApps, r => IsAppCreate(r) ? Status(HttpStatusCode.InternalServerError) : null);
        using var svc = new IntunePublishService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
    }

    [Fact]
    public async Task Publish_DoesNotRetryACreateThatLostItsConnection()
    {
        var handler = BuildHandler(NoApps, r => IsAppCreate(r) ? throw new HttpRequestException("connection reset") : null);
        using var svc = new IntunePublishService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
    }

    [Fact]
    public async Task Publish_RetriesAReadThatLostItsConnection()
    {
        var lost = 0;
        var handler = BuildHandler(NoApps, r => IsAppList(r) && lost++ == 0 ? throw new HttpRequestException("connection reset") : null);
        using var svc = new IntunePublishService(handler);

        Assert.Equal("app-100", await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));
    }

    [Fact]
    public async Task Publish_RetriesAnUpdateOfTheAppAfterABadGateway()
    {
        var failed = 0;
        var handler = BuildHandler(ExistingApp, r => r.Method == HttpMethod.Patch && failed++ == 0 ? Status(HttpStatusCode.BadGateway) : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Patch && r.Uri.EndsWith("/deviceAppManagement/mobileApps/app-existing")));
    }

    [Fact]
    public async Task Publish_GivesUpAfterTheConfiguredRetries()
    {
        var handler = BuildHandler(NoApps, r => IsAppList(r) ? Status(HttpStatusCode.ServiceUnavailable) : null);
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.MaxRetries = 2;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), options));

        Assert.Contains("503", error.Message);
        Assert.Equal(3, handler.Requests.Count(r => r.Method == HttpMethod.Get && r.Uri.Contains("isof(")));
    }

    [Fact]
    public async Task Publish_WithRetriesTurnedOffSendsEachRequestOnce()
    {
        var handler = BuildHandler(NoApps, r => IsAppList(r) ? Status(HttpStatusCode.ServiceUnavailable) : null);
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.MaxRetries = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), options));

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Get && r.Uri.Contains("isof("));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Publish_DoesNotRetryAnAnswerThatARetryCannotChange(HttpStatusCode code)
    {
        var handler = BuildHandler(NoApps, r => IsAppList(r) ? Status(code) : null);
        using var svc = new IntunePublishService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Get && r.Uri.Contains("isof("));
    }

    [Fact]
    public async Task Publish_RetriesATransientAnswerOfTheTokenEndpoint()
    {
        var failed = 0;
        var handler = BuildHandler(NoApps, r => r.RequestUri!.ToString().Contains("/oauth2/v2.0/token") && failed++ == 0 ? Status(HttpStatusCode.ServiceUnavailable) : null);
        using var svc = new IntunePublishService(handler);

        Assert.Equal("app-100", await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));
    }

    [Fact]
    public async Task Publish_RetriesABlobBlockThatTheStorageServiceAnsweredWithAnError()
    {
        var failed = 0;
        var handler = BuildHandler(NoApps, r => IsBlob(r) && failed++ < 2 ? Status(HttpStatusCode.ServiceUnavailable) : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        Assert.Equal(4, handler.Requests.Count(r => r.Uri.Contains("blob.core.windows.net")));
    }

    [Fact]
    public async Task Publish_RenewsAnExpiredUploadAddress_AndContinuesWithTheNewOne()
    {
        var blobAnswers = 0;
        var renewalRequested = false;
        var renewalPolled = false;
        var handler = BuildHandler(NoApps, r =>
        {
            if (IsBlob(r)) return blobAnswers++ == 0 ? Status(HttpStatusCode.Forbidden) : null;
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/renewUpload", StringComparison.Ordinal))
            {
                renewalRequested = true;
                return FakeGraphHandler.Json("{}");
            }
            if (renewalRequested && !renewalPolled && r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/files/file-1", StringComparison.Ordinal))
            {
                renewalPolled = true;
                return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"azureStorageUriRenewalSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=2"}""");
            }
            return null;
        });
        using var svc = new IntunePublishService(handler);
        var log = new List<string>();

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options(), log.Add);

        var blobUris = handler.Requests.Where(r => r.Uri.Contains("blob.core.windows.net")).Select(r => r.Uri).ToList();
        Assert.Contains("sv=1", blobUris[0]);
        Assert.All(blobUris.Skip(1), u => Assert.Contains("sv=2", u));
        Assert.Single(handler.Requests, r => r.Uri.EndsWith("/renewUpload"));
        Assert.Contains(log, l => l.Contains("renewing"));
    }

    [Fact]
    public async Task Publish_RenewsTheUploadAddressOnlyTheConfiguredNumberOfTimes()
    {
        var renewalRequests = 0;
        var pendingPoll = false;
        var handler = BuildHandler(NoApps, r =>
        {
            if (IsBlob(r)) return Status(HttpStatusCode.Forbidden);
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/renewUpload", StringComparison.Ordinal))
            {
                renewalRequests++;
                pendingPoll = true;
                return FakeGraphHandler.Json("{}");
            }
            if (pendingPoll && r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/files/file-1", StringComparison.Ordinal))
            {
                pendingPoll = false;
                return FakeGraphHandler.Json($$"""{"id":"file-1","uploadState":"azureStorageUriRenewalSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv={{renewalRequests + 1}}"}""");
            }
            return null;
        });
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.MaxStorageUriRenewals = 2;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), options));

        Assert.Contains("403", error.Message);
        Assert.Equal(2, renewalRequests);
    }

    [Fact]
    public async Task Publish_StopsWhenTheRenewalOfTheUploadAddressFails()
    {
        var renewalRequested = false;
        var handler = BuildHandler(NoApps, r =>
        {
            if (IsBlob(r)) return Status(HttpStatusCode.Forbidden);
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/renewUpload", StringComparison.Ordinal))
            {
                renewalRequested = true;
                return FakeGraphHandler.Json("{}");
            }
            if (renewalRequested && r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/files/file-1", StringComparison.Ordinal))
                return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"azureStorageUriRenewalFailed"}""");
            return null;
        });
        using var svc = new IntunePublishService(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));

        Assert.Contains("Renewal of the Azure storage URI failed", error.Message);
    }

    [Fact]
    public async Task Publish_WithNoRenewalsAllowedFailsOnTheFirstRefusalOfTheStorageService()
    {
        var handler = BuildHandler(NoApps, r => IsBlob(r) ? Status(HttpStatusCode.Forbidden) : null);
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.MaxStorageUriRenewals = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), options));

        Assert.DoesNotContain(handler.Requests, r => r.Uri.EndsWith("/renewUpload"));
    }

    [Fact]
    public async Task Publish_RequestsANewTokenOnceWhenTheServiceRefusesTheOldOne()
    {
        var unauthorized = 0;
        var handler = BuildHandler(NoApps, r => IsAppList(r) && unauthorized++ == 0 ? Status(HttpStatusCode.Unauthorized) : null);
        using var svc = new IntunePublishService(handler);
        var log = new List<string>();

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options(), log.Add);

        Assert.Equal(2, handler.Requests.Count(r => r.Uri.Contains("/oauth2/v2.0/token")));
        Assert.Contains(log, l => l.Contains("requesting a new token"));
    }

    [Fact]
    public async Task Publish_DoesNotAskForTokensInALoopWhenEveryCallIsUnauthorized()
    {
        var handler = BuildHandler(NoApps, r => IsAppList(r) ? Status(HttpStatusCode.Unauthorized) : null);
        using var svc = new IntunePublishService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options()));

        Assert.Equal(2, handler.Requests.Count(r => r.Uri.Contains("/oauth2/v2.0/token")));
    }

    [Fact]
    public async Task Publish_StopsAtOnceWhenTheRunIsCancelledDuringTheWaitBeforeARetry()
    {
        var handler = BuildHandler(NoApps, r => IsAppList(r) ? Status(HttpStatusCode.TooManyRequests) : null);
        using var svc = new IntunePublishService(handler);
        var options = Options();
        options.RetryBaseDelay = TimeSpan.FromSeconds(30);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), options, ct: cancellation.Token));

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Get && r.Uri.Contains("isof("));
    }

    [Fact]
    public async Task Publish_TheRetryLogNamesNoQueryOfTheStorageAddress()
    {
        var failed = 0;
        var handler = BuildHandler(NoApps, r =>
        {
            if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/files/file-1", StringComparison.Ordinal))
                return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"commitFileSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1&sig=private-upload-credential"}""");
            return IsBlob(r) && failed++ == 0 ? Status(HttpStatusCode.ServiceUnavailable) : null;
        });
        using var svc = new IntunePublishService(handler);
        var log = new List<string>();

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options(), log.Add);

        Assert.Contains(log, l => l.StartsWith("Intune request retry") && l.Contains("https://x.blob.core.windows.net/c/b"));
        Assert.DoesNotContain(log, l => l.Contains("sig=") || l.Contains("private-upload-credential"));
    }
}