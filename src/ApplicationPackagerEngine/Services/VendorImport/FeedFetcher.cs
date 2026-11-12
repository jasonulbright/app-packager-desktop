using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>The limits of the fetch of a feed (see "Limits of the fetch" in the specification).</summary>
public sealed class FeedFetchLimits
{
    public int MaxRedirects { get; init; } = 5;
    public int MaxBytes { get; init; } = 2 * 1024 * 1024;
    public TimeSpan TotalTime { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxRetries { get; init; } = 3;
    /// <summary>The longest wait that the answer of a server can ask for between two tries.</summary>
    public TimeSpan MaxRetryWait { get; init; } = TimeSpan.FromSeconds(20);
    public string UserAgent { get; init; } = "AppPackagerDesktop/" + (typeof(FeedFetcher).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
}

/// <summary>The document that a fetch returned, with what the preview and the source record show.</summary>
public sealed record FeedDocument(byte[] Bytes, Uri FinalAddress, string ContentType, string Sha256, int Size)
{
    /// <summary>The final address without its query: the form that a record or a log line may hold.</summary>
    public string SafeAddress => FeedFetcher.WithoutQuery(FinalAddress);
}

/// <summary>A fetch that the rules of the specification refused or that failed. The message names the rule and never a query.</summary>
public sealed class FeedFetchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Fetches a feed document over HTTPS with the manual redirect loop, the size and time limits, the content type rule and the retry for
/// 429 and 503. The handler that sends the requests is a parameter, so a test can answer without a network; the default has no
/// automatic redirect, no cookies and a connect time of 15 seconds, and checks the certificate chain and the host name of Windows
/// (there is no option to ignore an error).
/// </summary>
public sealed class FeedFetcher
{
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly FeedFetchLimits _limits;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public FeedFetcher(Func<HttpMessageHandler>? handlerFactory = null, FeedFetchLimits? limits = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _handlerFactory = handlerFactory ?? DefaultHandler;
        _limits = limits ?? new FeedFetchLimits();
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    private static HttpMessageHandler DefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        // The chain, the host name and the revocation of the certificate are checked by Windows; there is no option to ignore an error.
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online
        }
    };

    /// <summary>The address without its query and its fragment. A host, a port and a path only.</summary>
    public static string WithoutQuery(Uri address) =>
        new UriBuilder(address) { Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path);

    private static string Host(Uri address) => address.IsDefaultPort ? address.Host : $"{address.Host}:{address.Port}";

    /// <summary>The query names that carry a secret. An anonymous import refuses an address that has one; authenticated feeds come later.</summary>
    private static readonly string[] SecretQueryNames =
        ["token", "access_token", "id_token", "auth", "authorization", "key", "apikey", "api_key", "api-key", "password", "passwd", "pwd",
         "secret", "client_secret", "sig", "signature", "sas", "sv", "se", "sp", "x-amz-signature", "x-amz-credential", "expires"];

    /// <summary>The first query name of the address that looks like a secret, or null.</summary>
    public static string? SecretQueryName(Uri address)
    {
        foreach (var pair in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Uri.UnescapeDataString(pair.Split('=', 2)[0]).Trim();
            if (SecretQueryNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    /// <summary>Refuses an address that is not HTTPS, has user information, or has no host.</summary>
    public static void CheckAddress(Uri address, string what)
    {
        if (!address.IsAbsoluteUri)
            throw new FeedFetchException($"{what} must be an absolute HTTPS address.");
        if (!address.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new FeedFetchException($"{what} must be an HTTPS address; '{address.Scheme}' is refused.");
        if (!string.IsNullOrEmpty(address.UserInfo))
            throw new FeedFetchException($"{what} holds user information (user:password@host), which is refused.");
        if (string.IsNullOrEmpty(address.Host))
            throw new FeedFetchException($"{what} has no host.");
    }

    public async Task<FeedDocument> FetchAsync(Uri address, CancellationToken ct)
    {
        CheckAddress(address, "The address of the feed");
        if (SecretQueryName(address) is { } secretName)
            throw new FeedFetchException($"The address of the feed has a query value that looks like a secret ({secretName}). An anonymous import does not send a secret in an address; use a feed with its own access method, or ask the vendor for an address without it.");
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(_limits.TotalTime);
        try
        {
            return await FetchCoreAsync(address, total.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FeedFetchException($"The fetch from {Host(address)} did not finish within {(int)_limits.TotalTime.TotalSeconds} seconds, which is the limit (the waits of a retry count).");
        }
    }

    private async Task<FeedDocument> FetchCoreAsync(Uri start, CancellationToken ct)
    {
        using var client = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var current = start;
        var redirects = 0;
        var retries = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(_limits.UserAgent);
            request.Headers.Accept.ParseAdd("application/json, text/plain");
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // The message of the runtime can carry the address; the host is named and the query never is.
                var certificate = ex.InnerException is System.Security.Authentication.AuthenticationException;
                throw new FeedFetchException(certificate
                    ? $"The certificate of {Host(current)} was refused: {ex.InnerException!.Message}"
                    : $"{Host(current)} could not be reached: {ex.HttpRequestError}.", ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (++redirects > _limits.MaxRedirects)
                        throw new FeedFetchException($"More than {_limits.MaxRedirects} redirects from {Host(start)}; the fetch is refused.");
                    var location = response.Headers.Location
                        ?? throw new FeedFetchException($"{Host(current)} answered {status} with no address to go to.");
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!next.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        throw new FeedFetchException($"{Host(current)} redirects to a '{next.Scheme}' address, which is refused (HTTPS only).");
                    CheckAddress(next, "The target of a redirect");
                    current = next;
                    continue;
                }
                if (status is 429 or 503)
                {
                    if (++retries > _limits.MaxRetries)
                        throw new FeedFetchException($"{Host(current)} answered {status} {response.ReasonPhrase} {_limits.MaxRetries + 1} times; the fetch stopped.");
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, retries - 1));
                    if (wait > _limits.MaxRetryWait) wait = _limits.MaxRetryWait;
                    await _delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
                if (status is < 200 or >= 300)
                    throw new FeedFetchException($"{Host(current)} answered {status} {response.ReasonPhrase}.");

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                    && !contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
                    throw new FeedFetchException($"{Host(current)} sent the content type '{(contentType.Length == 0 ? "none" : contentType)}'; a feed is application/json or text/plain.");
                if (response.Content.Headers.ContentLength is { } declared && declared > _limits.MaxBytes)
                    throw new FeedFetchException($"The document is {declared} bytes; the limit is {_limits.MaxBytes} bytes.");

                byte[] bytes;
                try { bytes = await ReadLimitedAsync(response, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                { throw new FeedFetchException($"The connection to {Host(current)} broke while the document was read.", ex); }
                // UTF-8 text: a document that does not decode strictly is refused here, not later at the parse.
                try { _ = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { throw new FeedFetchException($"The document of {Host(current)} is not UTF-8 text."); }
                var digest = Convert.ToHexString(SHA256.HashData(bytes));
                return new FeedDocument(bytes, current, contentType, digest, bytes.Length);
            }
        }
    }

    private async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > _limits.MaxBytes)
                throw new FeedFetchException($"The document is larger than {_limits.MaxBytes} bytes, which is the limit; the fetch stopped.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
