using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>
/// The OAuth 2 token of method 5 (client credentials). The client secret goes to the token host only; the token is kept in memory until it expires and
/// is sent as a bearer token to the bound host. A token that the feed refuses is renewed once, and then the fetch fails.
/// </summary>
public static class OAuthTokens
{
    private sealed record Cached(string Token, DateTimeOffset Expires);

    private static readonly Dictionary<string, Cached> Tokens = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>The time source of the expiry; a test replaces it.</summary>
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public static void Forget(string entryId)
    {
        lock (Gate) Tokens.Remove(entryId);
    }

    public static async Task<string> GetAsync(FeedCredential credential, HttpClient client, string userAgent, bool renew, CancellationToken ct)
    {
        var entry = credential.Entry;
        if (!renew)
            lock (Gate)
                if (Tokens.TryGetValue(entry.Id, out var cached) && cached.Expires > Now()) return cached.Token;

        var tokenAddress = new Uri(entry.TokenAddress!);
        FeedFetcher.CheckAddress(tokenAddress, "The token address");
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"), new("client_id", entry.ClientId ?? ""), new("client_secret", credential.Secret)
        };
        if (!string.IsNullOrWhiteSpace(entry.Scope)) form.Add(new("scope", entry.Scope));
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenAddress) { Content = new FormUrlEncodedContent(form) };
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new FeedFetchException($"The token host {tokenAddress.Host} could not be reached: {ex.HttpRequestError}.", ex); }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                throw new FeedFetchException($"The token host {tokenAddress.Host} redirected the request; the client secret is never sent to another host.");
            if (status is 400 or 401 or 403)
                throw new FeedFetchException($"The token host {tokenAddress.Host} refused the client id and secret ({status}). The secret may be wrong, expired or revoked; rotate it.");
            if (status is < 200 or >= 300)
                throw new FeedFetchException($"The token host {tokenAddress.Host} answered {status}.");
            string text;
            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length > 64 * 1024) throw new FeedFetchException("The answer of the token host is larger than 64 KB.");
                text = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (HttpRequestException ex) { throw new FeedFetchException("The connection to the token host broke.", ex); }
            string? token;
            var seconds = 300;
            try
            {
                using var json = JsonDocument.Parse(text);
                token = json.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) && s > 0) seconds = s;
            }
            catch (JsonException) { throw new FeedFetchException($"The token host {tokenAddress.Host} did not answer with JSON."); }
            if (string.IsNullOrWhiteSpace(token)) throw new FeedFetchException($"The token host {tokenAddress.Host} sent no access token.");
            SecretRedactor.Register(token);
            lock (Gate) Tokens[entry.Id] = new Cached(token, Now().AddSeconds(Math.Max(0, seconds - 60)));
            return token;
        }
    }
}

/// <summary>
/// The handlers of the methods that have a transport of their own (the fetcher uses its own handler only when none is given). A fake handler cannot see a
/// TLS client certificate or a Negotiate challenge, so these two rules are proved in the lab.
/// </summary>
public static class FeedAuthTransports
{
    /// <summary>The certificate of the store CurrentUser\My with this thumbprint and a private key; the key stays in the store and is never read or exported.</summary>
    public static X509Certificate2 FindCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        var all = found.Cast<X509Certificate2>().ToList();
        var certificate = all.FirstOrDefault(c => c.HasPrivateKey);
        foreach (var other in all.Where(c => !ReferenceEquals(c, certificate))) other.Dispose();
        return certificate ?? throw new FeedFetchException("No certificate with this thumbprint and a private key is in the store CurrentUser\\My.");
    }

    public static SocketsHttpHandler Handler(FeedCredential? credential)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions { CertificateRevocationCheckMode = X509RevocationMode.Online }
        };
        if (credential?.Entry.Method == FeedAuthMethod.Integrated)
        {
            // The account of the administrator, through Negotiate, for the exact prefix of the bound host only. The redirect loop of the program
            // never follows a redirect inside the handler, so no other host sees it.
            var cache = new CredentialCache { { new Uri($"https://{credential.Entry.Host}:{credential.Entry.Port}/"), "Negotiate", CredentialCache.DefaultNetworkCredentials } };
            handler.Credentials = cache;
        }
        else if (credential?.Entry.Method == FeedAuthMethod.ClientCertificate)
        {
            var certificate = FindCertificate(credential.Entry.Thumbprint!);
            var host = credential.Entry.Host;
            handler.SslOptions.ClientCertificates = [certificate];
            // Offered to the bound host only; any other host gets none.
            handler.SslOptions.LocalCertificateSelectionCallback = (_, targetHost, _, _, _) =>
                targetHost.Equals(host, StringComparison.OrdinalIgnoreCase) ? certificate : null!;
        }
        return handler;
    }
}