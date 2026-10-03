using System.Diagnostics;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Runs a script in a Windows PowerShell 5.1 child process (powershell.exe).
/// .NET Framework modules and assemblies (the ConfigMgr module, the WSUS
/// administration API) do not load in a .NET 10 process, so every call into
/// them goes through this host.
/// </summary>
public static class WindowsPowerShellHost
{
    public const string ExecutableName = "powershell.exe";

    /// <summary>
    /// Writes <paramref name="script"/> to a temporary .ps1, runs it with
    /// -NoProfile -ExecutionPolicy Bypass -File, streams each standard output
    /// line to <paramref name="onProgress"/>, and returns the exit code, the
    /// joined output and the standard error text. Arguments go after the
    /// script path, one quoted argument each. With
    /// <paramref name="standardInput"/> set, the text is written to the
    /// child's standard input, which is then closed.
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string script, Action<string>? onProgress, CancellationToken ct,
        string scriptFilePrefix = "ap-mecm",
        IReadOnlyList<string>? scriptArguments = null,
        string? standardInput = null)
    {
        // Write script to temp file to avoid argument escaping issues
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{scriptFilePrefix}-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, ct);

        try
        {
            using var process = new Process();
            process.StartInfo = BuildStartInfo(scriptPath, scriptArguments, redirectStandardInput: standardInput is not null);

            process.Start();

            if (standardInput is not null)
            {
                await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), ct);
                process.StandardInput.Close();
            }

            var outLines = new List<string>();
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                outLines.Add(line);
                onProgress?.Invoke(line);
            }

            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode, string.Join(Environment.NewLine, outLines), stderr);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// The process start settings for one script run. Public so the argument
    /// and environment construction is assertable without starting a process.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(
        string scriptPath, IReadOnlyList<string>? scriptArguments = null, bool redirectStandardInput = false)
    {
        var arguments = new StringBuilder($"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"");
        foreach (var argument in scriptArguments ?? [])
            arguments.Append(' ').Append(QuoteArgument(argument));

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutableName,
            Arguments = arguments.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // A PSModulePath inherited from PowerShell 7 makes Windows PowerShell
        // load the 7.x security module, and the read-back's
        // Get-AuthenticodeSignature is then missing. The machine and user
        // values are what a logon session starts with, and the machine value
        // carries the console's module folder.
        var modulePath = LogonModulePath(
            Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.Machine));
        if (modulePath.Length > 0) startInfo.Environment["PSModulePath"] = modulePath;
        else startInfo.Environment.Remove("PSModulePath");
        return startInfo;
    }

    /// <summary>The user value, then the machine value, joined; an empty value is left out.</summary>
    public static string LogonModulePath(string? userValue, string? machineValue) =>
        string.Join(";", new[] { userValue, machineValue }.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>
    /// Quotes one argument the way the Windows command-line parser reads it
    /// back: backslashes before a quote are doubled and the quote escaped.
    /// </summary>
    public static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
