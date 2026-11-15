using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Certificates, detached signatures and RFC 3161 time stamps for the tests of the signed feed document. Only System.* types are used.</summary>
public static class FeedSigningFixtures
{
    public const string CodeSigning = "1.3.6.1.5.5.7.3.3";
    public const string TimeStamping = "1.3.6.1.5.5.7.3.8";

    /// <summary>A self-signed certificate (a root of its own) with the given usage and dates.</summary>
    public static X509Certificate2 NewCertificate(string subject, string usageOid, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usageOid)], true));
        using var created = request.CreateSelfSigned(notBefore, notAfter);
        // Re-import so that the private key is a normal key object that SignedCms can use on every platform.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "t"), "t", X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>The detached CMS signature of the document; <paramref name="stamp"/> runs on the signer before the signature is encoded.</summary>
    public static byte[] Sign(byte[] document, X509Certificate2 certificate, Action<SignerInfo>? stamp = null)
    {
        var cms = new SignedCms(new ContentInfo(document), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
        };
        cms.ComputeSignature(signer);
        stamp?.Invoke(cms.SignerInfos[0]);
        return cms.Encode();
    }

    /// <summary>Adds an RFC 3161 time stamp of <paramref name="time"/>, made by the authority <paramref name="authority"/>, for the signature of the signer.</summary>
    public static void AddTimeStamp(SignerInfo signer, X509Certificate2 authority, DateTimeOffset time)
    {
        signer.AddUnsignedAttribute(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.14"), Token(signer.GetSignature(), authority, time)));
    }

    /// <summary>A time stamp token for the given signature bytes (the message imprint is the SHA-256 of them).</summary>
    public static byte[] Token(byte[] signatureBytes, X509Certificate2 authority, DateTimeOffset time)
    {
        var info = new Rfc3161TimestampTokenInfo(
            new Oid("1.2.3.4.5"), new Oid("2.16.840.1.101.3.4.2.1"), SHA256.HashData(signatureBytes), new byte[] { 1 }, time);
        var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, authority)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
        };
        // The token must name the certificate of its authority (RFC 3161: the ESSCertIDv2 attribute).
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), SigningCertificateV2(authority)));
        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    private static byte[] SigningCertificateV2(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            writer.WriteOctetString(SHA256.HashData(certificate.RawData));
        }
        return writer.Encode();
    }
}
