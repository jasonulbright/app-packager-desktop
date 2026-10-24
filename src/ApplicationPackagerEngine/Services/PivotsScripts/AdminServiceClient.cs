using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>One answer of the administration service: the HTTP status and the body text.</summary>
public sealed record AdminServiceResponse(int Status, string Body, string ContentType = "");

/// <summary>Requests to the ConfigMgr administration service of the SMS Provider.</summary>
public interface IAdminServiceClient
{
    /// <summary>
    /// Sends one request to https://&lt;provider&gt;/AdminService/&lt;relative&gt; with the Windows identity.
    /// Every HTTP status is returned; a request that gets no HTTP answer throws
    /// <see cref="PivotsServiceException"/> with a plain message, and
    /// <see cref="AdminServiceUnreachableException"/> when the request may not have reached the site.
    /// </summary>
    Task<AdminServiceResponse> SendAsync(HttpMethod method, string relative, string? jsonBody, CancellationToken ct);

    /// <summary>The host name the client sends to; empty when the connection is not set.</summary>
    string Provider { get; }
}

/// <summary>A request that got no HTTP answer: the message is plain and safe to show.</summary>
public sealed class AdminServiceUnreachableException(string message, bool mayHaveReachedSite) : Exception(message)
{
    /// <summary>True when the request was on the wire when it failed: the site can have acted on it.</summary>
    public bool MayHaveReachedSite { get; } = mayHaveReachedSite;
}

/// <summary>
/// The administration service client: one <see cref="HttpClient"/> with the Windows identity of the
/// process (default credentials), no proxy, and a bounded time per request. The server certificate
/// is checked by the operating system's chain and name rules; an invalid certificate is never
/// accepted, and the plain message names what to fix. The only host it sends to is the provider of
/// the connection.
/// </summary>
public sealed class AdminServiceClient : IAdminServiceClient, IDisposable
{
    public const string LogArea = "PivotsScripts";

    /// <summary>The time one request may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The time the TCP connection may take; a connect that fails sent nothing.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The most bytes of one answer that are read.</summary>
    public const int MaxResponseBytes = 64 * 1024 * 1024;

    private readonly Func<string?> _provider;
    private readonly IAppLog? _log;
    private readonly HttpClient _http;
    private SslPolicyErrors _lastCertificateErrors;

    public AdminServiceClient(Func<string?> provider, IAppLog? log = null, HttpMessageHandler? handler = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log;
        _http = new HttpClient(handler ?? CreateHandler(this), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static SocketsHttpHandler CreateHandler(AdminServiceClient owner) => new()
    {
        Credentials = CredentialCache.DefaultCredentials,
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = AdminServiceClient.ConnectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions
        {
            // Accepts exactly what the default validation accepts; the errors are kept for the message.
            RemoteCertificateValidationCallback = (_, _, _, errors) =>
            {
                owner._lastCertificateErrors = errors;
                return errors == SslPolicyErrors.None;
            }
        }
    };

    public string Provider => NormalizeProvider(_provider());

    /// <summary>The provider host as typed, or the FQDN of this computer when the connection names none.</summary>
    internal static string NormalizeProvider(string? provider)
    {
        var text = (provider ?? "").Trim();
        if (text.Length > 0) return text;
        try
        {
            return Dns.GetHostEntry("").HostName;
        }
        catch (SocketException)
        {
            return Environment.MachineName;
        }
    }

    /// <summary>True for a host name or IP address with no scheme, path, port, credentials or spaces.</summary>
    internal static bool IsHostName(string host) =>
        host.Length is > 0 and <= 253 && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    public async Task<AdminServiceResponse> SendAsync(HttpMethod method, string relative, string? jsonBody, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(method);
        var host = Provider;
        if (!IsHostName(host))
            throw new PivotsServiceException("The SMS Provider name in Settings is not a host name. Enter the provider name without https:// or a path, normally the FQDN that its certificate carries.");
        var uri = new Uri($"https://{host}/AdminService/{relative.TrimStart('/')}", UriKind.Absolute);
        if (!string.Equals(uri.Host, host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            throw new PivotsServiceException("The administration service address could not be built from the SMS Provider name.");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var sent = false;
        try
        {
            _lastCertificateErrors = SslPolicyErrors.None;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            sent = true;
            var length = response.Content.Headers.ContentLength;
            if (length > MaxResponseBytes) throw new PivotsServiceException("The administration service returned more data than the page reads. Narrow the query.");
            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return new AdminServiceResponse((int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString() ?? "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _log?.Warning(LogArea, "An administration service request timed out.");
            throw new AdminServiceUnreachableException($"The administration service did not answer within {RequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.", mayHaveReachedSite: true);
        }
        catch (HttpRequestException ex)
        {
            _log?.Warning(LogArea, $"An administration service request failed: {Kind(ex)}.");
            throw new AdminServiceUnreachableException(Message(ex, host, _lastCertificateErrors), mayHaveReachedSite: sent || MayHaveReached(ex));
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new PivotsServiceException("The administration service returned more data than the page reads. Narrow the query.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Kind(HttpRequestException ex) => ex.InnerException is { } inner ? inner.GetType().Name : ex.HttpRequestError.ToString();

    /// <summary>A request that failed before or during the TLS handshake did not reach the site.</summary>
    private static bool MayHaveReached(HttpRequestException ex) =>
        ex.HttpRequestError is not (HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError);

    internal static string Message(HttpRequestException ex, string host, SslPolicyErrors errors)
    {
        if (ex.InnerException is AuthenticationException || ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                return $"The SMS Provider certificate does not carry the name {host}. In Settings, enter the provider name that the certificate carries, normally its FQDN.";
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                return "This computer does not trust the SMS Provider certificate. Trust the root of the certificate (for the site's self-signed certificate, the SMS Issuing root) or bind a PKI certificate to the administration service.";
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
                return "The SMS Provider sent no certificate. Check the HTTPS setup of the administration service.";
            return "A secure connection to the administration service could not be made. Check the certificate of the SMS Provider and the TLS settings of this computer.";
        }
        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => $"The SMS Provider name {host} could not be found. Check the provider name in Settings.",
            HttpRequestError.ConnectionError => $"The administration service on {host} could not be reached. Check that the SMS Provider is running and that port 443 is open.",
            _ => "The administration service closed the connection before it answered. Try again later."
        };
    }

    public void Dispose() => _http.Dispose();
}
