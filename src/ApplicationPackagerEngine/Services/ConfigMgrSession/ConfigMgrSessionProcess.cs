using System.Diagnostics;
using System.Text;

namespace ApplicationPackagerEngine.Services.ConfigMgrSession;

/// <summary>The child process of a session: request lines in, response lines out.</summary>
public interface IConfigMgrSessionProcess : IAsyncDisposable
{
    bool HasExited { get; }

    Task WriteLineAsync(string line);

    /// <summary>The next standard output line, or null once the process output ends.</summary>
    Task<string?> ReadLineAsync();

    /// <summary>Ends the process and everything it started.</summary>
    void Kill();
}

public interface IConfigMgrSessionProcessFactory
{
    IConfigMgrSessionProcess Start();
}

public sealed class PowerShellSessionProcessFactory : IConfigMgrSessionProcessFactory
{
    public IConfigMgrSessionProcess Start() => PowerShellSessionProcess.Start();
}

/// <summary>
/// powershell.exe running the embedded host script. Standard error is drained into a
/// bounded buffer: an undrained pipe blocks the child once the pipe buffer fills.
/// </summary>
public sealed class PowerShellSessionProcess : IConfigMgrSessionProcess
{
    private const int StandardErrorLimit = 8192;
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly Process _process;
    private readonly string _scriptPath;
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrPump;

    private PowerShellSessionProcess(Process process, string scriptPath)
    {
        _process = process;
        _scriptPath = scriptPath;
        _stderrPump = PumpStandardErrorAsync();
    }

    internal const string ScriptPrefix = "ap-cmsession-";

    /// <summary>A host script older than this is left from a process that ended without cleanup; a starting session deletes it.</summary>
    internal static readonly TimeSpan StaleScriptAge = TimeSpan.FromHours(1);

    public static PowerShellSessionProcess Start()
    {
        SweepStaleScripts(Path.GetTempPath(), DateTime.UtcNow);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{ScriptPrefix}{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, ConfigMgrScripts.Read(ConfigMgrScripts.Host), Utf8);
        var process = new Process { StartInfo = BuildStartInfo(scriptPath) };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            try { File.Delete(scriptPath); } catch (IOException) { }
            throw;
        }
        return new PowerShellSessionProcess(process, scriptPath);
    }

    public static ProcessStartInfo BuildStartInfo(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WindowsPowerShellHost.ExecutableName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
            startInfo.ArgumentList.Add(argument);
        WindowsPowerShellHost.ApplyLogonModulePath(startInfo);
        return startInfo;
    }

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public int ProcessId => _process.Id;

    internal string ScriptPath => _scriptPath;

    public string StandardErrorTail
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    public async Task WriteLineAsync(string line)
    {
        await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The next output line. The first line shows that powershell.exe has read and runs the host
    /// script, so the script file is deleted then: an application that ends without disposing
    /// the session leaves no script in the temporary folder.
    /// </summary>
    public async Task<string?> ReadLineAsync()
    {
        var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        if (line is not null && Interlocked.Exchange(ref _scriptDeleted, 1) == 0) DeleteScript(_scriptPath);
        return line;
    }

    private int _scriptDeleted;

    private static void DeleteScript(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes host scripts in <paramref name="folder"/> older than <see cref="StaleScriptAge"/>; returns how many.</summary>
    internal static int SweepStaleScripts(string folder, DateTime utcNow)
    {
        var deleted = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles(ScriptPrefix + "*.ps1"))
            {
                if (utcNow - file.LastWriteTimeUtc < StaleScriptAge || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return deleted;
    }

    public void Kill() => BoundedProcessRunner.Terminate(_process);

    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await _stderrPump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { }
        _process.Dispose();
        DeleteScript(_scriptPath);
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            var buffer = new char[1024];
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > StandardErrorLimit) _stderr.Remove(0, _stderr.Length - StandardErrorLimit);
                }
            }
        }
        catch (Exception) { }
    }
}
