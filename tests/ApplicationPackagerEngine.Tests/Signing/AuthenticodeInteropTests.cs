using System.Text;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>
/// The signer and verifier against Windows PowerShell 5.1's own
/// Set-AuthenticodeSignature and Get-AuthenticodeSignature, in both directions.
/// </summary>
[Collection(SigningCollection.Name)]
public sealed class AuthenticodeInteropTests(SigningCertificateFixture fixture)
{
    private const string Body = "Write-Output 'Detected'\r\nexit 0";

    private string Sign(string path)
    {
        Assert.Equal(0u, ScriptSigningService.SignFile(path, fixture.Signer, "SHA256", null));
        return path;
    }

    private static (string Thumbprint, string Status) PowerShellReads(string path)
    {
        var lines = SigningCertificateFixture.RunWindowsPowerShell(
            $"$s = Get-AuthenticodeSignature -LiteralPath {SigningCertificateFixture.PsLiteral(path)}; " +
            "if ($s.SignerCertificate) { $s.SignerCertificate.Thumbprint } else { 'none' }; [string]$s.Status");
        return (lines[^2].Trim(), lines[^1].Trim());
    }

    [Fact]
    public void ScriptSignedHere_IsReadByWindowsPowerShellAsSignedByTheTestCertificate()
    {
        var path = Path.Combine(fixture.NewDirectory("proof-a"), "detect.ps1");
        File.WriteAllText(path, Body, new ASCIIEncoding());
        Sign(path);

        Assert.Contains(ScriptSignatureVerifier.BeginMarker, File.ReadAllText(path));
        var (thumbprint, status) = PowerShellReads(path);
        Assert.Equal(fixture.Thumbprint, thumbprint, ignoreCase: true);
        // The fixture signer is in no Root store, so the chain is untrusted and the
        // hash matches: UnknownError, never HashMismatch or NotSigned.
        Assert.Equal("UnknownError", status);
    }

    [Fact]
    public void ScriptSignedByWindowsPowerShell_VerifiesAsIntactHere_AndMatchesThisSignerByteForByte()
    {
        var dir = fixture.NewDirectory("proof-b");
        var byPowerShell = Path.Combine(dir, "by-powershell.ps1");
        var byEngine = Path.Combine(dir, "by-engine.ps1");
        File.WriteAllText(byPowerShell, Body, new ASCIIEncoding());
        File.WriteAllText(byEngine, Body, new ASCIIEncoding());

        SigningCertificateFixture.RunWindowsPowerShell(
            $"$c = Get-Item -LiteralPath ('Cert:\\CurrentUser\\My\\' + {SigningCertificateFixture.PsLiteral(fixture.Thumbprint)}); " +
            $"$r = Set-AuthenticodeSignature -LiteralPath {SigningCertificateFixture.PsLiteral(byPowerShell)} -Certificate $c -HashAlgorithm SHA256; [string]$r.Status");
        Sign(byEngine);

        var verify = ScriptSignatureVerifier.Verify(byPowerShell);
        Assert.True(verify.SignatureIntact, verify.Status + " " + verify.Reason);
        Assert.Equal(fixture.Thumbprint, verify.Thumbprint);
        Assert.Contains(verify.Status, new[] { SignatureStatusName.Valid, SignatureStatusName.UnknownError });
        Assert.Equal(File.ReadAllBytes(byPowerShell), File.ReadAllBytes(byEngine));
    }

    [Fact]
    public void OneChangedByte_ReportsHashMismatch_FromAFileAndFromBytes()
    {
        var path = Sign(WriteScript(fixture.NewDirectory("proof-c"), "tamper.ps1", Body, new ASCIIEncoding()));
        Assert.True(ScriptSignatureVerifier.Verify(path).SignatureIntact);

        var bytes = File.ReadAllBytes(path);
        bytes[0] = (byte)(bytes[0] ^ 0x20);
        Assert.Equal(SignatureStatusName.HashMismatch, ScriptSignatureVerifier.VerifyBytes(bytes).Status);
        File.WriteAllBytes(path, bytes);

        var verify = ScriptSignatureVerifier.Verify(path);
        Assert.Equal(SignatureStatusName.HashMismatch, verify.Status);
        Assert.False(verify.SignatureIntact);
        Assert.Equal("HashMismatch", PowerShellReads(path).Status);
    }

    public static TheoryData<string> Encodings => ["ASCII", "UTF8BOM", "UTF8NoBOM", "UTF16LE"];

    private static Encoding EncodingFor(string name) => name switch
    {
        "ASCII" => new ASCIIEncoding(),
        "UTF8BOM" => new UTF8Encoding(true),
        "UTF8NoBOM" => new UTF8Encoding(false),
        _ => new UnicodeEncoding(false, true)
    };

    private static string BodyFor(string name) => name == "ASCII" ? Body : "Write-Output 'Detected café'\r\nexit 0";

    private static string WriteScript(string dir, string name, string body, Encoding encoding)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, body, encoding);
        return path;
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void EachEncoding_SignsAndVerifies_WithTheContentBytesUnchanged(string encodingName)
    {
        var path = WriteScript(fixture.NewDirectory("proof-d"), encodingName + ".ps1", BodyFor(encodingName), EncodingFor(encodingName));
        var original = File.ReadAllBytes(path);
        Sign(path);
        var signed = File.ReadAllBytes(path);

        Assert.True(signed.Length > original.Length);
        Assert.Equal(original, signed[..original.Length]);
        Assert.True(ScriptSignatureVerifier.Verify(path).SignatureIntact);
        Assert.Equal(encodingName, ScriptSignatureVerifier.Represent(path).Encoding);
        var (thumbprint, status) = PowerShellReads(path);
        Assert.Equal(fixture.Thumbprint, thumbprint, ignoreCase: true);
        Assert.Equal("UnknownError", status);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void EachEncoding_SurvivesByteFileAndSameEncodingTextTransport(string encodingName)
    {
        var dir = fixture.NewDirectory("transport");
        var path = Sign(WriteScript(dir, encodingName + ".ps1", BodyFor(encodingName), EncodingFor(encodingName)));
        var representation = ScriptSignatureVerifier.Represent(path);

        Assert.True(ScriptSignatureVerifier.VerifyBytes(Convert.FromBase64String(representation.Base64)).SignatureIntact);
        var copy = Path.Combine(dir, "copy-" + encodingName + ".ps1");
        File.Copy(path, copy);
        Assert.True(ScriptSignatureVerifier.Verify(copy).SignatureIntact);

        var text = Path.Combine(dir, "text-" + encodingName + ".ps1");
        File.WriteAllText(text, representation.Text, ScriptSignatureVerifier.DetectEncoding(representation.Bytes).Encoding);
        Assert.Equal(representation.Bytes, File.ReadAllBytes(text));
        Assert.True(ScriptSignatureVerifier.Verify(text).SignatureIntact);
    }

    [Theory]
    [InlineData("UTF8BOM")]
    [InlineData("UTF16LE")]
    public void TextTransportForcingUtf8WithoutBom_BreaksTheSignature(string encodingName)
    {
        var dir = fixture.NewDirectory("reencode");
        var path = Sign(WriteScript(dir, encodingName + ".ps1", BodyFor(encodingName), EncodingFor(encodingName)));
        var reencoded = Path.Combine(dir, "reencoded.ps1");
        File.WriteAllText(reencoded, ScriptSignatureVerifier.Represent(path).Text, new UTF8Encoding(false));

        Assert.NotEqual(File.ReadAllBytes(path), File.ReadAllBytes(reencoded));
        Assert.Equal(SignatureStatusName.HashMismatch, ScriptSignatureVerifier.Verify(reencoded).Status);
    }
}
