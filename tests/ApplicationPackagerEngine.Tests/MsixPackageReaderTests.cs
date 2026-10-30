using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Builds synthetic .msix containers for the identity and signature tests.</summary>
internal static class MsixFixture
{
    internal static string AppxManifest(
        string name = "contoso.app",
        string version = "1.2.3.0",
        string publisher = "CN=Contoso, O=Contoso, C=US",
        string architecture = "x64",
        string displayName = "Contoso App") =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="{name}" Version="{version}" Publisher="{publisher}" ProcessorArchitecture="{architecture}" />
          <Properties>
            <DisplayName>{displayName}</DisplayName>
            <PublisherDisplayName>Contoso</PublisherDisplayName>
          </Properties>
        </Package>
        """;

    /// <summary>Writes a zip container with an AppxManifest.xml and optional signature part.</summary>
    internal static string WritePackage(string dir, string fileName = "app.msix",
        string? manifestXml = null, byte[]? signature = null)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (manifestXml is not null)
            {
                using var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open(), Encoding.UTF8);
                writer.Write(manifestXml);
            }
            if (signature is not null)
            {
                using var stream = zip.CreateEntry("AppxSignature.p7x").Open();
                stream.Write(signature);
            }
        }
        return path;
    }

    /// <summary>A PKCX-prefixed CMS blob signed by a throwaway self-signed certificate.</summary>
    internal static (byte[] P7x, string Thumbprint) SignatureBlob(byte[]? content = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Fixture Signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var cms = new SignedCms(new ContentInfo(content ?? "APPX-indirect-data"u8.ToArray()));
        cms.ComputeSignature(new CmsSigner(cert) { IncludeOption = X509IncludeOption.EndCertOnly });

        var encoded = cms.Encode();
        var p7x = new byte[encoded.Length + 4];
        "PKCX"u8.CopyTo(p7x);
        encoded.CopyTo(p7x, 4);
        return (p7x, cert.Thumbprint);
    }
}

public sealed class MsixPackageReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msix-reader-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MsixPackageReader _reader = new();

    public void Dispose()
    {
        TestCleanup.DeleteDirectory(_dir);
    }

    [Fact]
    public void ReadIdentity_ReturnsNameVersionPublisherAndArchitecture()
    {
        var path = MsixFixture.WritePackage(_dir, manifestXml: MsixFixture.AppxManifest());

        var identity = _reader.ReadIdentity(path);

        Assert.Equal("contoso.app", identity.Name);
        Assert.Equal("1.2.3.0", identity.Version);
        Assert.Equal("CN=Contoso, O=Contoso, C=US", identity.Publisher);
        Assert.Equal("x64", identity.ProcessorArchitecture);
        Assert.Equal("Contoso App", identity.DisplayName);
    }

    [Fact]
    public void ReadIdentity_WithoutManifest_Throws()
    {
        var path = MsixFixture.WritePackage(_dir);

        var ex = Assert.Throws<InvalidOperationException>(() => _reader.ReadIdentity(path));
        Assert.Contains("AppxManifest.xml not found", ex.Message);
    }

    [Fact]
    public void ReadIdentity_IdentityWithoutName_Throws()
    {
        var xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Version="1.0.0.0" />
            </Package>
            """;
        var path = MsixFixture.WritePackage(_dir, manifestXml: xml);

        Assert.Throws<InvalidOperationException>(() => _reader.ReadIdentity(path));
    }

    [Fact]
    public void ValidateContainer_HtmlPayload_Throws()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "not-a-package.msix");
        File.WriteAllText(path, "<!DOCTYPE html><html><body>404</body></html>");

        var ex = Assert.Throws<InvalidOperationException>(() => MsixPackageReader.ValidateContainer(path));
        Assert.Contains("not an MSIX container", ex.Message);
    }

    [Fact]
    public void ValidateContainer_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => MsixPackageReader.ValidateContainer(Path.Combine(_dir, "absent.msix")));
    }

    [Fact]
    public void ReadSignature_ReturnsSignerThumbprintAndVerifies()
    {
        var (blob, thumbprint) = MsixFixture.SignatureBlob();
        var path = MsixFixture.WritePackage(_dir, manifestXml: MsixFixture.AppxManifest(), signature: blob);

        var signature = _reader.ReadSignature(path);

        Assert.NotNull(signature);
        Assert.Equal(thumbprint, signature!.Thumbprint);
        Assert.True(signature.SignatureValid);
        Assert.Contains("Fixture Signer", signature.Subject);
        // A throwaway self-signed certificate is not in any trusted root store.
        Assert.False(signature.ChainTrusted);
    }

    [Fact]
    public void ReadSignature_UnsignedPackage_ReturnsNull()
    {
        var path = MsixFixture.WritePackage(_dir, manifestXml: MsixFixture.AppxManifest());

        Assert.Null(_reader.ReadSignature(path));
    }

    [Fact]
    public void ReadSignature_TamperedContent_ReportsSignatureInvalid()
    {
        var (blob, _) = MsixFixture.SignatureBlob();
        // Flip a byte inside the signed content so the digest no longer matches.
        blob[^1] ^= 0xFF;
        var path = MsixFixture.WritePackage(_dir, manifestXml: MsixFixture.AppxManifest(), signature: blob);

        var signature = _reader.ReadSignature(path);

        Assert.NotNull(signature);
        Assert.False(signature!.SignatureValid);
    }

    [Fact]
    public void ReadSignature_WithoutPkcxMarker_StillDecodes()
    {
        var (blob, thumbprint) = MsixFixture.SignatureBlob();
        var bare = blob[4..];
        var path = MsixFixture.WritePackage(_dir, manifestXml: MsixFixture.AppxManifest(), signature: bare);

        Assert.Equal(thumbprint, _reader.ReadSignature(path)!.Thumbprint);
    }

    [Fact]
    public void ReadSignature_PartAboveTheCap_IsRefusedBeforeItIsRead()
    {
        var big = new byte[MsixPackageReader.MaxSignatureBytes + 1];
        Random.Shared.NextBytes(big);
        var path = MsixFixture.WritePackage(_dir, manifestXml: MsixFixture.AppxManifest(), signature: big);

        var ex = Assert.Throws<InvalidDataException>(() => _reader.ReadSignature(path));

        Assert.Contains("larger than", ex.Message);
    }

    [Fact]
    public void ReadSignature_PartAtTheCap_IsRead()
    {
        var (blob, _) = MsixFixture.SignatureBlob();
        var padded = new byte[MsixPackageReader.MaxSignatureBytes];
        Array.Copy(blob, padded, blob.Length);
        var path = MsixFixture.WritePackage(_dir, manifestXml: MsixFixture.AppxManifest(), signature: padded);

        var thrown = Record.Exception(() => _reader.ReadSignature(path));

        Assert.IsNotType<InvalidDataException>(thrown);
    }
}
