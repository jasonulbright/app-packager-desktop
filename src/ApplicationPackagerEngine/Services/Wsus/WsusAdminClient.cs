using System.Globalization;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>A WSUS operation that failed; Kind is the adapter's error kind.</summary>
public sealed class WsusOperationException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

/// <summary>The server's signing certificate as the public certificate reads.</summary>
public sealed record WsusCertificateInfo(
    string Subject, string Issuer, string Thumbprint, DateTimeOffset NotBefore, DateTimeOffset NotAfter, int KeyLength)
{
    public bool SelfSigned => Subject == Issuer;
    public bool IsExpired(DateTimeOffset now) => NotAfter < now;
}

/// <summary>Whether this computer can load the WSUS administration API, and from where.</summary>
public sealed record WsusApiStatus(bool Available, string Version, string Location, string Reason);

/// <summary>A connection test. Message carries the notes or the failure; the status never throws.</summary>
public sealed record WsusServerStatus(
    bool Connected, string ServerName, string Version, string Role, bool SecureConnection,
    WsusCertificateInfo? SigningCertificate, bool SigningCertificateExpired, string Message, WsusApiStatus Api);

/// <summary>One locally published update as the server lists it.</summary>
public sealed record WsusPublishedUpdate(
    string PackageId, string Title, string PackageType, string IdentityTag, string Version,
    DateTimeOffset? Created, string CreatedText, string ApprovedGroups, bool Declined, bool Superseded, bool Expired);

/// <summary>The outcome of one state change or one catalog import.</summary>
public sealed record WsusItemResult(string Id, string Title, bool Ok, string Message);

public static class WsusUpdateActions
{
    public const string Approve = "Approve";
    public const string Decline = "Decline";
    public const string Expire = "Expire";
    public const string Remove = "Remove";

    public static readonly IReadOnlyList<string> All = [Approve, Decline, Expire, Remove];
}

/// <summary>An update the publish scan found under this identity, as the server reported it.</summary>
public sealed record WsusUpdateCandidate(
    string PackageId, string Title, string Description, DateTimeOffset? Created, bool Declined, string PublicationState)
{
    public bool Expired => PublicationState.Equals("Expired", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Every WSUS server operation the application performs, each through one
/// adapter call. Settings are normalized before any call.
/// </summary>
public sealed class WsusAdminClient(IWsusAdapterRunner? runner = null, Func<DateTimeOffset>? clock = null) : IWsusAdminClient
{
    private readonly IWsusAdapterRunner _runner = runner ?? new WsusAdapterRunner();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    /// <summary>Connects and reads the server version, the account role and the signing certificate. Never throws for a server failure.</summary>
    public async Task<WsusServerStatus> GetStatusAsync(WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        WsusSettings normalized;
        try { normalized = Configured(settings); }
        catch (WsusOperationException ex)
        {
            return new WsusServerStatus(false, settings.ServerName ?? "", "", "", false, null, false, ex.Message, new WsusApiStatus(false, "", "", ""));
        }
        var result = await _runner.RunAsync("Status", Request(normalized), null, onProgress, ct);
        if (!result.Ok) return new WsusServerStatus(false, normalized.ServerName, "", "", false, null, false, WsusMessages.Compose(result, normalized), new WsusApiStatus(false, "", "", ""));
        return ReadStatus(result.Data!, normalized, _clock());
    }

    /// <summary>The status a Status operation's Data reports.</summary>
    public static WsusServerStatus ReadStatus(JsonObject data, WsusSettings settings, DateTimeOffset now)
    {
        var apiNode = data["Api"] as JsonObject;
        var api = new WsusApiStatus(Bool(apiNode, "Available"), Str(apiNode, "Version"), Str(apiNode, "Location"), Str(apiNode, "Reason"));
        if (!Bool(data, "Connected"))
        {
            var failure = new WsusAdapterResult(false, Str(data, "ConnectErrorKind"), Str(data, "ConnectError"), null);
            return new WsusServerStatus(false, Str(data, "ServerName"), "", "", false, null, false, WsusMessages.Compose(failure, settings), api);
        }
        var notes = new List<string>();
        var role = Str(data, "Role");
        if (Str(data, "RoleError") is { Length: > 0 } roleError) notes.Add("The account role could not be read: " + roleError);
        if (role.Length > 0 && role != "Administrator") notes.Add($"This account has the {role} role; publishing needs WSUS Administrators membership.");
        if (Str(data, "CertificateError") is { Length: > 0 } certificateError) notes.Add("The signing certificate could not be read: " + certificateError);
        var certificate = ReadCertificate(data["SigningCertificate"] as JsonObject);
        return new WsusServerStatus(true, Str(data, "ServerName"), Str(data, "Version"), role, Bool(data, "SecureConnection"),
            certificate, certificate?.IsExpired(now) == true, string.Join(" ", notes), api);
    }

    /// <summary>Has the server create a self-signed signing certificate and use it for local publishing.</summary>
    public async Task<WsusServerStatus> CreateSelfSignedCertificateAsync(WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var normalized = Configured(settings);
        var data = Data(await _runner.RunAsync("CreateSelfSigned", Request(normalized), null, onProgress, ct), normalized);
        onProgress?.Invoke($"WSUS signing certificate      : self-signed certificate created on {Str(data, "ServerName")}");
        return ReadStatus(data, normalized, _clock());
    }

    /// <summary>
    /// Makes the certificate in a PFX file the server's signing certificate.
    /// The file is opened here first, which proves the password, the private
    /// key and the code-signing usage before the server's configuration
    /// changes. The password reaches the adapter on standard input.
    /// </summary>
    public async Task<WsusServerStatus> ImportPfxAsync(WsusSettings settings, string pfxPath, string password,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        WsusPfxCheck.Check(pfxPath, password, _clock());
        var normalized = Configured(settings);
        var request = Request(normalized);
        request["PfxPath"] = Path.GetFullPath(pfxPath);
        var data = Data(await _runner.RunAsync("ImportPfx", request, password, onProgress, ct), normalized);
        onProgress?.Invoke($"WSUS signing certificate      : {Path.GetFileName(pfxPath)} set on {Str(data, "ServerName")}");
        return ReadStatus(data, normalized, _clock());
    }

    /// <summary>Writes the server's public signing certificate (DER .cer) for client distribution.</summary>
    public async Task<string> ExportCertificateAsync(WsusSettings settings, string cerPath, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var normalized = Configured(settings);
        var request = Request(normalized);
        request["CerPath"] = Path.GetFullPath(cerPath);
        var data = Data(await _runner.RunAsync("ExportCer", request, null, onProgress, ct), normalized);
        return Str(data, "CerPath");
    }

    /// <summary>The server's computer target group names, sorted.</summary>
    public async Task<IReadOnlyList<string>> GetComputerGroupNamesAsync(WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var normalized = Configured(settings);
        var data = Data(await _runner.RunAsync("Groups", Request(normalized), null, onProgress, ct), normalized);
        return Strings(data["Names"]);
    }

    /// <summary>
    /// The locally published updates, newest first. By default only those
    /// that carry an AppPackager identity line.
    /// </summary>
    public async Task<IReadOnlyList<WsusPublishedUpdate>> ListAsync(WsusSettings settings, bool includeOtherPublishers = false,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var normalized = Configured(settings);
        var data = Data(await _runner.RunAsync("List", Request(normalized), null, onProgress, ct), normalized);
        return ReadPublishedUpdates(data["Rows"] as JsonArray, includeOtherPublishers);
    }

    public static IReadOnlyList<WsusPublishedUpdate> ReadPublishedUpdates(JsonArray? rows, bool includeOtherPublishers)
    {
        var updates = new List<WsusPublishedUpdate>();
        foreach (var row in (rows ?? []).OfType<JsonObject>())
        {
            var identity = WsusIdentity.ParseIdentity(Str(row, "Description"));
            if (identity is null && !includeOtherPublishers) continue;
            var created = Date(row, "Created");
            var groups = Bool(row, "ApprovalsRead") ? string.Join(", ", Strings(row["ApprovedGroups"])) : "unknown";
            updates.Add(new WsusPublishedUpdate(
                Str(row, "PackageId"), Str(row, "Title"), identity?.PackageType ?? "", identity?.IdentityTag ?? "", identity?.Version ?? "",
                created, created?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
                groups, Bool(row, "Declined"), Bool(row, "Superseded"),
                Str(row, "PublicationState").Equals("Expired", StringComparison.OrdinalIgnoreCase)));
        }
        return updates.OrderByDescending(u => u.Created ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>
    /// Approves for a group, declines, expires, or removes updates. Each id
    /// succeeds or fails on its own; only a connection or permission failure
    /// throws.
    /// </summary>
    public async Task<IReadOnlyList<WsusItemResult>> SetStateAsync(WsusSettings settings, IEnumerable<Guid> packageIds, string action,
        string? groupName = null, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!WsusUpdateActions.All.Contains(action)) throw new ArgumentException($"Action '{action}' is not Approve, Decline, Expire or Remove.", nameof(action));
        if (action == WsusUpdateActions.Approve && string.IsNullOrWhiteSpace(groupName))
            throw new WsusOperationException(WsusErrorKinds.Input, "Approve needs a computer group.");
        var normalized = Configured(settings);
        var request = Request(normalized);
        request["Action"] = action;
        request["GroupName"] = groupName ?? "";
        request["PackageIds"] = new JsonArray(packageIds.Distinct().Select(id => (JsonNode)JsonValue.Create(id.ToString())!).ToArray());
        var data = Data(await _runner.RunAsync("SetState", request, null, onProgress, ct), normalized);
        var results = ReadStateResults(data["Results"] as JsonArray, action);
        foreach (var result in results.Where(r => r.Ok))
            onProgress?.Invoke($"WSUS update {action.ToLowerInvariant(),-17} : {result.Title} ({result.Id})");
        return results;
    }

    public static IReadOnlyList<WsusItemResult> ReadStateResults(JsonArray? rows, string action)
    {
        var results = new List<WsusItemResult>();
        foreach (var row in (rows ?? []).OfType<JsonObject>())
        {
            var id = Str(row, "PackageId");
            if (row["Found"]?.GetValue<bool>() == false)
            {
                results.Add(new WsusItemResult(id, "", false, $"WSUS has no update with id {id}."));
                continue;
            }
            var ok = Bool(row, "Ok");
            var message = ok
                ? (Str(row, "FolderError") is { Length: > 0 } folderError ? WsusMessages.PackageFolderNotDeleted(folderError) : "")
                : WsusMessages.StateChangeFailure(action, Str(row, "Message"), Bool(row, "DeclinedNow"));
            results.Add(new WsusItemResult(id, Str(row, "Title"), ok, message));
        }
        return results;
    }

    /// <summary>
    /// Imports Microsoft Update Catalog updates by id. The server downloads the
    /// metadata itself. Never throws for a server failure: every id then
    /// carries the failure.
    /// </summary>
    public async Task<IReadOnlyList<WsusItemResult>> ImportCatalogUpdatesAsync(WsusSettings settings, IEnumerable<Guid> updateIds,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var ids = updateIds.Distinct().ToList();
        WsusSettings normalized;
        try { normalized = Configured(settings); }
        catch (WsusOperationException ex) { return ids.Select(id => new WsusItemResult(id.ToString(), "", false, ex.Message)).ToList(); }
        var request = Request(normalized);
        request["UpdateIds"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id.ToString())!).ToArray());
        var result = await _runner.RunAsync("CatalogImport", request, null, onProgress, ct);
        if (!result.Ok)
        {
            var message = WsusMessages.CatalogImportFailure(WsusMessages.Compose(result, normalized));
            return ids.Select(id => new WsusItemResult(id.ToString(), "", false, message)).ToList();
        }
        var results = new List<WsusItemResult>();
        foreach (var row in (result.Data!["Results"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var ok = Bool(row, "Ok");
            results.Add(new WsusItemResult(Str(row, "UpdateId"), "", ok, ok ? "" : WsusMessages.CatalogImportFailure(Str(row, "Message"))));
            if (ok) onProgress?.Invoke($"WSUS catalog import           : {Str(row, "UpdateId")} on {normalized.ServerName}");
        }
        return results;
    }

    /// <summary>Normalized settings with a server, or the refusal.</summary>
    internal static WsusSettings Configured(WsusSettings settings)
    {
        WsusSettings normalized;
        try { normalized = settings.Normalize(); }
        catch (WsusSettingsException ex) { throw new WsusOperationException(WsusErrorKinds.Input, ex.Message); }
        if (!normalized.IsConfigured) throw new WsusOperationException(WsusErrorKinds.Input, "No WSUS server is configured.");
        return normalized;
    }

    internal static JsonObject Request(WsusSettings settings) => new()
    {
        ["Settings"] = new JsonObject
        {
            ["ServerName"] = settings.ServerName,
            ["PortNumber"] = settings.PortNumber,
            ["UseSsl"] = settings.UseSsl
        }
    };

    internal static JsonObject Data(WsusAdapterResult result, WsusSettings settings) =>
        result.Ok
            ? result.Data ?? new JsonObject()
            : throw new WsusOperationException(result.ErrorKind, WsusMessages.Compose(result, settings));

    internal static WsusCertificateInfo? ReadCertificate(JsonObject? node) => node is null
        ? null
        : new WsusCertificateInfo(Str(node, "Subject"), Str(node, "Issuer"), Str(node, "Thumbprint"),
            Date(node, "NotBefore") ?? DateTimeOffset.MinValue, Date(node, "NotAfter") ?? DateTimeOffset.MinValue,
            node["KeyLength"]?.GetValue<int>() ?? 0);

    internal static IReadOnlyList<WsusUpdateCandidate> ReadCandidates(JsonArray? rows) =>
        (rows ?? []).OfType<JsonObject>()
            .Select(r => new WsusUpdateCandidate(Str(r, "PackageId"), Str(r, "Title"), Str(r, "Description"), Date(r, "Created"),
                Bool(r, "Declined"), Str(r, "PublicationState")))
            .ToList();

    internal static string Str(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    internal static bool Bool(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    internal static DateTimeOffset? Date(JsonObject? node, string name) =>
        DateTimeOffset.TryParse(Str(node, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;

    internal static IReadOnlyList<string> Strings(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "").ToList(),
        JsonValue value when value.TryGetValue<string>(out var single) => [single],
        _ => []
    };
}
