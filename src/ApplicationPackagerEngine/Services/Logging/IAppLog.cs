namespace ApplicationPackagerEngine.Services.Logging;

public enum AppLogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>One log line after masking.</summary>
public sealed record AppLogEntry(DateTimeOffset Time, AppLogLevel Level, string Area, string Message);

/// <summary>
/// The application log shared by every page and service: one session file plus a small
/// in-memory tail. Every message is masked before it is stored, written or raised.
/// </summary>
public interface IAppLog
{
    /// <summary>
    /// The session file, or null before the first entry and while the file cannot be
    /// written. After a write fails the file is tried again once the retry interval passes.
    /// </summary>
    string? SessionFile { get; }

    AppLogLevel MinimumLevel { get; set; }

    event EventHandler<AppLogEntry>? Written;

    void Write(AppLogLevel level, string area, string message);

    /// <summary>Masks every later occurrence of the value in messages.</summary>
    void RegisterSecret(string value);

    /// <summary>The newest entries, oldest first.</summary>
    IReadOnlyList<AppLogEntry> Tail();
}

public static class AppLogExtensions
{
    public static void Debug(this IAppLog log, string area, string message) => log.Write(AppLogLevel.Debug, area, message);
    public static void Info(this IAppLog log, string area, string message) => log.Write(AppLogLevel.Info, area, message);
    public static void Warning(this IAppLog log, string area, string message) => log.Write(AppLogLevel.Warning, area, message);
    public static void Error(this IAppLog log, string area, string message) => log.Write(AppLogLevel.Error, area, message);
}
