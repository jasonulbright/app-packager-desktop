using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>Options for one Intune Win32 publish.</summary>
public sealed class IntunePublishOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Description { get; set; } = "";
    public string MinimumSupportedWindowsRelease { get; set; } = "Windows10_21H2";
    /// <summary>x86 or x64.</summary>
    public string Architecture { get; set; } = "x64";
    public string GraphBase { get; set; } = "https://graph.microsoft.com/v1.0";
    public string TokenAuthority { get; set; } = "https://login.microsoftonline.com";
    public int PollTimeoutSec { get; set; } = 600;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan CommitPollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int BlockSizeBytes { get; set; } = 6 * 1024 * 1024;
}

/// <summary>
/// Publishes a packaged .intunewin as an Intune Win32 app through Graph v1.0: create or update
/// the win32LobApp, create a content version and file entry, wait for the Azure Storage URI,
/// upload the encrypted payload in blocks, commit with the package's fileEncryptionInfo, wait
/// for the commit, and patch committedContentVersion. Assignment is left to the operator.
/// Requires an Entra app registration with application permission
/// DeviceManagementApps.ReadWrite.All (admin-consented).
/// </summary>
public sealed class IntunePublishService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly IntuneWinPackageReader _reader;

    public IntunePublishService(HttpMessageHandler? handler = null, IntuneWinPackageReader? reader = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _reader = reader ?? new IntuneWinPackageReader();
    }

    public IntunePublishService(HttpClient http, IntuneWinPackageReader? reader = null)
    {
        _http = http;
        _ownsClient = false;
        _reader = reader ?? new IntuneWinPackageReader();
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    /// <summary>Publishes the package and returns the mobile app id.</summary>
    public async Task<string> PublishAsync(
        string intuneWinPath,
        StageManifest manifest,
        IntunePublishOptions options,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var meta = _reader.ReadMetadata(intuneWinPath);
        var rules = BuildRules(manifest.Detection);

        var installCommand = FirstNonBlank(manifest.CommandOverrides?.Install, "install.bat");
        var uninstallCommand = FirstNonBlank(manifest.CommandOverrides?.Uninstall, "uninstall.bat");

        var token = await GetTokenAsync(options, ct).ConfigureAwait(false);
        var graph = options.GraphBase.TrimEnd('/');

        // Repeat publishes update the existing app (a new content version on the same app
        // identity) instead of stacking duplicates in the console.
        var filterName = manifest.AppName.Replace("'", "''");
        var listUri = $"{graph}/deviceAppManagement/mobileApps?$filter=displayName eq '{filterName}'";
        var existing = await GraphJsonAsync(HttpMethod.Get, listUri, token, null, ct).ConfigureAwait(false);
        var existingApp = existing?["value"] as JsonArray;
        var match = existingApp?.FirstOrDefault(n =>
            (string?)n?["@odata.type"] == "#microsoft.graph.win32LobApp");

        var appBody = new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.win32LobApp",
            ["displayName"] = manifest.AppName,
            ["description"] = string.IsNullOrWhiteSpace(options.Description) ? manifest.AppName : options.Description,
            ["publisher"] = manifest.Publisher,
            ["fileName"] = Path.GetFileName(intuneWinPath),
            ["setupFilePath"] = meta.SetupFile,
            ["installCommandLine"] = installCommand,
            ["uninstallCommandLine"] = uninstallCommand,
            ["applicableArchitectures"] = options.Architecture,
            ["minimumSupportedWindowsRelease"] = options.MinimumSupportedWindowsRelease,
            ["installExperience"] = new JsonObject
            {
                ["runAsAccount"] = "system",
                ["deviceRestartBehavior"] = "basedOnReturnCode"
            },
            ["rules"] = rules,
            ["returnCodes"] = new JsonArray(
                ReturnCode(0, "success"),
                ReturnCode(1707, "success"),
                ReturnCode(3010, "softReboot"),
                ReturnCode(1641, "hardReboot"),
                ReturnCode(1618, "retry"))
        };

        string appId;
        if (match is not null)
        {
            appId = (string?)match["id"] ?? throw new InvalidOperationException("Existing Win32 app carries no id.");
            onProgress?.Invoke($"Updating Intune Win32 app    : {manifest.AppName} (app id {appId})");
            await GraphJsonAsync(HttpMethod.Patch, $"{graph}/deviceAppManagement/mobileApps/{appId}", token, appBody, ct).ConfigureAwait(false);
        }
        else
        {
            onProgress?.Invoke($"Creating Intune Win32 app    : {manifest.AppName}");
            var created = await GraphJsonAsync(HttpMethod.Post, $"{graph}/deviceAppManagement/mobileApps", token, appBody, ct).ConfigureAwait(false);
            appId = (string?)created?["id"] ?? throw new InvalidOperationException("Graph returned no app id for the created Win32 app.");
            onProgress?.Invoke($"Intune app id                : {appId}");
        }

        var lobBase = $"{graph}/deviceAppManagement/mobileApps/{appId}/microsoft.graph.win32LobApp";
        var content = await GraphJsonAsync(HttpMethod.Post, $"{lobBase}/contentVersions", token, new JsonObject(), ct).ConfigureAwait(false);
        var contentId = (string?)content?["id"] ?? throw new InvalidOperationException("Graph returned no content version id.");

        var payloadPath = Path.GetTempFileName();
        try
        {
            var payloadSize = _reader.ExtractPayload(intuneWinPath, payloadPath);

            var fileBody = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.mobileAppContentFile",
                ["name"] = meta.FileName,
                ["size"] = meta.UnencryptedContentSize,
                ["sizeEncrypted"] = payloadSize,
                ["manifest"] = null,
                ["isDependency"] = false
            };
            var file = await GraphJsonAsync(HttpMethod.Post, $"{lobBase}/contentVersions/{contentId}/files", token, fileBody, ct).ConfigureAwait(false);
            var fileId = (string?)file?["id"] ?? throw new InvalidOperationException("Graph returned no content file id.");
            var fileUri = $"{lobBase}/contentVersions/{contentId}/files/{fileId}";

            var storageUri = await WaitForStorageUriAsync(fileUri, token, options, ct).ConfigureAwait(false);

            onProgress?.Invoke($"Uploading encrypted payload  : {Math.Round(payloadSize / 1048576.0, 1)} MB");
            await UploadBlockBlobAsync(storageUri, payloadPath, options.BlockSizeBytes, onProgress, ct).ConfigureAwait(false);

            var commitBody = new JsonObject
            {
                ["fileEncryptionInfo"] = new JsonObject
                {
                    ["encryptionKey"] = meta.EncryptionKey,
                    ["macKey"] = meta.MacKey,
                    ["initializationVector"] = meta.InitializationVector,
                    ["mac"] = meta.Mac,
                    ["profileIdentifier"] = meta.ProfileIdentifier,
                    ["fileDigest"] = meta.FileDigest,
                    ["fileDigestAlgorithm"] = meta.FileDigestAlgorithm
                }
            };
            await GraphJsonAsync(HttpMethod.Post, $"{fileUri}/commit", token, commitBody, ct).ConfigureAwait(false);

            await WaitForCommitAsync(fileUri, token, options, ct).ConfigureAwait(false);

            await GraphJsonAsync(HttpMethod.Patch, $"{graph}/deviceAppManagement/mobileApps/{appId}", token, new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.win32LobApp",
                ["committedContentVersion"] = contentId
            }, ct).ConfigureAwait(false);

            onProgress?.Invoke($"Published to Intune          : {manifest.AppName} (app id {appId})");
            return appId;
        }
        finally
        {
            try { File.Delete(payloadPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Acquires an app-only Graph token via the client-credentials flow.</summary>
    public async Task<string> GetTokenAsync(IntunePublishOptions options, CancellationToken ct = default)
    {
        var uri = $"{options.TokenAuthority.TrimEnd('/')}/{options.TenantId}/oauth2/v2.0/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default"
            })
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, uri, ct).ConfigureAwait(false);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions, ct).ConfigureAwait(false);
        var token = (string?)json?["access_token"];
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Token endpoint returned no access_token.");
        return token;
    }

    /// <summary>One Graph REST call. Central so retries and tests hang off one seam.</summary>
    public async Task<JsonObject?> GraphJsonAsync(HttpMethod method, string uri, string token, JsonNode? body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, uri, ct).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text) as JsonObject;
    }

    /// <summary>
    /// Uploads a file to an Azure block-blob SAS URI: PUT each block to
    /// &amp;comp=block&amp;blockid=&lt;base64 id&gt;, then PUT the ordered block list to &amp;comp=blocklist.
    /// 6 MiB blocks keep each request under proxy limits without ballooning the block count.
    /// </summary>
    public async Task UploadBlockBlobAsync(
        string uri, string filePath, int blockSize = 6 * 1024 * 1024,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var blockIds = new List<string>();
        var buffer = new byte[blockSize];

        await using (var stream = File.OpenRead(filePath))
        {
            var index = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false)) > 0)
            {
                var blockId = Convert.ToBase64String(Encoding.ASCII.GetBytes($"block-{index:D6}"));
                blockIds.Add(blockId);

                var blockUri = $"{uri}&comp=block&blockid={Uri.EscapeDataString(blockId)}";
                using var request = new HttpRequestMessage(HttpMethod.Put, blockUri);
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                request.Content = new ByteArrayContent(chunk);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                await EnsureSuccessAsync(response, blockUri, ct).ConfigureAwait(false);
                index++;
            }
        }

        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>"
                  + string.Concat(blockIds.Select(id => $"<Latest>{id}</Latest>"))
                  + "</BlockList>";
        var listUri = $"{uri}&comp=blocklist";
        using (var request = new HttpRequestMessage(HttpMethod.Put, listUri))
        {
            request.Content = new StringContent(xml, Encoding.UTF8, "text/plain");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, listUri, ct).ConfigureAwait(false);
        }

        onProgress?.Invoke($"Uploaded payload             : {blockIds.Count} block(s)");
    }

    /// <summary>Maps a stage-manifest detection onto Graph win32LobApp detection rules.</summary>
    public static JsonArray BuildRules(StageDetection detection)
        => new(MapOne(detection));

    private static JsonNode MapOne(StageDetection d)
    {
        var type = string.IsNullOrWhiteSpace(d.Type) ? "RegistryKeyValue" : d.Type;
        var check32 = !d.Is64Bit;

        switch (type)
        {
            case "RegistryKeyValue":
            {
                var expected = d.ExpectedValue ?? "";
                var valueName = string.IsNullOrWhiteSpace(d.ValueName) ? "DisplayVersion" : d.ValueName;
                var operationType = Version.TryParse(expected, out _) ? "version" : "string";
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppRegistryRule",
                    ["ruleType"] = "detection",
                    ["check32BitOn64System"] = check32,
                    ["keyPath"] = @"HKEY_LOCAL_MACHINE\" + (d.RegistryKeyRelative ?? ""),
                    ["valueName"] = valueName,
                    ["operationType"] = operationType,
                    ["operator"] = VersionOperator(d.Operator),
                    ["comparisonValue"] = expected
                };
            }
            case "RegistryKey":
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppRegistryRule",
                    ["ruleType"] = "detection",
                    ["check32BitOn64System"] = check32,
                    ["keyPath"] = @"HKEY_LOCAL_MACHINE\" + (d.RegistryKeyRelative ?? ""),
                    ["valueName"] = null,
                    ["operationType"] = "exists",
                    ["operator"] = "notConfigured",
                    ["comparisonValue"] = null
                };
            case "File":
            {
                if (string.Equals(d.PropertyType, "Version", StringComparison.OrdinalIgnoreCase))
                {
                    return new JsonObject
                    {
                        ["@odata.type"] = "#microsoft.graph.win32LobAppFileSystemRule",
                        ["ruleType"] = "detection",
                        ["path"] = d.FilePath ?? "",
                        ["fileOrFolderName"] = d.FileName ?? "",
                        ["check32BitOn64System"] = check32,
                        ["operationType"] = "version",
                        ["operator"] = VersionOperator(d.Operator),
                        ["comparisonValue"] = d.ExpectedValue ?? ""
                    };
                }
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppFileSystemRule",
                    ["ruleType"] = "detection",
                    ["path"] = d.FilePath ?? "",
                    ["fileOrFolderName"] = d.FileName ?? "",
                    ["check32BitOn64System"] = check32,
                    ["operationType"] = "exists",
                    ["operator"] = "notConfigured",
                    ["comparisonValue"] = null
                };
            }
            case "Script":
            {
                // The schema carries the detection script body in ExpectedValue; there is no
                // separate script field on StageDetection.
                var scriptText = d.ExpectedValue ?? "";
                if (string.IsNullOrWhiteSpace(scriptText))
                    throw new InvalidOperationException("Script detection carries no script text; the Intune rule cannot be built.");
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppPowerShellScriptRule",
                    ["ruleType"] = "detection",
                    ["enforceSignatureCheck"] = false,
                    ["runAs32Bit"] = false,
                    ["scriptContent"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptText)),
                    ["operationType"] = "notConfigured",
                    ["operator"] = "notConfigured"
                };
            }
            default:
                throw new InvalidOperationException($"Detection type '{type}' has no Intune rule mapping.");
        }
    }

    private static string VersionOperator(string? op)
        => string.Equals(op, "GreaterEquals", StringComparison.OrdinalIgnoreCase) ? "greaterThanOrEqual" : "equal";

    private async Task<string> WaitForStorageUriAsync(string fileUri, string token, IntunePublishOptions options, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(options.PollTimeoutSec);
        string? storageUri = null;
        do
        {
            await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
            var file = await GraphJsonAsync(HttpMethod.Get, fileUri, token, null, ct).ConfigureAwait(false);
            var state = (string?)file?["uploadState"] ?? "";
            if (state.Contains("Failed", StringComparison.Ordinal) || state.Contains("TimedOut", StringComparison.Ordinal))
                throw new InvalidOperationException($"Azure storage URI request failed: {state}");
            storageUri = (string?)file?["azureStorageUri"];
        }
        while (string.IsNullOrEmpty(storageUri) && DateTimeOffset.UtcNow <= deadline);

        if (string.IsNullOrEmpty(storageUri))
            throw new TimeoutException("Timed out waiting for the Azure storage upload URI.");
        return storageUri;
    }

    private async Task WaitForCommitAsync(string fileUri, string token, IntunePublishOptions options, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(options.PollTimeoutSec);
        var state = "";
        do
        {
            await Task.Delay(options.CommitPollInterval, ct).ConfigureAwait(false);
            var file = await GraphJsonAsync(HttpMethod.Get, fileUri, token, null, ct).ConfigureAwait(false);
            state = (string?)file?["uploadState"] ?? "";
            if (state is "commitFileFailed" or "commitFileTimedOut")
                throw new InvalidOperationException($"Content commit failed: {state}");
        }
        while (state != "commitFileSuccess" && DateTimeOffset.UtcNow <= deadline);

        if (state != "commitFileSuccess")
            throw new TimeoutException($"Timed out waiting for content commit (last state: {state}).");
    }

    private static JsonObject ReturnCode(int code, string type)
        => new() { ["returnCode"] = code, ["type"] = type };

    private static string FirstNonBlank(string? candidate, string fallback)
        => string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string uri, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Length > 500) body = body[..500];
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} from {uri}: {body}");
    }
}
