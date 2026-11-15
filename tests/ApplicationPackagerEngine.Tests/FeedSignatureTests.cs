using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Step 2 of the import of vendor definitions from a URL: the document signed by a pinned publisher (detached CMS, RFC 3161 time stamp).</summary>
public sealed class FeedSignatureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Document = Encoding.UTF8.GetBytes("""{"FeedFormat":1}""");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-vendor-sig-" + Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 _vendor = FeedSigningFixtures.NewCertificate("CN=Contoso Software, O=Contoso, C=US", FeedSigningFixtures.CodeSigning, Now.AddYears(-1), Now.AddYears(1));
    private readonly X509Certificate2 _other = FeedSigningFixtures.NewCertificate("CN=Other Vendor", FeedSigningFixtures.CodeSigning, Now.AddYears(-1), Now.AddYears(1));
    private readonly X509Certificate2 _noUsage = FeedSigningFixtures.NewCertificate("CN=No Code Signing", FeedSigningFixtures.TimeStamping, Now.AddYears(-1), Now.AddYears(1));
    private readonly X509Certificate2 _expired = FeedSigningFixtures.NewCertificate("CN=Contoso Software, O=Contoso, C=US", FeedSigningFixtures.CodeSigning, Now.AddYears(-3), Now.AddYears(-2));
    private readonly X509Certificate2 _untrusted = FeedSigningFixtures.NewCertificate("CN=Untrusted Vendor", FeedSigningFixtures.CodeSigning, Now.AddYears(-1), Now.AddYears(1));
    private readonly X509Certificate2 _authority = FeedSigningFixtures.NewCertificate("CN=Test Time Stamping Authority", FeedSigningFixtures.TimeStamping, Now.AddYears(-5), Now.AddYears(5));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static FeedSignatureVerifier Verifier(params X509Certificate2[] trusted) => new(policy =>
    {
        // The roots of these tests are the test certificates themselves; the revocation of a certificate that nobody published is not asked.
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.Clear();
        foreach (var certificate in trusted) policy.CustomTrustStore.Add(certificate);
    });

    private FeedSignatureVerifier Trusting() => Verifier(_vendor, _other, _noUsage, _expired, _authority);

    private static PublisherPin PinOf(X509Certificate2 certificate)
    {
        Assert.True(PublisherPin.TryParse(certificate.Subject + " | " + certificate.Issuer, out var pin, out var error), error);
        return pin;
    }

    // ---- the pin ----

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("CN=A | ")]
    [InlineData("CN=A | CN=B | CN=C")]
    [InlineData("key:123")]
    [InlineData("key:ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public void APinThatIsNotASubjectAndAnIssuerOrAKeyHashIsRefused(string text)
    {
        Assert.False(PublisherPin.TryParse(text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ThePinIsTheSubjectAndTheIssuerOrTheKeyAndNotTheThumbprint()
    {
        Assert.True(PublisherPin.TryParse("CN=Contoso Software, O=Contoso, C=US | CN=Contoso CA", out var byName, out _));
        Assert.Equal("CN=Contoso Software, O=Contoso, C=US", byName.Subject);
        var hash = new string('a', 64);
        Assert.True(PublisherPin.TryParse("key:" + hash, out var byKey, out _));
        Assert.Equal(hash.ToUpperInvariant(), byKey.PublicKeySha256);
    }

    // ---- the signature ----

    [Fact]
    public void ASignatureOfTheExactBytesByThePinnedPublisherIsValid()
    {
        var result = Trusting().Verify(Document, FeedSigningFixtures.Sign(Document, _vendor), PinOf(_vendor), Now);

        Assert.True(result.Valid, result.Error);
        Assert.Contains("Contoso Software", result.SignerText);
    }

    [Fact]
    public void TheKeyOfTheCertificateCanBeThePin()
    {
        var hash = Convert.ToHexString(SHA256.HashData(_vendor.PublicKey.ExportSubjectPublicKeyInfo()));
        Assert.True(PublisherPin.TryParse("key:" + hash, out var pin, out _));

        Assert.True(Trusting().Verify(Document, FeedSigningFixtures.Sign(Document, _vendor), pin, Now).Valid);
    }

    [Fact]
    public void AChangedByteInTheDocumentIsRefused()
    {
        var signature = FeedSigningFixtures.Sign(Document, _vendor);
        var changed = Encoding.UTF8.GetBytes("""{"FeedFormat":2}""");

        var result = Trusting().Verify(changed, signature, PinOf(_vendor), Now);

        Assert.False(result.Valid);
        Assert.Contains("does not match the bytes", result.Error);
    }

    [Fact]
    public void ASignerThatIsNotThePinnedPublisherIsRefusedAndNamed()
    {
        var result = Trusting().Verify(Document, FeedSigningFixtures.Sign(Document, _other), PinOf(_vendor), Now);

        Assert.False(result.Valid);
        Assert.Contains("Other Vendor", result.Error);
        Assert.Contains("pinned publisher", result.Error);
    }

    [Fact]
    public void ACertificateWithoutTheCodeSigningUsageIsRefused()
    {
        var result = Trusting().Verify(Document, FeedSigningFixtures.Sign(Document, _noUsage), PinOf(_noUsage), Now);

        Assert.False(result.Valid);
        Assert.Contains("code-signing usage", result.Error);
    }

    [Fact]
    public void ACertificateThatDoesNotChainToATrustedRootIsRefused()
    {
        var result = Trusting().Verify(Document, FeedSigningFixtures.Sign(Document, _untrusted), PinOf(_untrusted), Now);

        Assert.False(result.Valid);
        Assert.Contains("not trusted", result.Error);
    }

    [Fact]
    public void BytesThatAreNotASignatureAreRefused()
    {
        var result = Trusting().Verify(Document, [1, 2, 3], PinOf(_vendor), Now);

        Assert.False(result.Valid);
        Assert.Contains("detached CMS", result.Error);
    }

    [Fact]
    public void AnExpiredCertificateWithoutATimeStampIsRefused()
    {
        var result = Trusting().Verify(Document, FeedSigningFixtures.Sign(Document, _expired), PinOf(_expired), Now);

        Assert.False(result.Valid);
        Assert.Contains("no time stamp", result.Error);
    }

    [Fact]
    public void AnExpiredCertificateIsAcceptedWithACheckedTimeStampInsideItsLife()
    {
        var signature = FeedSigningFixtures.Sign(Document, _expired, s => FeedSigningFixtures.AddTimeStamp(s, _authority, Now.AddYears(-2).AddDays(-30)));

        Assert.True(Trusting().Verify(Document, signature, PinOf(_expired), Now).Valid);
    }

    [Fact]
    public void ATimeStampAfterTheCertificateExpiredDoesNotSaveIt()
    {
        var signature = FeedSigningFixtures.Sign(Document, _expired, s => FeedSigningFixtures.AddTimeStamp(s, _authority, Now.AddDays(-1)));

        Assert.False(Trusting().Verify(Document, signature, PinOf(_expired), Now).Valid);
    }

    [Fact]
    public void ATimeStampOfAnotherSignatureIsIgnored()
    {
        var signature = FeedSigningFixtures.Sign(Document, _expired, s => s.AddUnsignedAttribute(new AsnEncodedData(
            new Oid("1.2.840.113549.1.9.16.2.14"), FeedSigningFixtures.Token([9, 9, 9], _authority, Now.AddYears(-2).AddDays(-30)))));

        var result = Trusting().Verify(Document, signature, PinOf(_expired), Now);

        Assert.False(result.Valid);
        Assert.Contains("does not belong to this signature", result.Error);
    }

    [Fact]
    public void ATimeStampFromAnAuthorityThatIsNotTrustedIsIgnored()
    {
        var signature = FeedSigningFixtures.Sign(Document, _expired, s => FeedSigningFixtures.AddTimeStamp(s, _authority, Now.AddYears(-2).AddDays(-30)));

        var result = Verifier(_vendor, _expired).Verify(Document, signature, PinOf(_expired), Now);

        Assert.False(result.Valid);
        Assert.Contains("time stamping authority is not trusted", result.Error);
    }

    [Fact]
    public void ATimeStampFromAnAuthorityWithoutTheTimeStampingUsageIsIgnored()
    {
        // _other has the code-signing usage only; it cannot make a time stamp.
        var signature = FeedSigningFixtures.Sign(Document, _expired, s => FeedSigningFixtures.AddTimeStamp(s, _other, Now.AddYears(-2).AddDays(-30)));

        var result = Trusting().Verify(Document, signature, PinOf(_expired), Now);

        Assert.False(result.Valid);
        Assert.Contains("RFC 3161", result.Error);
    }

    // ---- the fetch of the signature file ----

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Ok(byte[] bytes, string type) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(type) } } };

    private static FeedFetcher Fetcher(Handler handler) => new(() => handler, null, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task TheSignatureFileIsTheFinalAddressWithP7sAddedAndWithoutItsQuery()
    {
        var handler = new Handler(_ => Ok([1, 2, 3], "application/pkcs7-signature"));

        var signature = await Fetcher(handler).FetchSignatureAsync(new Uri("https://vendor.example/feeds/feed.json?v=SECRET"), CancellationToken.None);

        Assert.Equal(new Uri("https://vendor.example/feeds/feed.json.p7s"), Assert.Single(handler.Requests));
        Assert.Equal([1, 2, 3], signature.Bytes);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    public async Task APageOrAJsonDocumentIsNotASignatureFile(string type)
    {
        var handler = new Handler(_ => Ok([1], type));

        await Assert.ThrowsAsync<FeedFetchException>(() =>
            Fetcher(handler).FetchSignatureAsync(new Uri("https://vendor.example/feed.json"), CancellationToken.None));
    }

    [Fact]
    public async Task ASignatureFileOverItsLimitIsRefused()
    {
        var handler = new Handler(_ => Ok(new byte[FeedFetcher.MaxSignatureBytes + 1], "application/octet-stream"));

        var error = await Assert.ThrowsAsync<FeedFetchException>(() =>
            Fetcher(handler).FetchSignatureAsync(new Uri("https://vendor.example/feed.json"), CancellationToken.None));

        Assert.Contains("limit", error.Message);
    }

    // ---- the import ----

    private static string DefinitionJson() => System.Text.Json.JsonSerializer.Serialize(new AppDefinition
    {
        Id = "contoso-agent", Vendor = "Contoso", Application = "Contoso Agent", CMName = "Contoso Agent", Architecture = "x64",
        VendorUrl = "https://contoso.example/", InstallerType = "MSI", DownloadUrl = "https://contoso.example/agent-{version}.msi",
        InstallArgs = "/qn", Detection = new DetectionConfig { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\Contoso" }
    });

    private VendorFeedImporter Importer(byte[] feed, Func<byte[]?> signature, FeedSignatureVerifier verifier, Handler? handlerOut = null)
    {
        var handler = handlerOut ?? new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".p7s", StringComparison.Ordinal))
                return signature() is { } bytes ? Ok(bytes, "application/pkcs7-signature") : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Ok(feed, "application/json");
        });
        return new VendorFeedImporter(
            Fetcher(handler), new ImportedDefinitionStore(_root), () => new HashSet<string>(), () => new HashSet<string>(),
            new FixedClock(Now), signatureVerifier: verifier);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static byte[] FeedBytes() =>
        Encoding.UTF8.GetBytes($$"""{"FeedFormat":1,"Vendor":"Contoso Software","FeedId":"contoso-software","Definitions":[{{DefinitionJson()}}]}""");

    private static readonly Uri Address = new("https://vendor.example/feed.json");

    [Fact]
    public async Task ADocumentSignedByThePinnedPublisherHasTheStateSignedByPinnedPublisher()
    {
        var feed = FeedBytes();
        var importer = Importer(feed, () => FeedSigningFixtures.Sign(feed, _vendor), Trusting());

        var preview = await importer.PreviewAsync(Address, null, CancellationToken.None, PinOf(_vendor).Text);

        Assert.False(preview.Refused, preview.Refusal);
        Assert.Equal(FeedTrust.SignedByPinnedPublisher, preview.TrustState);
        Assert.Contains("Contoso Software", preview.Signer);
        // A signed document needs no confirmation of the state "not verified".
        var source = importer.Import(preview, new ImportConfirmation("Contoso Software", "contoso-software", false, new HashSet<string>()));
        Assert.Equal(FeedTrust.SignedByPinnedPublisher, source.TrustState);
        Assert.Equal(PinOf(_vendor).Text, source.PinnedPublisher);
        Assert.Contains("Contoso Software", source.Signer);
    }

    [Fact]
    public async Task ASignatureByAnotherPublisherStopsTheImportWithoutFallingBack()
    {
        var feed = FeedBytes();
        var importer = Importer(feed, () => FeedSigningFixtures.Sign(feed, _other), Trusting());

        var preview = await importer.PreviewAsync(Address, null, CancellationToken.None, PinOf(_vendor).Text);

        Assert.True(preview.Refused);
        Assert.Contains("does not fall back", preview.Refusal);
        Assert.Throws<InvalidOperationException>(() =>
            importer.Import(preview, new ImportConfirmation("Contoso Software", "contoso-software", true, new HashSet<string>())));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task AMissingSignatureFileStopsTheImport()
    {
        var feed = FeedBytes();
        var importer = Importer(feed, () => null, Trusting());

        var preview = await importer.PreviewAsync(Address, null, CancellationToken.None, PinOf(_vendor).Text);

        Assert.True(preview.Refused);
        Assert.Contains("signature file could not be fetched", preview.Refusal);
        Assert.Contains("does not fall back", preview.Refusal);
    }

    [Fact]
    public async Task ASignatureForOtherBytesStopsTheImport()
    {
        var feed = FeedBytes();
        var importer = Importer(feed, () => FeedSigningFixtures.Sign(Encoding.UTF8.GetBytes("{}"), _vendor), Trusting());

        var preview = await importer.PreviewAsync(Address, null, CancellationToken.None, PinOf(_vendor).Text);

        Assert.True(preview.Refused);
        Assert.Contains("does not match the bytes", preview.Refusal);
    }

    [Fact]
    public async Task APublisherPinThatCannotBeReadStopsTheImport()
    {
        var feed = FeedBytes();
        var handler = new Handler(_ => Ok(feed, "application/json"));
        var importer = Importer(feed, () => null, Trusting(), handler);

        var preview = await importer.PreviewAsync(Address, null, CancellationToken.None, "not a pin");

        Assert.True(preview.Refused);
        Assert.Contains("does not fall back", preview.Refusal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ADocumentWithoutAnyPinIsStillNotVerifiedAndAskedNoSignature()
    {
        var feed = FeedBytes();
        var handler = new Handler(_ => Ok(feed, "application/json"));
        var importer = Importer(feed, () => null, Trusting(), handler);

        var preview = await importer.PreviewAsync(Address, null, CancellationToken.None);

        Assert.Equal(FeedTrust.NotVerified, preview.TrustState);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ADigestPinAndAPublisherPinBothMustHold()
    {
        var feed = FeedBytes();
        var digest = Convert.ToHexString(SHA256.HashData(feed));
        var importer = Importer(feed, () => FeedSigningFixtures.Sign(feed, _vendor), Trusting());

        var both = await importer.PreviewAsync(Address, digest, CancellationToken.None, PinOf(_vendor).Text);
        var wrongDigest = await importer.PreviewAsync(Address, new string('0', 64), CancellationToken.None, PinOf(_vendor).Text);

        Assert.False(both.Refused);
        Assert.Equal(FeedTrust.SignedByPinnedPublisher, both.TrustState);
        var source = importer.Import(both, new ImportConfirmation("Contoso Software", "contoso-software", false, new HashSet<string>()));
        Assert.Equal(digest, source.PinnedDigest);
        Assert.True(wrongDigest.Refused);
    }
}
