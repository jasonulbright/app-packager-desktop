using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApplicationPackagerEngine.Models.Signing;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>
/// Lists and resolves code-signing certificates in the CurrentUser or
/// LocalMachine personal store. Listing opens a private-key handle and reads
/// provider metadata but produces no signature, so no PIN dialog appears.
/// Resolution is by thumbprint only and never falls back to another entry.
/// </summary>
public sealed class SigningCertificateService
{
    public const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    /// <summary>Key storage providers that hold the key on hardware or behind an interactive PIN.</summary>
    public static readonly IReadOnlyList<string> InteractiveKeyProviderPatterns =
    [
        "Smart Card", "SmartCard", "Token", "eToken", "SafeNet",
        "nCipher", "Luna", "YubiKey", "Gemalto", "Thales"
    ];

    private readonly Func<string, IReadOnlyList<X509Certificate2>> _readStore;
    private readonly Func<DateTime> _now;

    /// <param name="readStore">
    /// Returns the personal store entries for a store location; throws when
    /// the store cannot be read. Null reads the real store.
    /// </param>
    public SigningCertificateService(
        Func<string, IReadOnlyList<X509Certificate2>>? readStore = null, Func<DateTime>? now = null)
    {
        _readStore = readStore ?? ReadPersonalStore;
        _now = now ?? (() => DateTime.Now);
    }

    public static string StorePath(string storeLocation) => $@"Cert:\{storeLocation}\My";

    public IReadOnlyList<CodeSigningCandidate> ListCandidates(string storeLocation = SigningPolicy.CurrentUser)
    {
        if (storeLocation is not (SigningPolicy.CurrentUser or SigningPolicy.LocalMachine))
            throw new ArgumentException($"Unsupported store location '{storeLocation}'.", nameof(storeLocation));

        IReadOnlyList<X509Certificate2> certificates;
        try { certificates = _readStore(storeLocation); }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or IOException) { return []; }

        var now = _now();
        var results = new List<CodeSigningCandidate>();
        foreach (var cert in certificates)
        {
            if (!IsCodeSigning(cert)) continue;

            var key = ReadPrivateKeyInfo(cert);
            var reasons = new List<string>();
            if (cert.NotBefore > now) reasons.Add("Certificate is not yet valid.");
            if (cert.NotAfter < now) reasons.Add($"Certificate expired on {cert.NotAfter:yyyy-MM-dd}.");
            if (!key.HasPrivateKey) reasons.Add("Certificate has no associated private key.");
            else if (!key.Accessible) reasons.Add(key.Reason);
            if (key.MayPrompt) reasons.Add(key.Reason);

            results.Add(new CodeSigningCandidate
            {
                Thumbprint = cert.Thumbprint.ToUpperInvariant(),
                Subject = cert.Subject,
                Issuer = cert.Issuer,
                NotBefore = cert.NotBefore,
                NotAfter = cert.NotAfter,
                HasPrivateKey = key.HasPrivateKey,
                KeyAccessible = key.Accessible,
                MayPrompt = key.MayPrompt,
                KeyProvider = key.Provider,
                StoreLocation = storeLocation,
                Usable = cert.NotBefore <= now && cert.NotAfter >= now && key.HasPrivateKey && key.Accessible,
                Reason = string.Join(" ", reasons)
            });
        }
        return results;
    }

    /// <summary>
    /// The configured certificate. Every refusal throws
    /// <see cref="SigningCertificateUnavailableException"/> with the precise reason.
    /// </summary>
    public X509Certificate2 Resolve(SigningPolicy policy)
    {
        var normalized = SigningPolicy.Normalize(policy);
        var thumb = normalized.CertificateThumbprint;
        if (string.IsNullOrWhiteSpace(thumb))
            throw new SigningCertificateUnavailableException(
                "no certificate selected. Choose a code-signing certificate thumbprint in Options.");

        var storePath = StorePath(normalized.StoreLocation);
        List<X509Certificate2> found;
        try
        {
            found = _readStore(normalized.StoreLocation)
                .Where(c => !string.IsNullOrEmpty(c.Thumbprint) && c.Thumbprint.ToUpperInvariant() == thumb)
                .ToList();
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or IOException)
        {
            throw new SigningCertificateUnavailableException(
                $"certificate store {storePath} could not be read ({ex.Message}).");
        }

        if (found.Count == 0)
            throw new SigningCertificateUnavailableException($"thumbprint {thumb} was not found in {storePath}.");
        if (found.Count > 1)
            throw new SigningCertificateUnavailableException(
                $"ambiguous configuration, thumbprint {thumb} resolves to {found.Count} entries in {storePath}.");

        var cert = found[0];
        var now = _now();
        if (cert.NotBefore > now)
            throw new SigningCertificateUnavailableException(
                $"certificate {thumb} is not valid before {cert.NotBefore:yyyy-MM-dd}.");
        if (cert.NotAfter < now)
            throw new SigningCertificateUnavailableException(
                $"certificate {thumb} expired on {cert.NotAfter:yyyy-MM-dd}. Select a renewed certificate.");
        if (!IsCodeSigning(cert))
            throw new SigningCertificateUnavailableException(
                $"certificate {thumb} does not carry the code-signing extended key usage.");

        var key = ReadPrivateKeyInfo(cert);
        if (!key.HasPrivateKey)
            throw new SigningCertificateUnavailableException(
                $"certificate {thumb} has no associated private key in {storePath}.");
        if (!key.Accessible)
            throw new SigningCertificateUnavailableException(
                $"the private key for {thumb} is not accessible to this account. {key.Reason}");

        return cert;
    }

    /// <summary>A certificate without an EKU extension is usable for any purpose.</summary>
    public static bool IsCodeSigning(X509Certificate2 certificate)
    {
        var sawEku = false;
        foreach (var ext in certificate.Extensions)
        {
            if (ext is not X509EnhancedKeyUsageExtension eku) continue;
            sawEku = true;
            foreach (var oid in eku.EnhancedKeyUsages)
                if (oid.Value == CodeSigningEku) return true;
        }
        return !sawEku;
    }

    /// <summary>
    /// Opens the private-key handle and reads the provider. A CNG key reports
    /// its provider name; a CAPI key also reports whether it is hardware-held
    /// or protected. Either kind whose provider matches a hardware or PIN
    /// pattern may prompt.
    /// </summary>
    public static SigningPrivateKeyInfo ReadPrivateKeyInfo(X509Certificate2 certificate)
    {
        var info = new SigningPrivateKeyInfo { HasPrivateKey = certificate.HasPrivateKey };
        if (!certificate.HasPrivateKey)
        {
            info.Reason = "Certificate has no associated private key.";
            return info;
        }

        AsymmetricAlgorithm? key;
        try
        {
            key = (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey();
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // A key the current process imported can stay bound to an ephemeral
            // handle the extension methods refuse to reopen (ArgumentException)
            // while the signer still reaches the stored key material.
            if (ex.Message.Contains("ephemeral", StringComparison.OrdinalIgnoreCase))
            {
                info.Accessible = true;
                return info;
            }
            info.Reason = $"Private key handle could not be opened: {ex.Message}";
            return info;
        }

        if (key is null)
        {
            info.Reason = "Private key handle could not be opened.";
            return info;
        }

        using (key)
        {
            try
            {
                info.Accessible = true;
                switch (key)
                {
                    case RSACng rsaCng: info.Provider = rsaCng.Key.Provider?.Provider ?? ""; break;
                    case ECDsaCng ecCng: info.Provider = ecCng.Key.Provider?.Provider ?? ""; break;
                    case ICspAsymmetricAlgorithm csp:
                        info.Provider = csp.CspKeyContainerInfo.ProviderName ?? "";
                        if (csp.CspKeyContainerInfo.HardwareDevice) info.MayPrompt = true;
                        if (csp.CspKeyContainerInfo.Protected) info.MayPrompt = true;
                        break;
                }
                foreach (var pattern in InteractiveKeyProviderPatterns)
                    if (info.Provider.Length > 0 && info.Provider.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        info.MayPrompt = true;
                if (info.MayPrompt)
                    info.Reason = "Private key provider may require a PIN or hardware token; unattended signing can block.";
            }
            catch (CryptographicException ex)
            {
                info.Reason = $"Private key metadata could not be read: {ex.Message}";
            }
        }
        return info;
    }

    private static IReadOnlyList<X509Certificate2> ReadPersonalStore(string storeLocation)
    {
        var location = storeLocation == SigningPolicy.LocalMachine
            ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return [.. store.Certificates];
    }
}
