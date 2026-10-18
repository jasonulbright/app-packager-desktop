using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.ConfigMgrSession;

/// <summary>
/// One long-lived Windows PowerShell session with the ConfigurationManager module loaded
/// and the CMSite drive connected. Commands run one at a time in the order they arrive.
/// </summary>
public interface IConfigMgrSession : IAsyncDisposable
{
    /// <summary>
    /// Runs the script in the session with the parameters bound by name. Cancellation ends
    /// the running command by ending the session; the next command starts a new one.
    /// Failures throw <see cref="ConfigMgrSessionException"/> with a plain message.
    /// Error text and warnings pass through <see cref="Logging.SecretMask"/>; output values
    /// are raw, so a caller that logs or shows output masks it first. A request larger than
    /// <see cref="ConfigMgrSessionHost.MaxRequestBytes"/> is refused before it is sent.
    /// </summary>
    Task<ConfigMgrCommandResult> InvokeAsync(
        ConfigMgrScript script,
        ConfigMgrParameters? parameters = null,
        CancellationToken ct = default,
        TimeSpan? timeout = null);
}

/// <summary>The site the session connects to; an empty provider is the local computer.</summary>
public sealed record ConfigMgrConnection(string? SiteCode, string? ProviderMachine)
{
    public string Key => SiteIdentity.Key(SiteCode, ProviderMachine);
    public string NormalizedSiteCode => (SiteCode ?? "").Trim().ToUpperInvariant();
    public string NormalizedProvider => (ProviderMachine ?? "").Trim();
}

public sealed class ConfigMgrSessionOptions
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>A script body with a param() block. Values never become part of the text.</summary>
public sealed record ConfigMgrScript(string Name, string Text)
{
    public static ConfigMgrScript FromText(string name, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new ConfigMgrScript(name, text);
    }

    /// <summary>An embedded script of the Engine assembly; the name is the file name without .ps1.</summary>
    public static ConfigMgrScript FromResource(string logicalName)
    {
        var text = ConfigMgrScripts.Read(logicalName);
        var name = logicalName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? logicalName[..^4] : logicalName;
        name = name[(name.LastIndexOf('.') + 1)..];
        return new ConfigMgrScript(name, text);
    }
}

/// <summary>The scripts shipped as embedded resources.</summary>
public static class ConfigMgrScripts
{
    public const string Host = "ApplicationPackagerEngine.Services.ConfigMgrSession.Scripts.ConfigMgrSessionHost.ps1";
    public const string GetSites = "ApplicationPackagerEngine.Services.ConfigMgrSession.Scripts.GetSites.ps1";
    public const string GetCollections = "ApplicationPackagerEngine.Services.ConfigMgrSession.Scripts.GetCollections.ps1";

    public static string Read(string logicalName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded script {logicalName} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Named parameter values for a script. A secret value travels to the child like any other
/// value but is shown as *** in every log line.
/// </summary>
public sealed partial class ConfigMgrParameters : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _secret = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NameRegex();

    public object? this[string name]
    {
        get => _values[name];
        set => _values[Checked(name)] = value;
    }

    public void Add(string name, object? value) => this[name] = value;

    public void AddSecret(string name, string? value)
    {
        this[name] = value;
        _secret.Add(name);
    }

    public bool IsSecret(string name) => _secret.Contains(name);

    public int Count => _values.Count;

    /// <summary>The non-empty secret values, for masking text that comes back from the child.</summary>
    public IEnumerable<string> SecretValues =>
        _values.Where(p => _secret.Contains(p.Key) && p.Value is string { Length: > 0 }).Select(p => (string)p.Value!);

    /// <summary>Name=value pairs for a log line, with secret values masked.</summary>
    public string ToLogText() => string.Join(", ", _values.Select(p =>
        p.Key + "=" + (_secret.Contains(p.Key) ? Logging.SecretMask.Mask : Describe(p.Value))));

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => s,
        IEnumerable e => "[" + e.Cast<object?>().Count() + " values]",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };

    private static string Checked(string name)
    {
        if (name is null || !NameRegex().IsMatch(name))
            throw new ArgumentException($"The parameter name '{name}' is not a PowerShell parameter name.", nameof(name));
        return name;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// What one command wrote to its pipeline, one JSON value per object. Output values are
/// raw; warnings are masked. A NaN or infinite number arrives as null.
/// </summary>
public sealed class ConfigMgrCommandResult(IReadOnlyList<JsonElement> output, IReadOnlyList<string> warnings)
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<JsonElement> Output { get; } = output;
    public IReadOnlyList<string> Warnings { get; } = warnings;

    /// <summary>A string value as its text; any other value as its compact JSON.</summary>
    public IReadOnlyList<string> Lines => Output
        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
        .ToList();

    /// <summary>Each object value read as <typeparamref name="T"/>, property names matched without case.</summary>
    public IReadOnlyList<T> As<T>() => Output
        .Where(e => e.ValueKind != JsonValueKind.Null)
        .Select(e => e.Deserialize<T>(ReadOptions)!)
        .ToList();
}

public enum ConfigMgrFailure
{
    InvalidSiteCode,
    PowerShellNotStarted,
    ModuleNotFound,
    DriveNotConnected,
    AccessDenied,
    ProcessDied,
    Timeout,
    ScriptFailed,
    RequestTooLarge
}

public sealed class ConfigMgrSessionException(ConfigMgrFailure failure, string message) : Exception(message)
{
    public ConfigMgrFailure Failure { get; } = failure;
}
