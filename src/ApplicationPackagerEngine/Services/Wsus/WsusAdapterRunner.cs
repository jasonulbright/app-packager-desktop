using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// One adapter call's result document: Ok, ErrorKind and Error from the
/// adapter, Data the operation's own object.
/// </summary>
public sealed record WsusAdapterResult(bool Ok, string ErrorKind, string Error, JsonObject? Data)
{
    public static WsusAdapterResult Failure(string kind, string error) => new(false, kind, error, null);
}

/// <summary>The error kinds the adapter reports.</summary>
public static class WsusErrorKinds
{
    public const string ApiMissing = "ApiMissing";
    public const string Unreachable = "Unreachable";
    public const string Connect = "Connect";
    public const string NotAdministrator = "NotAdministrator";
    public const string InsecureConnection = "InsecureConnection";
    public const string NoCertificate = "NoCertificate";
    public const string SelfSignedRefused = "SelfSignedRefused";
    public const string PublishRejected = "PublishRejected";

    /// <summary>A repeat publish of a version whose update on the server differs from the stage.</summary>
    public const string UpdateDiffers = "UpdateDiffers";
    public const string Input = "Input";
    public const string Failed = "Failed";
}

/// <summary>Runs one adapter operation. The seam lets the orchestration run under test without powershell.exe.</summary>
public interface IWsusAdapterRunner
{
    /// <param name="request">The operation's input; Operation and ResultPath are added by the runner.</param>
    /// <param name="standardInput">Text for the child's standard input (the PFX password); never written to a file.</param>
    Task<WsusAdapterResult> RunAsync(string operation, JsonObject request, string? standardInput = null,
        Action<string>? onProgress = null, CancellationToken ct = default);
}

/// <summary>
/// Runs the embedded WsusAdapter.ps1 in a Windows PowerShell 5.1 child. The
/// request goes to a JSON file in a private temporary folder, the result
/// comes back as one JSON document in the same folder, and the folder is
/// removed afterwards. The script is the application's own and is not
/// deployment content, so the deployment signing policy does not apply to it.
/// </summary>
public sealed class WsusAdapterRunner : IWsusAdapterRunner
{
    /// <summary>How long one adapter child may run before it is stopped.</summary>
    public static readonly TimeSpan DefaultChildTimeout = TimeSpan.FromSeconds(1800);

    /// <summary>Runs the child; the seam lets a test stand in for powershell.exe.</summary>
    internal delegate Task<(int ExitCode, string StdOut, string StdErr)> ChildHost(
        string script, Action<string>? onProgress, CancellationToken ct, string prefix,
        IReadOnlyList<string> arguments, string standardInput, TimeSpan timeout);

    private readonly TimeSpan _timeout;
    private readonly ChildHost _host;

    public WsusAdapterRunner() : this(DefaultChildTimeout) { }

    public WsusAdapterRunner(TimeSpan timeout) : this(timeout, null) { }

    internal WsusAdapterRunner(TimeSpan timeout, ChildHost? host)
    {
        _timeout = timeout;
        _host = host ?? ((script, progress, ct, prefix, arguments, stdin, limit) =>
            WindowsPowerShellHost.RunAsync(script, progress, ct, prefix, arguments, stdin, limit));
    }

    public const string ResourceName = "ApplicationPackagerEngine.Services.Wsus.WsusAdapter.ps1";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    /// <summary>The adapter script text as shipped in the assembly.</summary>
    public static string ScriptText()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public async Task<WsusAdapterResult> RunAsync(string operation, JsonObject request, string? standardInput = null,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"apwsus-req-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var inputPath = Path.Combine(folder, "request.json");
            var resultPath = Path.Combine(folder, "result.json");
            var document = (JsonObject)request.DeepClone();
            document["Operation"] = operation;
            document["ResultPath"] = resultPath;
            await File.WriteAllTextAsync(inputPath, document.ToJsonString(WriteOptions), new UTF8Encoding(false), ct);

            try
            {
                var run = await _host(ScriptText(), onProgress, ct, "ap-wsus", ["-InputPath", inputPath], standardInput ?? "", _timeout);
                return ReadResult(resultPath, run.ExitCode, run.StdErr);
            }
            catch (TimeoutException ex)
            {
                return WsusAdapterResult.Failure(WsusErrorKinds.Failed, ex.Message);
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    /// <summary>The result document, or a Failed result naming the exit code and standard error when the adapter wrote none.</summary>
    public static WsusAdapterResult ReadResult(string resultPath, int exitCode, string stdErr)
    {
        if (!File.Exists(resultPath))
        {
            var error = string.IsNullOrWhiteSpace(stdErr)
                ? $"The WSUS adapter ended with exit code {exitCode} and wrote no result."
                : stdErr.Trim();
            return WsusAdapterResult.Failure(WsusErrorKinds.Failed, error);
        }
        return Parse(File.ReadAllText(resultPath, Encoding.UTF8));
    }

    public static WsusAdapterResult Parse(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("The WSUS adapter result is empty.");
        return new WsusAdapterResult(
            node["Ok"]?.GetValue<bool>() ?? false,
            node["ErrorKind"]?.GetValue<string>() ?? "",
            node["Error"]?.GetValue<string>() ?? "",
            node["Data"] as JsonObject);
    }
}
