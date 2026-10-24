using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads and changes the content of distribution points through the ConfigMgr session. A load is
/// one script run with one query per class (distribution points, each content class, the
/// application model map, the status summarizer and the distribution status messages); the views
/// are built in memory in linear time. A pair is identified by its PackageID and the whole NAL
/// path. A change runs in chunks of <see cref="ChunkSize"/> pairs, each chunk one script run that
/// re-reads the pairs before it writes; after the last chunk a bounded read-back decides each
/// result. A removal needs <see cref="DpContentChange.RemovalConfirmed"/>, and a removal that
/// leaves a content on no distribution point also needs <see cref="DpContentChange.LastCopyAcknowledged"/>;
/// both are checked here, whatever the caller shows.
/// </summary>
public sealed partial class DpContentService : IDpContentService
{
    public const string LogArea = "DpContent";

    /// <summary>Starts the message of an outcome or a pair that changed nothing on purpose.</summary>
    public const string SkippedPrefix = "Skipped: ";

    /// <summary>The pairs one script run changes.</summary>
    public const int ChunkSize = 25;

    /// <summary>The most pairs one change may hold; a larger plan is refused at Prepare and at Apply.</summary>
    public const int MaxPairsPerRun = 500;

    /// <summary>The most status rows a load keeps; above it the snapshot is truncated and says so.</summary>
    public const int DefaultMaxPairs = 100_000;

    /// <summary>
    /// How long the read-back after a change waits for the site. A site without a backlog shows the
    /// result of a distribution, a refresh or a validation of a small content within it; a pair the
    /// site has not finished by then reads as Requested.
    /// </summary>
    public static readonly TimeSpan DefaultReadBackWindow = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>The time a load may take in the session.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(15);

    internal const string ScriptResource = "ApplicationPackagerEngine.Services.DpContent.Scripts.DpContent.ps1";

    internal const string AccessHint =
        "The account needs a ConfigMgr security role with read rights on distribution points and content, and rights to distribute and remove content.";

    private static readonly ConfigMgrScript Script = ConfigMgrScript.FromResource(ScriptResource);

    private readonly IConfigMgrSession _session;
    private readonly IDpContentAuditLog _audit;
    private readonly IAppLog? _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;

    /// <summary>The prepared pairs by fingerprint; Apply runs only pairs a Prepare returned.</summary>
    private readonly ConcurrentDictionary<string, Prepared> _prepared = new(StringComparer.Ordinal);

    private const int MaxPrepared = 64;

    private sealed record Prepared(DpContentChangeKind Kind, IReadOnlyDictionary<DpPairKey, PairRow> Pairs, DateTime PreparedUtc);

    public DpContentService(IConfigMgrSession session, IDpContentAuditLog audit, IAppLog? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? utcNow = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _log = log;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public int MaxPairs { get; init; } = DefaultMaxPairs;
    public TimeSpan ReadBackWindow { get; init; } = DefaultReadBackWindow;
    public TimeSpan PollInterval { get; init; } = DefaultPollInterval;

    [GeneratedRegex("^[A-Za-z0-9]{8}$")]
    internal static partial Regex PackageIdRegex();

    // ---- Load ---------------------------------------------------------------------------------

    public async Task<DpContentSnapshot> LoadAsync(IProgress<string> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report("Reading the distribution points, the content and the status rows from ConfigMgr.");
        var output = await RunAsync(new ConfigMgrParameters { { "Operation", "Load" }, { "MaxPairs", MaxPairs } }, ct, LoadTimeout).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        progress.Report("Building the views.");
        var read = Parse(output);
        var pairs = DpContentAggregator.Pairs(read);
        var snapshot = DpContentAggregator.Snapshot(read, pairs, read.TotalPairs < 0 ? pairs.Count : read.TotalPairs, _utcNow());
        if (snapshot.Truncated) _log?.Warning(LogArea, $"The status rows were cut at {snapshot.Pairs.Count} of {snapshot.TotalPairs}.");
        _log?.Info(LogArea, $"Loaded {snapshot.Dps.Count} distribution points, {snapshot.Content.Count} content objects and {snapshot.Pairs.Count} status rows.");
        progress.Report($"Loaded {Count(snapshot.Dps.Count, "distribution point")}, {Count(snapshot.Content.Count, "content object")} and {Count(snapshot.Pairs.Count, "status row")}.");
        return snapshot;
    }

    // ---- Session helpers ----------------------------------------------------------------------

    private async Task<ConfigMgrCommandResult> RunAsync(ConfigMgrParameters parameters, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            return await _session.InvokeAsync(Script, parameters, ct, timeout).ConfigureAwait(false);
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"A DP content command failed in the ConfigMgr session: {ex.Failure}.");
            throw new DpContentServiceException(ex.Failure == ConfigMgrFailure.ScriptFailed
                ? "The DP content data could not be read from ConfigMgr. The log has the kind of failure."
                : ComplianceService.SessionProblem(ex.Failure).Replace(ComplianceService.AccessHint, AccessHint, StringComparison.Ordinal));
        }
    }

    private DpContentSiteRead Parse(ConfigMgrCommandResult output)
    {
        try
        {
            return DpContentSiteRead.Parse(output.Output);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _log?.Warning(LogArea, $"ConfigMgr returned DP content data that could not be read ({ex.GetType().Name}).");
            throw new DpContentServiceException("ConfigMgr returned data that could not be read. The log has the details.");
        }
    }

    /// <summary>The site state of the given packages: status rows, instances and messages, with the deployment counts when <paramref name="impact"/> is true.</summary>
    private async Task<DpContentSiteRead> ReadStateAsync(IEnumerable<string> packageIds, bool impact, CancellationToken ct)
    {
        var ids = packageIds.Select(i => i.ToUpperInvariant()).Where(i => PackageIdRegex().IsMatch(i)).Distinct(StringComparer.Ordinal).ToArray();
        var output = await RunAsync(new ConfigMgrParameters { { "Operation", "State" }, { "PackageIds", ids }, { "Impact", impact } }, ct).ConfigureAwait(false);
        var read = Parse(output);
        if (!read.StateRead) throw new DpContentServiceException("ConfigMgr did not return the state of the content. Refresh and try again.");
        return read;
    }

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";

    internal static string Verb(DpContentChangeKind kind) => kind switch
    {
        DpContentChangeKind.Redistribute => "Redistribute",
        DpContentChangeKind.Remove => "Remove",
        DpContentChangeKind.Validate => "Validate",
        _ => "Distribute again"
    };

    /// <summary>The fingerprint of a prepared pair list: the kind and each pair's key and state, sorted.</summary>
    internal static string FingerprintOf(DpContentChangeKind kind, IEnumerable<PairRow> pairs)
    {
        var text = new StringBuilder(kind.ToString());
        foreach (var p in pairs.Select(p => (Id: p.PackageId.ToUpperInvariant(), Nal: p.NalPath.ToUpperInvariant(), p.State)).OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Nal, StringComparer.Ordinal))
            text.Append('\n').Append(p.Id).Append('|').Append(p.Nal).Append('|').Append(p.State.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private void Remember(string fingerprint, Prepared prepared)
    {
        if (_prepared.Count >= MaxPrepared)
            foreach (var old in _prepared.OrderBy(p => p.Value.PreparedUtc).Take(_prepared.Count - MaxPrepared + 1).Select(p => p.Key).ToList())
                _prepared.TryRemove(old, out _);
        _prepared[fingerprint] = prepared;
    }
}
