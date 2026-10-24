using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Resolves targets, reads and changes site scripts, and runs CMPivot queries and approved scripts on
/// client devices. CMPivot goes through the administration service; target reads, script reads and
/// script actions go through the ConfigMgr session. A target read is one script run with one query
/// per class; a run is one request to the site for all its devices, and each poll is one request
/// for all devices. Every rule a run needs (the prepared plan, the confirmations, the approved state,
/// the script hash, the device cap, the collection members, the saved session and the audit record
/// before the send) is checked here, whatever the caller shows.
/// </summary>
public sealed partial class PivotsScriptsService : IPivotsScriptsService
{
    public const string LogArea = "PivotsScripts";

    /// <summary>Starts the message of an outcome that changed nothing on purpose.</summary>
    public const string SkippedPrefix = "Skipped: ";

    /// <summary>The most client devices one run may target; a larger plan is refused at Prepare and at Apply.</summary>
    public const int MaxDevicesPerRun = 2000;

    /// <summary>The most devices a target read keeps; above it the result is truncated and says so.</summary>
    public const int MaxResolvedDevices = 100_000;

    /// <summary>The most names a List target takes.</summary>
    public const int MaxListNames = 50_000;

    /// <summary>The most characters of a CMPivot query.</summary>
    public const int MaxQueryLength = 16 * 1024;

    /// <summary>The most characters of a script text to submit.</summary>
    public const int MaxScriptLength = 256 * 1024;

    /// <summary>The most characters of one parameter value.</summary>
    public const int MaxParameterLength = 1024;

    /// <summary>The time a run waits for device results when the change names none.</summary>
    public static readonly TimeSpan DefaultResultWindow = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MinResultWindow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxResultWindow = TimeSpan.FromMinutes(60);

    /// <summary>The time between two polls of a run: one request for all devices each time.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);

    /// <summary>The time a target read may take in the session.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(5);

    internal const string ScriptResource = "ApplicationPackagerEngine.Services.PivotsScripts.Scripts.PivotsScripts.ps1";

    internal const string PivotRights = "The account needs Run CMPivot on the collection and Read on Inventory Reports.";
    internal const string ScriptRights = "The account needs Run Script on the collection, and Create, Approve or Delete on SMS Scripts for the script actions.";

    private static readonly ConfigMgrScript Script = ConfigMgrScript.FromResource(ScriptResource);

    private readonly IConfigMgrSession _session;
    private readonly IAdminServiceClient _admin;
    private readonly IPivotsAuditLog _audit;
    private readonly IPivotsSessionStore _sessions;
    private readonly IAppLog? _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string> _user;
    private readonly Func<(string Site, string Provider)> _connection;

    /// <summary>The prepared changes by fingerprint; Apply runs only a change a Prepare returned.</summary>
    private readonly ConcurrentDictionary<string, PreparedChange> _prepared = new(StringComparer.Ordinal);

    /// <summary>The runs this service sent, by operation ID, for <see cref="WaitForResultsAsync"/>.</summary>
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    private const int MaxPrepared = 64;
    private const int MaxRuns = 64;

    private sealed record PreparedChange(PivotsChange Change, DateTime PreparedUtc);

    public PivotsScriptsService(IConfigMgrSession session, IAdminServiceClient admin, IPivotsAuditLog audit, IPivotsSessionStore sessions,
        IAppLog? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? utcNow = null, Func<string>? user = null,
        Func<(string Site, string Provider)>? connection = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _log = log;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _user = user ?? (() => System.Security.Principal.WindowsIdentity.GetCurrent().Name);
        _connection = connection ?? (() => ("", admin.Provider));
    }

    public TimeSpan PollInterval { get; init; } = DefaultPollInterval;

    [GeneratedRegex("^[A-Za-z0-9]{8}$")]
    internal static partial Regex CollectionIdRegex();

    [GeneratedRegex("^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$")]
    internal static partial Regex GuidRegex();

    [GeneratedRegex("[,;\r\n]+")]
    private static partial Regex ListSeparatorRegex();

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";

    internal static string HashOf(string? text) => PivotsLibrary.HashOf(text ?? "");

    // ---- Session helpers ----------------------------------------------------------------------

    private async Task<IReadOnlyList<JsonElement>> RunAsync(ConfigMgrParameters parameters, CancellationToken ct, TimeSpan? timeout = null, string? rights = null)
    {
        try
        {
            var result = await _session.InvokeAsync(Script, parameters, ct, timeout).ConfigureAwait(false);
            return result.Output;
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"A Pivots and Scripts command failed in the ConfigMgr session: {ex.Failure}.");
            throw new PivotsServiceException(SessionMessage(ex, rights));
        }
    }

    /// <summary>The plain message of a session failure. A script failure carries the site's own text, which is masked by the session.</summary>
    internal static string SessionMessage(ConfigMgrSessionException ex, string? rights) => ex.Failure switch
    {
        ConfigMgrFailure.ScriptFailed => KnownRefusal(ex.Message) ?? "ConfigMgr refused the request. The log has the kind of failure." + (rights is null ? "" : " " + rights),
        ConfigMgrFailure.AccessDenied => "ConfigMgr refused access. " + (rights ?? ScriptRights),
        _ => ComplianceService.SessionProblem(ex.Failure)
    };

    /// <summary>The fixed texts the session script throws, and the refusals of the site that have a plain meaning.</summary>
    private static readonly (string Marker, string Message)[] Refusals =
    [
        ("Script Not Approved", "The site refused the run: the script is not approved."),
        ("The script is not approved.", "The site refused the run: the script is not approved."),
        ("The script GUID is not valid.", "The script GUID is not valid."),
        ("The collection ID is not valid.", "The collection ID is not valid."),
        ("The site holds no script with this GUID.", "The site holds no script with this GUID."),
        ("A parameter name is not defined on the script.", "A parameter name is not defined on the script on the site."),
        ("A value of an integer parameter is not a whole number.", "A value of an integer parameter is not a whole number."),
        ("A parameter has a type that Run Scripts does not take.", "A parameter of the script has a type that Run Scripts does not take."),
        ("The parameter names and values do not match.", "The parameter names and values do not match."),
        ("The script does not meet the Run Scripts rules.", "The script does not meet the Run Scripts rules. Check it first."),
        ("The created script was not found on the site.", "The created script was not found on the site."),
        ("The script belongs to a ConfigMgr feature.", "The script belongs to a ConfigMgr feature. The page does not change it."),
        ("No device to run on.", "The plan has no device to run on.")
    ];

    internal static string? KnownRefusal(string? text)
    {
        foreach (var (marker, message) in Refusals)
            if ((text ?? "").Contains(marker, StringComparison.Ordinal)) return message;
        return null;
    }

    private static string S(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.GetRawText()
        } : "";

    private static int I(JsonElement e, string name, int fallback = 0) =>
        int.TryParse(S(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static bool? B(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        } : null;

    private static IEnumerable<JsonElement> Kind(IReadOnlyList<JsonElement> output, string kind) => output.Where(o => S(o, "K") == kind);

    // ---- Targets ------------------------------------------------------------------------------

    public async Task<ResolvedTargets> ResolveTargetsAsync(RunTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var text = (target.Text ?? "").Trim();
        if (text.Length == 0) throw new PivotsServiceException("Enter a target.");
        var resolved = target.Kind switch
        {
            RunTargetKind.Collection => await ResolveCollectionAsync(text, ct).ConfigureAwait(false),
            _ => await ResolveDevicesAsync(target.Kind, text, ct).ConfigureAwait(false)
        };
        _log?.Info(LogArea, $"Resolved a {target.Kind} target: {resolved.Devices.Count} devices, {resolved.Unmatched.Count} unmatched names.");
        return resolved with { Kind = target.Kind, Text = text, ResolvedUtc = _utcNow() };
    }

    private async Task<ResolvedTargets> ResolveCollectionAsync(string text, CancellationToken ct)
    {
        var collections = await RunAsync(new ConfigMgrParameters { { "Operation", "Collection" } }, ct, ReadTimeout).ConfigureAwait(false);
        var list = Kind(collections, "C").Select(c => (Id: S(c, "Id"), Name: S(c, "N"), Members: I(c, "M"))).ToList();
        var byId = CollectionIdRegex().IsMatch(text) ? list.Where(c => string.Equals(c.Id, text, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        var match = byId.Count > 0 ? byId : list.Where(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0) throw new PivotsServiceException($"The site holds no device collection with the ID or the exact name \"{text}\".");
        if (match.Count > 1) throw new PivotsServiceException($"{match.Count} device collections have the name \"{text}\". Enter the collection ID.");
        var (id, name, _) = match[0];
        var devices = await MembersAsync(id, ct).ConfigureAwait(false);
        var builtIn = IsBuiltIn(id);
        var truncated = devices.Count > MaxResolvedDevices;
        return new ResolvedTargets(
            Devices: truncated ? devices.Take(MaxResolvedDevices).ToList() : devices,
            CollectionId: id.ToUpperInvariant(),
            Description: $"Collection {name} ({id.ToUpperInvariant()}): {Count(devices.Count, "member")}",
            IsBuiltInCollection: builtIn,
            Unmatched: [],
            Truncated: truncated)
        { TotalMatched = devices.Count, CollectionName = name };
    }

    /// <summary>A collection the site creates: its ID starts with SMS (All Systems is SMS00001).</summary>
    internal static bool IsBuiltIn(string? collectionId) => (collectionId ?? "").StartsWith("SMS", StringComparison.OrdinalIgnoreCase);

    /// <summary>The members of a collection with the fast channel state of each, from two queries.</summary>
    private async Task<List<ResolvedDevice>> MembersAsync(string collectionId, CancellationToken ct)
    {
        var output = await RunAsync(new ConfigMgrParameters { { "Operation", "Members" }, { "CollectionId", collectionId } }, ct, ReadTimeout).ConfigureAwait(false);
        var online = new Dictionary<int, bool?>();
        foreach (var d in Kind(output, "D")) online[I(d, "Id")] = B(d, "O");
        var seen = new HashSet<int>();
        var devices = new List<ResolvedDevice>();
        foreach (var m in Kind(output, "M"))
        {
            var id = I(m, "Id");
            if (id <= 0 || !seen.Add(id)) continue;
            devices.Add(new ResolvedDevice(id, S(m, "N"), online.TryGetValue(id, out var o) ? o : null) { IsClient = B(m, "C") ?? false });
        }
        return devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.ResourceId).ToList();
    }

    private async Task<ResolvedTargets> ResolveDevicesAsync(RunTargetKind kind, string text, CancellationToken ct)
    {
        var names = kind == RunTargetKind.List ? ListNames(text) : [text];
        if (names.Count > MaxListNames) throw new PivotsServiceException($"The list has {Count(names.Count, "name")}. A list takes at most {MaxListNames.ToString("N0", CultureInfo.InvariantCulture)}.");
        if (names.Count == 0) throw new PivotsServiceException("Enter a target.");
        var output = await RunAsync(new ConfigMgrParameters { { "Operation", "Devices" } }, ct, ReadTimeout).ConfigureAwait(false);
        var all = Kind(output, "D").Select(d => new ResolvedDevice(I(d, "Id"), S(d, "N"), B(d, "O")) { IsClient = B(d, "C") ?? false }).Where(d => d.ResourceId > 0).ToList();
        return Match(kind, text, names, all);
    }

    /// <summary>The names of a List target: split at commas, semicolons and line ends, trimmed, without empty or repeated names.</summary>
    internal static List<string> ListNames(string text) =>
        ListSeparatorRegex().Split(text ?? "").Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Matches names against the device list in linear work: a Device or List name matches a device
    /// name exactly without case; a Pattern matches with * for any run of characters and ? for one.
    /// </summary>
    internal static ResolvedTargets Match(RunTargetKind kind, string text, IReadOnlyList<string> names, IReadOnlyList<ResolvedDevice> all)
    {
        var matched = new List<ResolvedDevice>();
        var unmatched = new List<string>();
        var ambiguous = 0;
        if (kind == RunTargetKind.Pattern)
        {
            var pattern = WildcardRegex(text);
            matched.AddRange(all.Where(d => pattern.IsMatch(d.Name)));
        }
        else
        {
            var byName = all.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (!byName.TryGetValue(name, out var found)) { unmatched.Add(name); continue; }
                if (found.Count > 1) ambiguous++;
                matched.AddRange(found);
            }
        }
        var distinct = matched.GroupBy(d => d.ResourceId).Select(g => g.First()).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.ResourceId).ToList();
        if (kind == RunTargetKind.Device && distinct.Count > 1)
            throw new PivotsServiceException($"{Count(distinct.Count, "device")} have the name \"{text}\". Use a List target to run on all of them, or remove the obsolete records.");
        var truncated = distinct.Count > MaxResolvedDevices;
        var description = kind switch
        {
            RunTargetKind.Pattern => $"Devices that match {text}: {Count(distinct.Count, "device")}",
            RunTargetKind.Device => distinct.Count == 0 ? $"Device {text}: not found" : $"Device {distinct[0].Name}",
            _ => $"List of {Count(names.Count, "name")}: {Count(distinct.Count, "device")}, {unmatched.Count.ToString("N0", CultureInfo.InvariantCulture)} not found"
        };
        if (ambiguous > 0) description += $"; {Count(ambiguous, "name")} matched more than one device and all of them are in the list";
        return new ResolvedTargets(
            Devices: truncated ? distinct.Take(MaxResolvedDevices).ToList() : distinct,
            CollectionId: null,
            Description: description,
            IsBuiltInCollection: false,
            Unmatched: unmatched,
            Truncated: truncated)
        { TotalMatched = distinct.Count };
    }

    internal static Regex WildcardRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    // ---- Scripts ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SiteScript>> LoadScriptsAsync(CancellationToken ct)
    {
        var output = await RunAsync(new ConfigMgrParameters { { "Operation", "Scripts" } }, ct, ReadTimeout, ScriptRights).ConfigureAwait(false);
        var list = Kind(output, "S").Select(s => ToScript(s)).Where(s => s is not null).Select(s => s!).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _log?.Info(LogArea, $"Read {list.Count} site scripts.");
        return list;
    }

    public async Task<SiteScript?> LoadScriptAsync(string guid, CancellationToken ct)
    {
        if (guid is null || !GuidRegex().IsMatch(guid)) throw new PivotsServiceException("The script GUID is not valid.");
        var output = await RunAsync(new ConfigMgrParameters { { "Operation", "Script" }, { "ScriptGuid", guid.ToUpperInvariant() } }, ct, ReadTimeout, ScriptRights).ConfigureAwait(false);
        return Kind(output, "S").Select(s => ToScript(s)).FirstOrDefault(s => s is not null);
    }

    /// <summary>A script row of the session; null for a row of a ConfigMgr feature, which the page does not show or change.</summary>
    internal static SiteScript? ToScript(JsonElement s)
    {
        if (I(s, "Feature") != 0) return null;
        var full = B(s, "Full") == true;
        string? text = full && s.TryGetProperty("Text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var parameters = new List<ScriptParameter>();
        if (s.TryGetProperty("Parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
                parameters.Add(new ScriptParameter(S(p, "Name"), S(p, "Type"), B(p, "Required") == true, B(p, "HasDefault") == false ? null : S(p, "Default")) { Description = S(p, "Description") });
        var state = I(s, "ApprovalState", -1);
        return new SiteScript(
            Guid: S(s, "Guid").ToUpperInvariant(),
            Name: S(s, "Name"),
            Author: S(s, "Author"),
            Approver: S(s, "Approver"),
            State: StateName(state),
            Version: int.TryParse(S(s, "Version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0,
            Language: I(s, "ScriptType") == 0 ? "PowerShell" : "Type " + I(s, "ScriptType").ToString(CultureInfo.InvariantCulture),
            Hash: text is null ? "" : HashOf(text),
            Parameters: parameters,
            Text: text)
        {
            ApprovalState = state,
            Comment = S(s, "Comment"),
            LastUpdatedUtc = PivotsResults.Utc(S(s, "Updated")),
            ParametersUnreadable = B(s, "Unreadable") == true,
            SiteHash = S(s, "SiteHash")
        };
    }

    internal static string StateName(int approvalState) => approvalState switch
    {
        3 => SiteScriptStates.Approved,
        1 => SiteScriptStates.Declined,
        _ => SiteScriptStates.Waiting
    };

    public async Task<ScriptCheck> CheckScriptAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ScriptCheck(false, ["The script is empty."], [], []);
        if (text.Length > MaxScriptLength) return new ScriptCheck(false, [$"The script has more than {MaxScriptLength.ToString("N0", CultureInfo.InvariantCulture)} characters."], [], []);
        var output = await RunAsync(new ConfigMgrParameters { { "Operation", "Check" }, { "ScriptText", text } }, ct, ReadTimeout).ConfigureAwait(false);
        var c = Kind(output, "Check").FirstOrDefault();
        if (c.ValueKind != JsonValueKind.Object) throw new PivotsServiceException("ConfigMgr returned no result for the check.");
        var errors = Strings(c, "Errors");
        var problems = Strings(c, "Problems");
        var parameters = new List<ScriptParameter>();
        if (c.TryGetProperty("Parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
                parameters.Add(new ScriptParameter(S(p, "Name"), S(p, "Type"), B(p, "Required") == true, p.TryGetProperty("Default", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null));
        return new ScriptCheck(B(c, "Valid") == true && errors.Count == 0, errors, parameters, problems);
    }

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()).ToList() : [];

    // ---- Connection ---------------------------------------------------------------------------

    public async Task<string> TestConnectionAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            var output = await RunAsync(new ConfigMgrParameters { { "Operation", "Ping" } }, ct, ReadTimeout).ConfigureAwait(false);
            var ping = Kind(output, "Ping").FirstOrDefault();
            lines.Add(ping.ValueKind == JsonValueKind.Object
                ? $"ConfigMgr session: connected to site {S(ping, "Site")} (version {S(ping, "Version")})."
                : "ConfigMgr session: connected, but the site returned no site information.");
        }
        catch (PivotsServiceException ex)
        {
            lines.Add("ConfigMgr session: " + ex.Message);
        }
        try
        {
            var response = await _admin.SendAsync(HttpMethod.Get, "v1.0/", null, ct).ConfigureAwait(false);
            lines.Add(response.Status == 200
                ? $"Administration service on {_admin.Provider}: answers with the Windows identity of this session."
                : "Administration service: " + HttpMessage(response.Status, PivotRights));
        }
        catch (Exception ex) when (ex is PivotsServiceException or AdminServiceUnreachableException)
        {
            lines.Add("Administration service: " + ex.Message);
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The plain message of an HTTP answer that is not a success.</summary>
    internal static string HttpMessage(int status, string rights) => status switch
    {
        400 => "The site refused the query (HTTP 400) and gave no reason. Check the query syntax: an unknown entity, operator or column gives this answer.",
        401 => "The administration service did not accept the Windows identity of this session (HTTP 401).",
        403 => "The site refused the request (HTTP 403). " + rights,
        404 => "The site holds no such collection, device or operation (HTTP 404).",
        >= 500 => $"The administration service is not available now (HTTP {status.ToString(CultureInfo.InvariantCulture)}). The SMS Executive service can be starting. Try again later.",
        _ => $"The administration service answered HTTP {status.ToString(CultureInfo.InvariantCulture)}."
    };

    // ---- Fingerprints -------------------------------------------------------------------------

    /// <summary>The fingerprint of a change: every value that decides what runs, in a fixed order, hashed.</summary>
    internal static string FingerprintOf(PivotsChange c)
    {
        var text = new StringBuilder(c.Kind.ToString());
        void Add(string? value) => text.Append('\n').Append(value ?? "\u0000");
        Add(c.Targets?.CollectionId?.ToUpperInvariant());
        Add(c.Targets is null ? null : string.Join(",", c.Targets.Devices.Select(d => d.ResourceId).OrderBy(i => i)));
        Add(c.QueryText is null ? null : HashOf(c.QueryText));
        Add(c.ScriptGuid?.ToUpperInvariant());
        Add(c.ScriptHash);
        Add(c.Script?.SiteHash);
        Add(c.Parameters is null ? null : string.Join("\n", c.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)));
        Add(c.SubmitName);
        Add(c.SubmitText is null ? null : HashOf(c.SubmitText));
        Add(c.Comment);
        Add(c.DeviceCount.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private void Remember(PivotsChange change)
    {
        if (_prepared.Count >= MaxPrepared)
            foreach (var old in _prepared.OrderBy(p => p.Value.PreparedUtc).Take(_prepared.Count - MaxPrepared + 1).Select(p => p.Key).ToList())
                _prepared.TryRemove(old, out _);
        _prepared[change.Fingerprint!] = new PreparedChange(change, _utcNow());
    }
}
