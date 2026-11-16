using System.Text;

namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>
/// The credential of an authenticated feed for a payload download or a version check that runs through curl.exe ("Payload downloads and version
/// checks"). curl follows redirects by itself and would send a custom header to every target, so a request that needs a credential runs with no
/// <c>-L</c>, with the header on standard input (<c>-K -</c>, never on the command line), and the program follows the redirects itself: the
/// credential goes only to a hop on the bound scheme, host and port. Only the methods that send a header (a key, Basic) are used here; the other
/// methods open the feed document only. The command-line build sets no provider, so a payload that needs a credential fails there.
/// </summary>
public static class FeedPayloadCredentials
{
    /// <summary>Finds the credential for an address. The application sets it at start; null means no credential is used.</summary>
    public static Func<Uri, FeedCredential?>? Provider { get; set; }

    /// <summary>The credential for the address, only for the methods that send a header and only for the exact bound host.</summary>
    public static FeedCredential? For(Uri address)
    {
        var credential = Provider?.Invoke(address);
        return credential is { Entry.Method: FeedAuthMethod.HeaderKey or FeedAuthMethod.Basic } c && c.Entry.Matches(address) ? c : null;
    }

    /// <summary>The curl configuration for stdin: the header of the credential. A quote or a line break in a value is refused, so a value cannot end its line.</summary>
    public static string ConfigFor(FeedCredential credential)
    {
        var (name, value) = credential.Header();
        var line = $"{name}: {value}";
        if (line.Any(c => c is '"' or '\\' or '\r' or '\n' || char.IsControl(c)))
            throw new InvalidOperationException("The credential has a character that cannot go into a header safely (a quote, a backslash or a control character).");
        return $"header = \"{line}\"\n";
    }

    /// <summary>The outcome of one hop of a manual download.</summary>
    public sealed record Hop(bool Done, Uri? Next, bool SendCredential, string? Error);

    /// <summary>
    /// What to do with the answer of one hop: <paramref name="status"/> and <paramref name="redirect"/> come from curl (<c>%{http_code}</c> and
    /// <c>%{redirect_url}</c>). A redirect goes on with the credential only when the next address is on the bound endpoint; HTTPS only.
    /// </summary>
    public static Hop Next(Uri current, int status, string? redirect, FeedCredential credential, int redirectsSoFar, int maxRedirects = 5)
    {
        if (status is >= 200 and < 300) return new Hop(true, null, false, null);
        if (status is 301 or 302 or 303 or 307 or 308)
        {
            if (redirectsSoFar >= maxRedirects) return new Hop(false, null, false, $"More than {maxRedirects} redirects from {current.Host}.");
            if (!Uri.TryCreate(current, redirect ?? "", out var next)) return new Hop(false, null, false, $"{current.Host} answered {status} with no address to go to.");
            if (!next.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return new Hop(false, null, false, $"{current.Host} redirects to a '{next.Scheme}' address, which is refused (HTTPS only).");
            return new Hop(false, next, credential.Entry.Matches(next), null);
        }
        return new Hop(false, null, false, status is 401 or 403
            ? $"{current.Host} refused the credential ({status}). The key or the password may be wrong, expired or revoked; rotate it."
            : $"{current.Host} answered {status}.");
    }
}