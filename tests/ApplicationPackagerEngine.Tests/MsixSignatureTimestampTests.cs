using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Tests.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A package signature with a valid timestamp stays valid after the signing certificate expires, when the timestamp
/// falls inside the validity of the certificate. Without a timestamp, or with the lifetime signing usage, an expired
/// certificate fails. The chain is built against a root created in the test, never against the machine stores.
/// </summary>
public sealed class MsixSignatureTimestampTests : IDisposable
{
    private const string TimestampTokenOid = "1.3.6.1.4.1.311.3.3.1";
    private const string LifetimeSigningOid = "1.3.6.1.4.1.311.10.3.13";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msix-timestamp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly RSA _rootKey = RSA.Create(2048);
    private readonly X509Certificate2 _root;

    public MsixSignatureTimestampTests()
    {
        var request = new CertificateRequest("CN=Test Root", _rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        _root = request.CreateSelfSigned(_now.AddYears(-2), _now.AddYears(2));
    }

    public void Dispose()
    {
        _root.Dispose();
        _rootKey.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MsixPackageReader Reader() => new(customRoots: new X509Certificate2Collection(_root));

    private X509Certificate2 Issue(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter, params string[] usages)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var oids = new OidCollection();
        foreach (var usage in usages) oids.Add(new Oid(usage));
        // RFC 3161 section 2.3: the TSA certificate carries the time stamping usage in a critical extension.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: usages.Contains("1.3.6.1.5.5.7.3.8")));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var issued = request.Create(_root, notBefore, notAfter, serial);
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>The ESS signing-certificate-v2 attribute that names the TSA certificate by its SHA-256.</summary>
    private static AsnEncodedData SigningCertificateV2(X509Certificate2 tsa)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
            writer.WriteOctetString(SHA256.HashData(tsa.RawData));
        return new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), writer.Encode());
    }

    private static byte[] TimestampToken(SignerInfo signer, X509Certificate2 tsa, DateTimeOffset at)
    {
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4"), new Oid("2.16.840.1.101.3.4.2.1"),
            SHA256.HashData(signer.GetSignature()), RandomNumberGenerator.GetBytes(8), at);
        var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var tsaSigner = new CmsSigner(tsa) { IncludeOption = X509IncludeOption.EndCertOnly };
        tsaSigner.SignedAttributes.Add(SigningCertificateV2(tsa));
        cms.ComputeSignature(tsaSigner);
        return cms.Encode();
    }

    /// <summary>A PKCX-prefixed package signature by the leaf, with a timestamp at the given time when one is set.</summary>
    private string Package(X509Certificate2 leaf, DateTimeOffset? timestampAt, X509Certificate2? tsa = null)
    {
        var cms = new SignedCms(new ContentInfo("APPX-indirect-data"u8.ToArray()));
        cms.ComputeSignature(new CmsSigner(leaf) { IncludeOption = X509IncludeOption.EndCertOnly });
        if (timestampAt is { } at)
        {
            using var stamp = tsa ?? Issue("CN=Test TSA", _now.AddYears(-1), _now.AddYears(1), "1.3.6.1.5.5.7.3.8");
            var signer = cms.SignerInfos[0];
            signer.AddUnsignedAttribute(new AsnEncodedData(new Oid(TimestampTokenOid), TimestampToken(signer, stamp, at)));
        }
        var encoded = cms.Encode();
        var p7x = new byte[encoded.Length + 4];
        "PKCX"u8.CopyTo(p7x);
        encoded.CopyTo(p7x, 4);
        return MsixFixture.WritePackage(_dir, $"{Guid.NewGuid():N}.msix", MsixFixture.AppxManifest(), p7x);
    }

    private X509Certificate2 ExpiredLeaf(params string[] extraUsages) =>
        Issue("CN=Contoso", _now.AddDays(-60), _now.AddDays(-10), ["1.3.6.1.5.5.7.3.3", .. extraUsages]);

    [Fact]
    public void AValidCertificateThatChainsToTheRoot_IsTrusted()
    {
        using var leaf = Issue("CN=Contoso", _now.AddDays(-1), _now.AddDays(30), "1.3.6.1.5.5.7.3.3");

        var signature = Reader().ReadSignature(Package(leaf, timestampAt: null))!;

        Assert.True(signature.SignatureValid);
        Assert.True(signature.ChainTrusted);
        Assert.False(signature.CertificateExpired);
    }

    [Fact]
    public void AnExpiredCertificate_WithATimestampInsideItsValidity_IsTrusted()
    {
        using var leaf = ExpiredLeaf();

        var signature = Reader().ReadSignature(Package(leaf, _now.AddDays(-30)))!;

        Assert.True(signature.ChainTrusted);
        Assert.False(signature.CertificateExpired);
        Assert.Equal(_now.AddDays(-30).ToUnixTimeSeconds(), signature.Timestamp!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void AnExpiredCertificate_WithNoTimestamp_IsExpired_NotUntrusted()
    {
        using var leaf = ExpiredLeaf();

        var signature = Reader().ReadSignature(Package(leaf, timestampAt: null))!;

        Assert.False(signature.ChainTrusted);
        Assert.True(signature.CertificateExpired);
    }

    [Fact]
    public void AnExpiredCertificate_WithATimestampAfterItExpired_IsExpired()
    {
        using var leaf = ExpiredLeaf();

        var signature = Reader().ReadSignature(Package(leaf, _now.AddDays(-5)))!;

        Assert.False(signature.ChainTrusted);
        Assert.True(signature.CertificateExpired);
    }

    [Fact]
    public void AnExpiredCertificate_WithTheLifetimeSigningUsage_IsExpiredEvenWithATimestamp()
    {
        using var leaf = ExpiredLeaf(LifetimeSigningOid);

        var signature = Reader().ReadSignature(Package(leaf, _now.AddDays(-30)))!;

        Assert.False(signature.ChainTrusted);
        Assert.True(signature.CertificateExpired);
    }

    [Fact]
    public void ATimestampFromACertificateWithoutTheTimeStampingUsage_IsNotAccepted()
    {
        using var leaf = ExpiredLeaf();
        using var notATsa = Issue("CN=Not A TSA", _now.AddYears(-1), _now.AddYears(1), "1.3.6.1.5.5.7.3.3");

        var signature = Reader().ReadSignature(Package(leaf, _now.AddDays(-30), notATsa))!;

        Assert.False(signature.ChainTrusted);
        Assert.True(signature.CertificateExpired);
    }

    [Fact]
    public async Task TheReview_CallsAnExpiredUntimestampedSignatureExpired_AndATimestampedOneTrusted()
    {
        using var bed = new AddFromInstallerTestBed();
        var service = bed.Real();
        service.Msix = Reader();
        using var leaf = ExpiredLeaf();
        var stamped = File.ReadAllBytes(Package(leaf, _now.AddDays(-30)));
        var plain = File.ReadAllBytes(Package(leaf, timestampAt: null));
        string Signed(string name, byte[] package) => bed.Msix(name, withDependency: false, signature: ReadP7x(package));

        var trusted = await service.AnalyzeAsync(Signed("stamped.msix", stamped), new Progress<string>(), CancellationToken.None);
        var expired = await service.AnalyzeAsync(Signed("plain.msix", plain), new Progress<string>(), CancellationToken.None);

        Assert.Equal(PackageTrust.Trusted, trusted.Package!.Trust);
        Assert.Null(trusted.Package.TrustWarning);
        Assert.Equal(PackageTrust.Expired, expired.Package!.Trust);
        Assert.Contains("expired", expired.Package.TrustWarning);
        Assert.DoesNotContain("does not trust", expired.Package.TrustWarning);
    }

    private static byte[] ReadP7x(byte[] package)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(package));
        using var stream = zip.GetEntry("AppxSignature.p7x")!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
