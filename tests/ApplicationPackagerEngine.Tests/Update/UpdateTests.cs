using System.Net;
using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Services.Update;

namespace ApplicationPackagerEngine.Tests.Update;

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("v2026.1103.3", "2026.1103.3")]
    [InlineData("2026.1103.3", "2026.1103.3")]
    [InlineData(" v2026.1104.1 ", "2026.1104.1")]
    [InlineData("v1.2", "1.2")]
    [InlineData("v2026.1103.3-beta", null)]
    [InlineData("release-1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadsTheVersionOfATag(string? tag, string? expected) => Assert.Equal(expected, UpdateVersion.FromTag(tag));

    [Theory]
    [InlineData("2026.1103.3+99e096f46535b835f9bae265833809190a4c21bb", "2026.1103.3")]
    [InlineData("2026.1103.3", "2026.1103.3")]
    [InlineData("1.0.0-preview.1+abc", "1.0.0")]
    [InlineData("garbage", null)]
    public void ReadsTheRunningVersionWithoutBuildMetadata(string value, string? expected) => Assert.Equal(expected, UpdateVersion.FromRunning(value));

    [Theory]
    [InlineData("2026.1103.3", "2026.1103.3", 0)]
    [InlineData("2026.1103.3", "2026.1103.3.0", 0)]
    [InlineData("2026.1103.3", "2026.1104.1", -1)]
    [InlineData("2026.1104.1", "2026.1103.3", 1)]
    [InlineData("2026.1103.10", "2026.1103.9", 1)]
    [InlineData("2026.1103.3", "2027.101.1", -1)]
    [InlineData("v2026.1103.3", "2026.1103.2", 1)]
    [InlineData("nonsense", "2026.1103.3", -1)]
    [InlineData("2026.1103.3", "nonsense", 1)]
    [InlineData("nonsense", "other", 0)]
    public void ComparesTheNumericParts(string a, string b, int expected) => Assert.Equal(expected, UpdateVersion.Compare(a, b));
}

public sealed class UpdateAssetTests
{
    private const string Repo = "jasonulbright/app-packager-desktop";
    private const string Base = "https://github.com/jasonulbright/app-packager-desktop/releases/download/v2026.1104.1/";

    private static UpdateRelease Release(params UpdateAsset[] assets) => new("v2026.1104.1", "2026.1104.1", "https://example/page", "notes", assets);

    private static UpdateAsset Setup(string url = Base + "AppPackagerDesktop-Setup-2026.1104.1.exe") => new("AppPackagerDesktop-Setup-2026.1104.1.exe", url, 10);

    private static UpdateAsset Sums(string url = Base + "checksums.txt") => new("checksums.txt", url, 10);

    [Fact]
    public void SelectsTheSetupAndTheChecksumsByExactName()
    {
        var release = Release(new UpdateAsset("AppPackagerDesktop-2026.1104.1.zip", Base + "AppPackagerDesktop-2026.1104.1.zip", 1), Setup(), Sums());
        Assert.True(UpdateAssetSelector.TrySelect(release, Repo, out var package, out _));
        Assert.Equal("AppPackagerDesktop-Setup-2026.1104.1.exe", package!.Setup.Name);
        Assert.Equal("checksums.txt", package.Checksums.Name);
    }

    [Fact]
    public void RefusesAReleaseWithoutTheSetup()
    {
        Assert.False(UpdateAssetSelector.TrySelect(Release(Sums()), Repo, out _, out var reason));
        Assert.Contains("Setup", reason);
    }

    [Fact]
    public void RefusesAReleaseWithoutTheChecksums()
    {
        Assert.False(UpdateAssetSelector.TrySelect(Release(Setup()), Repo, out _, out var reason));
        Assert.Contains("checksums.txt", reason);
    }

    [Theory]
    [InlineData("http://github.com/jasonulbright/app-packager-desktop/releases/download/v2026.1104.1/AppPackagerDesktop-Setup-2026.1104.1.exe")]
    [InlineData("https://evil.example/jasonulbright/app-packager-desktop/releases/download/v2026.1104.1/AppPackagerDesktop-Setup-2026.1104.1.exe")]
    [InlineData("https://github.com/other/repo/releases/download/v2026.1104.1/AppPackagerDesktop-Setup-2026.1104.1.exe")]
    public void RefusesAFileOutsideTheRepositoryReleaseDownloads(string url) =>
        Assert.False(UpdateAssetSelector.TrySelect(Release(Setup(url), Sums()), Repo, out _, out _));

    [Fact]
    public void RefusesTwoFilesWithTheSameName() =>
        Assert.False(UpdateAssetSelector.TrySelect(Release(Setup(), Setup(), Sums()), Repo, out _, out _));

    [Fact]
    public void RefusesTheSetupOfAnotherVersion()
    {
        var other = new UpdateAsset("AppPackagerDesktop-Setup-2026.1103.3.exe", Base + "AppPackagerDesktop-Setup-2026.1103.3.exe", 1);
        Assert.False(UpdateAssetSelector.TrySelect(Release(other, Sums()), Repo, out _, out _));
    }
}

public sealed class UpdateChecksumTests
{
    private const string A = "fe1bbc5d868b75aeb2750b9cb74eec3081930e684e6b327596e30672222e643b";
    private const string B = "cd668ca1a26bc3d8c8208633cda7a2c117f5d614ff73e4b0bc867c6379e3fe35";

    [Fact]
    public void FindsTheEntryOfAFileInTheReleaseFormat()
    {
        var text = $"{B}  AppPackagerDesktop-2026.1103.3.zip\n{A}  AppPackagerDesktop-Setup-2026.1103.3.exe\n";
        Assert.Equal(A, UpdateChecksums.Find(text, "AppPackagerDesktop-Setup-2026.1103.3.exe"));
        Assert.Equal(B, UpdateChecksums.Find(text, "AppPackagerDesktop-2026.1103.3.zip"));
    }

    [Fact]
    public void AcceptsCrLfBinaryMarkerAndUpperCase() =>
        Assert.Equal(A, UpdateChecksums.Find($"{A.ToUpperInvariant()} *Setup.exe\r\n", "Setup.exe"));

    [Fact]
    public void ReturnsNullWithoutAnEntry() => Assert.Null(UpdateChecksums.Find($"{A}  Other.exe\n", "Setup.exe"));

    [Fact]
    public void ReturnsNullForTwoDifferentEntries() => Assert.Null(UpdateChecksums.Find($"{A}  Setup.exe\n{B}  Setup.exe\n", "Setup.exe"));

    [Fact]
    public void AcceptsTheSameEntryTwice() => Assert.Equal(A, UpdateChecksums.Find($"{A}  Setup.exe\n{A}  Setup.exe\n", "Setup.exe"));

    [Theory]
    [InlineData("abc  Setup.exe")]
    [InlineData("")]
    [InlineData("not a checksum line")]
    public void IgnoresLinesThatAreNotChecksums(string text) => Assert.Null(UpdateChecksums.Find(text, "Setup.exe"));

    [Fact]
    public void DoesNotMatchAPartOfAName() => Assert.Null(UpdateChecksums.Find($"{A}  MySetup.exe\n", "Setup.exe"));
}

public sealed class UpdateSignaturePolicyTests
{
    private static SignatureInfo Info(string status = "Valid", string subject = "CN=Jason Ulbright, O=Jason Ulbright, L=Denver, S=Colorado, C=US", bool stamp = true) =>
        new(status, subject, stamp);

    [Fact]
    public void AcceptsAValidTimestampedSignatureOfTheOwner() => Assert.Null(UpdateSignaturePolicy.Evaluate(Info()));

    [Fact]
    public void AcceptsTheSubjectWithOnlyTheCommonName() => Assert.Null(UpdateSignaturePolicy.Evaluate(Info(subject: "CN=Jason Ulbright")));

    [Theory]
    [InlineData("NotSigned")]
    [InlineData("HashMismatch")]
    [InlineData("NotTrusted")]
    [InlineData("UnknownError")]
    public void RefusesAStatusOtherThanValid(string status) => Assert.Contains(status, UpdateSignaturePolicy.Evaluate(Info(status: status)));

    [Fact]
    public void RefusesASignatureWithoutATimestamp() => Assert.Contains("timestamp", UpdateSignaturePolicy.Evaluate(Info(stamp: false)));

    [Theory]
    [InlineData("CN=Someone Else, O=Jason Ulbright")]
    [InlineData("CN=Jason Ulbright Evil")]
    [InlineData("O=Jason Ulbright")]
    [InlineData("CN=jason ulbright")]
    [InlineData("")]
    [InlineData("not a subject")]
    public void RefusesAnotherSigner(string subject) => Assert.NotNull(UpdateSignaturePolicy.Evaluate(Info(subject: subject)));

    [Fact]
    public void RefusesAnUnreadableSignature() => Assert.NotNull(UpdateSignaturePolicy.Evaluate(null));
}

public sealed class UpdateServiceTests : IDisposable
{
    private const string Repo = UpdateService.DefaultRepo;
    private const string Base = "https://github.com/jasonulbright/app-packager-desktop/releases/download/v2026.1104.1/";
    private const string SetupName = "AppPackagerDesktop-Setup-2026.1104.1.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-updtest-" + Guid.NewGuid().ToString("N"));

    public UpdateServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"The test folder '{_root}' was not deleted: {ex.Message}");
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class FixedSignature(SignatureInfo? info) : ISignatureReader
    {
        public Task<SignatureInfo?> ReadAsync(string path, CancellationToken ct = default) => Task.FromResult(info);
    }

    private static readonly SignatureInfo Good = new("Valid", "CN=Jason Ulbright, O=Jason Ulbright", true);

    private static HttpResponseMessage Text(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ReleaseJson(string tag = "v2026.1104.1") => $$"""
        {"tag_name":"{{tag}}","html_url":"https://github.com/{{Repo}}/releases/tag/{{tag}}","body":"Notes here","prerelease":false,
         "assets":[
           {"name":"{{SetupName}}","browser_download_url":"{{Base}}{{SetupName}}","size":5},
           {"name":"checksums.txt","browser_download_url":"{{Base}}checksums.txt","size":100}]}
        """;

    private UpdateService Service(Handler handler, SignatureInfo? signature = null, bool noSignature = false) =>
        new(handler, new FixedSignature(noSignature ? null : signature ?? Good), Repo, () => Path.Combine(_root, "dl-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task ReportsAnAvailableUpdateWithTheReleaseNotes()
    {
        var handler = new Handler(_ => Text(ReleaseJson()));
        var check = await Service(handler).CheckAsync("2026.1103.3");
        Assert.Equal(UpdateStatus.Available, check.Status);
        Assert.Equal("2026.1104.1", check.Release!.Version);
        Assert.Equal("Notes here", check.Release.Notes);
        Assert.EndsWith("/releases/tag/v2026.1104.1", check.Release.PageUrl);
    }

    [Fact]
    public async Task ReportsUpToDateForTheSameOrANewerRunningVersion()
    {
        var handler = new Handler(_ => Text(ReleaseJson()));
        using var service = Service(handler);
        Assert.Equal(UpdateStatus.UpToDate, (await service.CheckAsync("2026.1104.1")).Status);
        Assert.Equal(UpdateStatus.UpToDate, (await service.CheckAsync("2026.1105.1")).Status);
    }

    [Fact]
    public async Task SendsNoTokenAndNamesItself()
    {
        var handler = new Handler(_ => Text(ReleaseJson()));
        await Service(handler).CheckAsync("2026.1103.3");
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://api.github.com/repos/{Repo}/releases/latest", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Contains(UpdateService.UserAgent, request.Headers.GetValues("User-Agent"));
    }

    [Fact]
    public async Task AnOfflineMachineIsReportedNotThrown()
    {
        var handler = new Handler(_ => throw new HttpRequestException("No such host is known."));
        var check = await Service(handler).CheckAsync("2026.1103.3");
        Assert.Equal(UpdateStatus.Offline, check.Status);
        Assert.Contains("No such host", check.Message);
        Assert.Null(check.Release);
    }

    [Fact]
    public async Task TheRateLimitCarriesTheResetTime()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(37).ToUnixTimeSeconds();
        var handler = new Handler(_ =>
        {
            var response = Text("{\"message\":\"API rate limit exceeded\"}", HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToString());
            return response;
        });
        var check = await Service(handler).CheckAsync("2026.1103.3");
        Assert.Equal(UpdateStatus.RateLimited, check.Status);
        Assert.Equal(reset, check.RetryAfter!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ATooManyRequestsAnswerIsRateLimitedWithRetryAfter()
    {
        var handler = new Handler(_ =>
        {
            var response = Text("", (HttpStatusCode)429);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });
        var check = await Service(handler).CheckAsync("2026.1103.3");
        Assert.Equal(UpdateStatus.RateLimited, check.Status);
        Assert.InRange((check.RetryAfter!.Value - DateTimeOffset.UtcNow).TotalSeconds, 100, 125);
    }

    [Fact]
    public async Task AForbiddenAnswerWithoutLimitHeadersIsAFailure()
    {
        var check = await Service(new Handler(_ => Text("{}", HttpStatusCode.Forbidden))).CheckAsync("2026.1103.3");
        Assert.Equal(UpdateStatus.Failed, check.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherErrorsAreAFailure(HttpStatusCode code) =>
        Assert.Equal(UpdateStatus.Failed, (await Service(new Handler(_ => Text("{}", code))).CheckAsync("2026.1103.3")).Status);

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"nightly\"}")]
    public async Task ADocumentWithoutAUsableTagIsAFailure(string body) =>
        Assert.Equal(UpdateStatus.Failed, (await Service(new Handler(_ => Text(body))).CheckAsync("2026.1103.3")).Status);

    private static (byte[] Content, string Sums) Payload(string text = "installer", bool wrongHash = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = wrongHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes));
        return (bytes, $"{hash}  {SetupName}\n");
    }

    private Handler Downloads(byte[] setup, string sums, Action<string>? seen = null) => new(request =>
    {
        var url = request.RequestUri!.ToString();
        seen?.Invoke(url);
        return url.EndsWith("checksums.txt")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(setup) };
    });

    private static UpdateRelease Release(long size = 9) =>
        UpdateService.ParseRelease(ReleaseJson().Replace("\"size\":5", $"\"size\":{size}").Replace("\"size\":100", "\"size\":107"))!;

    [Fact]
    public async Task KeepsAnInstallerWhoseChecksumAndSignatureMatch()
    {
        var (content, sums) = Payload();
        using var service = Service(Downloads(content, sums));
        var verified = await service.DownloadAsync(Release());
        Assert.True(File.Exists(verified.Path));
        Assert.Equal("2026.1104.1", verified.Version);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), verified.Sha256);
        Assert.StartsWith(_root, verified.Path);
        UpdateService.ConfirmUnchanged(verified);
    }

    [Fact]
    public async Task RefusesAndDeletesTheFolderOnAChecksumMismatch()
    {
        var (content, sums) = Payload(wrongHash: true);
        using var service = Service(Downloads(content, sums));
        var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() => service.DownloadAsync(Release()));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task RefusesWhenTheChecksumListHasNoEntry()
    {
        var (content, _) = Payload();
        using var service = Service(Downloads(content, "0000000000000000000000000000000000000000000000000000000000000000  Other.exe\n"));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => service.DownloadAsync(Release()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("NotSigned", "CN=Jason Ulbright", true)]
    [InlineData("Valid", "CN=Someone Else", true)]
    [InlineData("Valid", "CN=Jason Ulbright", false)]
    public async Task RefusesAndDeletesTheFolderOnABadSignature(string status, string subject, bool stamp)
    {
        var (content, sums) = Payload();
        using var service = Service(Downloads(content, sums), new SignatureInfo(status, subject, stamp));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => service.DownloadAsync(Release()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task RefusesWhenTheSignatureCannotBeRead()
    {
        var (content, sums) = Payload();
        using var service = Service(Downloads(content, sums), noSignature: true);
        await Assert.ThrowsAsync<UpdateRefusedException>(() => service.DownloadAsync(Release()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task RefusesAFileWhoseSizeDiffersFromTheRelease()
    {
        var (content, sums) = Payload();
        using var service = Service(Downloads(content, sums));
        await Assert.ThrowsAsync<UpdateRefusedException>(() => service.DownloadAsync(Release(size: 12345)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task RefusesAReleaseWithoutTheExpectedFilesBeforeAnyDownload()
    {
        var seen = new List<string>();
        var (content, sums) = Payload();
        using var service = Service(Downloads(content, sums, seen.Add));
        var release = new UpdateRelease("v2026.1104.1", "2026.1104.1", "", "", []);
        await Assert.ThrowsAsync<UpdateRefusedException>(() => service.DownloadAsync(release));
        Assert.Empty(seen);
    }

    [Fact]
    public async Task ANetworkFailureDeletesTheFolderAndPropagates()
    {
        using var service = Service(new Handler(_ => throw new HttpRequestException("reset")));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync(Release()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task RefusesANonEmptyDownloadFolder()
    {
        var folder = Path.Combine(_root, "taken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "x.txt"), "x");
        var (content, sums) = Payload();
        using var service = new UpdateService(Downloads(content, sums), new FixedSignature(Good), Repo, () => folder);
        await Assert.ThrowsAsync<UpdateRefusedException>(() => service.DownloadAsync(Release()));
        Assert.True(File.Exists(Path.Combine(folder, "x.txt")));
    }

    [Fact]
    public async Task ConfirmUnchangedCatchesAFileSwappedAfterTheCheck()
    {
        var (content, sums) = Payload();
        using var service = Service(Downloads(content, sums));
        var verified = await service.DownloadAsync(Release());
        File.WriteAllText(verified.Path, "swapped");
        Assert.Throws<UpdateRefusedException>(() => UpdateService.ConfirmUnchanged(verified));
        File.Delete(verified.Path);
        Assert.Throws<UpdateRefusedException>(() => UpdateService.ConfirmUnchanged(verified));
    }

    [Fact]
    public async Task TheRealSignatureReaderReportsAnUnsignedFileAsNotSigned()
    {
        var path = Path.Combine(_root, "unsigned.dll");
        File.Copy(typeof(UpdateService).Assembly.Location, path);
        var info = await new AuthenticodeSignatureReader().ReadAsync(path);
        Assert.NotNull(info);
        Assert.Equal("NotSigned", info!.Status);
        Assert.NotNull(UpdateSignaturePolicy.Evaluate(info));
    }

    /// <summary>A signature reader whose answer can change between the download check and the start of the installer.</summary>
    private sealed class SwitchableSignature(SignatureInfo? first) : ISignatureReader
    {
        public SignatureInfo? Current { get; set; } = first;
        public int Reads { get; private set; }

        public Task<SignatureInfo?> ReadAsync(string path, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(Current);
        }
    }

    private async Task<(UpdateService Service, SwitchableSignature Signature, VerifiedUpdate Verified)> VerifiedAsync()
    {
        var signature = new SwitchableSignature(Good);
        var (content, sums) = Payload();
        var service = new UpdateService(Downloads(content, sums), signature, Repo, () => Path.Combine(_root, "dl-" + Guid.NewGuid().ToString("N")));
        var verified = await service.DownloadAsync(Release());
        return (service, signature, verified);
    }

    [Fact]
    public async Task ConfirmBeforeLaunchReadsTheSignatureAgainAndPassesWhenNothingChanged()
    {
        var (service, signature, verified) = await VerifiedAsync();
        using var _ = service;
        var readsAfterDownload = signature.Reads;

        await service.ConfirmBeforeLaunchAsync(verified);

        Assert.Equal(readsAfterDownload + 1, signature.Reads);
    }

    [Theory]
    [InlineData("NotSigned", "", false)]
    [InlineData("HashMismatch", "CN=Jason Ulbright, O=Jason Ulbright", true)]
    [InlineData("Valid", "CN=Jason Ulbright, O=Jason Ulbright", false)]
    [InlineData("Valid", "CN=Someone Else, O=Someone Else", true)]
    public async Task ConfirmBeforeLaunchRefusesASignatureThatNoLongerPasses(string status, string subject, bool timestamp)
    {
        var (service, signature, verified) = await VerifiedAsync();
        using var _ = service;
        signature.Current = new SignatureInfo(status, subject, timestamp);

        var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() => service.ConfirmBeforeLaunchAsync(verified));

        Assert.Contains("will not start", ex.Message);
    }

    [Fact]
    public async Task ConfirmBeforeLaunchRefusesASignatureThatCannotBeRead()
    {
        var (service, signature, verified) = await VerifiedAsync();
        using var _ = service;
        signature.Current = null;

        await Assert.ThrowsAsync<UpdateRefusedException>(() => service.ConfirmBeforeLaunchAsync(verified));
    }

    [Fact]
    public async Task ConfirmBeforeLaunchRefusesAnotherSignerWhoPassesThePolicy()
    {
        var (service, signature, verified) = await VerifiedAsync();
        using var _ = service;
        signature.Current = new SignatureInfo("Valid", "CN=Jason Ulbright, O=Another Company", true);

        var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() => service.ConfirmBeforeLaunchAsync(verified));

        Assert.Contains("signer", ex.Message);
    }

    [Fact]
    public async Task ConfirmBeforeLaunchStillCatchesAFileSwappedAfterTheCheck()
    {
        var (service, signature, verified) = await VerifiedAsync();
        using var _ = service;
        File.WriteAllText(verified.Path, "swapped");

        await Assert.ThrowsAsync<UpdateRefusedException>(() => service.ConfirmBeforeLaunchAsync(verified));

        Assert.Equal(1, signature.Reads);
    }

    [Fact]
    public async Task ConfirmBeforeLaunchRefusesAMissingInstallerAndReadsNoSignature()
    {
        var (service, signature, verified) = await VerifiedAsync();
        using var _ = service;
        File.Delete(verified.Path);

        await Assert.ThrowsAsync<UpdateRefusedException>(() => service.ConfirmBeforeLaunchAsync(verified));

        Assert.Equal(1, signature.Reads);
    }
}
