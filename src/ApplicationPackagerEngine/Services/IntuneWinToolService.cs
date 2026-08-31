using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public const string DefaultDownloadUrl =
        "https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool/raw/master/IntuneWinAppUtil.exe";

    private readonly DownloadService _downloads;

    public IntuneWinToolService(DownloadService? downloads = null)
        => _downloads = downloads ?? new DownloadService();

    /// <summary>
    /// Downloads IntuneWinAppUtil.exe into <paramref name="destinationFolder"/>. The file is
    /// verified as Authenticode-Valid and Microsoft-signed before it lands at its final path;
    /// a download that fails verification is deleted and the call throws.
    /// </summary>
    public async Task<string> InstallAsync(
        string destinationFolder,
        string downloadUrl = DefaultDownloadUrl,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationFolder);
        var target = Path.Combine(destinationFolder, "IntuneWinAppUtil.exe");
        var temp = Path.Combine(destinationFolder, $"IntuneWinAppUtil.{Guid.NewGuid():N}.tmp");

        try
        {
            await _downloads.DownloadAsync(downloadUrl, temp, onProgress: onProgress, ct: ct).ConfigureAwait(false);
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
        var status = WinTrust.VerifyAuthenticode(filePath);
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

    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        /// <summary>Returns 0 when the Authenticode signature verifies, otherwise the WinVerifyTrust status.</summary>
        internal static int VerifyAuthenticode(string filePath)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = Marshal.StringToCoTaskMemUni(filePath),
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            var pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            var pGuid = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                Marshal.StructureToPtr(GenericVerifyV2, pGuid, false);

                var data = new WINTRUST_DATA
                {
                    cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = 2,           // WTD_UI_NONE
                    fdwRevocationChecks = 0,  // WTD_REVOKE_NONE
                    dwUnionChoice = 1,        // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = IntPtr.Zero,
                    dwProvFlags = 0x00000010, // WTD_SAFER_FLAG
                    dwUIContext = 0,
                    pSignatureSettings = IntPtr.Zero
                };
                return WinVerifyTrust(IntPtr.Zero, pGuid, ref data);
            }
            finally
            {
                Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
                Marshal.FreeCoTaskMem(pFile);
                Marshal.FreeCoTaskMem(pGuid);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, IntPtr pgActionID, ref WINTRUST_DATA pWVTData);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public int cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public int dwUIChoice;
            public int fdwRevocationChecks;
            public int dwUnionChoice;
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public int dwProvFlags;
            public int dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
