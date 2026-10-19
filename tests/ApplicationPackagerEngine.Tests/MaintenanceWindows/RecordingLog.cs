using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

internal sealed class RecordingLog : IAppLog
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) return _lines.ToList(); }
    }

    public string? SessionFile => null;
    public AppLogLevel MinimumLevel { get; set; } = AppLogLevel.Debug;
    public event EventHandler<AppLogEntry>? Written;
    public void RegisterSecret(string value) { }
    public IReadOnlyList<AppLogEntry> Tail() => [];

    public void Write(AppLogLevel level, string area, string message)
    {
        lock (_lines) _lines.Add($"{level} {area} {message}");
        Written?.Invoke(this, new AppLogEntry(DateTimeOffset.UtcNow, level, area, message));
    }
}
