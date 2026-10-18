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
    /// child's standard input as UTF-8, which is then closed; the script reads
    /// it back as UTF-8 (see <see cref="StandardInputEncoding"/>). Both output
    /// streams are read at the same time, so a child that fills one pipe
    /// while the other is being read still completes. Cancellation ends the
    /// child's process tree and waits for it before the script file is removed.
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string script, Action<string>? onProgress, CancellationToken ct,
        string scriptFilePrefix = "ap-mecm",
        IReadOnlyList<string>? scriptArguments = null,
        string? standardInput = null)
    {
        // A script file keeps the script text out of command-line quoting.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{scriptFilePrefix}-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, ct);

        try
        {
            using var process = new Process();
            process.StartInfo = BuildStartInfo(scriptPath, scriptArguments, redirectStandardInput: standardInput is not null);

            process.Start();

            // Neither reader takes the caller's token: an abandoned read leaves the child
            // blocked on a full pipe, and terminating the child is what ends both streams.
            var outLines = new List<string>();
            async Task ReadOutputAsync()
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None)) is not null)
                {
                    outLines.Add(line);
                    onProgress?.Invoke(line);
                }
            }
            var stdoutTask = ReadOutputAsync();
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                if (standardInput is not null)
                {
                    // A script that never reads its input can exit first and close the pipe.
                    try
                    {
                        await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), ct);
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        try { process.StandardInput.Dispose(); } catch (IOException) { }
                    }
                }
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                BoundedProcessRunner.Terminate(process);
                try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
                catch (Exception) { }
                throw;
            }

            await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, string.Join(Environment.NewLine, outLines), stderr);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// The encoding of the child's standard input. The script reads it with a UTF-8
    /// reader over the raw standard input stream, because the console input code
    /// page of a hidden console is the OEM page and decodes non-ASCII text differently.
    /// </summary>
    public static readonly Encoding StandardInputEncoding = new UTF8Encoding(false);

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
            StandardInputEncoding = redirectStandardInput ? StandardInputEncoding : null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ApplyLogonModulePath(startInfo);
        return startInfo;
    }

    /// <summary>
    /// Gives a Windows PowerShell child the module path a logon session starts
    /// with. A PSModulePath inherited from PowerShell 7 makes Windows PowerShell
    /// load 7.x modules: the security module then lacks
    /// Get-AuthenticodeSignature and the utility module lacks Get-FileHash. The
    /// machine value carries the ConfigMgr console's module folder.
    /// </summary>
    public static void ApplyLogonModulePath(ProcessStartInfo startInfo)
    {
        var modulePath = LogonModulePath(
            Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.Machine));
        if (modulePath.Length > 0) startInfo.Environment["PSModulePath"] = modulePath;
        else startInfo.Environment.Remove("PSModulePath");
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
