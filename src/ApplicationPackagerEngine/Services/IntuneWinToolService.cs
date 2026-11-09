using System.Diagnostics;
using System.Security.Cryptography;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Result of a .intunewin build.
/// </summary>
public sealed record IntuneWinBuildResult(string IntuneWinPath, long SizeBytes, string Sha256, int ToolExitCode, double DurationSec);

/// <summary>
/// Installs the Microsoft Win32 Content Prep Tool and builds .intunewin packages with it.
/// The tool is downloaded per workstation on first use and never redistributed.
/// </summary>
public sealed class IntuneWinToolService
{
    /// <summary>The release of the Microsoft repository that the download is pinned to.</summary>
    public const string PinnedVersion = "1.8.7";

    /// <summary>
    /// The SHA-256 of IntuneWinAppUtil.exe in the tagged release <see cref="PinnedVersion"/>. The file is Authenticode-signed by
    /// Microsoft Corporation (checked when the pin was set). A new release of the tool is taken by changing both constants.
    /// </summary>
    public const string PinnedSha256 = "C1BA45B5CB939E84AF064BB7FF4B38FB3DFE33C8DC1078FD9B157672EAE671F6";

    public const string DefaultDownloadUrl =
        $"https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool/raw/v{PinnedVersion}/IntuneWinAppUtil.exe";

    private readonly DownloadService _downloads;

    public IntuneWinToolService(DownloadService? downloads = null)
        => _downloads = downloads ?? new DownloadService();

    /// <summary>
    /// Downloads IntuneWinAppUtil.exe into <paramref name="destinationFolder"/>. The file must match
    /// <paramref name="expectedSha256"/> (the pinned release by default; null skips this check for a
    /// download from another source), and must be Authenticode-Valid and Microsoft-signed, before it lands at
    /// its final path; a download that fails either check is deleted and the call throws.
    /// </summary>
    public async Task<string> InstallAsync(
        string destinationFolder,
        string downloadUrl = DefaultDownloadUrl,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? expectedSha256 = PinnedSha256)
    {
        Directory.CreateDirectory(destinationFolder);
        var target = Path.Combine(destinationFolder, "IntuneWinAppUtil.exe");
        var temp = Path.Combine(destinationFolder, $"IntuneWinAppUtil.{Guid.NewGuid():N}.tmp");

        try
        {
            await _downloads.DownloadAsync(downloadUrl, temp, onProgress: onProgress, ct: ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = StagingService.ComputeSha256(temp);
                if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"IntuneWinAppUtil.exe download SHA-256 '{actual}' does not match the pinned value '{expectedSha256.Trim().ToUpperInvariant()}'; file discarded.");
            }
            VerifyMicrosoftSignature(temp);
            File.Move(temp, target, overwrite: true);
            onProgress?.Invoke($"IntuneWinAppUtil.exe verified and installed: {target}");
            return target;
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Throws unless the file carries a valid Authenticode signature from a Microsoft Corporation certificate.
    /// </summary>
    public static void VerifyMicrosoftSignature(string filePath)
    {
        var status = AuthenticodeVerifier.Verify(filePath);
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"IntuneWinAppUtil.exe download signature status is '0x{status:X8}', expected 'Valid'; file discarded.");
        }

        string subject;
        try
        {
            // CreateFromSignedFile is the only API that extracts the signer certificate from an
            // Authenticode-signed file; X509CertificateLoader loads certificate blobs, not signatures.
#pragma warning disable SYSLIB0057
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            subject = cert.Subject;
        }
        catch (CryptographicException)
        {
            subject = "";
        }

        if (!subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"IntuneWinAppUtil.exe download signer is '{subject}', expected a Microsoft Corporation certificate; file discarded.");
        }
    }

    /// <summary>
    /// Runs the content prep tool against <paramref name="contentFolder"/>. The tool writes
    /// &lt;setup-basename&gt;.intunewin into <paramref name="outputFolder"/>; when
    /// <paramref name="outputName"/> is given the file is renamed to it. The output folder must
    /// differ from the content folder: a file added to staged content fails stage hash verification.
    /// </summary>
    public async Task<IntuneWinBuildResult> BuildAsync(
        string toolPath,
        string contentFolder,
        string setupFile,
        string outputFolder,
        string outputName = "",
        int timeoutSec = 900,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(toolPath))
            throw new FileNotFoundException($"IntuneWinAppUtil.exe not found: {toolPath}", toolPath);
        if (!Directory.Exists(contentFolder))
            throw new DirectoryNotFoundException($"Content folder not found: {contentFolder}");

        var setupPath = Path.Combine(contentFolder, setupFile);
        if (!File.Exists(setupPath))
            throw new FileNotFoundException($"Setup file not found in content folder: {setupPath}", setupPath);

        var contentFull = Path.GetFullPath(contentFolder).TrimEnd('\\');
        Directory.CreateDirectory(outputFolder);
        var outputFull = Path.GetFullPath(outputFolder).TrimEnd('\\');
        if (string.Equals(outputFull, contentFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "OutputFolder must differ from ContentFolder; a .intunewin inside the content folder fails stage hash verification.",
                nameof(outputFolder));
        }

        var expected = Path.Combine(outputFull, Path.GetFileNameWithoutExtension(setupFile) + ".intunewin");

        // -q answers the tool's overwrite prompt; without it a pre-existing output file stalls
        // the run waiting for console input.
        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = $"-c \"{contentFull}\" -s \"{setupPath}\" -o \"{outputFull}\" -q",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var sw = Stopwatch.StartNew();
        int exitCode;
        string stdOut, stdErr;

        using (var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {toolPath}."))
        {
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new TimeoutException($"IntuneWinAppUtil.exe did not finish within {timeoutSec}s; process terminated.");
            }

            sw.Stop();
            exitCode = proc.ExitCode;
            stdOut = await outTask.ConfigureAwait(false);
            stdErr = await errTask.ConfigureAwait(false);
        }

        if (exitCode != 0)
        {
            var tail = string.Join(" | ", (stdErr + "\n" + stdOut)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .TakeLast(5));
            throw new InvalidOperationException($"IntuneWinAppUtil.exe exit code {exitCode}: {tail}");
        }
        if (!File.Exists(expected))
            throw new FileNotFoundException($"IntuneWinAppUtil.exe reported success but the output file is missing: {expected}", expected);

        var final = expected;
        if (!string.IsNullOrWhiteSpace(outputName))
        {
            final = Path.Combine(outputFull, outputName);
            if (!string.Equals(final, expected, StringComparison.OrdinalIgnoreCase))
                File.Move(expected, final, overwrite: true);
        }

        var size = new FileInfo(final).Length;
        var sha = ComputeSha256(final);
        onProgress?.Invoke($"Built .intunewin: {final} ({size} bytes)");
        return new IntuneWinBuildResult(final, size, sha, exitCode, sw.Elapsed.TotalSeconds);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
