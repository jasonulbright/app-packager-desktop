using System.Text;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>
/// "Intact but untrusted" is granted only for an untrusted-root or chaining
/// result and only to a signer whose chain is valid for code signing. The
/// signer here carries server authentication as its only EKU; the operating
/// system's Authenticode signer accepts it, so the signed file is real.
/// </summary>
[Collection(SigningCollection.Name)]
public sealed class UntrustedChainVerdictTests(SigningCertificateFixture fixture)
{
    private string SignedBy(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, string name)
    {
        var path = Path.Combine(fixture.NewDirectory(name), "detect.ps1");
        File.WriteAllText(path, "Write-Output 'Detected'\r\nexit 0", new ASCIIEncoding());
        Assert.Equal(0u, AuthenticodeNative.SignFile(Path.GetFullPath(path), certificate, null, null));
        Assert.Contains(ScriptSignatureVerifier.BeginMarker, File.ReadAllText(path));
        return path;
    }

    [Fact]
    public void ASignerWithoutTheCodeSigningUsage_IsNotIntact()
    {
        var verify = ScriptSignatureVerifier.Verify(SignedBy(fixture.ServerAuthOnly, "serverauth"));
        Assert.False(verify.SignatureIntact);
        Assert.False(verify.Valid);
    }

    [Fact]
    public void ASelfSignedCodeSigningSigner_StaysIntactButUntrusted()
    {
        var verify = ScriptSignatureVerifier.Verify(SignedBy(fixture.Signer, "codesign"));
        Assert.True(verify.SignatureIntact);
        Assert.False(verify.TrustedOnThisHost);
    }

    [Theory]
    [InlineData(0x800B0109u, true)]   // CERT_E_UNTRUSTEDROOT
    [InlineData(0x800B010Au, true)]   // CERT_E_CHAINING
    [InlineData(0x800B0110u, false)]  // CERT_E_WRONG_USAGE
    [InlineData(0x800B0101u, false)]  // CERT_E_EXPIRED
    [InlineData(0x80096004u, false)]  // TRUST_E_CERT_SIGNATURE
    [InlineData(0x800B0100u, false)]  // TRUST_E_NOSIGNATURE
    [InlineData(0x80004005u, false)]
    public void OnlyUntrustedRootAndChainingResults_QualifyForTheTrustIgnoringCheck(uint error, bool qualifies) =>
        Assert.Equal(qualifies, ScriptSignatureVerifier.IsUntrustedChainError(error));

    [Fact]
    public void TheTrustIgnoringChain_RequiresTheCodeSigningPolicy()
    {
        Assert.True(ScriptSignatureVerifier.ChainIgnoringTrust(fixture.Signer));
        Assert.False(ScriptSignatureVerifier.ChainIgnoringTrust(fixture.ServerAuthOnly));
    }

    private static string RunStoredSignatureCheck(string path)
    {
        var command = MecmScriptSigningPs.ReadBackHelpers() + "\r\n" +
                      "try { Test-ApkgStoredScriptSignature -Bytes ([System.IO.File]::ReadAllBytes(" +
                      SigningCertificateFixture.PsLiteral(path) + ")) -Context 'fixture' | Out-Null; 'ACCEPTED' } catch { 'REFUSED' }";
        return SigningCertificateFixture.RunWindowsPowerShell(command).Last();
    }

    [Fact]
    public void TheConfigMgrReadBack_RefusesASignerWithoutTheCodeSigningUsage() =>
        Assert.Equal("REFUSED", RunStoredSignatureCheck(SignedBy(fixture.ServerAuthOnly, "ps-serverauth")));

    [Fact]
    public void TheConfigMgrReadBack_AcceptsAnUntrustedCodeSigningSigner() =>
        Assert.Equal("ACCEPTED", RunStoredSignatureCheck(SignedBy(fixture.Signer, "ps-codesign")));
}
