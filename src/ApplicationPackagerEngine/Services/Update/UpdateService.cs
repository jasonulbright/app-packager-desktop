using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ApplicationPackagerEngine.Services.Update;

/// <summary>Reads the signature facts of a file.</summary>
public interface ISignatureReader
{
    Task<SignatureInfo?> ReadAsync(string path, CancellationToken ct = default);
}

/// <summary>Reads the Authenticode signature with Get-AuthenticodeSignature in Windows PowerShell.</summary>
public sealed class AuthenticodeSignatureReader : ISignatureReader
{
    private const string Script = """
        param([string]$Path)
        $s = Get-AuthenticodeSignature -LiteralPath $Path
        [pscustomobject]@{
            Status = [string]$s.Status
            Subject = $(if ($s.SignerCertificate) { [string]$s.SignerCertificate.Subject } else { '' })
            HasTimestamp = [bool]$s.TimeStamperCertificate
        } | ConvertTo-Json -Compress
        """;

    public async Task<SignatureInfo?> ReadAsync(string path, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await WindowsPowerShellHost.RunAsync(Script, null, ct, "ap-update", [path], timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (exit != 0)
        {
            System.Diagnostics.Trace.WriteLine($"The signature read ended with exit code {exit}: {stderr}");
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;
            return new SignatureInfo(
                root.GetProperty("Status").GetString() ?? "",
                root.GetProperty("Subject").GetString() ?? "",
                root.GetProperty("HasTimestamp").GetBoolean());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine($"The signature answer could not be read: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Looks up the latest GitHub release without a token and downloads its installer. A download is kept
/// only when the checksum and the Authenticode signature match; otherwise the folder is removed and the
/// update is refused. The service never starts the installer.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string DefaultRepo = "jasonulbright/app-packager-desktop";
    public const string UserAgent = "AppPackagerDesktop-UpdateCheck";

    private const long MaxChecksumBytes = 64 * 1024;
    private const long MaxSetupBytes = 1024L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ISignatureReader _signatures;
    private readonly string _repo;
    private readonly Func<string> _newFolder;

    public UpdateService(HttpMessageHandler? handler = null, ISignatureReader? signatures = null, string repo = DefaultRepo, Func<string>? newFolder = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsHttp = true;
        _http.Timeout = TimeSpan.FromMinutes(30);
        _signatures = signatures ?? new AuthenticodeSignatureReader();
        _repo = repo;
        _newFolder = newFolder ?? (() => Path.Combine(Path.GetTempPath(), "apkg-update-" + Guid.NewGuid().ToString("N")));
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private HttpRequestMessage Get(string url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    /// <summary>
    /// The latest release against the running version. It never throws for a network, rate-limit or
    /// data problem; the status says which one happened.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = Get($"https://api.github.com/repos/{_repo}/releases/latest", "application/vnd.github+json");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            if (IsRateLimited(response, out var retryAfter))
                return new UpdateCheck(UpdateStatus.RateLimited, currentVersion, null,
                    "GitHub limits the checks without a sign-in. Try again later.", retryAfter);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheck(UpdateStatus.Failed, currentVersion, null, "GitHub reports no release for this app.");
            if (!response.IsSuccessStatusCode)
                return new UpdateCheck(UpdateStatus.Failed, currentVersion, null, $"GitHub answered {(int)response.StatusCode}.");

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var release = ParseRelease(json);
            if (release is null)
                return new UpdateCheck(UpdateStatus.Failed, currentVersion, null, "The latest release has no version tag that this app understands.");

            return UpdateVersion.Compare(currentVersion, release.Version) < 0
                ? new UpdateCheck(UpdateStatus.Available, currentVersion, release, $"Version {release.Version} is available.")
                : new UpdateCheck(UpdateStatus.UpToDate, currentVersion, release, "This app is up to date.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheck(UpdateStatus.Offline, currentVersion, null, "GitHub did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheck(UpdateStatus.Offline, currentVersion, null, $"GitHub cannot be reached: {ex.Message}");
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response, out DateTimeOffset? retryAfter)
    {
        retryAfter = null;
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return false;
        var remaining = Header(response, "X-RateLimit-Remaining");
        var limited = response.StatusCode == HttpStatusCode.TooManyRequests || remaining == "0" || response.Headers.RetryAfter is not null;
        if (!limited) return false;
        if (response.Headers.RetryAfter?.Delta is { } delta) retryAfter = DateTimeOffset.UtcNow + delta;
        else if (long.TryParse(Header(response, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
            retryAfter = DateTimeOffset.FromUnixTimeSeconds(reset);
        return true;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    internal static UpdateRelease? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            var version = UpdateVersion.FromTag(tag);
            if (version is null) return null;
            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    assets.Add(new UpdateAsset(
                        item.GetProperty("name").GetString() ?? "",
                        item.GetProperty("browser_download_url").GetString() ?? "",
                        item.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0));
            return new UpdateRelease(tag!, version,
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "",
                assets);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine($"The release document could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the Setup exe and checksums.txt into a new empty folder and checks both. Any mismatch deletes
    /// the folder and throws <see cref="UpdateRefusedException"/>. Network failures propagate as HttpRequestException.
    /// </summary>
    public async Task<VerifiedUpdate> DownloadAsync(UpdateRelease release, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        if (!UpdateAssetSelector.TrySelect(release, _repo, out var package, out var reason))
            throw new UpdateRefusedException(reason);

        var folder = _newFolder();
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new UpdateRefusedException($"The download folder '{folder}' is not empty.");
        Directory.CreateDirectory(folder);
        try
        {
            var sumsPath = Path.Combine(folder, UpdateAssetSelector.ChecksumsName);
            await DownloadFileAsync(package!.Checksums, sumsPath, MaxChecksumBytes, null, ct).ConfigureAwait(false);
            var setupPath = Path.Combine(folder, package.Setup.Name);
            await DownloadFileAsync(package.Setup, setupPath, MaxSetupBytes, progress, ct).ConfigureAwait(false);

            var expected = UpdateChecksums.Find(await File.ReadAllTextAsync(sumsPath, ct).ConfigureAwait(false), package.Setup.Name)
                ?? throw new UpdateRefusedException($"{UpdateAssetSelector.ChecksumsName} has no single entry for {package.Setup.Name}.");
            var actual = UpdateChecksums.Sha256Of(setupPath);
            if (actual != expected) throw new UpdateRefusedException($"The SHA-256 of {package.Setup.Name} does not match {UpdateAssetSelector.ChecksumsName}.");

            var info = await _signatures.ReadAsync(setupPath, ct).ConfigureAwait(false);
            if (UpdateSignaturePolicy.Evaluate(info) is { } refusal) throw new UpdateRefusedException(refusal);

            return new VerifiedUpdate(setupPath, release.Version, actual, info!.Subject);
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    /// <summary>Hashes the installer again just before it starts, so a file swapped after the check is caught.</summary>
    public static void ConfirmUnchanged(VerifiedUpdate update)
    {
        if (!File.Exists(update.Path)) throw new UpdateRefusedException("The downloaded installer is gone.");
        if (UpdateChecksums.Sha256Of(update.Path) != update.Sha256)
            throw new UpdateRefusedException("The downloaded installer changed after the check. It will not start.");
    }

    /// <summary>
    /// The check just before the installer starts: the hash again, then the Authenticode signature again, against the same
    /// policy as the download check and for the same signer. A file replaced after the download, or a signature that no longer
    /// passes, throws <see cref="UpdateRefusedException"/> and the installer does not start.
    /// </summary>
    public async Task ConfirmBeforeLaunchAsync(VerifiedUpdate update, CancellationToken ct = default)
    {
        ConfirmUnchanged(update);

        var info = await _signatures.ReadAsync(update.Path, ct).ConfigureAwait(false);
        if (UpdateSignaturePolicy.Evaluate(info) is { } refusal)
            throw new UpdateRefusedException(refusal + " The installer will not start.");
        if (!string.Equals(info!.Subject, update.Subject, StringComparison.Ordinal))
            throw new UpdateRefusedException($"The signer changed from '{update.Subject}' to '{info.Subject}' after the check. The installer will not start.");
    }

    private async Task DownloadFileAsync(UpdateAsset asset, string path, long maxBytes, Action<long, long>? progress, CancellationToken ct)
    {
        using var request = Get(asset.Url, "application/octet-stream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        if (total > maxBytes) throw new UpdateRefusedException($"{asset.Name} is larger than the limit.");
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long read = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            read += count;
            if (read > maxBytes) throw new UpdateRefusedException($"{asset.Name} is larger than the limit.");
            await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            progress?.Invoke(read, total);
        }
        if (asset.Size > 0 && read != asset.Size) throw new UpdateRefusedException($"{asset.Name} is {read} bytes; the release lists {asset.Size}.");
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"The update folder '{folder}' was not deleted: {ex.Message}");
        }
    }
}
