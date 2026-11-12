using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Step 1 of the import of vendor definitions from a URL: the fetch, the validation, the preview rules and the storage.</summary>
public sealed class VendorImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-vendor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- helpers ----

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request, Requests.Count));
        }
    }

    /// <summary>A stream of x characters that never ends and has no length.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'x', offset, count); return count; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Json(string body, string type = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static FeedFetcher Fetcher(ScriptedHandler handler, FeedFetchLimits? limits = null) =>
        new(() => handler, limits, (_, _) => Task.CompletedTask);

    private static readonly Uri Feed = new("https://vendor.example/feed.json?v=SECRETQUERY");

    private static string Definition(string id = "contoso-agent", Action<JsonObject>? change = null)
    {
        var def = new AppDefinition
        {
            Id = id, Vendor = "Contoso", Application = "Contoso Agent", CMName = "Contoso Agent", Architecture = "x64",
            VendorUrl = "https://contoso.example/", InstallerType = "MSI", DownloadUrl = "https://contoso.example/agent-{version}.msi",
            InstallArgs = "/qn", Detection = new DetectionConfig { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\Contoso" }
        };
        var node = JsonSerializer.SerializeToNode(def)!.AsObject();
        change?.Invoke(node);
        return node.ToJsonString();
    }

    private static byte[] Doc(params string[] definitions) =>
        Encoding.UTF8.GetBytes($$"""{"FeedFormat":1,"Vendor":"Contoso Software","FeedId":"contoso-software","Updated":"2026-11-12T08:00:00Z","Definitions":[{{string.Join(",", definitions)}}]}""");

    // ---- fetch limits ----

    [Theory]
    [InlineData("http://vendor.example/feed.json")]
    [InlineData("file:///C:/feed.json")]
    [InlineData("ftp://vendor.example/feed.json")]
    [InlineData("https://user:pw@vendor.example/feed.json")]
    public async Task AnAddressThatIsNotPlainHttpsIsRefusedBeforeAnyRequest(string address)
    {
        var handler = new ScriptedHandler((_, _) => Json("{}"));

        await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(handler).FetchAsync(new Uri(address), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ARedirectToHttpIsRefusedAndTooManyRedirectsAreRefused()
    {
        var toHttp = new ScriptedHandler((_, _) => Redirect("http://vendor.example/feed.json"));
        var loop = new ScriptedHandler((_, n) => Redirect($"https://vendor.example/{n}"));

        var http = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(toHttp).FetchAsync(Feed, CancellationToken.None));
        var many = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(loop).FetchAsync(Feed, CancellationToken.None));

        Assert.Contains("HTTPS only", http.Message);
        Assert.Contains("More than 5 redirects", many.Message);
        Assert.Equal(6, loop.Requests.Count);
    }

    [Fact]
    public async Task ARelativeRedirectOnTheSameHostIsFollowedAndTheFinalAddressKeepsNoQuery()
    {
        var handler = new ScriptedHandler((request, n) => n == 1 ? Redirect("/v2/feed.json?sig=ABC") : Json("{}"));

        var document = await Fetcher(handler).FetchAsync(Feed, CancellationToken.None);

        Assert.Equal("https://vendor.example/v2/feed.json", document.SafeAddress);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ADocumentOverTheSizeLimitIsCutOffAndRefused()
    {
        // A stream with no declared length: only the read loop can stop it.
        var big = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        });
        var declared = new ScriptedHandler((_, _) =>
        {
            var response = Json("{}");
            response.Content.Headers.ContentLength = 5 * 1024 * 1024;
            return response;
        });

        var read = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(big).FetchAsync(Feed, CancellationToken.None));
        var header = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(declared).FetchAsync(Feed, CancellationToken.None));

        Assert.Contains("limit", read.Message);
        Assert.Contains("limit", header.Message);
    }

    [Theory]
    [InlineData("text/html", false)]
    [InlineData("application/xml", false)]
    [InlineData("application/json", true)]
    [InlineData("text/plain", true)]
    public async Task OnlyJsonOrPlainTextIsAccepted(string type, bool accepted)
    {
        var handler = new ScriptedHandler((_, _) => Json("{}", type));

        if (accepted) await Fetcher(handler).FetchAsync(Feed, CancellationToken.None);
        else Assert.Contains(type, (await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(handler).FetchAsync(Feed, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task ADocumentThatIsNotUtf8IsRefused()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xFE, 0xFD, 0x80]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        });

        Assert.Contains("not UTF-8", (await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(handler).FetchAsync(Feed, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task AnHttpErrorNamesTheStatusAndTheHostAndNeverTheQuery()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(handler).FetchAsync(Feed, CancellationToken.None));

        Assert.Contains("404", ex.Message);
        Assert.Contains("vendor.example", ex.Message);
        Assert.DoesNotContain("SECRETQUERY", ex.Message);
    }

    [Fact]
    public async Task ACertificateErrorIsRefusedWithTheHostNamed()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("ssl", new AuthenticationException("The remote certificate is invalid.")));

        var ex = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(handler).FetchAsync(Feed, CancellationToken.None));

        Assert.Contains("certificate of vendor.example", ex.Message);
        Assert.DoesNotContain("SECRETQUERY", ex.Message);
    }

    [Fact]
    public async Task A429IsRetriedWithTheWaitOfTheAnswerAndThenSucceedsOrStopsAfterThreeRetries()
    {
        var waits = new List<TimeSpan>();
        var recovering = new ScriptedHandler((_, n) =>
        {
            if (n >= 3) return Json("{}");
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });
        var stubborn = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var ok = await new FeedFetcher(() => recovering, null, (span, _) => { waits.Add(span); return Task.CompletedTask; }).FetchAsync(Feed, CancellationToken.None);
        var stop = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(stubborn).FetchAsync(Feed, CancellationToken.None));

        Assert.Equal(3, recovering.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], waits);
        Assert.Equal(4, stubborn.Requests.Count);
        Assert.Contains("503", stop.Message);
        Assert.NotNull(ok);
    }

    [Fact]
    public async Task TheTimeLimitCountsTheWaitsOfARetry()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return response;
        });
        var limits = new FeedFetchLimits { TotalTime = TimeSpan.FromMilliseconds(300) };

        var ex = await Assert.ThrowsAsync<FeedFetchException>(() => new FeedFetcher(() => handler, limits).FetchAsync(Feed, CancellationToken.None));

        Assert.Contains("did not finish", ex.Message);
    }

    [Fact]
    public async Task ACancelledFetchIsACancellationAndNotAFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new ScriptedHandler((_, _) => Json("{}"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetcher(handler).FetchAsync(Feed, cts.Token));
    }

    [Fact]
    public async Task TheRequestIsGenericAndTheDigestIsTheSha256OfTheBytes()
    {
        HttpRequestMessage? seen = null;
        var handler = new ScriptedHandler((request, _) => { seen = request; return Json("{\"a\":1}"); });

        var document = await Fetcher(handler).FetchAsync(Feed, CancellationToken.None);

        Assert.Equal(Convert.ToHexString(SHA256.HashData("{\"a\":1}"u8.ToArray())), document.Sha256);
        Assert.StartsWith("AppPackagerDesktop/", seen!.Headers.UserAgent.ToString());
        Assert.False(seen.Headers.Contains("Cookie"));
        Assert.False(seen.Headers.Contains("Referer"));
    }

    [Theory]
    [InlineData("https://v.example/feed.json?token=abc")]
    [InlineData("https://v.example/feed.json?a=1&API_KEY=abc")]
    [InlineData("https://v.example/feed.json?sig=abc")]
    [InlineData("https://v.example/feed.json?sv=2024&sas=abc")]
    public async Task AnAnonymousImportRefusesAnAddressWhoseQueryLooksLikeASecret(string address)
    {
        var handler = new ScriptedHandler((_, _) => Json("{}"));

        var ex = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(handler).FetchAsync(new Uri(address), CancellationToken.None));

        Assert.Contains("looks like a secret", ex.Message);
        Assert.DoesNotContain("abc", ex.Message);
        Assert.Empty(handler.Requests);
    }

    // ---- validation ----

    [Fact]
    public void AValidFeedIsReadAndTheStoredCopyIsReSerialized()
    {
        var feed = FeedValidator.Validate(Doc(Definition()));

        Assert.False(feed.Refused);
        var definition = Assert.Single(feed.Definitions);
        Assert.False(definition.Blocked);
        Assert.Equal("contoso-software", feed.FeedId);
        Assert.Contains("contoso.example", definition.Hosts);
        // The stored file is the model written again, not the bytes of the vendor.
        Assert.Equal(JsonSerializer.Serialize(JsonSerializer.Deserialize<AppDefinition>(definition.Json), new JsonSerializerOptions { WriteIndented = true }), definition.Json);
    }

    [Theory]
    [InlineData("""{"FeedFormat":2,"Definitions":[]}""", "FeedFormat")]
    [InlineData("""[1,2]""", "not a JSON object")]
    [InlineData("""{"FeedFormat":1,""", "not valid JSON")]
    public void ADocumentThatIsNotAFeedOfFormat1IsRefusedWithTheReason(string text, string reason)
    {
        var feed = FeedValidator.Validate(Encoding.UTF8.GetBytes(text));

        Assert.True(feed.Refused);
        Assert.Contains(reason, feed.Findings.Single().Message);
    }

    [Fact]
    public void ADuplicatePropertyNameIsRefusedAlsoWithAnotherLetterCase()
    {
        var same = FeedValidator.Validate(Encoding.UTF8.GetBytes("""{"FeedFormat":1,"Vendor":"a","Vendor":"b","Definitions":[]}"""));
        var cased = FeedValidator.Validate(Encoding.UTF8.GetBytes("""{"FeedFormat":1,"Definitions":[{"Id":"a","id":"b"}]}"""));

        Assert.Contains("appears twice", same.Findings.Single().Message);
        Assert.Contains("appears twice", cased.Findings.Single().Message);
    }

    [Fact]
    public void ADefinitionWithAnUnknownPropertyIsBlockedAndOthersStillImport()
    {
        var feed = FeedValidator.Validate(Doc(Definition("good-one"), Definition("bad-one", n => n["Surprise"] = "x")));

        Assert.False(feed.Definitions[0].Blocked);
        Assert.True(feed.Definitions[1].Blocked);
        Assert.Contains("Surprise", feed.Definitions[1].Findings.Single().Message);
    }

    [Fact]
    public void SchemaAndAcquisitionAreAllowedAndTheOriginPropertiesAreRefused()
    {
        var allowed = FeedValidator.Validate(Doc(Definition("a-1", n => { n["Schema"] = 1; n["Acquisition"] = new JsonObject { ["Note"] = "x" }; })));
        var refused = FeedValidator.Validate(Doc(Definition("a-2", n => n["ImportedFrom"] = "https://x/"), Definition("a-3", n => n["ImportedVendor"] = "x")));

        Assert.False(allowed.Definitions[0].Blocked);
        Assert.All(refused.Definitions, d => Assert.Contains(d.Findings, f => f.Code == "RefusedProperty"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("con")]
    [InlineData("NUL.txt")]
    [InlineData("name.")]
    [InlineData("....")]
    public void AnIdThatIsNotInTheGrammarIsBlocked(string id)
    {
        var feed = FeedValidator.Validate(Doc(Definition(id)));

        Assert.Contains(feed.Definitions.Single().Findings, f => f.Code == "IdInvalid");
    }

    [Fact]
    public void ADuplicateIdInTheDocumentIsBlockedForTheSecondDefinition()
    {
        var feed = FeedValidator.Validate(Doc(Definition("same"), Definition("SAME")));

        Assert.False(feed.Definitions[0].Blocked);
        Assert.Contains(feed.Definitions[1].Findings, f => f.Code == "IdDuplicate");
    }

    [Theory]
    [InlineData("DownloadUrl", "http://contoso.example/a.msi", "AddressNotHttps")]
    [InlineData("DownloadUrl", @"\\server\share\a.msi", "AddressNotHttps")]
    [InlineData("DownloadUrl", "file:///C:/a.msi", "AddressNotHttps")]
    [InlineData("DownloadUrl", "https://user:pw@contoso.example/a.msi", "AddressUserInfo")]
    [InlineData("VendorUrl", "ftp://contoso.example/", "AddressNotHttps")]
    [InlineData("SecurityAdvisoryUrl", "http://contoso.example/sa", "AddressNotHttps")]
    [InlineData("PayloadSha256Url", "http://contoso.example/a.sha256", "AddressNotHttps")]
    public void AnAddressThatIsNotPlainHttpsIsBlockedAndNamed(string field, string value, string code)
    {
        var feed = FeedValidator.Validate(Doc(Definition("a-1", n => n[field] = value)));

        var finding = Assert.Single(feed.Definitions.Single().Findings, f => f.Code == code);
        Assert.Contains(field, finding.Message);
    }

    [Fact]
    public void TheAddressesOfTheScraperTheBootstrapperAndTheVariantsAreChecked()
    {
        var feed = FeedValidator.Validate(Doc(
            Definition("a-1", n => n["VersionScraper"] = new JsonObject { ["Url"] = "http://x.example/v", ["Type"] = "Regex", ["Pattern"] = "x" }),
            Definition("a-2", n => n["VersionScraper"] = new JsonObject { ["Hops"] = new JsonArray(new JsonObject { ["Url"] = "https://a.example/x" }, new JsonObject { ["Url"] = "ftp://b.example/y" }) }),
            Definition("a-3", n => n["PayloadVariants"] = new JsonArray(new JsonObject { ["NameSuffix"] = "x86", ["DownloadUrl"] = "http://v.example/x86.msi" }))));

        Assert.Contains(feed.Definitions[0].Findings, f => f.Message.Contains("VersionScraper.Url"));
        Assert.Contains(feed.Definitions[1].Findings, f => f.Message.Contains("Hops[1].Url"));
        Assert.Contains(feed.Definitions[2].Findings, f => f.Message.Contains("PayloadVariants[0].DownloadUrl"));
    }

    [Fact]
    public void ARelativeHopAfterTheFirstIsAllowedAndATemplateAddressIsChecked()
    {
        var feed = FeedValidator.Validate(Doc(Definition("a-1", n => n["VersionScraper"] = new JsonObject
        {
            ["Hops"] = new JsonArray(new JsonObject { ["Url"] = "https://a.example/{version}/page" }, new JsonObject { ["Url"] = "{capture}/next.html" })
        })));

        Assert.False(feed.Definitions.Single().Blocked);
        Assert.Contains("a.example", feed.Definitions.Single().Hosts);
    }

    [Theory]
    [InlineData("Architecture", "ia64")]
    [InlineData("Application", "..\\evil")]
    [InlineData("Vendor", "C:\\temp")]
    [InlineData("CMName", "")]
    public void AnArchitectureOrANameThatIsNotPlainIsBlocked(string field, string value)
    {
        var feed = FeedValidator.Validate(Doc(Definition("a-1", n => n[field] = value)));

        Assert.True(feed.Definitions.Single().Blocked);
    }

    [Fact]
    public void ASlashInANameIsAllowed()
    {
        var feed = FeedValidator.Validate(Doc(Definition("a-1", n => { n["Application"] = "MySQL Connector/NET"; n["Vendor"] = "GN Audio A/S"; })));

        Assert.False(feed.Definitions.Single().Blocked);
    }

    [Fact]
    public void MoreThan200DefinitionsAreRefused()
    {
        var many = Enumerable.Range(0, 201).Select(i => Definition($"d{i}")).ToArray();

        Assert.Contains("201 definitions", FeedValidator.Validate(Doc(many)).Findings.Single().Message);
    }

    [Fact]
    public void ADocumentNestedDeeperThan64LevelsIsRefused()
    {
        var deep = new string('[', 70) + new string(']', 70);

        Assert.True(FeedValidator.Validate(Encoding.UTF8.GetBytes($$"""{"FeedFormat":1,"Definitions":[],"x":{{deep}}}""")).Refused);
    }

    [Fact]
    public void ADocumentWithoutAnEnvelopeIsOneDefinitionAndNeedsAVendorAndAFeedId()
    {
        var feed = FeedValidator.Validate(Encoding.UTF8.GetBytes(Definition()));

        Assert.False(feed.HasEnvelope);
        Assert.Null(feed.FeedId);
        Assert.Single(feed.Definitions);
    }

    [Theory]
    [InlineData("contoso-software", true)]
    [InlineData("ab", false)]
    [InlineData("Contoso", false)]
    [InlineData("con", false)]
    [InlineData("nul", false)]
    [InlineData("a/b", false)]
    [InlineData("com1", false)]
    public void TheFeedIdGrammar(string feedId, bool valid) => Assert.Equal(valid, FeedValidator.IsValidFeedId(feedId));

    [Theory]
    [InlineData("""{"FeedFormat":1,"Vendor":1,"Definitions":[]}""")]
    [InlineData("""{"FeedFormat":1,"FeedId":true,"Definitions":[]}""")]
    [InlineData("""{"FeedFormat":1,"Updated":5,"Definitions":[]}""")]
    [InlineData("""{"FeedFormat":1,"Definitions":[null]}""")]
    [InlineData("""{"FeedFormat":1,"Definitions":[1,"x",[]]}""")]
    [InlineData("""{"FeedFormat":1,"Definitions":[{"Id":5}]}""")]
    [InlineData("""{"FeedFormat":1,"Definitions":[{"Id":null}]}""")]
    [InlineData("""{"FeedFormat":1,"Definitions":[{"Id":"a-1","Application":null,"Vendor":"v","CMName":"c"}]}""")]
    [InlineData("""{"FeedFormat":1,"Definitions":[{"Id":"a-1","Detection":{"Type":null}}]}""")]
    [InlineData("""{"FeedFormat":1,"Definitions":[{"Id":"a-1","PayloadVariants":[null]}]}""")]
    [InlineData("""{"FeedFormat":1,"Definitions":[{"Id":"a-1","InstallerSettings":[null]}]}""")]
    [InlineData("""{"FeedFormat":1,"Definitions":[{"Id":"a-1","VersionScraper":{"Hops":[null]}}]}""")]
    public void AMalformedValueGivesAFindingAndNeverAnException(string text)
    {
        var feed = FeedValidator.Validate(Encoding.UTF8.GetBytes(text));

        // A refused document, or a definition that is blocked; never a thrown exception and never an importable definition.
        Assert.True(feed.Refused || feed.Definitions.All(d => d.Blocked), text);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example/x")]
    [InlineData("\\evil.example\\x")]
    [InlineData("http:evil.example/x")]
    [InlineData("ftp://evil.example/x")]
    public void ARelativeHopThatLeavesTheHostOfTheHopBeforeItIsBlocked(string hop)
    {
        var feed = FeedValidator.Validate(Doc(Definition("a-1", n => n["VersionScraper"] = new JsonObject
        {
            ["Hops"] = new JsonArray(new JsonObject { ["Url"] = "https://a.example/start" }, new JsonObject { ["Url"] = hop })
        })));

        Assert.True(feed.Definitions.Single().Blocked, hop);
    }

    [Theory]
    [InlineData("https://{capture}/a.msi")]
    [InlineData("https://good.example{capture}")]
    [InlineData("{scheme}://good.example/a.msi")]
    [InlineData("https://{host}.good.example/a.msi")]
    public void APlaceholderInTheSchemeOrTheHostIsBlocked(string address)
    {
        var feed = FeedValidator.Validate(Doc(Definition("a-1", n => n["DownloadUrl"] = address)));

        Assert.Contains(feed.Definitions.Single().Findings, f => f.Code is "AddressPlaceholderInHost" or "AddressNotHttps" or "AddressInvalid");
    }

    [Fact]
    public void AnInvalidFeedIdInTheDocumentIsAWarningAndTheAdministratorChoosesAnother()
    {
        var feed = FeedValidator.Validate(Encoding.UTF8.GetBytes(
            $$"""{"FeedFormat":1,"Vendor":"V","FeedId":"Contoso","Definitions":[{{Definition()}}]}"""));

        Assert.False(feed.Refused);
        Assert.Contains(feed.Findings, f => f.Severity == FindingSeverity.Warning && f.Code == "FeedIdInvalid");
    }

    [Theory]
    [InlineData("contoso-agent\n")]
    [InlineData("a b")]
    public void AnIdWithATrailingNewlineIsNotInTheGrammar(string id) => Assert.False(FeedValidator.IsValidDefinitionId(id));

    [Fact]
    public void TheFeedIdGrammarRefusesATrailingNewlineAndTheReservedZeroDevices()
    {
        Assert.False(FeedValidator.IsValidFeedId("contoso\n"));
        Assert.False(FeedValidator.IsValidFeedId("com0"));
        Assert.False(FeedValidator.IsValidFeedId("lpt0"));
    }

    // ---- code in a definition ----

    [Fact]
    public void EveryTopLevelPropertyOfTheFormatIsClassifiedAsCodeOrNot() =>
        Assert.Empty(DefinitionCode.Unclassified());

    [Theory]
    [InlineData("UninstallCommand", "msiexec /x {GUID}")]
    [InlineData("PostInstallScript", null)]
    [InlineData("BootstrapperLayout", null)]
    [InlineData("LocalInstaller", null)]
    [InlineData("SideFiles", null)]
    public void ADefinitionThatCarriesCodeIsBlockedInEveryTrustState(string property, string? text)
    {
        JsonNode value = text is not null ? text
            : property == "SideFiles" ? new JsonArray(new JsonObject { ["Key"] = "k", ["FileName"] = "a.txt", ["Label"] = "a" })
            : new JsonObject();
        var feed = FeedValidator.Validate(Doc(Definition("a-1", n => n[property] = value)));

        var definition = feed.Definitions.Single();
        Assert.True(definition.Blocked);
        Assert.True(definition.CarriesCode);
    }

    public static TheoryData<string, string> CodeCases => new()
    {
        { "PostInstallScript", "PostInstallScript" },
        { "UninstallCommand", "UninstallCommand" },
        { "ExtractSetup", "ExtractSetup" },
        { "BootstrapperLayout", "BootstrapperLayout" },
        { "InstallerSettings", "InstallerSettings" },
        { "LocalSource", "LocalSource" },
        { "LocalInstaller", "LocalInstaller" },
        { "SideFiles", "SideFiles" },
        { "TargetSecret", "TargetSecret" },
        { "Odt", "Odt" },
        { "ZipPayload", "ZipPayload" }
    };

    private static AppDefinition WithCode(string property)
    {
        var d = new AppDefinition();
        switch (property)
        {
            case "PostInstallScript": d.PostInstallScript = new PostInstallScriptConfig(); break;
            case "UninstallCommand": d.UninstallCommand = "msiexec /x x"; break;
            case "ExtractSetup": d.ExtractSetup = new ExtractSetupConfig(); break;
            case "BootstrapperLayout": d.BootstrapperLayout = new BootstrapperLayoutConfig(); break;
            case "InstallerSettings": d.InstallerSettings = [new InstallerParameter { Key = "k", CommandFragment = "/x" }]; break;
            case "LocalSource": d.LocalSource = new LocalSourceConfig(); break;
            case "LocalInstaller": d.LocalInstaller = new LocalInstallerConfig(); break;
            case "SideFiles": d.SideFiles = [new SideFileConfig { Key = "k" }]; break;
            case "TargetSecret": d.TargetSecret = new TargetSecretConfig(); break;
            case "Odt": d.Odt = new OdtConfig(); break;
            case "ZipPayload": d.ZipPayload = new ZipPayloadConfig { InstallDir = @"C:\Windows\System32" }; break;
            default: throw new InvalidOperationException(property);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(CodeCases))]
    public void EveryPropertyOfTheCodeListBlocksTheDefinition(string property, string reported)
    {
        var found = DefinitionCode.Find(WithCode(property));

        Assert.Contains(found, f => f.StartsWith(reported, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryEntryOfTheCodeListHasACaseAndNothingIsInBothLists()
    {
        Assert.Equal(DefinitionCode.CodeProperties.Order(), CodeCases.Select(c => (string)c[0]).Order());
        Assert.Empty(DefinitionCode.CodeProperties.Intersect(DefinitionCode.OtherProperties));
    }

    [Fact]
    public void ANestedScriptAndAVariantUninstallCommandAndAVariantScriptCarryCode()
    {
        var clauses = new AppDefinition { Detection = new DetectionConfig { Type = "Compound", Clauses = [new DetectionConfig { Type = "Script", ScriptText = "1" }] } };
        var variant = new AppDefinition { PayloadVariants = [new AppDefinitionPayloadVariant { NameSuffix = "x86", UninstallCommand = "x" }] };
        var variantScript = new AppDefinition { PayloadVariants = [new AppDefinitionPayloadVariant { NameSuffix = "x86", Detection = new DetectionConfig { Type = "Script", ScriptText = "1" } }] };

        Assert.Contains("Detection.Clauses[0].ScriptText", DefinitionCode.Find(clauses));
        Assert.Contains("PayloadVariants[x86].UninstallCommand", DefinitionCode.Find(variant));
        Assert.Contains("PayloadVariants[x86].Detection.ScriptText", DefinitionCode.Find(variantScript));
    }

    [Fact]
    public void AnOdtAndAZipPayloadInADocumentAreBlockedWithTheirNames()
    {
        var feed = FeedValidator.Validate(Doc(
            Definition("a-1", n => n["Odt"] = new JsonObject { ["Channel"] = "Current\" DownloadPath=\"https://evil.example/o", ["ProductIds"] = new JsonArray("O365ProPlusRetail") }),
            Definition("a-2", n => n["ZipPayload"] = new JsonObject { ["InstallDir"] = @"C:\Windows\System32" })));

        Assert.Contains("Odt", feed.Definitions[0].CodeProperties);
        Assert.Contains("ZipPayload", feed.Definitions[1].CodeProperties);
        Assert.All(feed.Definitions, d => Assert.True(d.Blocked));
    }

    [Fact]
    public void AScriptDetectionAndAnExpandedInstallCommandCarryCode()
    {
        var script = FeedValidator.Validate(Doc(Definition("a-1", n => n["Detection"] = new JsonObject { ["Type"] = "Script", ["ScriptText"] = "Write-Output 1" })));
        var expand = FeedValidator.Validate(Doc(Definition("a-2", n => n["ExpandInstallArgs"] = true)));
        var plain = FeedValidator.Validate(Doc(Definition("a-3")));

        Assert.Contains("Detection.ScriptText", script.Definitions.Single().CodeProperties);
        Assert.Contains("InstallArgs with ExpandInstallArgs", expand.Definitions.Single().CodeProperties);
        Assert.False(plain.Definitions.Single().CarriesCode);
    }

    // ---- preview and import ----

    private ImportedDefinitionStore Store() => new(_root);

    private VendorFeedImporter Importer(byte[] document, string[]? shipped = null, string[]? user = null, ImportedDefinitionStore? store = null) =>
        new(Fetcher(new ScriptedHandler((_, _) => Json(Encoding.UTF8.GetString(document)))), store ?? Store(),
            () => new HashSet<string>(shipped ?? [], StringComparer.OrdinalIgnoreCase),
            () => new HashSet<string>(user ?? [], StringComparer.OrdinalIgnoreCase),
            new FixedClock(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ImportConfirmation Confirm(bool notVerified = true, params string[] replace) =>
        new("Contoso Software", "contoso-software", notVerified, new HashSet<string>(replace, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public async Task ThePreviewWritesNothingAndCancelLeavesNothing()
    {
        var preview = await Importer(Doc(Definition())).PreviewAsync(Feed, null, CancellationToken.None);

        Assert.False(Directory.Exists(_root));
        Assert.Equal(FeedTrust.NotVerified, preview.TrustState);
        Assert.Equal("Definition document NOT verified", preview.TrustLabel);
    }

    [Fact]
    public async Task ADocumentThatIsNotVerifiedNeedsTheConfirmationOfThatStateEveryTime()
    {
        var importer = Importer(Doc(Definition()));
        var preview = await importer.PreviewAsync(Feed, null, CancellationToken.None);

        var ex = Assert.Throws<InvalidOperationException>(() => importer.Import(preview, Confirm(notVerified: false)));

        Assert.Contains("Definition document NOT verified", ex.Message);
        Assert.False(Directory.Exists(_root));
        importer.Import(preview, Confirm());
        Assert.True(File.Exists(Path.Combine(_root, "contoso-software", "contoso-agent.json")));
    }

    [Fact]
    public async Task APinThatMatchesGivesThePinnedStateAndNeedsNoNotVerifiedConfirmation()
    {
        var document = Doc(Definition());
        var pin = Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant();
        var importer = Importer(document);

        var preview = await importer.PreviewAsync(Feed, pin, CancellationToken.None);
        var source = importer.Import(preview, Confirm(notVerified: false));

        Assert.Equal(FeedTrust.PinnedDigest, preview.TrustState);
        Assert.Equal(FeedTrust.PinnedDigest, source.TrustState);
        Assert.Equal(source.Sha256, source.PinnedDigest);
    }

    [Fact]
    public async Task APinThatDoesNotMatchStopsTheImportAndDoesNotFallBack()
    {
        var importer = Importer(Doc(Definition()));

        var preview = await importer.PreviewAsync(Feed, new string('0', 64), CancellationToken.None);

        Assert.True(preview.Refused);
        Assert.Contains("does not fall back", preview.Refusal);
        Assert.Throws<InvalidOperationException>(() => importer.Import(preview, Confirm()));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task TheConflictsAreListedAndTheDefaultIsToSkip()
    {
        var store = Store();
        await Importer(Doc(Definition("other-feed-def")), store: store).PreviewAsync(Feed, null, CancellationToken.None);
        var first = Importer(Doc(Definition("other-feed-def")), store: store);
        first.Import(await first.PreviewAsync(Feed, null, CancellationToken.None), new ImportConfirmation("Other", "other-vendor", true, new HashSet<string>()));

        var importer = Importer(Doc(Definition("shipped-one"), Definition("user-one"), Definition("other-feed-def"), Definition("fresh-one")),
            shipped: ["shipped-one"], user: ["user-one"], store: store);
        var preview = await importer.PreviewAsync(Feed, null, CancellationToken.None);

        Assert.Equal([ConflictKind.Shipped, ConflictKind.User, ConflictKind.AnotherFeed, ConflictKind.None], preview.Candidates.Select(c => c.Conflict));
        Assert.Equal("other-vendor", preview.Candidates[2].ConflictFeedId);
        var source = importer.Import(preview, Confirm());
        Assert.Equal(["fresh-one"], source.Ids);
        Assert.Empty(source.ReplacedShippedIds);
    }

    [Fact]
    public async Task AFeedIdThatHoldsAFeedAlreadyIsNeverReplacedByAnImport()
    {
        var store = Store();
        var first = Importer(Doc(Definition("a-one"), Definition("b-one")), store: store);
        first.Import(await first.PreviewAsync(Feed, null, CancellationToken.None), Confirm());

        var second = Importer(Doc(Definition("a-one"), Definition("b-one"), Definition("c-one")), store: store);
        var preview = await second.PreviewAsync(Feed, null, CancellationToken.None);

        var ex = Assert.Throws<InvalidOperationException>(() => second.Import(preview, Confirm()));

        Assert.Contains("holds a feed already", ex.Message);
        Assert.Equal(["a-one", "b-one"], Assert.Single(store.Feeds()).Source.Ids);
    }

    [Fact]
    public async Task ADocumentWithAnInvalidFeedIdImportsUnderTheFeedIdThatTheAdministratorChose()
    {
        var document = Encoding.UTF8.GetBytes($$"""{"FeedFormat":1,"Vendor":"V","FeedId":"Contoso","Definitions":[{{Definition()}}]}""");
        var importer = Importer(document);
        var preview = await importer.PreviewAsync(Feed, null, CancellationToken.None);

        var source = importer.Import(preview, Confirm());

        Assert.Equal("contoso-software", source.FeedId);
    }

    [Fact]
    public async Task AShippedIdIsReplacedOnlyWithItsOwnConfirmation()
    {
        var importer = Importer(Doc(Definition("shipped-one"), Definition("fresh-one")), shipped: ["shipped-one"]);
        var preview = await importer.PreviewAsync(Feed, null, CancellationToken.None);

        var source = importer.Import(preview, Confirm(true, "shipped-one"));

        Assert.Equal(["shipped-one", "fresh-one"], source.Ids);
        Assert.Equal(["shipped-one"], source.ReplacedShippedIds);
    }

    [Fact]
    public async Task AUserDefinitionIsNeverReplacedEvenWhenConfirmed()
    {
        var importer = Importer(Doc(Definition("user-one"), Definition("fresh-one")), user: ["user-one"]);
        var preview = await importer.PreviewAsync(Feed, null, CancellationToken.None);

        Assert.Equal(["fresh-one"], importer.Import(preview, Confirm(true, "user-one")).Ids);
    }

    [Fact]
    public async Task ABlockedDefinitionIsNotWrittenAndTheOthersAre()
    {
        var importer = Importer(Doc(Definition("good-one"), Definition("code-one", n => n["UninstallCommand"] = "x")));
        var preview = await importer.PreviewAsync(Feed, null, CancellationToken.None);

        var source = importer.Import(preview, Confirm());

        Assert.Equal(["good-one"], source.Ids);
        Assert.False(File.Exists(Path.Combine(_root, "contoso-software", "code-one.json")));
    }

    [Fact]
    public async Task TheSourceRecordHoldsNoQueryAndNoSecret()
    {
        var importer = Importer(Doc(Definition()));
        var preview = await importer.PreviewAsync(Feed, null, CancellationToken.None);

        importer.Import(preview, Confirm());

        var text = File.ReadAllText(Path.Combine(_root, "contoso-software", "source.json"));
        Assert.DoesNotContain("SECRETQUERY", text);
        Assert.Contains("https://vendor.example/feed.json", text);
        Assert.Contains("Definition document NOT verified", text);
    }

    // ---- storage ----

    private static ValidatedDefinition Valid(string id) => FeedValidator.Validate(Doc(Definition(id))).Definitions.Single();

    private static FeedSource Source(string feedId, DateTimeOffset at) =>
        new("https://v.example/f", "V", feedId, at, "AB", 1, FeedTrust.NotVerified, null, null, null, [], [], []);

    [Fact]
    public void AFeedIsSwappedInWithTheSourceRecordLastAndTheOldFilesGo()
    {
        var store = Store();
        store.Write(Source("vendor-one", DateTimeOffset.UtcNow), [Valid("old-one"), Valid("keep-one")]);
        store.Write(Source("vendor-one", DateTimeOffset.UtcNow), [Valid("keep-one")]);

        var feed = Assert.Single(store.Feeds());
        Assert.Equal(["keep-one"], feed.Source.Ids);
        Assert.False(File.Exists(Path.Combine(_root, "vendor-one", "old-one.json")));
        Assert.Empty(Directory.GetDirectories(_root).Where(d => Path.GetFileName(d).StartsWith('.') || Path.GetFileName(d).Contains(".old-")));
    }

    [Fact]
    public void AFailedWriteKeepsTheEarlierStateAndLeavesNoStagingFolder()
    {
        var store = Store();
        store.Write(Source("vendor-one", DateTimeOffset.UtcNow), [Valid("keep-one")]);
        var blocked = FeedValidator.Validate(Doc(Definition("bad-one", n => n["UninstallCommand"] = "x"))).Definitions.Single();

        Assert.Throws<InvalidOperationException>(() => store.Write(Source("vendor-one", DateTimeOffset.UtcNow), [Valid("new-one"), blocked]));

        Assert.Equal(["keep-one"], Assert.Single(store.Feeds()).Source.Ids);
        Assert.Empty(Directory.GetDirectories(_root).Where(d => Path.GetFileName(d).StartsWith('.')));
    }

    [Fact]
    public void StartRestoresTheOldCopyOfAFeedWhoseSwapStoppedBetweenTheRenames()
    {
        var store = Store();
        store.Write(Source("vendor-one", DateTimeOffset.UtcNow), [Valid("keep-one")]);
        Directory.Move(Path.Combine(_root, "vendor-one"), Path.Combine(_root, "vendor-one.old-abc"));
        Directory.CreateDirectory(Path.Combine(_root, ".staging-xyz"));

        store.Recover();

        Assert.Equal(["keep-one"], Assert.Single(store.Feeds()).Source.Ids);
        Assert.False(Directory.Exists(Path.Combine(_root, ".staging-xyz")));
    }

    [Fact]
    public void TheFeedsComeOutInTheOrderOfTheirImportAndNotByName()
    {
        var store = Store();
        store.Write(Source("zzz-vendor", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), [Valid("late-one")]);
        store.Write(Source("aaa-vendor", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)), [Valid("early-one")]);

        Assert.Equal(["zzz-vendor", "aaa-vendor"], store.Feeds().Select(f => f.Source.FeedId));
    }

    [Fact]
    public void ThePathsOfAFeedAreOnlyTheFilesThatTheRecordListsAndNeverTheRecord()
    {
        var store = Store();
        store.Write(Source("vendor-one", DateTimeOffset.UtcNow), [Valid("one-def")]);

        var feed = Assert.Single(store.Feeds());

        Assert.Equal(["one-def.json"], feed.DefinitionFiles.Select(Path.GetFileName));
        Assert.DoesNotContain(feed.DefinitionFiles, f => f.EndsWith("source.json"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("con")]
    public void AFeedIdThatIsNotAllowedNeverReachesTheDisk(string feedId)
    {
        Assert.Throws<ArgumentException>(() => Store().Write(Source(feedId, DateTimeOffset.UtcNow), [Valid("one-def")]));
    }

    [Fact]
    public void ARecordThatNamesAPathOutsideTheFolderOrHasNoIdsIsReadSafely()
    {
        var store = Store();
        store.Write(Source("vendor-one", DateTimeOffset.UtcNow), [Valid("one-def")]);
        var record = Path.Combine(_root, "vendor-one", "source.json");
        var json = File.ReadAllText(record).Replace("\"one-def\"", "\"..\\\\..\\\\x\", \"one-def\"");
        File.WriteAllText(record, json);

        var feed = Assert.Single(store.Feeds());

        Assert.Equal(["one-def"], feed.Source.Ids);
        File.WriteAllText(record, "{\"FeedId\":\"vendor-one\",\"Ids\":null}");
        Assert.Empty(Assert.Single(store.Feeds()).DefinitionFiles);
    }

    [Fact]
    public void RemoveFeedDeletesTheFolderOnly()
    {
        var store = Store();
        store.Write(Source("vendor-one", DateTimeOffset.UtcNow), [Valid("one-def")]);
        store.Write(Source("vendor-two", DateTimeOffset.UtcNow), [Valid("two-def")]);

        store.RemoveFeed("vendor-one");

        Assert.Equal(["vendor-two"], store.Feeds().Select(f => f.Source.FeedId));
    }
}
