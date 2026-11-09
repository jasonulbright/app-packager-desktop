using System.Diagnostics;
using System.Net;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A cancelled Intune publish stops at once at each step that waits: the token request, the wait for the storage address,
/// the block upload and the wait for the commit. The tenant stand-in answers or blocks per step; a blocked request ends
/// only when the token it was sent with is cancelled.
/// </summary>
public sealed class IntunePublishCancellationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-intune-cancel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Answers each step like a tenant, except the step named <c>stuck</c>, which waits for the token of its request.</summary>
    private sealed class SteppedHandler(string stuck) : HttpMessageHandler
    {
        public bool PatchSent;
        public List<string> Steps { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();
            var step = Step(request, uri);
            lock (Steps) Steps.Add(step);
            if (step == "activate") PatchSent = true;
            if (step == stuck)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            return step switch
            {
                "token" => Json("""{"access_token":"fake-token","expires_in":3599}"""),
                "find" => Json("""{"value":[]}"""),
                "contentVersion" => Json("""{"id":"7"}"""),
                "file" => Json("""{"id":"file-1","uploadState":"azureStorageUriRequestPending"}"""),
                // The first read of the file asks for the storage address; the tenant gives it at once unless this step is the stuck one.
                "storagePoll" => Json("""{"id":"file-1","uploadState":"azureStorageUriRequestSuccess","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1"}"""),
                "block" or "blocklist" => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") },
                "commit" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") },
                "commitPoll" => Json("""{"id":"file-1","uploadState":"commitFilePending","azureStorageUri":"https://x.blob.core.windows.net/c/b?sv=1"}"""),
                _ => Json("""{"id":"app-100"}""")
            };
        }

        private bool _storageRead;

        private string Step(HttpRequestMessage request, string uri)
        {
            if (uri.Contains("/oauth2/v2.0/token")) return "token";
            if (uri.Contains("blob.core.windows.net")) return uri.Contains("comp=blocklist") ? "blocklist" : "block";
            if (uri.Contains("mobileApps?$filter=") || uri.Contains("mobileApps?%24filter=")) return "find";
            if (uri.EndsWith("/contentVersions") && request.Method == HttpMethod.Post) return "contentVersion";
            if (uri.EndsWith("/files") && request.Method == HttpMethod.Post) return "file";
            if (uri.EndsWith("/commit") && request.Method == HttpMethod.Post) return "commit";
            if (uri.Contains("/files/file-1") && request.Method == HttpMethod.Get)
            {
                // The first read is the wait for the storage address (before the upload); every later read is the wait for the commit.
                if (!_storageRead)
                {
                    _storageRead = true;
                    return "storagePoll";
                }
                return "commitPoll";
            }
            if (request.Method == HttpMethod.Patch) return "activate";
            return "other";
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }

    private static IntunePublishOptions Options() => new()
    {
        TenantId = "tenant-id", ClientId = "client-id", ClientSecret = "secret",
        PollInterval = TimeSpan.FromMilliseconds(20), CommitPollInterval = TimeSpan.FromMilliseconds(20), PollTimeoutSec = 120
    };

    private static StageManifest Manifest() => new()
    {
        AppName = "Contoso App", Publisher = "Contoso Ltd", SoftwareVersion = "1.2.3.4",
        Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Contoso", ExpectedValue = "1.2.3.4", Operator = "GreaterEquals" }
    };

    /// <summary>Runs a publish, cancels it 300 ms in, and returns how long the cancellation took to show.</summary>
    private async Task<(SteppedHandler Handler, TimeSpan Elapsed)> CancelledPublishAsync(string stuck)
    {
        var handler = new SteppedHandler(stuck);
        using var service = new IntunePublishService(handler);
        using var cancellation = new CancellationTokenSource();
        var package = IntuneWinFixture.Create(_dir);
        var clock = Stopwatch.StartNew();

        var publish = service.PublishAsync(package, Manifest(), Options(), ct: cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publish.WaitAsync(TimeSpan.FromSeconds(20)));

        return (handler, clock.Elapsed);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("storagePoll")]
    [InlineData("block")]
    [InlineData("commitPoll")]
    public async Task APublishCancelledWhileAStepWaitsEndsAtOnceAndActivatesNothing(string stuck)
    {
        var (handler, elapsed) = await CancelledPublishAsync(stuck);

        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"The cancellation took {elapsed.TotalSeconds:0.0} s at step '{stuck}'.");
        Assert.False(handler.PatchSent, "A cancelled publish must not activate the app.");

        // The publish reached the step that was stuck and went no further: the upload follows the storage address,
        // the commit follows the upload, and the wait for the commit follows the commit request.
        string[] seen;
        lock (handler.Steps) seen = handler.Steps.ToArray();
        Assert.Contains(stuck, seen);
        if (stuck is "token" or "storagePoll") Assert.DoesNotContain("block", seen);
        if (stuck is "token" or "storagePoll" or "block") Assert.DoesNotContain("commit", seen);
        if (stuck == "commitPoll")
        {
            Assert.Contains("block", seen);
            Assert.Contains("commit", seen);
            Assert.Equal(2, seen.Count(s => s is "storagePoll" or "commitPoll"));
        }
    }
}
