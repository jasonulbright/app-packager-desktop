using System.Globalization;
using System.Text;

namespace ApplicationPackagerEngine.Services.Logging;

/// <summary>
/// Writes app-&lt;timestamp&gt;-&lt;process id&gt;.log in the Logs folder of the Packages
/// operation log and keeps the newest <see cref="KeepSessionFiles"/> such files, the same
/// retention as the packr-*.log session files, which this class never touches.
/// </summary>
public sealed class AppLog : IAppLog
{
    public const int KeepSessionFiles = 30;
    public const int DefaultTailSize = 500;
    public static readonly TimeSpan FileRetryInterval = TimeSpan.FromSeconds(30);

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly object _gate = new();
    private readonly string _folder;
    private readonly int _tailSize;
    private readonly TimeProvider _time;
    private readonly Queue<AppLogEntry> _tail = new();
    private readonly List<string> _secrets = [];
    private bool _fileOpened;
    private DateTimeOffset? _fileFailedAt;
    private string? _sessionFile;

    public AppLog(string? folder = null, int tailSize = DefaultTailSize, TimeProvider? time = null)
    {
        _folder = folder ?? DefaultFolder();
        _tailSize = Math.Max(1, tailSize);
        _time = time ?? TimeProvider.System;
    }

    public static string DefaultFolder() => Path.Combine(AppDataPaths.LegacyRoot, "Logs");

    public string? SessionFile
    {
        get { lock (_gate) return _fileFailedAt is null ? _sessionFile : null; }
    }

    public AppLogLevel MinimumLevel { get; set; } = AppLogLevel.Debug;

    public event EventHandler<AppLogEntry>? Written;

    public void RegisterSecret(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_gate)
        {
            if (!_secrets.Contains(value)) _secrets.Add(value);
        }
    }

    public void Write(AppLogLevel level, string area, string message)
    {
        if (level < MinimumLevel) return;
        AppLogEntry entry;
        lock (_gate)
        {
            var text = SingleLine(SecretMask.Apply(message, _secrets));
            entry = new AppLogEntry(_time.GetUtcNow(), level, SingleLine(area ?? ""), text);
            _tail.Enqueue(entry);
            while (_tail.Count > _tailSize) _tail.Dequeue();
            AppendToFile(entry);
        }
        RaiseWritten(entry);
    }

    // A subscriber is page code; its failure must not fail the write that a ConfigMgr
    // operation made, nor stop the subscribers after it.
    private void RaiseWritten(AppLogEntry entry)
    {
        if (Written is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<AppLogEntry>>())
        {
            try { handler(this, entry); }
            catch (Exception) { }
        }
    }

    public IReadOnlyList<AppLogEntry> Tail()
    {
        lock (_gate) return _tail.ToList();
    }

    private static string SingleLine(string text) =>
        string.Join(" | ", text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None));

    private void AppendToFile(AppLogEntry entry)
    {
        if (_fileFailedAt is { } failedAt)
        {
            if (_time.GetUtcNow() - failedAt < FileRetryInterval) return;
            _fileFailedAt = null;
        }
        try
        {
            if (!_fileOpened)
            {
                Directory.CreateDirectory(_folder);
                var local = entry.Time.ToLocalTime();
                _sessionFile = Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture,
                    $"app-{local:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log"));
                File.AppendAllText(_sessionFile, string.Create(CultureInfo.InvariantCulture,
                    $"--- App Packager Desktop application log started {local:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}"), Utf8);
                _fileOpened = true;
                Prune();
            }
            var stamp = entry.Time.ToLocalTime();
            File.AppendAllText(_sessionFile!, string.Create(CultureInfo.InvariantCulture,
                $"{stamp:yyyy-MM-dd HH:mm:ss.fff}  {Label(entry.Level),-7}  {entry.Area}  {entry.Message}{Environment.NewLine}"), Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _fileFailedAt = _time.GetUtcNow();
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var old in Directory.GetFiles(_folder, "app-*.log")
                         .Where(f => !string.Equals(f, _sessionFile, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                         .Skip(KeepSessionFiles - 1))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Label(AppLogLevel level) => level switch
    {
        AppLogLevel.Debug => "DEBUG",
        AppLogLevel.Info => "INFO",
        AppLogLevel.Warning => "WARNING",
        _ => "ERROR"
    };
}
