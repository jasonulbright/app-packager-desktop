using System.Net;
using System.Text;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Authenticated feeds, methods 3 to 6 (step 6): the rules that a fake handler can prove. The certificate and Negotiate transports are proved in the lab.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class FeedAuthMethodsTests : IDisposable
{
    private const string ClientSecret = "CLIENT-SECRET-7f3a9c-VALUE";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-methods-" + Guid.NewGuid().ToString("N"));
    private int _tokenCounter;

    public void Dispose()
    {
        OAuthTokens.Now = () => DateTimeOffset.UtcNow;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Read(string id) => Values.GetValueOrDefault(id);
        public void Write(string id, string secret) => Values[id] = secret;
        public void Delete(string id) => Values.Remove(id);
    }

    private sealed record Seen(HttpMethod Method, Uri Address, IReadOnlyDictionary<string, string> Headers, string Body);

    private sealed class RecordingHandler(Func<Seen, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var seen = new Seen(request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase), body);
            Requests.Add(seen);
            return answer(seen);
        }
    }

    private static HttpResponseMessage Ok(string body = "{}") => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static FeedFetcher Fetcher(RecordingHandler handler) => new(() => handler, null, (_, _) => Task.CompletedTask);

    private FeedCredentialStore Store() => new(Path.Combine(_root, "FeedCredentials.json"), new MemorySecrets());

    private static readonly Uri Feed = new("https://vendor.example/feed.json");
    private const string TokenAddress = "https://login.example/oauth/token";

    private HttpResponseMessage TokenOrFeed(Seen s, Func<Seen, HttpResponseMessage>? feed = null)
    {
        if (s.Address.Host == "login.example") return Ok("{\"access_token\":\"TOKEN-" + (++_tokenCounter) + "-ABCDEFGH\",\"expires_in\":3600}");
        return feed?.Invoke(s) ?? Ok();
    }

    [Fact]
    public async Task TheTokenIsFetchedFromTheTokenHostWithTheClientSecretAndSentAsABearerToTheBoundHostOnly()
    {
        OAuthTokens.Forget("oauth-feed");
        var store = Store();
        store.Add("oauth-feed", Feed, FeedAuthMethod.OAuth, null, null, ClientSecret, TokenAddress, "client-1", "feeds.read");
        var handler = new RecordingHandler(s => TokenOrFeed(s));

        await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));

        var token = handler.Requests.Single(r => r.Address.Host == "login.example");
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Contains("grant_type=client_credentials", token.Body);
        Assert.Contains("client_secret=" + ClientSecret, token.Body);
        Assert.Contains("scope=feeds.read", token.Body);
        var feed = handler.Requests.Single(r => r.Address.Host == "vendor.example");
        Assert.Equal("Bearer TOKEN-1-ABCDEFGH", feed.Headers["Authorization"]);
        Assert.DoesNotContain(ClientSecret, feed.Body);
        Assert.False(feed.Headers.Values.Any(v => v.Contains(ClientSecret)));
    }

    [Fact]
    public async Task TheTokenIsKeptUntilItExpiresAndThenFetchedAgain()
    {
        OAuthTokens.Forget("oauth-feed");
        var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        OAuthTokens.Now = () => now;
        var store = Store();
        store.Add("oauth-feed", Feed, FeedAuthMethod.OAuth, null, null, ClientSecret, TokenAddress, "client-1", null);
        var handler = new RecordingHandler(s => TokenOrFeed(s));

        await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));
        await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));
        Assert.Single(handler.Requests, r => r.Address.Host == "login.example");

        now = now.AddHours(2);
        await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));
        Assert.Equal(2, handler.Requests.Count(r => r.Address.Host == "login.example"));
    }

    [Fact]
    public async Task ATokenThatTheFeedRefusesIsRenewedOnceAndASecondRefusalEndsTheFetch()
    {
        OAuthTokens.Forget("oauth-feed");
        var store = Store();
        store.Add("oauth-feed", Feed, FeedAuthMethod.OAuth, null, null, ClientSecret, TokenAddress, "client-1", null);
        var feedCalls = 0;
        var renewed = new RecordingHandler(s => TokenOrFeed(s, _ => ++feedCalls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Ok()));

        await Fetcher(renewed).FetchAsync(Feed, CancellationToken.None, store.For(Feed));

        Assert.Equal(2, renewed.Requests.Count(r => r.Address.Host == "login.example"));
        Assert.Equal("Bearer TOKEN-2-ABCDEFGH", renewed.Requests.Last().Headers["Authorization"]);

        OAuthTokens.Forget("oauth-feed");
        var refused = new RecordingHandler(s => TokenOrFeed(s, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var ex = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(refused).FetchAsync(Feed, CancellationToken.None, store.For(Feed)));
        Assert.Contains("refused the credential", ex.Message);
        Assert.Equal(2, refused.Requests.Count(r => r.Address.Host == "login.example"));
    }

    [Fact]
    public async Task ARevokedClientSecretGivesItsMessageAndATokenHostRedirectIsRefusedWithoutTheSecretGoingElsewhere()
    {
        OAuthTokens.Forget("oauth-feed");
        var store = Store();
        store.Add("oauth-feed", Feed, FeedAuthMethod.OAuth, null, null, ClientSecret, TokenAddress, "client-1", null);

        var revoked = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(revoked).FetchAsync(Feed, CancellationToken.None, store.For(Feed)));
        Assert.Contains("refused the client id and secret", ex.Message);
        Assert.DoesNotContain(ClientSecret, ex.Message);
        Assert.Single(revoked.Requests);

        var redirect = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return response;
        });
        await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(redirect).FetchAsync(Feed, CancellationToken.None, store.For(Feed)));
        Assert.DoesNotContain(redirect.Requests, r => r.Address.Host == "evil.example");
    }

    [Fact]
    public async Task ASignedAddressIsFetchedAsItIsAndItsQueryIsNeverInARecordOrAMessage()
    {
        var store = Store();
        var signed = "https://vendor.example/feed.json?Signature=SIGNATURE-VALUE-123&Expires=1893456000";
        store.Add("signed-feed", new Uri(signed), FeedAuthMethod.SignedAddress, null, null, signed);
        var handler = new RecordingHandler(_ => Ok());

        var document = await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));

        Assert.Equal(signed, handler.Requests.Single().Address.ToString());
        Assert.False(handler.Requests.Single().Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain("SIGNATURE-VALUE-123", document.SafeAddress);
        Assert.DoesNotContain("SIGNATURE-VALUE-123", File.ReadAllText(Path.Combine(_root, "FeedCredentials.json")));
        var failing = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(failing).FetchAsync(Feed, CancellationToken.None, store.For(Feed)));
        Assert.DoesNotContain("SIGNATURE-VALUE-123", ex.Message);
        Assert.Throws<InvalidOperationException>(() => store.Add("other-feed", new Uri("https://other.example/feed.json"), FeedAuthMethod.SignedAddress, null, null, signed));
    }

    [Fact]
    public async Task AnAnonymousFetchStillRefusesAnAddressWithASecretInItsQuery()
    {
        var handler = new RecordingHandler(_ => Ok());

        await Assert.ThrowsAsync<FeedFetchException>(() => Fetcher(handler).FetchAsync(new Uri("https://vendor.example/feed.json?sig=ABC"), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IntegratedAuthenticationAndACertificateHaveNoSecretAndSendNoHeader()
    {
        var store = Store();
        store.Add("windows-feed", Feed, FeedAuthMethod.Integrated, null, null, "");
        var secure = new Uri("https://secure.example/feed.json");
        store.Add("cert-feed", secure, FeedAuthMethod.ClientCertificate, null, null, "", thumbprint: new string('A', 40));
        var handler = new RecordingHandler(_ => Ok());

        await Fetcher(handler).FetchAsync(Feed, CancellationToken.None, store.For(Feed));
        await Fetcher(handler).FetchAsync(secure, CancellationToken.None, store.For(secure));

        Assert.All(handler.Requests, r => Assert.False(r.Headers.ContainsKey("Authorization")));
        Assert.Throws<InvalidOperationException>(() => store.Rotate("windows-feed", "some-secret-value"));
        Assert.Throws<InvalidOperationException>(() => store.Add("bad-cert", new Uri("https://third.example/"), FeedAuthMethod.ClientCertificate, null, null, "", thumbprint: "nothex"));
        Assert.Equal(new string('A', 40), store.Entries().Single(e => e.Id == "cert-feed").Thumbprint);
    }

    [Fact]
    public void TheTokenAddressMustBeHttpsWithoutUserInformationOrQuery()
    {
        var store = Store();

        Assert.Throws<InvalidOperationException>(() => store.Add("oauth-a", Feed, FeedAuthMethod.OAuth, null, null, ClientSecret, "http://login.example/token", "c", null));
        Assert.Throws<InvalidOperationException>(() => store.Add("oauth-b", Feed, FeedAuthMethod.OAuth, null, null, ClientSecret, "https://u:p@login.example/token", "c", null));
        Assert.Throws<InvalidOperationException>(() => store.Add("oauth-c", Feed, FeedAuthMethod.OAuth, null, null, ClientSecret, "https://login.example/token?x=1", "c", null));
        Assert.Throws<InvalidOperationException>(() => store.Add("oauth-d", Feed, FeedAuthMethod.OAuth, null, null, ClientSecret, TokenAddress, "", null));
    }
}
