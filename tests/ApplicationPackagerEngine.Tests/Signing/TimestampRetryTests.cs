using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>
/// A signing that fails because the timestamp server did not answer is tried again a bounded number of
/// times before the failure is reported (or thrown when a timestamp is required). The signer is a
/// fake that answers with a fixed network error, so no test depends on the host's network.
/// </summary>
[Collection(SigningCollection.Name)]
public sealed class TimestampRetryTests(SigningCertificateFixture fixture)
{
    private const string Server = "http://timestamp.test/";
    private const uint CannotConnect = 0x80072EFD;

    private string NewScript()
    {
        var path = Path.Combine(fixture.NewDirectory("retry"), "detect.ps1");
        File.WriteAllText(path, "Write-Output 'Detected'\r\nexit 0", new ASCIIEncoding());
        return path;
    }

    /// <summary>A signer that fails with <paramref name="error"/> for the first <paramref name="failures"/> calls, then signs without a timestamp.</summary>
    private static (ScriptSigningService Service, Func<int> Calls) Signer(int failures, uint error = CannotConnect)
    {
        var calls = 0;
        uint Fake(string path, X509Certificate2 certificate, string hash, string? server) =>
            ++calls <= failures ? error : ScriptSigningService.SignFile(path, certificate, hash, null);
        return (new ScriptSigningService(null, Fake, TimeSpan.Zero), () => calls);
    }

    [Fact]
    public void ANetworkErrorIsTriedAgainTwiceBeforeTheSigningIsReportedFailed()
    {
        var (service, calls) = Signer(failures: int.MaxValue);

        var result = service.SignForCategory(NewScript(), fixture.Policy(sd: true, timestamp: Server), SigningCategory.Detection);

        Assert.Equal(3, calls());
        Assert.Equal(SigningStatus.Failed, result.Status);
        Assert.Contains("0x80072EFD", result.Reason);
    }

    [Fact]
    public void ASignatureWithoutATimestampIsSignedAgainUpToTheBoundThenAccepted()
    {
        // The fake signer signs without a timestamp after its first failure, as a signer does when the server name does not resolve.
        var (service, calls) = Signer(failures: 1);
        var path = NewScript();

        var result = service.SignForCategory(path, fixture.Policy(sd: true, timestamp: Server), SigningCategory.Detection);

        Assert.Equal(3, calls());
        Assert.Equal(SigningStatus.SignedAndVerified, result.Status);
        Assert.False(result.Timestamped);
        Assert.True(ScriptSignatureVerifier.Verify(path).SignatureIntact);
    }

    [Fact]
    public void AnUntimestampedSignatureIsKeptWhenLaterAttemptsFailWithNetworkErrors()
    {
        // Call 1 signs without a timestamp; calls 2 and 3 fail. The optional-timestamp result of call 1 must survive.
        var calls = 0;
        uint Fake(string path, X509Certificate2 certificate, string hash, string? server) =>
            ++calls == 1 ? ScriptSigningService.SignFile(path, certificate, hash, null) : CannotConnect;
        var service = new ScriptSigningService(null, Fake, TimeSpan.Zero);
        var path = NewScript();

        var result = service.SignForCategory(path, fixture.Policy(sd: true, timestamp: Server), SigningCategory.Detection);

        Assert.Equal(3, calls);
        Assert.Equal(SigningStatus.SignedAndVerified, result.Status);
        Assert.True(ScriptSignatureVerifier.Verify(path).SignatureIntact);
    }
    [Fact]
    public void ARequiredTimestampStillThrowsAfterTheRetries()
    {
        var (service, calls) = Signer(failures: int.MaxValue);

        Assert.Throws<InvalidOperationException>(() =>
            service.SignForCategory(NewScript(), fixture.Policy(sd: true, timestamp: Server, timestampRequired: true), SigningCategory.Detection));

        Assert.Equal(3, calls());
    }

    [Fact]
    public void AnErrorThatIsNotANetworkErrorIsNotTriedAgain()
    {
        var (service, calls) = Signer(failures: int.MaxValue, error: 0x80090010);

        var result = service.SignForCategory(NewScript(), fixture.Policy(sd: true, timestamp: Server), SigningCategory.Detection);

        Assert.Equal(1, calls());
        Assert.Equal(SigningStatus.Failed, result.Status);
    }

    [Fact]
    public void NoTimestampServerMeansOneAttempt()
    {
        var (service, calls) = Signer(failures: int.MaxValue);

        var result = service.SignForCategory(NewScript(), fixture.Policy(sd: true), SigningCategory.Detection);

        Assert.Equal(1, calls());
        Assert.Equal(SigningStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData(0x80072EDFu, false)]
    [InlineData(0x80072EE0u, true)]
    [InlineData(0x80072EE7u, true)]
    [InlineData(0x80072EFDu, true)]
    [InlineData(0x80072F8Fu, true)]
    [InlineData(0x80072F90u, false)]
    public void OnlyTheNetworkErrorRangeIsRetried(uint error, bool retried) =>
        Assert.Equal(retried, ScriptSigningService.IsNetworkError(error));
}
