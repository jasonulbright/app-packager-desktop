using System.Diagnostics;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Runs install → detect → uninstall → verify-clean locally on bare metal.
/// User apps: direct launch (standard user context).
/// Machine apps: psexec -s (SYSTEM context).
/// No CM dependency — tests the install scripts and detection rules directly.
/// </summary>
public sealed class DefinitionTestRunner
{
    private readonly VersionHistoryService? _historyService;

    public DefinitionTestRunner(VersionHistoryService? historyService = null)
    {
        _historyService = historyService;
    }

    /// <summary>
    /// Runs the full test pipeline: install → detect → uninstall → verify clean.
    /// </summary>
    public async Task<TestResult> RunAsync(
        string stagedContentPath,
        StageManifest manifest,
        AppDefinition def,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var result = new TestResult { AppName = manifest.AppName };
        var isUserInstall = def.InstallContext.Equals("User", StringComparison.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();

        onProgress?.Invoke($"Testing: {manifest.AppName}");
        onProgress?.Invoke($"Context: {(isUserInstall ? "User (standard)" : "SYSTEM (psexec -s)")}");

        // 1. Install
        onProgress?.Invoke("Phase 1: Installing...");
        var installScript = Path.Combine(stagedContentPath, "install.bat");
        if (!File.Exists(installScript))
        {
            result.InstallResult = "FAIL: install.bat not found";
            return result;
        }

        int installExit = -1;
        string installOut = "", installErr = "";

        // Retry loop for MSI 1618 (another installation in progress)
        var maxRetries = 3;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            (installExit, installOut, installErr) = isUserInstall
                ? await RunDirectAsync(installScript, stagedContentPath, ct)
                : await RunAsSystemAsync(installScript, stagedContentPath, ct);

            if (installExit != 1618 || attempt == maxRetries)
                break;

            onProgress?.Invoke($"  Exit 1618 (MSI busy) — retrying in 15s (attempt {attempt + 1}/{maxRetries})...");
            await Task.Delay(15000, ct);
        }

        result.InstallExitCode = installExit;
        result.InstallResult = installExit == 0 ? "PASS" : $"FAIL (exit {installExit})";
        if (installExit == 3010) result.InstallResult = "PASS (reboot required)";
        if (installExit == 1641) result.InstallResult = "PASS (hard reboot initiated)";
        onProgress?.Invoke($"  Install: {result.InstallResult}");

        if (installExit != 0 && installExit != 3010)
        {
            result.OverallResult = "FAIL";
            LogResult(def, manifest, result, sw.ElapsedMilliseconds);
            return result;
        }

        // 2. Detect
        onProgress?.Invoke("Phase 2: Evaluating detection...");
        var detected = EvaluateDetection(manifest.Detection);
        result.DetectResult = detected ? "PASS" : "FAIL (not detected after install)";
        onProgress?.Invoke($"  Detect: {result.DetectResult}");

        if (!detected)
        {
            result.OverallResult = "FAIL";
            LogResult(def, manifest, result, sw.ElapsedMilliseconds);
            return result;
        }

        // 3. Uninstall
        if (!def.CanUninstall)
        {
            onProgress?.Invoke("Phase 3: Skipping uninstall (CanUninstall=false)");
            result.UninstallResult = "SKIP (by design)";
            result.CleanResult = "SKIP (by design)";
            result.OverallResult = "PASS";
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            LogResult(def, manifest, result, sw.ElapsedMilliseconds);
            return result;
        }

        onProgress?.Invoke("Phase 3: Uninstalling...");
        var uninstallScript = Path.Combine(stagedContentPath, "uninstall.bat");
        if (!File.Exists(uninstallScript))
        {
            result.UninstallResult = "SKIP: uninstall.bat not found";
            result.OverallResult = "PASS (no uninstall)";
            LogResult(def, manifest, result, sw.ElapsedMilliseconds);
            return result;
        }

        var (uninstallExit, _, _) = isUserInstall
            ? await RunDirectAsync(uninstallScript, stagedContentPath, ct)
            : await RunAsSystemAsync(uninstallScript, stagedContentPath, ct);

        result.UninstallExitCode = uninstallExit;
        result.UninstallResult = uninstallExit == 0 ? "PASS" : $"FAIL (exit {uninstallExit})";
        if (uninstallExit == 3010) result.UninstallResult = "PASS (reboot required)";
        onProgress?.Invoke($"  Uninstall: {result.UninstallResult}");

        // 4. Verify clean
        onProgress?.Invoke("Phase 4: Verifying clean removal...");
        var stillDetected = EvaluateDetection(manifest.Detection);
        result.CleanResult = stillDetected ? "FAIL (still detected after uninstall)" : "PASS";
        onProgress?.Invoke($"  Clean: {result.CleanResult}");

        result.OverallResult = (result.InstallResult.StartsWith("PASS")
            && result.DetectResult == "PASS"
            && result.UninstallResult.StartsWith("PASS")
            && result.CleanResult == "PASS")
            ? "PASS" : "FAIL";

        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;
        onProgress?.Invoke($"Result: {result.OverallResult} ({sw.ElapsedMilliseconds}ms)");

        LogResult(def, manifest, result, sw.ElapsedMilliseconds);
        return result;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunDirectAsync(
        string batPath, string workingDir, CancellationToken ct)
    {
        // Run install.ps1 directly instead of install.bat to avoid exit code propagation issues
        var ps1Path = Path.ChangeExtension(batPath, ".ps1");
        string fileName, arguments;

        if (File.Exists(ps1Path))
        {
            fileName = "powershell.exe";
            arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ps1Path}\"";
        }
        else
        {
            fileName = "cmd.exe";
            arguments = $"/c \"{batPath}\"";
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true
            // Do NOT redirect stdout/stderr — child processes inherit the pipe
            // handles and prevent WaitForExitAsync from completing until ALL
            // children exit, even after the parent powershell.exe exits.
        };

        process.Start();
        // Hard 5-minute per-phase timeout. Kill process tree if exceeded.
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        if (await Task.WhenAny(exitTask, Task.Delay(300_000)) != exitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-999, "", "TIMEOUT - process killed");
        }
        await exitTask;
        return (process.ExitCode, "", "");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsSystemAsync(
        string batPath, string workingDir, CancellationToken ct)
    {
        // If already running as SYSTEM, just run directly — no need for psexec
        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        if (currentUser.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase))
            return await RunDirectAsync(batPath, workingDir, ct);

        // psexec -s -accepteula -w <workingDir> cmd /c <batPath>
        var psexecPath = FindPsExec();
        if (psexecPath is null)
            return await RunDirectAsync(batPath, workingDir, ct);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = psexecPath,
            Arguments = $"-s -accepteula -w \"{workingDir}\" cmd.exe /c \"{batPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdout, stderr);
    }

    private static string? FindPsExec()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Sysinternals\PsExec.exe",
            @"C:\Sysinternals\PsExec.exe",
            @"C:\Windows\PsExec.exe",
            @"C:\Tools\PsExec.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Evaluates a detection rule against the local machine's registry and filesystem.
    /// </summary>
    private static bool EvaluateDetection(StageDetection det)
    {
        try
        {
            return det.Type switch
            {
                "RegistryKeyValue" => EvaluateRegistryKeyValue(det),
                "RegistryKey" => EvaluateRegistryKey(det),
                "File" => EvaluateFile(det),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool EvaluateRegistryKeyValue(StageDetection det)
    {
        if (string.IsNullOrEmpty(det.RegistryKeyRelative)) return false;

        var view = det.Is64Bit
            ? Microsoft.Win32.RegistryView.Registry64
            : Microsoft.Win32.RegistryView.Registry32;
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.LocalMachine, view);
        using var key = hklm.OpenSubKey(det.RegistryKeyRelative);
        if (key is null) return false;

        var value = key.GetValue(det.ValueName ?? "DisplayVersion")?.ToString();
        if (string.IsNullOrEmpty(value)) return false;

        if (string.IsNullOrEmpty(det.ExpectedValue)) return true; // existence only

        if (det.PropertyType?.Equals("Version", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (Version.TryParse(value, out var actual) && Version.TryParse(det.ExpectedValue, out var expected))
            {
                return det.Operator switch
                {
                    "GreaterEquals" => actual >= expected,
                    "IsEquals" => actual == expected,
                    _ => actual >= expected
                };
            }
        }

        // String comparison
        return det.Operator switch
        {
            "IsEquals" => string.Equals(value, det.ExpectedValue, StringComparison.OrdinalIgnoreCase),
            "GreaterEquals" => string.Compare(value, det.ExpectedValue, StringComparison.OrdinalIgnoreCase) >= 0,
            _ => string.Equals(value, det.ExpectedValue, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool EvaluateRegistryKey(StageDetection det)
    {
        if (string.IsNullOrEmpty(det.RegistryKeyRelative)) return false;

        var view = det.Is64Bit
            ? Microsoft.Win32.RegistryView.Registry64
            : Microsoft.Win32.RegistryView.Registry32;
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.LocalMachine, view);
        using var key = hklm.OpenSubKey(det.RegistryKeyRelative);
        return key is not null;
    }

    private static bool EvaluateFile(StageDetection det)
    {
        if (string.IsNullOrEmpty(det.FilePath) || string.IsNullOrEmpty(det.FileName))
            return false;

        var filePath = Path.Combine(
            Environment.ExpandEnvironmentVariables(det.FilePath),
            det.FileName);

        if (!File.Exists(filePath)) return false;

        if (det.PropertyType?.Equals("Existence", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        if (det.PropertyType?.Equals("Version", StringComparison.OrdinalIgnoreCase) == true
            && !string.IsNullOrEmpty(det.ExpectedValue))
        {
            var fi = FileVersionInfo.GetVersionInfo(filePath);
            if (Version.TryParse(fi.FileVersion, out var actual) && Version.TryParse(det.ExpectedValue, out var expected))
            {
                return det.Operator switch
                {
                    "GreaterEquals" => actual >= expected,
                    "IsEquals" => actual == expected,
                    _ => actual >= expected
                };
            }
        }

        return true;
    }

    private void LogResult(AppDefinition def, StageManifest manifest, TestResult result, long durationMs)
    {
        _historyService?.Record(new HistoryEntry
        {
            AppId = def.Id,
            AppName = manifest.AppName,
            Version = manifest.SoftwareVersion,
            Operation = "Test",
            Status = result.OverallResult ?? "Unknown",
            DurationMs = durationMs,
            ErrorMessage = result.OverallResult != "PASS"
                ? $"Install:{result.InstallResult} Detect:{result.DetectResult} Uninstall:{result.UninstallResult} Clean:{result.CleanResult}"
                : null
        });
    }
}

public sealed class TestResult
{
    public string AppName { get; set; } = "";
    public string? OverallResult { get; set; }
    public int? InstallExitCode { get; set; }
    public string? InstallResult { get; set; }
    public string? DetectResult { get; set; }
    public int? UninstallExitCode { get; set; }
    public string? UninstallResult { get; set; }
    public string? CleanResult { get; set; }
    public long? DurationMs { get; set; }
}
