using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// The checks a PFX passes before it becomes the server's signing
/// certificate. The key is loaded as an ephemeral key set, so nothing is
/// written to a key container or a certificate store.
/// </summary>
public static class WsusPfxCheck
{
    public const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    public static void Check(string pfxPath, string password, DateTimeOffset now)
    {
        if (!File.Exists(pfxPath)) throw Refuse($"PFX file not found: {pfxPath}");
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException ex)
        {
            throw Refuse($"The PFX file could not be opened: {ex.Message}");
        }
        using (certificate)
        {
            if (!certificate.HasPrivateKey) throw Refuse("The PFX file carries no private key; WSUS signs with the private key.");
            var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>()).Select(o => o.Value ?? "").ToList();
            if (usages.Count > 0 && !usages.Contains(CodeSigningOid))
                throw Refuse("The certificate is not valid for code signing (enhanced key usage 1.3.6.1.5.5.7.3.3).");
            var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            if (keyUsage is not null && !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
                throw Refuse("The certificate key usage does not include Digital Signature.");
            // Clients reject update signatures from keys under 1024 bits, and the
            // publishing guidance for this workload asks for 2048 or more.
            using var rsa = certificate.GetRSAPublicKey();
            var keySize = rsa?.KeySize ?? 0;
            if (keySize > 0 && keySize < 2048)
                throw Refuse($"The certificate key is {keySize} bits; WSUS update signing needs an RSA key of 2048 bits or more.");
            if (new DateTimeOffset(certificate.NotAfter) < now)
                throw Refuse($"The certificate expired on {certificate.NotAfter.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.");
        }
    }

    private static WsusOperationException Refuse(string message) => new(WsusErrorKinds.Input, message);
}
