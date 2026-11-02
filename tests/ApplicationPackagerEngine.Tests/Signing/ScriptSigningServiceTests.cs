using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

[Collection(SigningCollection.Name)]
public sealed class SigningCertificateServiceTests(SigningCertificateFixture fixture)
{
    private readonly SigningCertificateService _service = new();

    [Fact]
    public void ListsTheTestCertificateAsUsable_WithAnAccessibleKeyThatDoesNotPrompt()
    {
        var candidate = Assert.Single(_service.ListCandidates(), c => c.Thumbprint == fixture.Thumbprint);
        Assert.True(candidate.HasPrivateKey);
        Assert.True(candidate.KeyAccessible);
        Assert.True(candidate.Usable);
        Assert.False(candidate.MayPrompt);
        Assert.Equal("CurrentUser", candidate.StoreLocation);
    }

    [Fact]
    public void MarksAnExpiredCertificateUnusable_WithADatedReason()
    {
        var candidate = Assert.Single(_service.ListCandidates(), c => c.Thumbprint == fixture.ExpiredThumbprint);
        Assert.False(candidate.Usable);
        Assert.Matches(@"expired on \d{4}-\d{2}-\d{2}\.", candidate.Reason);
    }

    [Fact]
    public void MarksAPublicOnlyCertificateUnusable()
    {
        var candidate = Assert.Single(_service.ListCandidates(), c => c.Thumbprint == fixture.PublicOnlyThumbprint);
        Assert.False(candidate.HasPrivateKey);
        Assert.False(candidate.Usable);
    }

    [Fact]
    public void ListsTheLocalMachineStoreWithoutThrowing() =>
        Assert.All(_service.ListCandidates("LocalMachine"), c => Assert.Equal("LocalMachine", c.StoreLocation));

    [Fact]
    public void ResolvesTheConfiguredThumbprint() =>
        Assert.Equal(fixture.Thumbprint, _service.Resolve(fixture.Policy()).Thumbprint.ToUpperInvariant());

    [Theory]
    [InlineData("", "SigningCertificateUnavailable: no certificate selected. Choose a code-signing certificate thumbprint in Settings.")]
    [InlineData("0000000000000000000000000000000000000000", @"SigningCertificateUnavailable: thumbprint 0000000000000000000000000000000000000000 was not found in Cert:\CurrentUser\My.")]
    public void RefusesAMissingSelection(string thumbprint, string message)
    {
        var ex = Assert.Throws<SigningCertificateUnavailableException>(() => _service.Resolve(fixture.Policy(thumbprint: thumbprint)));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void RefusesAnExpiredCertificate()
    {
        var ex = Assert.Throws<SigningCertificateUnavailableException>(() => _service.Resolve(fixture.Policy(thumbprint: fixture.ExpiredThumbprint)));
        Assert.Matches(@"^SigningCertificateUnavailable: certificate [0-9A-F]{40} expired on \d{4}-\d{2}-\d{2}\. Select a renewed certificate\.$", ex.Message);
    }

    [Fact]
    public void RefusesACertificateWithoutAPrivateKey()
    {
        var ex = Assert.Throws<SigningCertificateUnavailableException>(() => _service.Resolve(fixture.Policy(thumbprint: fixture.PublicOnlyThumbprint)));
        Assert.Equal($@"SigningCertificateUnavailable: certificate {fixture.PublicOnlyThumbprint} has no associated private key in Cert:\CurrentUser\My.", ex.Message);
    }

    [Fact]
    public void RefusesAnAmbiguousConfiguration()
    {
        var duplicated = new SigningCertificateService(_ => [fixture.Signer, fixture.Signer]);
        var ex = Assert.Throws<SigningCertificateUnavailableException>(() => duplicated.Resolve(fixture.Policy()));
        Assert.Equal($@"SigningCertificateUnavailable: ambiguous configuration, thumbprint {fixture.Thumbprint} resolves to 2 entries in Cert:\CurrentUser\My.", ex.Message);
    }

    [Fact]
    public void RefusesANotYetValidCertificate()
    {
        var early = new SigningCertificateService(now: () => fixture.Signer.NotBefore.AddDays(-1));
        var ex = Assert.Throws<SigningCertificateUnavailableException>(() => early.Resolve(fixture.Policy()));
        Assert.Contains("is not valid before", ex.Message);
    }

    [Fact]
    public void RefusesAnUnreadableStore()
    {
        var broken = new SigningCertificateService(_ => throw new CryptographicException("store locked"));
        var ex = Assert.Throws<SigningCertificateUnavailableException>(() => broken.Resolve(fixture.Policy()));
        Assert.Equal(@"SigningCertificateUnavailable: certificate store Cert:\CurrentUser\My could not be read (store locked).", ex.Message);
    }

    [Fact]
    public void NeverSelectsTheFirstCertificate_WhenTheThumbprintIsBlank()
    {
        Assert.NotEmpty(_service.ListCandidates());
        var ex = Assert.Throws<SigningCertificateUnavailableException>(() => _service.Resolve(new SigningPolicy()));
        Assert.Contains("no certificate selected", ex.Message);
    }

    [Fact]
    public void TheRunSnapshotGate_ResolvesThroughTheSigningService()
    {
        var gate = new ScriptSigningCertificateGate();
        var policy = System.Text.Json.Nodes.JsonNode.Parse($$"""{"RequireDetection":true,"CertificateThumbprint":"{{fixture.Thumbprint.ToLowerInvariant()}}"}""")!;
        var cert = Assert.IsType<X509Certificate2>(gate.ResolveCertificate(policy));
        Assert.Equal(fixture.Thumbprint, cert.Thumbprint);

        var missing = System.Text.Json.Nodes.JsonNode.Parse("""{"SignDeployment":true}""")!;
        Assert.Throws<SigningCertificateUnavailableException>(() => gate.ResolveCertificate(missing));
    }

    [Fact]
    public void ACertificateWithoutCodeSigningEku_IsNotACandidate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Server Auth Only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        Assert.False(SigningCertificateService.IsCodeSigning(cert));
        Assert.Empty(new SigningCertificateService(_ => [cert]).ListCandidates());
    }
}

[Collection(SigningCollection.Name)]
public sealed class ScriptSigningServiceTests(SigningCertificateFixture fixture)
{
    private readonly ScriptSigningService _service = new();

    private string NewScript(string body = "Write-Output 'Detected'\r\nexit 0")
    {
        var path = Path.Combine(fixture.NewDirectory("case"), "detect.ps1");
        File.WriteAllText(path, body, new ASCIIEncoding());
        return path;
    }

    [Fact]
    public void NotRequested_WhenNeitherSwitchIsSet()
    {
        var path = NewScript();
        var result = _service.SignForCategory(path, new SigningPolicy(), SigningCategory.Detection);
        Assert.Equal(SigningStatus.NotRequested, result.Status);
        Assert.Equal(64, result.Sha256.Length);
        Assert.Equal(SignatureStatusName.NotSigned, ScriptSignatureVerifier.Verify(path).Status);
    }

    [Fact]
    public void NotApplicable_ForAMissingFile() =>
        Assert.Equal(SigningStatus.NotApplicable,
            _service.SignForCategory(Path.Combine(fixture.Root, "absent.ps1"), fixture.Policy(sd: true), SigningCategory.Detection).Status);

    [Fact]
    public void RequireOnly_FailsAnUnsignedFile_WithTheRequiredMessage()
    {
        var result = _service.SignForCategory(NewScript(), new SigningPolicy { RequireRequirements = true }, SigningCategory.Requirements);
        Assert.Equal(SigningStatus.Failed, result.Status);
        Assert.StartsWith("A valid signature is required for Requirements but the file verifies as NotSigned. ", result.Reason);
    }

    [Fact]
    public void Sign_SignsAndRecordsTheSignerThumbprint()
    {
        var path = NewScript();
        var result = _service.SignForCategory(path, fixture.Policy(sd: true), SigningCategory.Detection);
        Assert.Equal(SigningStatus.SignedAndVerified, result.Status);
        Assert.Equal(fixture.Thumbprint, result.Thumbprint);
        Assert.Contains(ScriptSignatureVerifier.BeginMarker, File.ReadAllText(path));
        Assert.Equal(ScriptSignatureVerifier.FileSha256Hex(path), result.Sha256);
        Assert.Equal("Signature is intact; the signer chain is not trusted on this host, which says nothing about endpoint trust.", result.Reason);
    }

    [Fact]
    public void ActsOnlyOnTheRequestedCategory()
    {
        var path = NewScript();
        Assert.Equal(SigningStatus.NotRequested, _service.SignForCategory(path, fixture.Policy(sd: true), SigningCategory.Deployment).Status);
        Assert.DoesNotContain(ScriptSignatureVerifier.BeginMarker, File.ReadAllText(path));
    }

    [Fact]
    public void PreserveExisting_LeavesAValidThirdPartySignatureAlone()
    {
        var path = NewScript();
        _service.SignForCategory(path, fixture.Policy(sp: true), SigningCategory.Deployment);
        var before = File.ReadAllBytes(path);
        var again = _service.SignForCategory(path, fixture.Policy(sp: true), SigningCategory.Deployment, preserveExisting: true);
        Assert.Equal(SigningStatus.ExistingSignatureValid, again.Status);
        Assert.Equal("Existing valid signature preserved.", again.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RequireWithoutSign_AcceptsAnAlreadyValidSignature()
    {
        var path = NewScript();
        _service.SignForCategory(path, fixture.Policy(sd: true), SigningCategory.Detection);
        Assert.Equal(SigningStatus.ExistingSignatureValid,
            _service.SignForCategory(path, fixture.Policy(rd: true), SigningCategory.Detection).Status);
    }

    [Fact]
    public void TimestampRequired_WithoutATimestamp_Throws()
    {
        var path = NewScript();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.SignForCategory(path, fixture.Policy(sd: true, timestampRequired: true), SigningCategory.Detection));
        Assert.Equal($"TimestampRequired is set but the Detection script '{path}' carries no timestamp counter-signature (server '').", ex.Message);
    }

    [Fact]
    public void SignAlone_ASigningFailureIsRecordedNotThrown()
    {
        var policy = fixture.Policy(sd: true);
        policy.HashAlgorithm = "NOT-A-HASH";
        var result = _service.SignForCategory(NewScript(), policy, SigningCategory.Detection);
        Assert.Equal(SigningStatus.Failed, result.Status);
        Assert.Contains("NOT-A-HASH", result.Reason);
    }

    [Fact]
    public void ASigningFailure_ThrowsWhenATimestampIsRequired()
    {
        var policy = fixture.Policy(sd: true, timestamp: "ftp://not-http", timestampRequired: true);
        var path = NewScript();
        var ex = Assert.Throws<InvalidOperationException>(() => _service.SignForCategory(path, policy, SigningCategory.Detection));
        Assert.StartsWith($"Signing Detection script '{path}' failed and a timestamp is required: ", ex.Message);
    }

    [Fact]
    public void SignWithoutAResolvableCertificate_ThrowsCertificateUnavailable() =>
        Assert.Throws<SigningCertificateUnavailableException>(() =>
            _service.SignForCategory(NewScript(), fixture.Policy(sd: true, thumbprint: "A".PadRight(40, 'A')), SigningCategory.Detection));

    [Fact]
    public void TestConfiguration_SignsAndVerifiesATemporaryFileWithoutLeavingItBehind()
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "appackager-sign-*.ps1").Length;
        var result = _service.TestConfiguration(fixture.Policy(sd: true, sr: true, sp: true), TimeSpan.FromSeconds(120));
        Assert.True(result.Ok, result.Reason);
        Assert.True(result.SignatureIntact);
        Assert.Equal(fixture.Thumbprint, result.Thumbprint);
        Assert.False(result.TimestampVerified);
        Assert.False(result.TimedOut);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "appackager-sign-*.ps1").Length);
    }

    [Fact]
    public void TestConfiguration_ReportsACertificateProblemInsteadOfThrowing()
    {
        var result = _service.TestConfiguration(fixture.Policy(thumbprint: new string('0', 40)));
        Assert.False(result.Ok);
        Assert.Contains("was not found", result.Reason);
    }

    [Fact]
    public void TestConfiguration_ReportsATimeoutWithoutBlockingTheCaller()
    {
        var result = _service.TestConfiguration(fixture.Policy(), TimeSpan.FromTicks(1));
        Assert.True(result.TimedOut);
        Assert.False(result.Ok);
        Assert.StartsWith("Signing did not complete within 0 seconds", result.Reason);
    }

    [Fact]
    public void TestConfiguration_FailsARequiredTimestampThatWasNotProduced()
    {
        var result = _service.TestConfiguration(fixture.Policy(timestampRequired: true), TimeSpan.FromSeconds(120));
        Assert.False(result.Ok);
        Assert.Equal("A timestamp is required but no verifiable timestamp counter-signature was produced.", result.Reason);
    }
}

[Collection(SigningCollection.Name)]
public sealed class ScriptSignatureVerifierTests(SigningCertificateFixture fixture)
{
    [Fact]
    public void APlainScript_IsNotSigned()
    {
        var path = Path.Combine(fixture.NewDirectory("plain"), "plain.ps1");
        File.WriteAllText(path, "exit 0");
        var result = ScriptSignatureVerifier.Verify(path);
        Assert.False(result.Valid);
        Assert.False(result.SignatureIntact);
        Assert.Equal(SignatureStatusName.NotSigned, result.Status);
        Assert.False(result.Timestamped);
        Assert.False(result.TimestampVerified);
    }

    [Fact]
    public void AMissingPath_IsNotFound() =>
        Assert.Equal(SignatureStatusName.NotFound, ScriptSignatureVerifier.Verify(Path.Combine(fixture.Root, "nope.ps1")).Status);

    [Fact]
    public void AnIntactSignature_IsSeparatedFromHostTrust()
    {
        var path = Path.Combine(fixture.NewDirectory("intact"), "intact.ps1");
        File.WriteAllText(path, "exit 0");
        ScriptSigningService.SignFile(path, fixture.Signer, "SHA256", null);
        var result = ScriptSignatureVerifier.Verify(path);
        Assert.True(result.SignatureIntact);
        Assert.Equal(fixture.Thumbprint, result.Thumbprint);
        Assert.False(result.Timestamped);
        Assert.Equal(result.Valid, result.TrustedOnThisHost);
        Assert.False(result.TrustedOnThisHost);
        Assert.Equal(SignatureStatusName.UnknownError, result.Status);
    }

    [Fact]
    public void ByteVerification_LeavesNoTemporaryFileBehind()
    {
        var path = Path.Combine(fixture.NewDirectory("bytes"), "bytes.ps1");
        File.WriteAllText(path, "Write-Output 'Detected'\r\nexit 0");
        ScriptSigningService.SignFile(path, fixture.Signer, "SHA256", null);
        var before = Directory.GetFiles(Path.GetTempPath(), "appackager-sign-*.ps1").Length;
        var result = ScriptSignatureVerifier.VerifyBytes(File.ReadAllBytes(path));
        Assert.True(result.SignatureIntact);
        Assert.Equal(64, result.Sha256.Length);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "appackager-sign-*.ps1").Length);
    }

    [Fact]
    public void ASizeAboveTheConfigMgrLimit_IsFlagged()
    {
        var path = Path.Combine(fixture.NewDirectory("big"), "big.ps1");
        File.WriteAllText(path, "#" + new string('x', 40000), new ASCIIEncoding());
        var representation = ScriptSignatureVerifier.Represent(path);
        Assert.True(representation.ExceedsCMLimit);
        Assert.Equal(32768, representation.CMScriptMaxBytes);
    }

    /// <summary>
    /// A signature block whose PKCS#7 signer carries the RFC 3161 timestamp
    /// attribute; built locally, so no timestamp server is contacted.
    /// </summary>
    private static string SignatureBlockFixture(bool withTimestampAttribute)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Timestamp Fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        var cms = new SignedCms(new ContentInfo([1, 2, 3]));
        var signer = new CmsSigner(cert);
        if (withTimestampAttribute)
            signer.UnsignedAttributes.Add(new AsnEncodedData(new Oid(ScriptSignatureVerifier.Rfc3161TimestampOid), [0x30, 0x00]));
        cms.ComputeSignature(signer);
        var base64 = Convert.ToBase64String(cms.Encode());
        var lines = Enumerable.Range(0, (base64.Length + 63) / 64)
            .Select(i => "# " + base64.Substring(i * 64, Math.Min(64, base64.Length - i * 64)));
        return "exit 0\r\n" + ScriptSignatureVerifier.BeginMarker + "\r\n" + string.Join("\r\n", lines) + "\r\n" + ScriptSignatureVerifier.EndMarker + "\r\n";
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimestampPresence_IsReadFromTheSignatureBlockAttribute(bool withAttribute)
    {
        var path = Path.Combine(fixture.NewDirectory("timestamp"), "ts.ps1");
        File.WriteAllText(path, SignatureBlockFixture(withAttribute));
        Assert.Equal(withAttribute, ScriptSignatureVerifier.HasCounterSignature(path));
    }

    [Fact]
    public void AnUntimestampedSignature_HasNoCounterSignature()
    {
        var path = Path.Combine(fixture.NewDirectory("untimestamped"), "u.ps1");
        File.WriteAllText(path, "exit 0");
        ScriptSigningService.SignFile(path, fixture.Signer, "SHA256", null);
        Assert.False(ScriptSignatureVerifier.HasCounterSignature(path));
    }

    [Fact]
    public void ChainIgnoringTrust_AcceptsTheSelfSignedSigner_AndRejectsAnExpiredOne()
    {
        Assert.True(ScriptSignatureVerifier.ChainIgnoringTrust(fixture.Signer));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Expired Chain", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var expired = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-10), DateTimeOffset.Now.AddDays(-1));
        Assert.False(ScriptSignatureVerifier.ChainIgnoringTrust(expired));
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0x42 }, "ASCII")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }, "UTF8BOM")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, "UTF16LE")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }, "UTF16BE")]
    [InlineData(new byte[] { 0x63, 0xC3, 0xA9 }, "UTF8NoBOM")]
    [InlineData(new byte[] { 0x63, 0xE9, 0x41 }, "ANSI")]
    public void DetectsTheScriptEncoding(byte[] bytes, string expected) =>
        Assert.Equal(expected, ScriptSignatureVerifier.DetectEncoding(bytes).Name);
}
