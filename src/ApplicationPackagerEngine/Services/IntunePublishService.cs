using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

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
/// The finished Graph values of an app converted from ConfigMgr. They replace what a stage derives
/// from a detection rule: the rules, the return codes, the restart behavior, the hardware minimums
/// and the architectures. The converted plan carries its own findings, so the compatibility check
/// of a stage does not run for such an app.
/// </summary>
public sealed record IntuneConversionOverrides(
    IReadOnlyList<JsonObject> Rules,
    IReadOnlyList<JsonObject> ReturnCodes,
    string RestartBehavior,
    IReadOnlyDictionary<string, int> Minimums,
    string AllowedArchitectures);

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
        CancellationToken ct = default,
        IntuneScriptRuleContext? scriptSigning = null,
        IReadOnlyList<IntuneCompatibilityFinding>? findings = null,
        IntuneConversionOverrides? conversion = null)
    {
        var meta = _reader.ReadMetadata(intuneWinPath);
        if (conversion is not null) findings ??= [];

        // Decided before the first Graph call: a blocking gap must not leave a
        // half-created app in the tenant. Findings a caller passes were already logged.
        if (findings is null)
        {
            findings = IntuneCompatibilityFindings.Build(manifest);
            foreach (var finding in findings) onProgress?.Invoke(IntuneCompatibilityFindings.LogLine(finding));
        }
        if (IntuneCompatibilityFindings.RefusalMessage(findings) is { } refusal)
            throw new InvalidOperationException(refusal);

        var rules = conversion is null
            ? BuildRules(manifest.Detection, scriptSigning)
            : new JsonArray(CloneAll(conversion.Rules, "rules"));

        var installCommand = FirstNonBlank(manifest.CommandOverrides?.Install, "install.bat");
        var uninstallCommand = FirstNonBlank(manifest.CommandOverrides?.Uninstall, "uninstall.bat");
        var identityTag = IdentityTag(manifest);

        var token = await GetTokenAsync(options, ct).ConfigureAwait(false);
        var graph = options.GraphBase.TrimEnd('/');

        // Repeat publishes update the existing app (a new content version on the same app
        // identity) instead of stacking duplicates in the console.
        var match = await FindExistingAppAsync(graph, token, identityTag, manifest.AppName, ct, FormerIdentityTags(manifest)).ConfigureAwait(false);

        // applicableArchitectures has no arm64 member; arm64 is expressed through
        // allowedArchitectures, and Graph forces applicableArchitectures to none whenever
        // allowedArchitectures is non-null. Both are written on every publish so a repeat
        // publish that moves an app between architectures cannot leave a stale
        // allowedArchitectures behind to override the incoming one.
        var (applicableArchitectures, allowedArchitectures) = conversion is null
            ? GraphArchitectures(options.Architecture)
            : GraphArchitecturesOfConversion(conversion.AllowedArchitectures);

        var appBody = new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.win32LobApp",
            ["applicableArchitectures"] = applicableArchitectures,
            ["allowedArchitectures"] = allowedArchitectures,
            ["displayName"] = manifest.AppName,
            ["description"] = string.IsNullOrWhiteSpace(options.Description) ? manifest.AppName : options.Description,
            ["publisher"] = manifest.Publisher,
            ["fileName"] = Path.GetFileName(intuneWinPath),
            // The real entry the package runs; the prep tool's own setup reference is
            // only correct for the generic install.bat layout.
            ["setupFilePath"] = FirstNonBlank(manifest.SetupFile, meta.SetupFile),
            ["installCommandLine"] = installCommand,
            ["uninstallCommandLine"] = uninstallCommand,
            ["notes"] = NotesWithTag((string?)match?["notes"], identityTag),
            ["minimumSupportedWindowsRelease"] = options.MinimumSupportedWindowsRelease,
            ["installExperience"] = new JsonObject
            {
                // A per-user deployment runs its installer in the user's session; a
                // machine install under the user account has no rights to Program Files.
                ["runAsAccount"] = (manifest.InstallationBehaviorType is { } behavior
                    ? behavior.Equals("InstallForUser", StringComparison.OrdinalIgnoreCase)
                    : manifest.InstallContext.Equals("User", StringComparison.OrdinalIgnoreCase)) ? "user" : "system",
                ["deviceRestartBehavior"] = conversion?.RestartBehavior ?? DeviceRestartBehavior(manifest.PostExecutionBehavior)
            },
            ["rules"] = rules,
            ["returnCodes"] = conversion is null
                ? new JsonArray(
                    ReturnCode(0, "success"),
                    ReturnCode(1707, "success"),
                    ReturnCode(3010, "softReboot"),
                    ReturnCode(1641, "hardReboot"),
                    ReturnCode(1618, "retry"))
                : new JsonArray(CloneAll(conversion.ReturnCodes, "return codes"))
        };
        if (conversion is not null)
        {
            if (conversion.Minimums.TryGetValue("MinimumMemoryInMB", out var memory)) appBody["minimumMemoryInMB"] = memory;
            if (conversion.Minimums.TryGetValue("MinimumNumberOfProcessors", out var processors)) appBody["minimumNumberOfProcessors"] = processors;
        }

        // largeIcon is a mimeContent (type + base64 bytes) on the app entity,
        // not a separate upload. An unresolvable icon leaves the property unset
        // rather than failing the publish.
        // A custom icon chosen in a profile outranks the extracted or icon-pack image.
        var iconName = FirstNonBlank(
            manifest.CustomAssets?.FirstOrDefault(a => string.Equals(a.Category, "Icon", StringComparison.OrdinalIgnoreCase))?.RelativePath,
            manifest.Icon ?? "");
        if (!string.IsNullOrWhiteSpace(iconName))
        {
            var iconPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(intuneWinPath))!, iconName);
            var largeIcon = BuildIconMimeContent(iconPath);
            if (largeIcon is not null)
            {
                appBody["largeIcon"] = largeIcon;
                onProgress?.Invoke($"Intune app icon              : {iconName} ({(string?)largeIcon["type"]})");
            }
            else
            {
                onProgress?.Invoke($"Intune app icon not applied  : {iconPath}");
            }
        }

        string appId;
        if (match is not null)
        {
            appId = (string?)match["id"] ?? throw new InvalidOperationException("Existing Win32 app carries no id.");
            onProgress?.Invoke($"Updating Intune Win32 app    : {manifest.AppName} (app id {appId})");
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

            // Existing assignments must keep the old commands and detection while their
            // old content is active. Activate the replacement metadata and content together
            // only after Intune has validated the uploaded file.
            var activation = match is not null ? appBody : new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.win32LobApp"
            };
            activation["committedContentVersion"] = contentId;
            await GraphJsonAsync(HttpMethod.Patch, $"{graph}/deviceAppManagement/mobileApps/{appId}", token, activation, ct).ConfigureAwait(false);

            onProgress?.Invoke($"Published to Intune          : {manifest.AppName} (app id {appId})");
            return appId;
        }
        finally
        {
            try { File.Delete(payloadPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The notes value that identifies an Intune app as this build's target:
    /// AppPackager:&lt;ApplicationId&gt;/&lt;ProfileId&gt;. A manifest with no ApplicationId takes
    /// a legacy key derived from its whole title, version included, so two major lines
    /// whose titles differ only in the version never share one app; no ProfileId reads
    /// as default.
    /// </summary>
    public static string IdentityTag(StageManifest manifest)
    {
        var applicationId = manifest.ApplicationId;
        if (string.IsNullOrWhiteSpace(applicationId)) applicationId = LegacyKey(manifest);
        return $"AppPackager:{applicationId}/{ProfileOf(manifest)}";
    }

    /// <summary>
    /// For a manifest that carries an ApplicationId, the legacy tag the same manifest
    /// carried before it had one. An app carrying it, and no other identity tag, is this
    /// application's and is updated and retagged, not duplicated. Empty for a manifest
    /// without an ApplicationId.
    /// </summary>
    public static IReadOnlyList<string> FormerIdentityTags(StageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.ApplicationId)) return [];
        var former = $"AppPackager:{LegacyKey(manifest)}/{ProfileOf(manifest)}";
        return former == IdentityTag(manifest) ? [] : [former];
    }

    private static string ProfileOf(StageManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.ProfileId) ? "default" : manifest.ProfileId;

    /// <summary>legacy: and the title with every character outside [\w.-] replaced by a hyphen.</summary>
    private static string LegacyKey(StageManifest manifest) =>
        "legacy:" + System.Text.RegularExpressions.Regex.Replace(manifest.AppName ?? "", @"[^\w.-]", "-");

    /// <summary>
    /// The installExperience.deviceRestartBehavior for every published app. The restart
    /// decision follows the installer's return code whatever the ConfigMgr post-execution
    /// behavior is; the return code table carries the soft and hard reboot codes.
    /// </summary>
    public static string DeviceRestartBehavior(string? postExecutionBehavior) => "basedOnReturnCode";

    /// <summary>
    /// The existing Win32 app this publish updates. notes is not a filterable property, so
    /// every Win32 app is paged and matched on the identity tag first; with no tagged app the
    /// display name decides among apps that carry no identity tag at all, so an app tagged
    /// for another application or profile is never adopted. More than one candidate on
    /// either path refuses the publish. With no app on the identity tag, an app on one of
    /// <paramref name="formerTags"/> is this application's and is the match.
    /// </summary>
    public async Task<JsonNode?> FindExistingAppAsync(string graph, string token, string identityTag, string displayName, CancellationToken ct = default,
        IReadOnlyCollection<string>? formerTags = null)
    {
        const string Win32Type = "#microsoft.graph.win32LobApp";
        var tagged = new List<JsonNode>();
        var formerTagged = new List<JsonNode>();
        string? uri = $"{graph}/deviceAppManagement/mobileApps?$filter=isof('microsoft.graph.win32LobApp')";
        while (!string.IsNullOrEmpty(uri))
        {
            var page = await GraphJsonAsync(HttpMethod.Get, uri, token, null, ct).ConfigureAwait(false);
            foreach (var app in page?["value"] as JsonArray ?? [])
                if (app is not null && (string?)app["@odata.type"] == Win32Type)
                {
                    if (CarriesTag((string?)app["notes"], identityTag)) tagged.Add(app);
                    else if (formerTags is { Count: > 0 } && CarriesOnlyTagsOf((string?)app["notes"], formerTags)) formerTagged.Add(app);
                }
            uri = (string?)page?["@odata.nextLink"];
        }
        if (tagged.Count > 1)
            throw new InvalidOperationException(
                $"Multiple Intune apps carry the identity tag '{identityTag}' (ids: {string.Join(", ", tagged.Select(a => (string?)a["id"]))}); resolve the duplicates before publishing.");
        if (tagged.Count == 1) return tagged[0];
        if (formerTagged.Count > 1)
            throw new InvalidOperationException(
                $"Multiple Intune apps carry an earlier identity tag of this application (ids: {string.Join(", ", formerTagged.Select(a => (string?)a["id"]))}); resolve the duplicates before publishing.");
        if (formerTagged.Count == 1) return formerTagged[0];

        var filter = Uri.EscapeDataString($"displayName eq '{displayName.Replace("'", "''")}'");
        uri = $"{graph}/deviceAppManagement/mobileApps?$filter={filter}";
        var candidates = new List<JsonNode>();
        while (!string.IsNullOrEmpty(uri))
        {
            var page = await GraphJsonAsync(HttpMethod.Get, uri, token, null, ct).ConfigureAwait(false);
            foreach (var app in page?["value"] as JsonArray ?? [])
                if (app is not null && (string?)app["@odata.type"] == Win32Type && !CarriesAnyTag((string?)app["notes"]))
                    candidates.Add(app);
            uri = (string?)page?["@odata.nextLink"];
        }
        if (candidates.Count > 1)
            throw new InvalidOperationException(
                $"Multiple Intune apps are named '{displayName}' and none carries this application's identity tag (ids: {string.Join(", ", candidates.Select(a => (string?)a?["id"]))}); rename or tag the intended app before publishing.");
        return candidates.FirstOrDefault();
    }

    private const string IdentityTagPrefix = "AppPackager:";

    private static IEnumerable<string> NoteLines(string? notes) =>
        (notes ?? "").Split('\n').Select(line => line.Trim());

    /// <summary>True when one line of <paramref name="notes"/> is exactly the identity tag.</summary>
    public static bool CarriesTag(string? notes, string identityTag) =>
        NoteLines(notes).Any(line => line == identityTag);

    /// <summary>
    /// True when <paramref name="notes"/> carries at least one identity tag and every
    /// identity tag line in it is one of <paramref name="tags"/>.
    /// </summary>
    public static bool CarriesOnlyTagsOf(string? notes, IReadOnlyCollection<string> tags)
    {
        var tagLines = NoteLines(notes).Where(line => line.StartsWith(IdentityTagPrefix, StringComparison.Ordinal)).ToList();
        return tagLines.Count > 0 && tagLines.All(tags.Contains);
    }

    /// <summary>True when one line of <paramref name="notes"/> is an identity tag of any application.</summary>
    public static bool CarriesAnyTag(string? notes) =>
        NoteLines(notes).Any(line => line.StartsWith(IdentityTagPrefix, StringComparison.Ordinal));

    /// <summary>
    /// The notes an app is written with: the operator's lines as they were, without any
    /// identity tag line, then the identity tag on its own line.
    /// </summary>
    public static string NotesWithTag(string? notes, string identityTag)
    {
        if (string.IsNullOrWhiteSpace(notes)) return identityTag;
        var newline = notes.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var kept = notes.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => !line.Trim().StartsWith(IdentityTagPrefix, StringComparison.Ordinal))
            .ToList();
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1])) kept.RemoveAt(kept.Count - 1);
        kept.Add(identityTag);
        return string.Join(newline, kept);
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
                await EnsureSuccessAsync(response, blockUri, ct, includeResponseBody: false).ConfigureAwait(false);
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
            await EnsureSuccessAsync(response, listUri, ct, includeResponseBody: false).ConfigureAwait(false);
        }

        onProgress?.Invoke($"Uploaded payload             : {blockIds.Count} block(s)");
    }

    /// <summary>
    /// Builds a win32LobApp.largeIcon mimeContent for an icon file: a content
    /// mime type plus the raw bytes, which Graph JSON carries base64-encoded.
    /// Null when the file is missing, empty, or unreadable.
    /// </summary>
    public static JsonObject? BuildIconMimeContent(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return null;
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
        if (bytes.Length == 0) return null;

        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/x-icon"
        };

        return new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.mimeContent",
            ["type"] = type,
            ["value"] = Convert.ToBase64String(bytes)
        };
    }

    /// <summary>
    /// Splits an installer architecture across the two Graph properties. x86 and x64 are
    /// members of applicableArchitectures; arm64 is not, and is carried by
    /// allowedArchitectures with applicableArchitectures set to none. The unused property
    /// is written explicitly rather than omitted, so updating an existing app clears
    /// whichever value the previous publish set.
    /// </summary>
    public static (string Applicable, string? Allowed) GraphArchitectures(string? architecture)
    {
        var resolved = InstallerArchitectures.Resolve(architecture);
        return resolved == InstallerArchitectures.Arm64
            ? ("none", "arm64")
            : (resolved, null);
    }

    private static JsonNode[] CloneAll(IReadOnlyList<JsonObject> items, string what)
    {
        if (items.Any(i => i is null)) throw new InvalidOperationException($"The converted {what} hold an empty entry.");
        return items.Select(i => (JsonNode)i.DeepClone()).ToArray();
    }

    /// <summary>
    /// The architecture properties of a converted app. One architecture, or x86 with x64, is the documented
    /// applicableArchitectures. A set with arm64 in it is sent as allowedArchitectures, with
    /// applicableArchitectures set to none.
    /// </summary>
    public static (string Applicable, string? Allowed) GraphArchitecturesOfConversion(string architectures)
    {
        var set = architectures.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.ToLowerInvariant()).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
        var unknown = set.FirstOrDefault(a => a is not ("x86" or "x64" or "arm64"));
        if (unknown is not null)
            throw new InvalidOperationException($"The architecture '{unknown}' is not one of x86, x64 and arm64.");
        if (set.Count == 0) return GraphArchitectures("x64");
        if (set.Contains("arm64")) return ("none", string.Join(",", set));
        return (string.Join(",", set), null);
    }

    /// <summary>
    /// enforceSignatureCheck and runAs32Bit for a PowerShell script rule. The
    /// check is set when the detection category produced a valid signature or
    /// the policy requires one; a required signature that was not produced
    /// refuses the publish instead of shipping an unenforced rule. Signature
    /// enforcement only asks the client to verify; it never signs.
    /// </summary>
    public static (bool EnforceSignatureCheck, bool RunAs32Bit) ScriptRuleSettings(IntuneScriptRuleContext? context)
    {
        if (context is null) return (false, false);
        var status = context.DetectionStatus ?? "";
        var signed = SigningStatus.IsSigned(status);
        if (context.RequireDetection && !signed)
            throw new InvalidOperationException(
                $"Valid detection script signatures are required, but the staged detection reports status '{(status.Length > 0 ? status : "none")}'; re-stage with signing enabled before publishing to Intune.");
        return (signed || context.RequireDetection,
            string.Equals(context.ScriptHost, DeploymentLauncher.HostX86, StringComparison.Ordinal));
    }

    public static JsonArray BuildRules(StageDetection detection) => BuildRules(detection, null);

    /// <summary>
    /// The Graph detection rules for a manifest detection. With a signing
    /// context, script rules carry the resolved signature settings, and a
    /// Script detection with a finalized file publishes that file's exact
    /// bytes, because re-encoding the text reproduces signed bytes only for an
    /// ASCII or BOM-less UTF-8 original.
    /// </summary>
    public static JsonArray BuildRules(StageDetection detection, IntuneScriptRuleContext? signing)
    {
        // A manifest that carries no value converts; only an explicit false opts out.
        var conversionAllowed = detection.IntuneScriptConversion != false;
        if (!string.Equals(detection.Type, "Compound", StringComparison.OrdinalIgnoreCase))
            return new JsonArray(MapOne(detection, signing, signing?.DetectionScriptFile, conversionAllowed));

        // Graph ANDs every detection rule it is given, so an OR-connected
        // compound would publish as a narrower rule than the definition states.
        if (string.Equals(detection.Connector, "Or", StringComparison.OrdinalIgnoreCase)
            || detection.GroupSizes is { Length: > 0 })
            throw new InvalidOperationException(
                "OR-connected compound detections cannot map to Intune rules (Graph ANDs all detection rules); use a Script detection for this app.");

        var clauses = detection.Clauses ?? [];
        if (clauses.Length < 2)
            throw new InvalidOperationException("A Compound detection requires at least two Clauses.");

        var rules = clauses.Select(c => MapOne(c, signing, null, conversionAllowed)).ToArray();
        if (rules.Length > 1 && rules.Any(IsScriptRule))
            throw new InvalidOperationException(
                "A prefix, suffix, or substring clause maps to a script rule, which this mapping does not combine with other clauses; use a Script detection for this app.");

        return new JsonArray(rules);
    }

    private static bool IsScriptRule(JsonNode rule) =>
        string.Equals((string?)rule["@odata.type"], "#microsoft.graph.win32LobAppPowerShellScriptRule", StringComparison.Ordinal);

    private static JsonNode MapOne(StageDetection d, IntuneScriptRuleContext? signing, string? scriptFile, bool conversionAllowed)
    {
        var type = string.IsNullOrWhiteSpace(d.Type) ? "RegistryKeyValue" : d.Type;
        var check32 = !d.Is64Bit;

        switch (type)
        {
            case "RegistryKeyValue":
            {
                var expected = d.ExpectedValue ?? "";
                var valueName = string.IsNullOrWhiteSpace(d.ValueName) ? "DisplayVersion" : d.ValueName;
                // A detection that names no operator defaults the way the MECM
                // clause builder defaults it.
                var opName = string.IsNullOrWhiteSpace(d.Operator) ? "IsEquals" : d.Operator!;
                if (ScriptOperators.Contains(opName, StringComparer.OrdinalIgnoreCase))
                {
                    if (!conversionAllowed)
                        throw new InvalidOperationException(
                            $"Detection operator '{opName}' on registry value '{valueName}' has no native Intune rule; converting it to a PowerShell detection script is an explicit profile choice (Detection.IntuneScriptConversion). Set it in the workbench, or publish this application to ConfigMgr only.");
                    return RegistryValueScriptRule(d, valueName, expected, opName, signing);
                }
                var registryProperty = (d.PropertyType ?? "").Trim();
                // A value that is only checked for existence has no comparison.
                if (registryProperty.Equals("Existence", StringComparison.OrdinalIgnoreCase))
                    return new JsonObject
                    {
                        ["@odata.type"] = "#microsoft.graph.win32LobAppRegistryRule",
                        ["ruleType"] = "detection",
                        ["check32BitOn64System"] = check32,
                        ["keyPath"] = RegistryKeyPath(d),
                        ["valueName"] = valueName,
                        ["operationType"] = "exists",
                        ["operator"] = "notConfigured",
                        ["comparisonValue"] = null
                    };
                var operationType = RegistryOperationType(registryProperty, expected, valueName);
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppRegistryRule",
                    ["ruleType"] = "detection",
                    ["check32BitOn64System"] = check32,
                    ["keyPath"] = RegistryKeyPath(d),
                    ["valueName"] = valueName,
                    ["operationType"] = operationType,
                    ["operator"] = MapOperator(opName, "registry value"),
                    ["comparisonValue"] = expected
                };
            }
            case "RegistryKey":
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppRegistryRule",
                    ["ruleType"] = "detection",
                    ["check32BitOn64System"] = check32,
                    ["keyPath"] = RegistryKeyPath(d),
                    ["valueName"] = null,
                    ["operationType"] = "exists",
                    ["operator"] = "notConfigured",
                    ["comparisonValue"] = null
                };
            case "File":
            {
                if (string.Equals(d.PropertyType, "Version", StringComparison.OrdinalIgnoreCase))
                {
                    // A file-version detection that names no operator defaults
                    // the way the MECM clause builder defaults it.
                    var opName = string.IsNullOrWhiteSpace(d.Operator) ? "GreaterEquals" : d.Operator!;
                    return new JsonObject
                    {
                        ["@odata.type"] = "#microsoft.graph.win32LobAppFileSystemRule",
                        ["ruleType"] = "detection",
                        ["path"] = d.FilePath ?? "",
                        ["fileOrFolderName"] = d.FileName ?? "",
                        ["check32BitOn64System"] = check32,
                        ["operationType"] = "version",
                        ["operator"] = MapOperator(opName, "file version"),
                        ["comparisonValue"] = d.ExpectedValue ?? ""
                    };
                }
                // Size and the two dates have their own Graph operations. A property that
                // the rule cannot carry is refused: publishing it as "file exists" would
                // detect a different state than the definition and ConfigMgr do.
                var fileProperty = (d.PropertyType ?? "").Trim();
                if (fileProperty.Equals("Size", StringComparison.OrdinalIgnoreCase))
                    return FileValueRule(d, check32, "sizeInMB", FileSizeInMegabytes(d.ExpectedValue, d.Operator));
                if (fileProperty.Equals("DateModified", StringComparison.OrdinalIgnoreCase))
                    return FileValueRule(d, check32, "modifiedDate", FileDateInUtc(d.ExpectedValue, "modified date"));
                if (fileProperty.Equals("DateCreated", StringComparison.OrdinalIgnoreCase))
                    return FileValueRule(d, check32, "createdDate", FileDateInUtc(d.ExpectedValue, "created date"));
                if (fileProperty.Length > 0 && !fileProperty.Equals("Existence", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"File detection property '{d.PropertyType}' has no Intune rule mapping; the rule would publish as \"file exists\" and detect a different state. Use Version, Size, DateModified, DateCreated or Existence, or a Script detection.");
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
                // ScriptText carries the body. ExpectedValue on a Script detection is a
                // comparison value, not a script: MSIX staging writes the package version
                // there beside the body, so encoding it would publish the version as the
                // detection.
                var scriptText = d.ScriptText;
                if (string.IsNullOrWhiteSpace(scriptText))
                    throw new InvalidOperationException("Script detection carries no ScriptText; the Intune rule cannot be built.");
                // Graph runs a Win32 detection script as PowerShell; it has no rule shape
                // for the other languages the MECM clause builder accepts.
                if (!string.IsNullOrWhiteSpace(d.ScriptLanguage)
                    && !d.ScriptLanguage.Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Detection script language '{d.ScriptLanguage}' has no Intune rule mapping; Graph runs a Win32 detection script as PowerShell.");
                var (enforce, runAs32Bit) = ScriptRuleSettings(signing);
                byte[] content;
                if (!string.IsNullOrWhiteSpace(scriptFile))
                {
                    if (!File.Exists(scriptFile))
                        throw new InvalidOperationException(
                            $"The manifest names detection script file '{scriptFile}' but it is missing under the content root; re-run the Stage phase so the finalized script is written before publishing.");
                    content = File.ReadAllBytes(scriptFile);
                }
                else
                {
                    content = Encoding.UTF8.GetBytes(scriptText);
                }
                VerifyPublishedDetection(content, signing);
                return new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.win32LobAppPowerShellScriptRule",
                    ["ruleType"] = "detection",
                    ["enforceSignatureCheck"] = enforce,
                    ["runAs32Bit"] = runAs32Bit,
                    ["scriptContent"] = Convert.ToBase64String(content),
                    ["operationType"] = "notConfigured",
                    ["operator"] = "notConfigured"
                };
            }
            default:
                throw new InvalidOperationException($"Detection type '{type}' has no Intune rule mapping.");
        }
    }

    /// <summary>Graph registry rule key path: the hive root name plus the manifest's relative key.</summary>
    private static string RegistryKeyPath(StageDetection d) =>
        (d.IsCurrentUserHive ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\") + (d.RegistryKeyRelative ?? "");

    /// <summary>Detection operators Graph expresses directly on a registry or file rule.</summary>
    private static readonly Dictionary<string, string> RuleOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IsEquals"] = "equal",
        ["NotEquals"] = "notEqual",
        ["GreaterThan"] = "greaterThan",
        ["GreaterEquals"] = "greaterThanOrEqual",
        ["LessThan"] = "lessThan",
        ["LessEquals"] = "lessThanOrEqual"
    };

    /// <summary>Operators Graph has no rule operator for; they map to a script rule.</summary>
    private static readonly string[] ScriptOperators = ["BeginsWith", "EndsWith", "Contains"];

    /// <summary>The names that the definition format gives to an integer registry value type.</summary>
    private static readonly string[] IntegerPropertyTypes = ["Integer", "Int", "Int32", "Int64", "DWORD", "QWORD"];

    /// <summary>
    /// The Graph operation of a registry value comparison. An integer property compares as an
    /// integer, so that 10 is greater than 9, and a version property compares with version
    /// semantics, also for a version of one number. A string property, or none, keeps the guess
    /// from the value, because the manifest cannot tell a declared String from the default that
    /// stage writes.
    /// </summary>
    private static string RegistryOperationType(string propertyType, string expected, string valueName)
    {
        if (propertyType.Length == 0 || propertyType.Equals("String", StringComparison.OrdinalIgnoreCase))
            return Version.TryParse(expected, out _) ? "version" : "string";
        if (IntegerPropertyTypes.Contains(propertyType, StringComparer.OrdinalIgnoreCase))
        {
            if (long.TryParse(expected, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
                return "integer";
            throw new InvalidOperationException(
                $"Detection value '{expected}' on registry value '{valueName}' is not an integer, but its property type is '{propertyType}'. The Intune rule compares it as an integer, so the value cannot be published; correct the detection value.");
        }
        if (propertyType.Equals("Version", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.IsMatch(expected, @"^\d+(\.\d+){0,3}$") ? "version" : "string";
        throw new InvalidOperationException(
            $"Registry detection property '{propertyType}' on registry value '{valueName}' has no Intune rule mapping. Use Version, String, Integer or Existence, or a Script detection.");
    }

    /// <summary>A file-system detection rule that compares the size or a date of the file.</summary>
    private static JsonObject FileValueRule(StageDetection d, bool check32, string operationType, string comparisonValue)
    {
        // The same default as the ConfigMgr clause builder gives a file detection.
        var opName = string.IsNullOrWhiteSpace(d.Operator) ? "GreaterEquals" : d.Operator!;
        return new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppFileSystemRule",
            ["ruleType"] = "detection",
            ["path"] = d.FilePath ?? "",
            ["fileOrFolderName"] = d.FileName ?? "",
            ["check32BitOn64System"] = check32,
            ["operationType"] = operationType,
            ["operator"] = MapOperator(opName, "file " + (operationType == "sizeInMB" ? "size" : "date")),
            ["comparisonValue"] = comparisonValue
        };
    }

    /// <summary>
    /// The size of a file detection in whole megabytes. The definition carries bytes and the
    /// Graph rule compares megabytes, so a size that is not a whole number of megabytes
    /// cannot keep its meaning and is refused.
    /// </summary>
    private static string FileSizeInMegabytes(string? bytesText, string? operatorName)
    {
        // The Graph rule compares the size in MiB rounded down. "At least N MiB" and "less than N MiB" give the
        // same answer as a comparison in bytes against N MiB. The other operators do not: "equal" matches every
        // size in [N, N+1) MiB, and "greater than", "at most" and "not equal" miss or add the sizes in between.
        var op = string.IsNullOrWhiteSpace(operatorName) ? "GreaterEquals" : operatorName.Trim();
        if (!op.Equals("GreaterEquals", StringComparison.OrdinalIgnoreCase) && !op.Equals("LessThan", StringComparison.OrdinalIgnoreCase)
            && RuleOperators.ContainsKey(op))
            throw new InvalidOperationException(
                $"File size detection with operator '{op}' cannot keep its meaning in the Intune rule, which compares the size in megabytes rounded down. Only GreaterEquals and LessThan give the same answer. Use one of those, a Version or date detection, or a Script detection, or publish this application to ConfigMgr only.");
        const long Megabyte = 1024L * 1024L;
        if (long.TryParse(bytesText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes)
            && bytes > 0 && bytes % Megabyte == 0)
            return (bytes / Megabyte).ToString(System.Globalization.CultureInfo.InvariantCulture);
        throw new InvalidOperationException(
            $"File size detection value '{bytesText}' is not a whole number of megabytes. The Intune rule compares the size in whole megabytes, so it cannot keep this value; use a Version or date detection, or a Script detection, or publish this application to ConfigMgr only.");
    }

    /// <summary>
    /// A date of a file detection as UTC text. The value needs a UTC offset, because Intune
    /// compares an absolute time and the offset of a value without one is not known.
    /// </summary>
    private static string FileDateInUtc(string? text, string what)
    {
        if (!string.IsNullOrWhiteSpace(text)
            && System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"(Z|[+-]\d{2}:?\d{2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && DateTimeOffset.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when))
        {
            if (when.Ticks % TimeSpan.TicksPerSecond != 0)
                throw new InvalidOperationException(
                    $"File {what} detection value '{text}' has fractions of a second. The Intune rule is sent in whole seconds, which would move the boundary; give the value in whole seconds.");
            return when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        }
        throw new InvalidOperationException(
            $"File {what} detection value '{text}' is not a date with a UTC offset (for example 2025-03-01T10:00:00Z). The Intune rule compares an absolute time, so the value needs an offset.");
    }

    private static string MapOperator(string name, string context)
        => RuleOperators.TryGetValue(name, out var mapped)
            ? mapped
            : throw new InvalidOperationException($"Detection operator '{name}' ({context}) has no Intune rule mapping.");

    /// <summary>
    /// Registry-value rule for a prefix, suffix, or substring comparison. Graph
    /// registry rules carry equality and ordering operators only, so the
    /// comparison keeps its meaning through a script that reads the value from
    /// the requested registry view.
    /// </summary>
    private static JsonNode RegistryValueScriptRule(StageDetection d, string valueName, string expected, string op,
        IntuneScriptRuleContext? signing)
    {
        var hive = d.IsCurrentUserHive ? "CurrentUser" : "LocalMachine";
        var view = d.Is64Bit ? "Registry64" : "Registry32";
        // String.Contains(string, StringComparison) is absent from .NET Framework,
        // which the client-side Windows PowerShell runs the script under.
        var test = op.ToUpperInvariant() switch
        {
            "BEGINSWITH" => $"$value.StartsWith({PsLiteral(expected)}, [System.StringComparison]::OrdinalIgnoreCase)",
            "ENDSWITH" => $"$value.EndsWith({PsLiteral(expected)}, [System.StringComparison]::OrdinalIgnoreCase)",
            _ => $"$value.IndexOf({PsLiteral(expected)}, [System.StringComparison]::OrdinalIgnoreCase) -ge 0"
        };

        var script = string.Join("\r\n",
            $"$base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::{hive}, [Microsoft.Win32.RegistryView]::{view})",
            $"$key = $base.OpenSubKey({PsLiteral(d.RegistryKeyRelative ?? "")})",
            "if ($key) {",
            $"    $value = [string]$key.GetValue({PsLiteral(valueName)})",
            $"    if ({test}) {{ Write-Output 'Detected'; exit 0 }}",
            "}",
            "exit 1");

        var (enforce, runAs32Bit) = ScriptRuleSettings(signing);
        var content = Encoding.UTF8.GetBytes(script);
        VerifyPublishedDetection(content, signing);
        return new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppPowerShellScriptRule",
            ["ruleType"] = "detection",
            ["enforceSignatureCheck"] = enforce,
            ["runAs32Bit"] = runAs32Bit,
            ["scriptContent"] = Convert.ToBase64String(content),
            ["operationType"] = "notConfigured",
            ["operator"] = "notConfigured"
        };
    }

    /// <summary>
    /// Verifies the exact bytes a script rule publishes when the detection is
    /// recorded as signed or RequireDetection is set: the signature is intact,
    /// the SHA-256 equals the recorded hash and the signer equals the recorded
    /// thumbprint. Anything else refuses before the first Graph call.
    /// </summary>
    public static void VerifyPublishedDetection(byte[] content, IntuneScriptRuleContext? context)
    {
        if (context is null || (!SigningStatus.IsSigned(context.DetectionStatus) && !context.RequireDetection)) return;
        var verify = ScriptSignatureVerifier.VerifyBytes(content);
        string? problem = null;
        if (!verify.SignatureIntact)
            problem = $"does not carry an intact signature ({verify.Status})";
        else if (!string.Equals(verify.Sha256, context.DetectionSha256, StringComparison.OrdinalIgnoreCase))
            problem = $"has hash {verify.Sha256}, not the recorded {(context.DetectionSha256.Length > 0 ? context.DetectionSha256 : "none")}";
        else if (context.DetectionThumbprint.Length > 0
                 && !string.Equals(verify.Thumbprint, context.DetectionThumbprint, StringComparison.OrdinalIgnoreCase))
            problem = $"is signed by {verify.Thumbprint}, not by the recorded {context.DetectionThumbprint}";
        if (problem is not null)
            throw new InvalidOperationException($"The detection script encoded into scriptContent {problem}; refusing to publish.");
    }

    /// <summary>A single-quoted PowerShell string literal.</summary>
    private static string PsLiteral(string value) => PowerShellLiteral.SingleQuoted(value);

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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string uri, CancellationToken ct,
        bool includeResponseBody = true)
    {
        if (response.IsSuccessStatusCode) return;
        // Storage URLs carry SAS credentials. A storage/proxy error body can echo the
        // request URL, so neither its query nor its response body belongs in a run log.
        var body = includeResponseBody ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : "";
        if (body.Length > 500) body = body[..500];
        var endpoint = new Uri(uri).GetLeftPart(UriPartial.Path);
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} from {endpoint}: {body}", null, response.StatusCode);
    }
}
