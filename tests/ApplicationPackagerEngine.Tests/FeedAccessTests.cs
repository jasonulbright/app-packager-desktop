using System.Net;
using System.Text;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Authenticated feeds, methods 1 and 2 (step 5): the credential goes to the bound host only, the secret lives in the store and never in a file, and the redactor keeps it out of every message.</summary>
public sealed class FeedAccessTests : IDisposable
{
    private const string Canary = "CANARY-7f3a9c-SECRET-VALUE";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-access-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Read(string id) => Values.GetValueOrDefault(id);
        public void Write(string id, string secret) => Values[id] = secret;
        public void Delete(string id) => Values.Remove(id);
    }

    private sealed record Seen(Uri Address, IReadOnlyDictionary<string, string> Headers);

    private sealed class RecordingHandler(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string to)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to);
        return response;
    }

    private static FeedFetcher Fetcher(RecordingHandler handler) => new(() => handler, null, (_, _) => Task.CompletedTask);

    private FeedCredentialStore Store(MemorySecrets? secrets = null) => new(Path.Combine(_root, "FeedCredentials.json"), secrets ?? new MemorySecrets());

    private static readonly Uri Feed = new("https://vendor.example/feed.json");

    [Fact]
    public async Task AKeyInAHeaderGoesToTheBoundHostAsABearerOrAsItIsInACustomHeader()
    {
        var store = Store();
        store.Add("bearer-feed", Feed, FeedAuthMethod.HeaderKey, null, null, Canary);
        store.Add("custom-feed", new Uri("https://other.example/feed.json"), FeedAuthMethod.HeaderKey, "X-License-Key", null, "LICENSE-123456");
        var handler = new RecordingHandler(_ => Ok());

        await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));
        await Fetcher(handler).FetchAsync(new Uri("https://other.example/feed.json"), CancellationToken.None, store.For(new Uri("https://other.example/feed.json")));

        Assert.Equal("Bearer " + Canary, handler.Requests[0].Headers["Authorization"]);
        Assert.Equal("LICENSE-123456", handler.Requests[1].Headers["X-License-Key"]);
        Assert.False(handler.Requests[1].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task BasicSendsTheEncodedUserAndPasswordOverHttpsToTheBoundHost()
    {
        var store = Store();
        store.Add("basic-feed", Feed, FeedAuthMethod.Basic, null, "alice", Canary);
        var handler = new RecordingHandler(_ => Ok());

        await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));

        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:" + Canary)), handler.Requests.Single().Headers["Authorization"]);
        Assert.Throws<FeedFetchException>(() => store.Add("http-feed", new Uri("http://vendor.example/feed.json"), FeedAuthMethod.Basic, null, "alice", "pw-123456"));
    }

    [Fact]
    public async Task ARedirectInsideTheHostKeepsTheCredentialAndARedirectToAnotherHostDropsIt()
    {
        var store = Store();
        store.Add("feed-a", Feed, FeedAuthMethod.HeaderKey, null, null, Canary);
        var handler = new RecordingHandler(uri => uri.AbsolutePath switch
        {
            "/feed.json" => Redirect("https://vendor.example/inside.json"),
            "/inside.json" => Redirect("https://cdn.example/outside.json"),
            _ => Ok()
        });

        await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));

        Assert.Equal(3, handler.Requests.Count);
        Assert.True(handler.Requests[0].Headers.ContainsKey("Authorization"));
        Assert.True(handler.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.False(handler.Requests[2].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task ARedirectToAHostThatAnswers401GivesTheNoEntryMessageAndTheCredentialIsNeverReused()
    {
        var store = Store();
        store.Add("feed-a", Feed, FeedAuthMethod.HeaderKey, null, null, Canary);
        var handler = new RecordingHandler(uri => uri.Host == "vendor.example" ? Redirect("https://cdn.example/x.json") : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed)));

        Assert.Contains("no credential entry", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Address.Host == "cdn.example" && r.Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusedKeyGivesItsMessageAndNoRetryLoop(HttpStatusCode status)
    {
        var store = Store();
        store.Add("feed-a", Feed, FeedAuthMethod.HeaderKey, null, null, Canary);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed)));

        Assert.Contains("refused the credential", ex.Message);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(Canary, ex.Message);
    }

    [Fact]
    public void TheSecretGoesToTheStoreAndNeverToTheSettingsFileAndRotateAndRemoveActOnTheStore()
    {
        var secrets = new MemorySecrets();
        var store = Store(secrets);

        store.Add("feed-a", Feed, FeedAuthMethod.Basic, null, "alice", Canary);
        var file = File.ReadAllText(Path.Combine(_root, "FeedCredentials.json"));
        Assert.DoesNotContain(Canary, file);
        Assert.Contains("vendor.example", file);
        Assert.Equal(Canary, secrets.Values["feed-a"]);

        store.Rotate("feed-a", "ROTATED-VALUE-123");
        Assert.Equal("ROTATED-VALUE-123", secrets.Values["feed-a"]);
        Assert.Equal("ROTATED-VALUE-123", store.For(Feed)!.Secret);

        store.Remove("feed-a");
        Assert.Empty(secrets.Values);
        Assert.Empty(store.Entries());
        Assert.Null(store.For(Feed));
    }

    [Fact]
    public void ACredentialIsBoundToTheExactHostAndPort()
    {
        var store = Store();
        store.Add("feed-a", Feed, FeedAuthMethod.HeaderKey, null, null, Canary);

        Assert.NotNull(store.For(new Uri("https://vendor.example/other.json")));
        Assert.Null(store.For(new Uri("https://vendor.example:8443/feed.json")));
        Assert.Null(store.For(new Uri("https://sub.vendor.example/feed.json")));
        Assert.Null(store.For(new Uri("https://evil.example/feed.json")));
    }

    [Fact]
    public void ASecretOverTheLimitOfTheCredentialManagerIsRefusedBeforeTheStoreIsTouched()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new WindowsCredentialStore().Write("feed-limit", new string('x', 3000)));

        Assert.Contains("2560", ex.Message);
    }

    [Fact]
    public void TheRedactorRemovesRegisteredSecretsSecretHeadersSecretQueryValuesAndUserInformation()
    {
        SecretRedactor.Register(Canary);
        var text = $"Failed: Authorization: Bearer abc.def X-License-Key: {Canary} GET https://vendor.example/feed.json?token=QQQQ1111&v=1 and https://user:pw@vendor.example/x and plain {Canary}";

        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain(Canary, redacted);
        Assert.DoesNotContain("abc.def", redacted);
        Assert.DoesNotContain("QQQQ1111", redacted);
        Assert.DoesNotContain("pw@", redacted);
        Assert.Contains("v=1", redacted);
    }

    [Fact]
    public void AnExceptionMessageThatCarriesASecretIsRedactedWhenItIsCreated()
    {
        SecretRedactor.Register(Canary);

        var ex = new FeedFetchException($"The runtime said: {Canary} was refused at https://vendor.example/feed.json?apikey=ZZZZ9999");

        Assert.DoesNotContain(Canary, ex.Message);
        Assert.DoesNotContain("ZZZZ9999", ex.Message);
    }


    [Fact]
    public void ASecondEntryForTheSameHostAShortSecretAndABasicUserWithAColonAreRefused()
    {
        var store = Store();
        store.Add("feed-a", Feed, FeedAuthMethod.HeaderKey, null, null, Canary);

        Assert.Throws<InvalidOperationException>(() => store.Add("feed-b", Feed, FeedAuthMethod.HeaderKey, null, null, "another-secret-1"));
        Assert.Throws<InvalidOperationException>(() => store.Add("feed-c", new Uri("https://third.example/"), FeedAuthMethod.HeaderKey, null, null, "short"));
        Assert.Throws<InvalidOperationException>(() => store.Add("feed-d", new Uri("https://fourth.example/"), FeedAuthMethod.Basic, null, "corp:svc", "long-enough-secret"));
        Assert.Single(store.Entries());
    }

    [Fact]
    public void AnUnreadableSettingsFileStopsAChangeAndGivesNoCredential()
    {
        var store = Store();
        store.Add("feed-a", Feed, FeedAuthMethod.HeaderKey, null, null, Canary);
        File.WriteAllText(Path.Combine(_root, "FeedCredentials.json"), "{ not json");

        Assert.Null(store.For(Feed));
        Assert.Throws<InvalidOperationException>(() => store.Add("feed-b", new Uri("https://other.example/"), FeedAuthMethod.HeaderKey, null, null, "another-secret-1"));
        Assert.Throws<InvalidOperationException>(() => store.Remove("feed-a"));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(_root, "FeedCredentials.json")));
    }

    [Fact]
    public async Task ARedirectBackToTheBoundHostCarriesTheCredentialAgain()
    {
        var store = Store();
        store.Add("feed-a", Feed, FeedAuthMethod.HeaderKey, null, null, Canary);
        var handler = new RecordingHandler(uri => uri.AbsolutePath switch
        {
            "/feed.json" => Redirect("https://cdn.example/y.json"),
            "/y.json" => Redirect("https://vendor.example/z.json"),
            _ => Ok()
        });

        await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));

        Assert.True(handler.Requests[0].Headers.ContainsKey("Authorization"));
        Assert.False(handler.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.True(handler.Requests[2].Headers.ContainsKey("Authorization"));
    }
    [Fact]
    public async Task APreviewUsesTheCredentialOfTheBoundHostAndAnAnonymousFeedOfAnotherHostGetsNone()
    {
        var store = Store();
        store.Add("feed-a", Feed, FeedAuthMethod.HeaderKey, null, null, Canary);
        var body = "{\"FeedFormat\":1,\"Vendor\":\"V\",\"FeedId\":\"vendor-feed\",\"Updated\":\"2026-11-12T08:00:00Z\",\"Definitions\":[]}";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var importer = new VendorFeedImporter(Fetcher(handler), new ImportedDefinitionStore(Path.Combine(_root, "imported")),
            () => new HashSet<string>(), () => new HashSet<string>(), credentialFor: store.For);

        await importer.PreviewAsync(Feed, null, CancellationToken.None);
        await importer.PreviewAsync(new Uri("https://anonymous.example/feed.json"), null, CancellationToken.None);

        Assert.True(handler.Requests[0].Headers.ContainsKey("Authorization"));
        Assert.False(handler.Requests[1].Headers.ContainsKey("Authorization"));
    }
}
