using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

public class PackagerExecutionService(SettingsService settingsService, StagingService stagingService)
{
    private static readonly TimeSpan GetLatestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Runs a packager with -GetLatestVersionOnly. Routes JSON apps to engine DLL.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(
        PackagerInfo packager, CancellationToken ct = default)
    {
        // Route JSON-defined apps through the engine DLL
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            var acqMethod = settingsService.Load().AcquisitionMethod;
            return await stagingService.GetLatestVersionAsync(def, acqMethod, ct);
        }

        var settings = settingsService.Load();
        var args = BuildGetLatestArgs(packager, settings);

        using var process = CreateProcess(args);
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(GetLatestTimeout, ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        var stdout = await stdoutTask;

        if (process.ExitCode != 0)
            return null;

        // First non-empty line is the version
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return null;
    }

    /// <summary>
    /// Runs a packager with -StageOnly, streaming stdout line-by-line via the callback.
    /// </summary>
    public async Task<PackagerExecutionResult> StageAsync(
        PackagerInfo packager, Action<string>? onOutputLine = null, CancellationToken ct = default)
    {
        // Route JSON-defined apps through the engine DLL
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            var appSettings = settingsService.Load();
            var result = await stagingService.StageAsync(def, appSettings.DownloadRoot, onOutputLine, ct);
            return new PackagerExecutionResult
            {
                ExitCode = result.Success ? 0 : 1,
                StdOut = result.Success ? $"Staged to: {result.ContentPath}" : "",
                StdErr = result.ErrorMessage ?? "",
                OutLogPath = result.ManifestPath
            };
        }

        var settings = settingsService.Load();
        var (args, logPaths) = BuildStageArgs(packager, settings);
        return await RunStreamingAsync(args, logPaths, onOutputLine, ct);
    }

    /// <summary>
    /// Runs a packager with -PackageOnly, streaming stdout line-by-line via the callback.
    /// </summary>
    public async Task<PackagerExecutionResult> PackageAsync(
        PackagerInfo packager, string comment, Action<string>? onOutputLine = null, CancellationToken ct = default)
    {
        var settings = settingsService.Load();
        var (args, logPaths) = BuildPackageArgs(packager, settings, comment);
        return await RunStreamingAsync(args, logPaths, onOutputLine, ct);
    }

    // ── Process helpers ──────────────────────────────────────────

    private static Process CreateProcess(string arguments)
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return process;
    }

    private async Task<PackagerExecutionResult> RunStreamingAsync(
        string arguments, LogPaths logPaths, Action<string>? onOutputLine, CancellationToken ct)
    {
        using var process = CreateProcess(arguments);
        process.Start();

        // Stream stdout line-by-line
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var outLines = new List<string>();

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            outLines.Add(line);
            if (onOutputLine is not null)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => onOutputLine(line));
            }
        }

        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        // Write log files
        try
        {
            var logDir = Path.GetDirectoryName(logPaths.OutLog);
            if (logDir is not null)
                Directory.CreateDirectory(logDir);

            await File.WriteAllLinesAsync(logPaths.OutLog, outLines, ct);
            if (!string.IsNullOrEmpty(stderr))
                await File.WriteAllTextAsync(logPaths.ErrLog, stderr, ct);
        }
        catch { /* Log write failure is non-fatal */ }

        return new PackagerExecutionResult
        {
            ExitCode = process.ExitCode,
            StdOut = string.Join(Environment.NewLine, outLines),
            StdErr = stderr,
            OutLogPath = logPaths.OutLog,
            ErrLogPath = logPaths.ErrLog,
            StructuredLogPath = logPaths.StructuredLog
        };
    }

    // ── Argument builders ────────────────────────────────────────

    private string BuildGetLatestArgs(PackagerInfo packager, AppSettings settings)
    {
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{packager.FullPath}\" -SiteCode \"{settings.SiteCode}\" -GetLatestVersionOnly";

        if (SupportsFileServerPath(packager))
            args += $" -FileServerPath \"{settings.FileShareRoot}\"";
        if (!string.IsNullOrEmpty(settings.DownloadRoot))
            args += $" -DownloadRoot \"{settings.DownloadRoot}\"";
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args += $" -M365Channel \"{settings.M365Channel}\"";

        return args;
    }

    private (string args, LogPaths logs) BuildStageArgs(PackagerInfo packager, AppSettings settings)
    {
        var logs = CreateLogPaths(packager, "stage", settings.DownloadRoot);

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{packager.FullPath}\" -StageOnly -LogPath \"{logs.StructuredLog}\"";

        if (!string.IsNullOrEmpty(settings.DownloadRoot))
            args += $" -DownloadRoot \"{settings.DownloadRoot}\"";
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args += $" -M365Channel \"{settings.M365Channel}\"";

        return (args, logs);
    }

    private (string args, LogPaths logs) BuildPackageArgs(PackagerInfo packager, AppSettings settings, string comment)
    {
        var logs = CreateLogPaths(packager, "package", settings.DownloadRoot);

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{packager.FullPath}\" -PackageOnly";
        args += $" -SiteCode \"{settings.SiteCode}\"";
        args += $" -Comment \"{comment}\"";
        args += $" -LogPath \"{logs.StructuredLog}\"";

        if (SupportsFileServerPath(packager))
            args += $" -FileServerPath \"{settings.FileShareRoot}\"";
        if (!string.IsNullOrEmpty(settings.DownloadRoot))
            args += $" -DownloadRoot \"{settings.DownloadRoot}\"";
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args += $" -M365Channel \"{settings.M365Channel}\"";
        if (settings.EstimatedRuntimeMins > 0)
            args += $" -EstimatedRuntimeMins {settings.EstimatedRuntimeMins}";
        if (settings.MaximumRuntimeMins > 0)
            args += $" -MaximumRuntimeMins {settings.MaximumRuntimeMins}";

        return (args, logs);
    }

    /// <summary>
    /// Checks if a packager script accepts -FileServerPath by scanning the first 120 lines.
    /// </summary>
    private static bool SupportsFileServerPath(PackagerInfo packager)
    {
        try
        {
            var count = 0;
            foreach (var line in File.ReadLines(packager.FullPath))
            {
                if (++count > 120) break;
                if (line.Contains("$FileServerPath", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private sealed record LogPaths(string OutLog, string ErrLog, string StructuredLog);

    private static LogPaths CreateLogPaths(PackagerInfo packager, string phase, string downloadRoot)
    {
        var logDir = Path.Combine(
            string.IsNullOrEmpty(downloadRoot) ? Path.GetTempPath() : downloadRoot,
            "Logs");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = Path.GetFileNameWithoutExtension(packager.Script);

        return new LogPaths(
            OutLog: Path.Combine(logDir, $"{baseName}-{phase}-{stamp}.out.log"),
            ErrLog: Path.Combine(logDir, $"{baseName}-{phase}-{stamp}.err.log"),
            StructuredLog: Path.Combine(logDir, $"{baseName}-{phase}-{stamp}.structured.log")
        );
    }

    private static AppDefinition LoadDefinition(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppDefinition>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to load definition: {path}");
    }
}
