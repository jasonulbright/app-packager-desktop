using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// The operator-facing text for adapter failures: the server's own message
/// with the fix for the failures whose cause is known.
/// </summary>
public static partial class WsusMessages
{
    public const string ConsoleVersionHint = "The WSUS console on this computer must match the server version.";
    public const string TlsHint = "For SSL, this computer must trust the server certificate and use TLS 1.2 (SchUseStrongCrypto = 1 under HKLM\\SOFTWARE\\Microsoft\\.NETFramework\\v4.0.30319).";

    [GeneratedRegex("SSL|TLS|secure channel|trust relationship", RegexOptions.IgnoreCase)]
    private static partial Regex SecureChannelFailure();

    [GeneratedRegex("SSL/TLS|secure channel|forcibly closed|underlying connection was closed", RegexOptions.IgnoreCase)]
    private static partial Regex CatalogTlsFailure();

    /// <summary>The message a failed adapter call raises, by its error kind.</summary>
    public static string Compose(WsusAdapterResult result, WsusSettings settings) => result.ErrorKind switch
    {
        WsusErrorKinds.Connect => ConnectFailure(settings, result.Error),
        WsusErrorKinds.SelfSignedRefused => SelfSignedRefused(result.Error),
        _ => result.Error
    };

    public static string ConnectFailure(WsusSettings settings, string message)
    {
        var hint = settings.UseSsl && SecureChannelFailure().IsMatch(message) ? TlsHint : ConsoleVersionHint;
        return $"Connecting to WSUS {settings.ServerName}:{settings.PortNumber} failed: {message}. {hint}";
    }

    public static string SelfSignedRefused(string message) =>
        $"WSUS did not create a self-signed certificate: {message}. WSUS on Windows Server 2012 R2 and later creates one only when HKLM\\SOFTWARE\\Microsoft\\Update Services\\Server\\Setup\\EnableSelfSignedCertificates = 1 (DWORD); Microsoft treats this path as deprecated. A code-signing certificate from your PKI, imported with Import PFX, is the supported path.";

    /// <summary>The server's own error texts for the common local-publishing failures, mapped to the fix; empty for any other text.</summary>
    public static string PublishFailureHint(string? message)
    {
        var text = message ?? "";
        if (Regex.IsMatch(text, "Verification of file signature failed", RegexOptions.IgnoreCase))
            return "This computer must trust the WSUS signing certificate: import it into the Local Computer Trusted Publishers store, and into Trusted Root Certification Authorities when it is self-signed. Options, WSUS Publishing, Export certificate saves it.";
        if (Regex.IsMatch(text, "not a WSUS Administrator|Unauthorized", RegexOptions.IgnoreCase))
            return "The account must be a member of the WSUS Administrators group on the server.";
        if (Regex.IsMatch(text, "CreateDirectory failed", RegexOptions.IgnoreCase))
            return "The UpdateServicesPackages or WSUSContent share on the server is missing, not shared, or not writable.";
        if (Regex.IsMatch(text, "Failed to sign package", RegexOptions.IgnoreCase))
            return "The server could not sign the package: check its signing certificate and that it reaches its time stamp server.";
        if (Regex.IsMatch(text, "too many locally published categories", RegexOptions.IgnoreCase))
            return "The server holds too many locally published vendor and product categories; remove unused ones on the server.";
        if (Regex.IsMatch(text, "version", RegexOptions.IgnoreCase))
            return ConsoleVersionHint;
        return "";
    }

    /// <summary>
    /// 'WSUS rejected the package: &lt;server text&gt;' with the fix when the text is
    /// known, else the cab-size fix when the payload exceeds the server's limit.
    /// </summary>
    public static string PublishRejected(string message, double payloadMegabytes, int cabLimitMegabytes)
    {
        var hint = PublishFailureHint(message);
        if (hint.Length == 0 && cabLimitMegabytes > 0 && payloadMegabytes > cabLimitMegabytes)
            hint = $"The payload is {payloadMegabytes:N0} MB and the server allows {cabLimitMegabytes} MB per cab: raise LocalPublishingMaxCabSize on the server (at most 2047).";
        return $"WSUS rejected the package: {message}{(hint.Length > 0 ? " " + hint : "")}";
    }

    /// <summary>The message of one failed state change, with the fix for the refusals whose cause is known.</summary>
    public static string StateChangeFailure(string action, string message, bool declinedNow)
    {
        if (action == WsusUpdateActions.Remove && Regex.IsMatch(message, "referenced", RegexOptions.IgnoreCase))
            message += " Another update on the server still references this update. Expire this update instead, or remove the update that references it first.";
        else if (action == WsusUpdateActions.Expire && Regex.IsMatch(message, "already (been )?expired|not imported|SDP", RegexOptions.IgnoreCase))
            message += " Only a locally published update that is not already expired can be expired on this server.";
        if (declinedNow) message += " The update is now declined.";
        return message;
    }

    public static string PackageFolderNotDeleted(string message) =>
        "The update is removed; its package folder on the server was not deleted: " + message;

    /// <summary>The import runs on the WSUS server, which downloads the metadata itself; this computer's TLS settings play no part.</summary>
    public static string CatalogImportFailure(string message) =>
        CatalogTlsFailure().IsMatch(message)
            ? message + " The WSUS server could not reach the Microsoft Update Catalog over TLS 1.2. On the WSUS server, set SchUseStrongCrypto = 1 (DWORD) under HKLM\\SOFTWARE\\Microsoft\\.NETFramework\\v4.0.30319, then restart the WSUS service and the World Wide Web Publishing service. SoftwareDistribution.log in the Update Services LogFiles folder on the server records the import."
            : message;
}
