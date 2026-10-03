using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// Throwaway certificates exist only in memory and in PFX files under a
/// temporary folder; no certificate store or key container is touched.
/// </summary>
public class WsusPfxCheckTests : IDisposable
{
    private const string Password = "p-test-password";
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Pfx(string name, int keySize = 2048, string eku = "1.3.6.1.5.5.7.3.3", X509KeyUsageFlags usage = X509KeyUsageFlags.DigitalSignature,
        int notAfterDays = 1, bool withKey = true)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest($"CN=AppPackager WSUS Test {name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(eku) }, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, false));
        using var certificate = request.CreateSelfSigned(Now.AddDays(-2), Now.AddDays(notAfterDays));
        var bytes = withKey
            ? certificate.Export(X509ContentType.Pkcs12, Password)
            : X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert)).Export(X509ContentType.Pkcs12, Password);
        var path = Path.Combine(_temp.Path, name + ".pfx");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void AcceptsA2048BitCodeSigningCertificate()
    {
        WsusPfxCheck.Check(Pfx("Strong"), Password, Now);
    }

    [Theory]
    [InlineData("Weak", "1024 bits; WSUS update signing needs an RSA key of 2048 bits or more")]
    [InlineData("Tls", "not valid for code signing")]
    [InlineData("NoSignature", "key usage does not include Digital Signature")]
    [InlineData("Expired", "The certificate expired on ")]
    [InlineData("NoKey", "carries no private key")]
    public void RefusesBeforeTheServerConfigurationChanges(string name, string message)
    {
        var path = name switch
        {
            "Weak" => Pfx(name, keySize: 1024),
            "Tls" => Pfx(name, eku: "1.3.6.1.5.5.7.3.1"),
            "NoSignature" => Pfx(name, usage: X509KeyUsageFlags.KeyEncipherment),
            "Expired" => Pfx(name, notAfterDays: -1),
            _ => Pfx(name, withKey: false)
        };
        var ex = Assert.Throws<WsusOperationException>(() => WsusPfxCheck.Check(path, Password, Now));
        Assert.Contains(message, ex.Message);
        Assert.Equal(WsusErrorKinds.Input, ex.Kind);
    }

    [Fact]
    public void RefusesAWrongPasswordAndAMissingFile()
    {
        Assert.StartsWith("The PFX file could not be opened: ", Assert.Throws<WsusOperationException>(() => WsusPfxCheck.Check(Pfx("Strong"), "wrong", Now)).Message);
        Assert.StartsWith("PFX file not found: ", Assert.Throws<WsusOperationException>(() => WsusPfxCheck.Check(Path.Combine(_temp.Path, "none.pfx"), Password, Now)).Message);
    }

    [Fact]
    public async Task RefusesAWrongPasswordBeforeAnyServerCallAndSendsTheGoodOneOnStandardInputOnly()
    {
        var runner = new FakeAdapterRunner((_, _, _) => FakeAdapterRunner.Ok(new JsonObject { ["Connected"] = true, ["ServerName"] = "wsus01" }));
        var client = new WsusAdminClient(runner);
        var settings = new WsusSettings { ServerName = "wsus01.contoso.com", UseSsl = true };
        var pfx = Pfx("Strong");
        await Assert.ThrowsAsync<WsusOperationException>(() => client.ImportPfxAsync(settings, pfx, "wrong"));
        Assert.Empty(runner.Calls);

        await client.ImportPfxAsync(settings, pfx, Password);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("ImportPfx", call.Operation);
        Assert.Equal(Password, call.StandardInput);
        Assert.DoesNotContain(Password, call.Request.ToJsonString());
        Assert.Equal(pfx, call.Request["PfxPath"]!.GetValue<string>());
    }
}
