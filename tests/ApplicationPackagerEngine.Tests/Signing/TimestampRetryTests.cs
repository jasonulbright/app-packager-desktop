using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>
/// A signing that fails because the timestamp server did not answer is tried again a bounded number of
/// times before the failure is reported (or thrown when a timestamp is required).
/// </summary>
[Collection(SigningCollection.Name)]
public sealed class TimestampRetryTests(SigningCertificateFixture fixture)
{
    private const string UnreachableServer = "http://127.0.0.1:9/";

    private string NewScript()
    {
        var path = Path.Combine(fixture.NewDirectory("retry"), "detect.ps1");
        File.WriteAllText(path, "Write-Output 'Detected'\r\nexit 0", new ASCIIEncoding());
        return path;
    }

    private static (ScriptSigningService Service, Func<int> Calls) Counting(SigningCertificateFixture f)
    {
        var calls = 0;
        uint Signer(string p, X509Certificate2 c, string h, string? t) { calls++; return ScriptSigningService.SignFile(p, c, h, t); }
        return (new ScriptSigningService(null, Signer, TimeSpan.Zero), () => calls);
    }

    [Fact]
    public void AnUnreachableTimestampServerIsTriedAgainTwiceBeforeTheSigningIsReportedFailed()
    {
        var (service, calls) = Counting(fixture);
        var path = NewScript();

        var result = service.SignForCategory(path, fixture.Policy(sd: true, timestamp: UnreachableServer), SigningCategory.Detection);

        Assert.Equal(3, calls());
        Assert.Equal(SigningStatus.Failed, result.Status);
        Assert.Contains("0x80072EFD", result.Reason);
    }

    [Fact]
    public void ARequiredTimestampStillThrowsAfterTheRetries()
    {
        var (service, calls) = Counting(fixture);
        var path = NewScript();

        Assert.Throws<InvalidOperationException>(() =>
            service.SignForCategory(path, fixture.Policy(sd: true, timestamp: UnreachableServer, timestampRequired: true), SigningCategory.Detection));

        Assert.Equal(3, calls());
    }

    [Fact]
    public void NoTimestampServerMeansOneAttempt()
    {
        var (service, calls) = Counting(fixture);
        var path = NewScript();

        var result = service.SignForCategory(path, fixture.Policy(sd: true), SigningCategory.Detection);

        Assert.Equal(1, calls());
        Assert.Equal(SigningStatus.SignedAndVerified, result.Status);
    }
}
