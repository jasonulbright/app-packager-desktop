using System.Text;

namespace ApplicationPackagerBuild;

/// <summary>Where a build's lines go: progress to standard output, errors to standard error, both to the run log.</summary>
public interface IBuildOutput
{
    void Info(string line);
    void Error(string line);
}

/// <summary>
/// Console output plus a run log file. A log that cannot be written leaves the console
/// output intact, so a scheduled run with a read-only log folder still reports.
/// </summary>
public sealed class ConsoleBuildOutput : IBuildOutput, IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _log;

    public string? LogPath { get; private set; }

    public void OpenLog(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"build-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            _log = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
            LogPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Run log not written: {ex.Message}");
        }
    }

    public void Info(string line)
    {
        lock (_gate)
        {
            Console.Out.WriteLine(line);
            Write(line);
        }
    }

    public void Error(string line)
    {
        lock (_gate)
        {
            Console.Error.WriteLine(line);
            Write("ERROR: " + line);
        }
    }

    private void Write(string line)
    {
        if (_log is null) return;
        try { _log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}"); }
        catch (IOException) { _log = null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _log?.Dispose();
            _log = null;
        }
    }
}
