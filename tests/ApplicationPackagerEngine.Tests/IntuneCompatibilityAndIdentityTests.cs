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
    public void TranslatedRequirementsAreInformationAndAMissingArchitectureNeedsReview()
    {
        var m = Manifest();
        m.Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "x64" }];
        m.Architecture = "";
        var findings = IntuneCompatibilityFindings.Build(m, [new StageRequirement { ConditionId = "vpn-connected", Value = true }]);
        // The saved conditions of the run replace the requirement rules of the manifest, as on ConfigMgr.
        Assert.Equal(["Info:RequirementTranslated", "Review:ArchitectureUnresolved"], findings.Select(f => $"{f.Severity}:{f.Code}"));
        Assert.Contains("'vpn-connected'", findings[0].Message);
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
        Assert.Empty(IntunePublishService.FormerIdentityTags(legacy));
    }

    [Fact]
    public void TwoMajorLinesOfALegacyTitleKeepTheirOwnTags()
    {
        var net8 = Manifest();
        net8.AppName = "Microsoft .NET Runtime 8.0.6 (x64)";
        net8.SoftwareVersion = "8.0.6";
        var net6 = Manifest();
        net6.AppName = "Microsoft .NET Runtime 6.0.31 (x64)";
        net6.SoftwareVersion = "6.0.31";
        Assert.NotEqual(IntunePublishService.IdentityTag(net8), IntunePublishService.IdentityTag(net6));
    }

    [Fact]
    public void TheFormerTagOfACatalogManifestIsExactlyTheLegacyTagOfThatManifest()
    {
        var m = Manifest();
        m.ApplicationId = "catalog:dotnet8";
        m.ProfileId = "";
        m.AppName = "Microsoft .NET Runtime 8.0.6 (x64)";
        m.SoftwareVersion = "8.0.6";
        var former = Assert.Single(IntunePublishService.FormerIdentityTags(m));
        var legacy = Manifest();
        legacy.AppName = m.AppName;
        legacy.SoftwareVersion = m.SoftwareVersion;
        legacy.ProfileId = "";
        Assert.Equal(IntunePublishService.IdentityTag(legacy), former);
    }

    [Fact]
    public void AnAppTaggedForAnotherApplicationIsNotAdoptedThroughAFormerTag()
    {
        const string former = "AppPackager:legacy:Contoso-1.0/default";
        Assert.True(IntunePublishService.CarriesOnlyTagsOf("operator note\n" + former, [former]));
        Assert.False(IntunePublishService.CarriesOnlyTagsOf(former + "\nAppPackager:catalog:other/default", [former]));
        Assert.False(IntunePublishService.CarriesOnlyTagsOf("operator note", [former]));
    }

    [Fact]
    public async Task AnAppOnTheFormerLegacyTagOfACatalogManifestIsUpdatedAndRetagged()
    {
        var manifest = Manifest();
        manifest.ApplicationId = "catalog:contoso";
        manifest.ProfileId = "";
        manifest.AppName = "Contoso App 1.0";
        const string former = "AppPackager:legacy:Contoso-App-1.0/default";
        var handler = Graph(uri => uri.Contains("isof(")
            ? $$"""{"value":[{"id":"app-former","@odata.type":"#microsoft.graph.win32LobApp","notes":"{{former}}"}]}"""
            : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), manifest, Options());

        var update = handler.Requests.First(r => r.Method == HttpMethod.Patch && r.Uri.EndsWith("/mobileApps/app-former"));
        Assert.Contains("\"notes\":\"AppPackager:catalog:contoso/default\\nAppPackagerContent:", update.Body);
    }

    [Fact]
    public async Task AnAppThatAlsoCarriesAnotherApplicationsTagIsNotAdoptedThroughTheFormerTag()
    {
        var manifest = Manifest();
        manifest.ApplicationId = "catalog:contoso";
        manifest.ProfileId = "";
        manifest.AppName = "Contoso App 1.0";
        const string notes = "AppPackager:legacy:Contoso-App-1.0/default\\nAppPackager:catalog:fabrikam/default";
        var handler = Graph(uri => uri.Contains("isof(")
            ? $$"""{"value":[{"id":"app-other","@odata.type":"#microsoft.graph.win32LobApp","notes":"{{notes}}"}]}"""
            : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), manifest, Options());

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch && r.Uri.EndsWith("/mobileApps/app-other"));
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
        Assert.Contains("\"notes\":\"AppPackager:legacy:Contoso-App/default\\nAppPackagerContent:", create.Body);
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

    [Fact]
    public async Task AnAppWithTheNameButAnotherApplicationsTagIsNeverAdopted()
    {
        var handler = Graph(uri => uri.Contains("displayName eq")
            ? """{"value":[{"id":"other-profile","@odata.type":"#microsoft.graph.win32LobApp","notes":"AppPackager:legacy:Contoso-App/pilot001"}]}"""
            : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        Assert.DoesNotContain(handler.Requests, r => r.Uri.Contains("/mobileApps/other-profile"));
        var create = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.EndsWith("/deviceAppManagement/mobileApps"));
        Assert.Contains("\"notes\":\"AppPackager:legacy:Contoso-App/default\\nAppPackagerContent:", create.Body);
    }

    [Fact]
    public async Task OperatorTextInNotesIsKeptAndTheTagIsMatchedOnItsOwnLine()
    {
        const string tag = "AppPackager:legacy:Contoso-App/default";
        var handler = Graph(uri => uri.Contains("isof(")
            ? $$"""{"value":[{"id":"app-tagged","@odata.type":"#microsoft.graph.win32LobApp","notes":"Owner: desktop team\r\nTicket 4411\r\n{{tag}}"}]}"""
            : null);
        using var svc = new IntunePublishService(handler);

        await svc.PublishAsync(IntuneWinFixture.Create(_dir), Manifest(), Options());

        var update = handler.Requests.First(r => r.Method == HttpMethod.Patch && r.Uri.EndsWith("/mobileApps/app-tagged"));
        Assert.Contains($"\"notes\":\"Owner: desktop team\\r\\nTicket 4411\\r\\n{tag}\\r\\nAppPackagerContent:", update.Body);
        Assert.DoesNotContain(handler.Requests, r => Uri.UnescapeDataString(r.Uri).Contains("displayName eq"));
    }

    [Theory]
    [InlineData(null, "T")]
    [InlineData("", "T")]
    [InlineData("note", "note\nT")]
    [InlineData("note\r\nAppPackager:old/default\r\n", "note\r\nT")]
    [InlineData("AppPackager:old/default", "T")]
    public void TheTagReplacesAnyEarlierTagLineAndKeepsTheRest(string? notes, string expected)
    {
        Assert.Equal(expected.Replace("T", "AppPackager:x/default"), IntunePublishService.NotesWithTag(notes, "AppPackager:x/default"));
    }

    [Theory]
    [InlineData(null, "T\nC")]
    [InlineData("", "T\nC")]
    [InlineData("note", "note\nT\nC")]
    [InlineData("note\r\nAppPackager:old/default\r\nAppPackagerContent:old\r\n", "note\r\nT\r\nC")]
    [InlineData("AppPackagerContent:old", "T\nC")]
    [InlineData("AppPackager:old/default\nnote after", "note after\nT\nC")]
    public void TheContentLineIsWrittenAfterTheTagAndReplacesAnEarlierOne(string? notes, string expected)
    {
        Assert.Equal(expected.Replace("T", "AppPackager:x/default").Replace("C", "AppPackagerContent:d1"),
            IntunePublishService.NotesWithTag(notes, "AppPackager:x/default", "AppPackagerContent:d1"));
    }

    [Fact]
    public void TheContentLineIsNotAnIdentityTagAndDoesNotChangeWhoseAppItIs()
    {
        const string notes = "AppPackager:x/default\nAppPackagerContent:d1";

        Assert.True(IntunePublishService.CarriesTag(notes, "AppPackager:x/default"));
        Assert.False(IntunePublishService.CarriesAnyTag("AppPackagerContent:d1"));
        Assert.True(IntunePublishService.CarriesOnlyTagsOf(notes, ["AppPackager:x/default"]));
        Assert.True(IntunePublishService.CarriesContentTag(notes, "AppPackagerContent:d1"));
        Assert.False(IntunePublishService.CarriesContentTag(notes, "AppPackagerContent:d2"));
        Assert.False(IntunePublishService.CarriesContentTag("see AppPackagerContent:d1 here", "AppPackagerContent:d1"));
    }

    [Fact]
    public void ATagInsideALongerLineIsNotAMatch()
    {
        Assert.False(IntunePublishService.CarriesTag("see AppPackager:x/default for details", "AppPackager:x/default"));
        Assert.True(IntunePublishService.CarriesTag("text\n AppPackager:x/default ", "AppPackager:x/default"));
        Assert.False(IntunePublishService.CarriesAnyTag("text only"));
    }

    // The ConfigMgr post-execution behavior and the Intune restart behavior mean the same thing:
    // "No specific action" suppresses a restart, "The software install may force a restart" allows
    // one, and "The client will force a mandatory restart" forces one.
    [Theory]
    [InlineData(null, "basedOnReturnCode")]
    [InlineData("", "basedOnReturnCode")]
    [InlineData("BasedOnExitCode", "basedOnReturnCode")]
    [InlineData("NoAction", "suppress")]
    [InlineData("ProgramReboot", "allow")]
    [InlineData("ForceReboot", "force")]
    [InlineData("forcereboot", "force")]
    [InlineData(" ForceReboot ", "force")]
    [InlineData("SomethingElse", "basedOnReturnCode")]
    public void ThePostExecutionBehaviorPublishesTheMatchingRestartBehavior(string? behavior, string expected) =>
        Assert.Equal(expected, IntunePublishService.DeviceRestartBehavior(behavior));

    [Fact]
    public void TheConversionOfAConfigMgrApplicationUsesTheSameMapping()
    {
        foreach (var behavior in new[] { "BasedOnExitCode", "NoAction", "ProgramReboot", "ForceReboot", "" })
            Assert.Equal(IntunePublishService.DeviceRestartBehavior(behavior), ApplicationPackagerEngine.Services.CmImport.CmImportPlanner.RestartBehavior(behavior));
    }
}
