using System.Text;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>
/// A signing attempt that leaves another certificate's intact signature in
/// place is a failure: SignedAndVerified names the certificate the policy
/// resolved, never whichever signature the file already carried.
/// </summary>
[Collection(SigningCollection.Name)]
public sealed class SignerResultTests(SigningCertificateFixture fixture)
{
    private readonly ScriptSigningService _service = new();

    private string SignedByFirstCertificate_ReadOnly()
    {
        var path = Path.Combine(fixture.NewDirectory("readonly"), "detect.ps1");
        File.WriteAllText(path, "Write-Output 'Detected'\r\nexit 0", new ASCIIEncoding());
        Assert.Equal(0u, ScriptSigningService.SignFile(path, fixture.Signer, "SHA256", null));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    private static void ClearReadOnly(string path) => File.SetAttributes(path, FileAttributes.Normal);

    [Fact]
    public void AReadOnlyFileSignedByAnotherCertificate_IsFailed_NotSignedAndVerified()
    {
        var path = SignedByFirstCertificate_ReadOnly();
        try
        {
            var result = _service.SignForCategory(path, fixture.Policy(sd: true, thumbprint: fixture.SecondThumbprint),
                SigningCategory.Detection);
            Assert.Equal(SigningStatus.Failed, result.Status);
            Assert.Equal(fixture.Thumbprint, ScriptSignatureVerifier.Verify(path).Thumbprint);
        }
        finally { ClearReadOnly(path); }
    }

    [Fact]
    public void AReadOnlyFileSignedByAnotherCertificate_ThrowsWhenATimestampIsRequired()
    {
        var path = SignedByFirstCertificate_ReadOnly();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => _service.SignForCategory(path,
                fixture.Policy(sd: true, thumbprint: fixture.SecondThumbprint, timestampRequired: true), SigningCategory.Detection));
            Assert.StartsWith($"Signing Detection script '{path}' failed and a timestamp is required: ", ex.Message);
        }
        finally { ClearReadOnly(path); }
    }

    [Fact]
    public void AnIntactSignatureFromAnotherCertificate_IsNotSignedAndVerified()
    {
        var path = Path.Combine(fixture.NewDirectory("other"), "detect.ps1");
        File.WriteAllText(path, "Write-Output 'Detected'\r\nexit 0", new ASCIIEncoding());
        ScriptSigningService.SignFile(path, fixture.Signer, "SHA256", null);
        var verify = ScriptSignatureVerifier.Verify(path);
        Assert.True(verify.SignatureIntact);

        Assert.Equal(SigningStatus.Failed, ScriptSigningService.Classify(verify, fixture.SecondThumbprint).Status);
        Assert.Equal(SigningStatus.SignedAndVerified, ScriptSigningService.Classify(verify, fixture.Thumbprint).Status);
    }

    [Fact]
    public void ReSigningWithTheSecondCertificate_RecordsTheSecondThumbprint()
    {
        var path = Path.Combine(fixture.NewDirectory("resign"), "detect.ps1");
        File.WriteAllText(path, "Write-Output 'Detected'\r\nexit 0", new ASCIIEncoding());
        ScriptSigningService.SignFile(path, fixture.Signer, "SHA256", null);
        var result = _service.SignForCategory(path, fixture.Policy(sd: true, thumbprint: fixture.SecondThumbprint),
            SigningCategory.Detection);
        Assert.Equal(SigningStatus.SignedAndVerified, result.Status);
        Assert.Equal(fixture.SecondThumbprint, result.Thumbprint);
    }
}
