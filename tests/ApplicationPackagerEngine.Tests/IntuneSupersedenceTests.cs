using System.Net;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Intune supersedence (Q-94) against a fake Graph. Not proven against a tenant: the lab has none, and the relationship write is a
/// beta call (v1.0 has no write), so these tests prove the requests this program sends, not what a tenant does with them.
/// </summary>
public sealed class IntuneSupersedenceTests
{
    private const string Beta = "https://graph.microsoft.com/beta/";
    private const string V1 = "https://graph.microsoft.com/v1.0/";

    private static IntunePublishOptions Options() => new()
    {
        TenantId = "tenant-id", ClientId = "client-id", ClientSecret = "secret",
        MaxRetries = 0, RetryBaseDelay = TimeSpan.Zero
    };

    private static SupersedenceKey Key(string publisher = "Contoso Ltd") => new("Contoso App", publisher);

    private static string AppsJson(params (string Id, string Name, string Publisher)[] apps) =>
        new JsonObject
        {
            ["value"] = new JsonArray(apps.Select(a => (JsonNode)new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.win32LobApp", ["id"] = a.Id, ["displayName"] = a.Name, ["publisher"] = a.Publisher
            }).ToArray())
        }.ToJsonString();

    private static FakeGraphHandler Tenant(string appsJson, string relationsJson = """{"value":[]}""", HttpStatusCode? writeStatus = null,
        HttpStatusCode? readRelationsStatus = null)
    {
        return new FakeGraphHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/oauth2/v2.0/token")) return FakeGraphHandler.Json("""{"access_token":"fake-token","expires_in":3599}""");
            if (uri.EndsWith("/updateRelationships"))
                return writeStatus is { } status ? new HttpResponseMessage(status) { Content = new StringContent("""{"error":{"message":"boom"}}""") }
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            if (uri.EndsWith("/relationships"))
                return readRelationsStatus is { } rs ? new HttpResponseMessage(rs) { Content = new StringContent("nope") } : FakeGraphHandler.Json(relationsJson);
            if (uri.Contains("mobileApps?")) return FakeGraphHandler.Json(appsJson);
            return FakeGraphHandler.Json("{}");
        });
    }

    private static async Task<LookupResult> Find(FakeGraphHandler handler, string newTitle = "Contoso App 2.0.0", string newVersion = "2.0.0", SupersedenceKey? key = null)
    {
        using var service = new IntunePublishService(handler);
        return await service.FindOlderVersionAsync(Options(), key ?? Key(), newTitle, newVersion);
    }

    [Fact]
    public async Task Lookup_TakesTheHighestOlderVersion_AndNeverAnOtherTitleOrN2()
    {
        var handler = Tenant(AppsJson(
            ("n2", "Contoso App 0.9.0", "Contoso Ltd"),
            ("n1", "Contoso App 1.5.0", "Contoso Ltd"),
            ("other", "Contoso App Pro 1.9.0", "Contoso Ltd"),
            ("newer", "Contoso App 3.0.0", "Contoso Ltd"),
            ("same", "Contoso App 2.0.0", "Contoso Ltd")));
        var found = await Find(handler);
        Assert.Equal("n1", found.Old?.CiId);
        Assert.Contains(found.Notes, n => n.Contains("0.9.0") && n.Contains("not superseded"));
        Assert.All(handler.Requests.Where(r => r.Uri.Contains("mobileApps")), r => Assert.StartsWith(V1, r.Uri));
    }

    [Fact]
    public async Task Lookup_FindsNothing_WhenThereIsNoOlderVersion()
    {
        var found = await Find(Tenant(AppsJson(("same", "Contoso App 2.0.0", "Contoso Ltd"), ("x", "Unrelated 1.0.0", "Contoso Ltd"))));
        Assert.Null(found.Old);
    }

    [Fact]
    public async Task Lookup_IgnoresAnOlderVersionOfAnotherVendor_CaseInsensitiveOnTheSame()
    {
        var wrong = await Find(Tenant(AppsJson(("v", "Contoso App 1.0.0", "Fabrikam"))));
        Assert.Null(wrong.Old);
        var same = await Find(Tenant(AppsJson(("v", "Contoso App 1.0.0", "CONTOSO ltd "))));
        Assert.Equal("v", same.Old?.CiId);
    }

    [Fact]
    public async Task Apply_WritesUpdateRelationOnBeta_AndKeepsDependenciesAndOtherRelations()
    {
        var existing = """
            {"value":[
              {"@odata.type":"#microsoft.graph.mobileAppDependency","id":"r1","targetId":"dep-1","targetType":"child","dependencyType":"autoInstall","targetDisplayName":"Runtime"},
              {"@odata.type":"#microsoft.graph.mobileAppSupersedence","id":"r2","targetId":"older-other","targetType":"child","supersedenceType":"replace"},
              {"@odata.type":"#microsoft.graph.mobileAppSupersedence","id":"r3","targetId":"parent-app","targetType":"parent","supersedenceType":"update"}
            ]}
            """;
        var handler = Tenant(AppsJson(), existing);
        using var service = new IntunePublishService(handler);
        var result = await service.ApplySupersedenceAsync(Options(), "new-app", "n1", "Contoso App 1.5.0", "1.5.0", uninstall: false);

        Assert.Equal(SupersedenceStepStatus.Applied, result.Status);
        var write = Assert.Single(handler.Requests, r => r.Uri.EndsWith("/updateRelationships"));
        Assert.Equal(HttpMethod.Post, write.Method);
        Assert.Equal(Beta + "deviceAppManagement/mobileApps/new-app/updateRelationships", write.Uri);
        var sent = JsonNode.Parse(write.Body)!["relationships"]!.AsArray().Select(r => r!.AsObject()).ToList();
        // The dependency and the other supersedence survive; the relation of a parent app is not resent; the new one is added.
        Assert.Equal(3, sent.Count);
        Assert.Contains(sent, r => (string?)r["targetId"] == "dep-1" && (string?)r["dependencyType"] == "autoInstall"
            && (string?)r["@odata.type"] == "#microsoft.graph.mobileAppDependency");
        Assert.Contains(sent, r => (string?)r["targetId"] == "older-other" && (string?)r["supersedenceType"] == "replace");
        Assert.DoesNotContain(sent, r => (string?)r["targetId"] == "parent-app");
        Assert.Contains(sent, r => (string?)r["targetId"] == "n1" && (string?)r["supersedenceType"] == "update"
            && (string?)r["@odata.type"] == "#microsoft.graph.mobileAppSupersedence");
        // Read-only fields are not sent back.
        Assert.All(sent, r => { Assert.Null(r["id"]); Assert.Null(r["targetDisplayName"]); });
    }

    [Fact]
    public async Task Apply_UsesReplaceType_WhenTheModeUninstalls()
    {
        var handler = Tenant(AppsJson());
        using var service = new IntunePublishService(handler);
        var result = await service.ApplySupersedenceAsync(Options(), "new-app", "n1", "Contoso App 1.5.0", "1.5.0", uninstall: true);
        Assert.Contains("uninstalls", result.Message);
        var write = Assert.Single(handler.Requests, r => r.Uri.EndsWith("/updateRelationships"));
        Assert.Equal("replace", (string?)JsonNode.Parse(write.Body)!["relationships"]![0]!["supersedenceType"]);
    }

    [Fact]
    public async Task Apply_OnlyTheRelationshipCallsUseBeta()
    {
        var handler = Tenant(AppsJson(("n1", "Contoso App 1.5.0", "Contoso Ltd")));
        using var service = new IntunePublishService(handler);
        await service.FindOlderVersionAsync(Options(), Key(), "Contoso App 2.0.0", "2.0.0");
        await service.ApplySupersedenceAsync(Options(), "new-app", "n1", "Contoso App 1.5.0", "1.5.0", false);
        var graphCalls = handler.Requests.Where(r => !r.Uri.Contains("/oauth2/")).ToList();
        Assert.All(graphCalls.Where(r => r.Uri.Contains("/relationships") || r.Uri.EndsWith("/updateRelationships")), r => Assert.StartsWith(Beta, r.Uri));
        Assert.All(graphCalls.Where(r => r.Uri.Contains("mobileApps?")), r => Assert.StartsWith(V1, r.Uri));
    }

    [Fact]
    public async Task Apply_LeavesAnExistingRelationAlone()
    {
        var existing = """{"value":[{"@odata.type":"#microsoft.graph.mobileAppSupersedence","targetId":"n1","targetType":"child","supersedenceType":"update"}]}""";
        var handler = Tenant(AppsJson(), existing);
        using var service = new IntunePublishService(handler);
        var result = await service.ApplySupersedenceAsync(Options(), "new-app", "n1", "Contoso App 1.5.0", "1.5.0", true);
        Assert.Equal(SupersedenceStepStatus.Applied, result.Status);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.EndsWith("/updateRelationships"));
    }

    [Fact]
    public async Task Apply_WritesNothing_WhenTheExistingRelationsCannotBeRead()
    {
        var handler = Tenant(AppsJson(), readRelationsStatus: HttpStatusCode.Forbidden);
        using var service = new IntunePublishService(handler);
        var result = await service.ApplySupersedenceAsync(Options(), "new-app", "n1", "Contoso App 1.5.0", "1.5.0", false);
        Assert.Equal(SupersedenceStepStatus.NotApplied, result.Status);
        Assert.Contains("nothing was written", result.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.EndsWith("/updateRelationships"));
    }

    [Fact]
    public async Task Apply_Fails_WithAClearMessage_WhenTheWriteIsRefused()
    {
        var handler = Tenant(AppsJson(), writeStatus: HttpStatusCode.BadRequest);
        using var service = new IntunePublishService(handler);
        var result = await service.ApplySupersedenceAsync(Options(), "new-app", "n1", "Contoso App 1.5.0", "1.5.0", false);
        Assert.Equal(SupersedenceStepStatus.NotApplied, result.Status);
        Assert.Contains("relation could not be written", result.Message);
        Assert.Contains("400", result.Message);
    }

    private static IntuneSupersedenceRequest Request(SupersedenceConfirmer confirmer, SupersedenceMode mode = SupersedenceMode.Replace,
        SupersedenceAnswer? answered = null) => new(Key(), "Contoso App 2.0.0", "2.0.0", mode, confirmer, answered);

    [Fact]
    public async Task Flow_Yes_FindsConfirmsAndAppliesAfterThePublish()
    {
        var handler = Tenant(AppsJson(("n1", "Contoso App 1.5.0", "Contoso Ltd")));
        var log = new List<string>();
        using var service = new IntunePublishService(handler);
        var plan = await IntuneSupersedenceFlow.PrepareAsync(service, Options(), Request(SupersedenceConfirmer.Fixed(SupersedenceAnswer.Yes)), log.Add, default);
        Assert.NotNull(plan);
        Assert.Equal("n1", plan!.Old.CiId);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.EndsWith("/updateRelationships"));
        var failure = await IntuneSupersedenceFlow.ApplyAsync(service, Options(), plan, "new-app", log.Add, default);
        Assert.Null(failure);
        Assert.Single(handler.Requests, r => r.Uri.EndsWith("/updateRelationships"));
    }

    [Fact]
    public async Task Flow_Skip_AppliesNothing()
    {
        var handler = Tenant(AppsJson(("n1", "Contoso App 1.5.0", "Contoso Ltd")));
        var log = new List<string>();
        using var service = new IntunePublishService(handler);
        var plan = await IntuneSupersedenceFlow.PrepareAsync(service, Options(), Request(SupersedenceConfirmer.Fixed(SupersedenceAnswer.Skip)), log.Add, default);
        Assert.Null(plan);
        Assert.Contains(log, l => l.Contains("skipped"));
    }

    [Fact]
    public async Task Flow_AnUnattendedRun_Skips()
    {
        var handler = Tenant(AppsJson(("n1", "Contoso App 1.5.0", "Contoso Ltd")));
        using var service = new IntunePublishService(handler);
        var plan = await IntuneSupersedenceFlow.PrepareAsync(service, Options(), Request(new SupersedenceConfirmer(null)), null, default);
        Assert.Null(plan);
    }

    [Fact]
    public async Task Flow_No_StopsTheRun()
    {
        var handler = Tenant(AppsJson(("n1", "Contoso App 1.5.0", "Contoso Ltd")));
        using var service = new IntunePublishService(handler);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            IntuneSupersedenceFlow.PrepareAsync(service, Options(), Request(SupersedenceConfirmer.Fixed(SupersedenceAnswer.No)), null, default));
    }

    [Fact]
    public async Task Flow_ReusesTheAnswerOfTheConfigMgrHalf_WithoutAsking()
    {
        var handler = Tenant(AppsJson(("n1", "Contoso App 1.5.0", "Contoso Ltd")));
        var asked = 0;
        var confirmer = new SupersedenceConfirmer(_ => { asked++; return Task.FromResult(new SupersedenceDecision(SupersedenceAnswer.No, false)); });
        using var service = new IntunePublishService(handler);
        var plan = await IntuneSupersedenceFlow.PrepareAsync(service, Options(), Request(confirmer, answered: SupersedenceAnswer.Yes), null, default);
        Assert.NotNull(plan);
        Assert.Equal(0, asked);
        var skipped = await IntuneSupersedenceFlow.PrepareAsync(service, Options(), Request(confirmer, answered: SupersedenceAnswer.Skip), null, default);
        Assert.Null(skipped);
    }

    [Fact]
    public async Task Flow_NoOlderVersion_LogsOneLineAndAsksNothing()
    {
        var handler = Tenant(AppsJson(("v", "Contoso App 1.0.0", "Fabrikam")));
        var log = new List<string>();
        using var service = new IntunePublishService(handler);
        var plan = await IntuneSupersedenceFlow.PrepareAsync(service, Options(), Request(SupersedenceConfirmer.Fixed(SupersedenceAnswer.No)), log.Add, default);
        Assert.Null(plan);
        Assert.Single(log, l => l.Contains("no older version"));
    }

    [Fact]
    public async Task Flow_ModeOff_DoesNotTouchTheTenant()
    {
        var handler = Tenant(AppsJson(("n1", "Contoso App 1.5.0", "Contoso Ltd")));
        using var service = new IntunePublishService(handler);
        var plan = await IntuneSupersedenceFlow.PrepareAsync(service, Options(), Request(SupersedenceConfirmer.Fixed(SupersedenceAnswer.Yes), SupersedenceMode.Off), null, default);
        Assert.Null(plan);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Flow_FailedRelation_ReturnsTheSentence_AndLogsIt()
    {
        var handler = Tenant(AppsJson(("n1", "Contoso App 1.5.0", "Contoso Ltd")), writeStatus: HttpStatusCode.InternalServerError);
        var log = new List<string>();
        using var service = new IntunePublishService(handler);
        var plan = await IntuneSupersedenceFlow.PrepareAsync(service, Options(), Request(SupersedenceConfirmer.Fixed(SupersedenceAnswer.Yes)), log.Add, default);
        var failure = await IntuneSupersedenceFlow.ApplyAsync(service, Options(), plan!, "new-app", log.Add, default);
        Assert.NotNull(failure);
        Assert.Contains("relation could not be written", failure);
        Assert.Contains(log, l => l.StartsWith("Supersedence not applied") && l.Contains("app is published"));
    }

    [Theory]
    [InlineData("Contoso App 1.5.0", "Contoso App", "1.5.0")]
    [InlineData("Contoso App v2.1 (x64)", "Contoso App", "2.1")]
    [InlineData("Contoso App - 3", "Contoso App", "3")]
    [InlineData("Contoso App", "Contoso App", null)]
    [InlineData("Other 1.0", "Contoso App", null)]
    public void TitleVersion_ReadsTheVersionAfterTheBaseTitle(string name, string baseTitle, string? expected) =>
        Assert.Equal(expected, IntuneTitleVersion.Read(name, baseTitle));
}
