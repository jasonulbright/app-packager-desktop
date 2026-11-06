using System.Net;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.CmImport;

namespace ApplicationPackagerEngine.Tests.CmImport;

/// <summary>A converted manifest travels to Graph with its finished rules, return codes, minimums and architectures.</summary>
public sealed class CmImportPublishTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-cmpub-" + Guid.NewGuid().ToString("N"));

    public CmImportPublishTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static CmImportPlan Plan(string fixture) =>
        CmImportPlanner.Plan(CmApplicationDigestReader.Read(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "CmImport", fixture + ".app.xml"))));

    private static CmIntuneManifest Manifest(string fixture, int index = 0) => Plan(fixture).DeploymentTypes[index].Manifest!;

    private static IntunePublishOptions Options() => new()
    {
        TenantId = "t", ClientId = "c", ClientSecret = "s", PollInterval = TimeSpan.Zero, CommitPollInterval = TimeSpan.Zero, PollTimeoutSec = 30
    };

    private static FakeGraphHandler Handler() => new((request, _) =>
    {
        var uri = request.RequestUri!.ToString();
        if (uri.Contains("/oauth2/v2.0/token")) return FakeGraphHandler.Json("""{"access_token":"fake-token","expires_in":3599}""");
        if (uri.Contains("blob.core.windows.net")) return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") };
        if (uri.Contains("mobileApps?$filter=") || uri.Contains("mobileApps?%24filter=")) return FakeGraphHandler.Json("""{"value":[]}""");
        if (uri.EndsWith("/contentVersions") && request.Method == HttpMethod.Post) return FakeGraphHandler.Json("""{"id":"7"}""");
        if (uri.EndsWith("/files") && request.Method == HttpMethod.Post) return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"azureStorageUriRequestPending"}""");
        if (uri.EndsWith("/commit") && request.Method == HttpMethod.Post) return FakeGraphHandler.Empty();
        if (uri.Contains("/files/file-1") && request.Method == HttpMethod.Get)
            return FakeGraphHandler.Json("""{"id":"file-1","uploadState":"commitFileSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1"}""");
        if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch) return FakeGraphHandler.Json("""{"id":"app-100"}""");
        return FakeGraphHandler.Json("{}");
    });

    private async Task<JsonObject> PublishAsync(CmIntuneManifest manifest)
    {
        var package = IntuneWinFixture.Create(_dir);
        var handler = Handler();
        using var service = new IntunePublishService(handler);
        var id = await service.PublishAsync(package, CmImportPublisher.ToStageManifest(manifest), Options(),
            conversion: CmImportPublisher.ToOverrides(manifest));
        Assert.Equal("app-100", id);
        var write = handler.Requests.First(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        return (JsonObject)JsonNode.Parse(write.Body)!;
    }

    [Fact]
    public async Task SendsTheFinishedRulesReturnCodesMinimumsAndTheArchitecture()
    {
        var body = await PublishAsync(Manifest("misc-reqs"));
        Assert.Equal("x64", (string?)body["applicableArchitectures"]);
        Assert.Null(body["allowedArchitectures"]);
        Assert.Equal(4096, (int?)body["minimumMemoryInMB"]);
        Assert.Equal(2, (int?)body["minimumNumberOfProcessors"]);
        var rule = Assert.Single(body["rules"]!.AsArray())!.AsObject();
        Assert.Equal("#microsoft.graph.win32LobAppFileSystemRule", (string?)rule["@odata.type"]);
        Assert.Equal(("exists", "app.exe"), ((string?)rule["operationType"], (string?)rule["fileOrFolderName"]));
        Assert.Equal(@"C:\Program Files\ImpTestMisc", (string?)rule["path"]);
        Assert.True((bool?)rule["check32BitOn64System"]);
        Assert.Equal(5, body["returnCodes"]!.AsArray().Count);
        Assert.Equal("basedOnReturnCode", (string?)body["installExperience"]!["deviceRestartBehavior"]);
        Assert.Equal("IMPTEST Misc Reqs", (string?)body["displayName"]);
        Assert.Matches("^AppPackager:cm:Application_.+/DeploymentType_", (string?)body["notes"]);
    }

    [Fact]
    public async Task SendsTheDocumentedPropertyWhenTheAppRanOnX86AndX64()
    {
        var manifest = Manifest("clauses");
        Assert.Equal("x86,x64", manifest.IntuneAllowedArchitectures);
        var body = await PublishAsync(manifest);
        Assert.Null(body["allowedArchitectures"]);
        Assert.Equal("x64,x86", (string?)body["applicableArchitectures"]);
        Assert.Equal(4, body["rules"]!.AsArray().Count);
    }

    [Fact]
    public async Task PublishesAManifestReadBackFromItsJsonFile()
    {
        var stage = CmImportStage.Export(Plan("multi-lang"), _dir, skipContent: true);
        var read = System.Text.Json.JsonSerializer.Deserialize<CmIntuneManifest>(File.ReadAllText(stage.Staged[1].ManifestPath))!;
        var body = await PublishAsync(read);
        var rules = body["rules"]!.AsArray();
        Assert.Equal(2, rules.Count);
        Assert.Single(rules, r => (string?)r!["ruleType"] == "requirement");
        Assert.Equal(5, body["returnCodes"]!.AsArray().Count);
    }

    [Fact]
    public void EachVariantHasItsOwnIdentityTag()
    {
        var tags = Plan("multi-arch").DeploymentTypes
            .Select(d => IntunePublishService.IdentityTag(CmImportPublisher.ToStageManifest(d.Manifest!))).ToList();
        Assert.Equal(2, tags.Distinct().Count());
    }

    [Theory]
    [InlineData("x64", "x64", null)]
    [InlineData("x86,x64", "x64,x86", null)]
    [InlineData("arm64", "none", "arm64")]
    [InlineData("x64,arm64", "none", "arm64,x64")]
    public void SplitsTheArchitecturesAcrossTheTwoGraphProperties(string cm, string applicable, string? allowed) =>
        Assert.Equal((applicable, allowed), IntunePublishService.GraphArchitecturesOfConversion(cm));
}
