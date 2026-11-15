using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>
/// The publisher that an administrator pinned for a feed: the subject and the issuer of the code-signing certificate, or the SHA-256 of its
/// public key. The thumbprint is not a pin, so that a renewal of the certificate by the vendor does not break it.
/// </summary>
public sealed record PublisherPin(string? Subject, string? Issuer, string? PublicKeySha256)
{
    /// <summary>The text that the record and the window show. It parses back to the same pin.</summary>
    public string Text => PublicKeySha256 is not null ? "key:" + PublicKeySha256 : $"{Subject} | {Issuer}";

    /// <summary>
    /// Parses <c>key:&lt;64 hex characters&gt;</c> (the SHA-256 of the public key) or <c>&lt;subject&gt; | &lt;issuer&gt;</c> (two distinguished
    /// names, for example <c>CN=Contoso Software, O=Contoso, C=US | CN=Contoso Code Signing CA</c>).
    /// </summary>
    public static bool TryParse(string? text, out PublisherPin pin, out string error)
    {
        pin = new PublisherPin(null, null, null);
        error = "";
        var value = text?.Trim() ?? "";
        if (value.Length == 0) { error = "The pinned publisher is empty."; return false; }
        if (value.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = value[4..].Trim();
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) { error = "The key pin is key: and the 64 hex characters of the SHA-256 of the public key."; return false; }
            pin = new PublisherPin(null, null, hex.ToUpperInvariant());
            return true;
        }
        var parts = value.Split('|');
        if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
        {
            error = "The publisher pin is the subject and the issuer of the certificate, separated by |, or key: and the SHA-256 of its public key.";
            return false;
        }
        try
        {
            pin = new PublisherPin(Normalize(parts[0]), Normalize(parts[1]), null);
            return true;
        }
        catch (CryptographicException)
        {
            error = "The subject or the issuer of the publisher pin is not a distinguished name (for example CN=Contoso Software, O=Contoso, C=US).";
            return false;
        }
    }

    public static string Normalize(string distinguishedName) => new X500DistinguishedName(distinguishedName.Trim()).Name;
}

/// <summary>The outcome of the check of a detached signature of a feed document.</summary>
public sealed record SignatureResult(bool Valid, string? Subject, string? Issuer, string? PublicKeySha256, string? Error)
{
    public string SignerText => $"{Subject} (issuer {Issuer})";
}

/// <summary>
/// Checks the detached CMS signature of a feed document (a <c>.p7s</c> file) for the exact bytes, and that the signer is the pinned publisher.
/// The signature is checked for the bytes first. Then the chain of the code-signing certificate is built at the current time, to a trusted root,
/// with the revocation check of Windows. An expired certificate is accepted only with an RFC 3161 time stamp that was itself checked: its token
/// belongs to this signature (the message imprint), its authority chains to a trusted root and carries the time-stamping usage, and then the
/// time of the token is the time of the chain. A time stamp that is not checked is ignored: anyone who has the key of an expired
/// certificate can attach any time to a signature.
/// </summary>
public sealed class FeedSignatureVerifier(Action<X509ChainPolicy>? configure = null)
{
    public const string CodeSigningUsage = "1.3.6.1.5.5.7.3.3";
    public const string TimeStampingUsage = "1.3.6.1.5.5.7.3.8";
    private const string TimeStampTokenAttribute = "1.2.840.113549.1.9.16.2.14";

    public SignatureResult Verify(byte[] document, byte[] signature, PublisherPin pin, DateTimeOffset now)
    {
        try { return VerifyCore(document, signature, pin, now); }
        catch (CryptographicException ex) { return Fail("The signature could not be checked: " + ex.Message); }
    }

    private SignatureResult VerifyCore(byte[] document, byte[] signature, PublisherPin pin, DateTimeOffset now)
    {
        SignedCms cms;
        try
        {
            cms = new SignedCms(new ContentInfo(document), detached: true);
            cms.Decode(signature);
        }
        catch (CryptographicException)
        {
            return Fail("The signature file is not a detached CMS signature (.p7s).");
        }
        if (cms.SignerInfos.Count != 1) return Fail($"The signature has {cms.SignerInfos.Count} signers; exactly one is expected.");
        try { cms.CheckSignature(verifySignatureOnly: true); }
        catch (CryptographicException) { return Fail("The signature does not match the bytes of the document."); }

        var signerInfo = cms.SignerInfos[0];
        var certificate = signerInfo.Certificate;
        if (certificate is null) return Fail("The signature holds no certificate of the signer.");
        var subject = Describe(certificate.SubjectName);
        var issuer = Describe(certificate.IssuerName);
        var keyHash = Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
        SignatureResult Result(bool valid, string? error) => new(valid, subject, issuer, keyHash, error);

        if (!HasUsage(certificate, CodeSigningUsage)) return Result(false, "The certificate of the signer does not have the code-signing usage.");

        var chainError = BuildChain(certificate, cms.Certificates, now.UtcDateTime, CodeSigningUsage);
        if (chainError is not null)
        {
            // Only a certificate that is outside its dates can be saved by a checked time stamp; any other chain error stands.
            if (!chainError.Value.OnlyDates) return Result(false, "The certificate of the signer is not trusted: " + chainError.Value.Message);
            var stamped = CheckedTimeStamp(signerInfo, now);
            if (stamped.Time is null) return Result(false, "The certificate of the signer is outside its dates, and " + stamped.Reason);
            var again = BuildChain(certificate, cms.Certificates, stamped.Time.Value.UtcDateTime, CodeSigningUsage);
            if (again is not null) return Result(false, "The certificate of the signer is not trusted at the time of its time stamp: " + again.Value.Message);
        }

        if (!Matches(pin, subject, issuer, keyHash))
            return Result(false, $"The signer is {subject} (issuer {issuer}); the pinned publisher is {pin.Text}.");
        return Result(true, null);
    }

    private static SignatureResult Fail(string error) => new(false, null, null, null, error);

    private static string Describe(X500DistinguishedName name) => name.Name;

    private static bool Matches(PublisherPin pin, string subject, string issuer, string keyHash) =>
        pin.PublicKeySha256 is not null
            ? string.Equals(pin.PublicKeySha256, keyHash, StringComparison.OrdinalIgnoreCase)
            : string.Equals(pin.Subject, subject, StringComparison.OrdinalIgnoreCase) && string.Equals(pin.Issuer, issuer, StringComparison.OrdinalIgnoreCase);

    private static bool HasUsage(X509Certificate2 certificate, string oid)
    {
        foreach (var extension in certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            foreach (var usage in extension.EnhancedKeyUsages)
                if (usage.Value == oid) return true;
        return false;
    }

    private readonly record struct ChainFailure(string Message, bool OnlyDates);

    private ChainFailure? BuildChain(X509Certificate2 certificate, X509Certificate2Collection extra, DateTime verificationTime, string usageOid)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(usageOid));
        chain.ChainPolicy.VerificationTime = verificationTime;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        configure?.Invoke(chain.ChainPolicy);
        if (chain.Build(certificate)) return null;
        var statuses = chain.ChainStatus.Select(s => s.Status).ToList();
        var message = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
        var onlyDates = statuses.Count > 0 && statuses.All(s => s == X509ChainStatusFlags.NotTimeValid);
        return new ChainFailure(message.Length > 0 ? message : string.Join(", ", statuses), onlyDates);
    }

    private (DateTimeOffset? Time, string Reason) CheckedTimeStamp(SignerInfo signerInfo, DateTimeOffset now)
    {
        var sawToken = false;
        foreach (var attribute in signerInfo.UnsignedAttributes)
        {
            if (attribute.Oid?.Value != TimeStampTokenAttribute) continue;
            foreach (var value in attribute.Values)
            {
                sawToken = true;
                if (!Rfc3161TimestampToken.TryDecode(value.RawData, out var token, out _) || token is null) continue;
                if (!token.VerifySignatureForSignerInfo(signerInfo, out var authority) || authority is null)
                    return (null, "the time stamp does not belong to this signature or is not signed by its authority.");
                if (!HasUsage(authority, TimeStampingUsage))
                    return (null, "the certificate of the time stamping authority does not have the time-stamping usage.");
                var extra = token.AsSignedCms().Certificates;
                var failure = BuildChain(authority, extra, token.TokenInfo.Timestamp.UtcDateTime, TimeStampingUsage);
                if (failure is not null) return (null, "the time stamping authority is not trusted: " + failure.Value.Message);
                return (token.TokenInfo.Timestamp, "");
            }
        }
        return (null, sawToken
            ? "its time stamp is not a valid RFC 3161 token (the certificate of the authority must carry the time-stamping usage as its critical usage and be valid at the time of the stamp)."
            : "it has no time stamp.");
    }
}
