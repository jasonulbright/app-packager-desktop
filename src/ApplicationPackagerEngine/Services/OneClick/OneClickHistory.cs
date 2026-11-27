using System.Globalization;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;

namespace ApplicationPackagerEngine.Services.OneClick;

/// <summary>The version last published to one destination. At is UTC, yyyy-MM-ddTHH:mm:ssZ.</summary>
public sealed class OneClickPublishRecord
{
    public string Version { get; set; } = "";
    public string At { get; set; } = "";
    public string Id { get; set; } = "";
    /// <summary>
    /// The application is on the site but its supersedence step failed: the version stays pending for the supersedence-only
    /// pass of a later run. Only the ConfigMgr destination has supersedence.
    /// </summary>
    public bool SupersedencePending { get; set; }
    /// <summary>The reason the step gave; empty when the record is not pending.</summary>
    public string SupersedenceReason { get; set; } = "";
    /// <summary>
    /// The stored version of a local-installer publish whose build record is gone. An earlier version of the app could store the website
    /// version there, so no skip rests on it.
    /// </summary>
    public bool Unverified { get; set; }
}

/// <summary>
/// The version one destination refused. Reason is the short form a result
/// cell shows; for WSUS, "not supported: &lt;Code&gt;".
/// </summary>
public sealed class OneClickRefusalRecord
{
    public string Version { get; set; } = "";
    public string At { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>What the planner and the run read about one application's earlier checks, stages and publishes.</summary>
public sealed record OneClickHistoryEntry
{
    public DateTime? LastChecked { get; init; }
    public string LastKnownVersion { get; init; } = "";
    public DateTime? LastStaged { get; init; }
    public IReadOnlyDictionary<string, OneClickPublishRecord> Published { get; init; } = new Dictionary<string, OneClickPublishRecord>();
    public IReadOnlyDictionary<string, OneClickRefusalRecord> NotSupported { get; init; } = new Dictionary<string, OneClickRefusalRecord>();

    /// <summary>
    /// Per destination, the build key (<see cref="OneClickHistory.BuildKey"/>) of the build
    /// the last publish carried. A destination without an entry has a publish whose build
    /// is not known.
    /// </summary>
    public IReadOnlyDictionary<string, string> PublishedBuild { get; init; } = new Dictionary<string, string>();

    /// <summary>The version that the destination holds by its record; empty with no record or with one that cannot be verified.</summary>
    public string PublishedVersion(string destination) =>
        Published.TryGetValue(destination, out var record) && !record.Unverified ? record.Version ?? "" : "";

    public string PublishedBuildKey(string destination) =>
        PublishedBuild.TryGetValue(destination, out var key) ? key ?? "" : "";

    /// <summary>True when the ConfigMgr record names a version whose supersedence step failed and is still to do.</summary>
    public bool SupersedencePending(string destination) =>
        destination == OneClickDestinations.ConfigMgr && Published.TryGetValue(destination, out var record) && record.SupersedencePending;

    public string RefusedVersion(string destination) =>
        NotSupported.TryGetValue(destination, out var record) ? record.Version ?? "" : "";

    public string RefusedReason(string destination) =>
        NotSupported.TryGetValue(destination, out var record) ? record.Reason ?? "" : "";
}

/// <summary>Writes publish and refusal records into the mutable dictionaries a settings file holds.</summary>
public static class OneClickHistory
{
    public static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// The identity of a build for the publish guard: profile, profile revision and
    /// signing-policy digest. A publish of another build does not count as this one.
    /// </summary>
    public static string BuildKey(string? profileId, int profileRevision, string? policyDigest) =>
        $"{profileId}|{profileRevision.ToString(CultureInfo.InvariantCulture)}|{policyDigest}";

    /// <summary>
    /// Records a publish. A publish clears that destination's refusal record. A new publish replaces the record, so it clears an
    /// earlier pending state. <paramref name="supersedenceNotApplied"/> marks the ConfigMgr record pending; WSUS and Intune have no
    /// supersedence step and are never marked.
    /// </summary>
    public static void SetPublished(IDictionary<string, OneClickPublishRecord> published, IDictionary<string, OneClickRefusalRecord> notSupported,
        string destination, string version, string id, DateTimeOffset at, bool supersedenceNotApplied = false, string supersedenceReason = "")
    {
        var pending = supersedenceNotApplied && destination == OneClickDestinations.ConfigMgr;
        published[destination] = new OneClickPublishRecord
        {
            Version = version, Id = id, At = Stamp(at),
            SupersedencePending = pending, SupersedenceReason = pending ? supersedenceReason ?? "" : ""
        };
        notSupported.Remove(destination);
    }

    /// <summary>
    /// Ends the pending state of the supersedence step. The record keeps its version, time and identifier. Does nothing when the
    /// destination has no record or the record is not pending.
    /// </summary>
    public static void ClearSupersedencePending(IDictionary<string, OneClickPublishRecord> published, string destination)
    {
        if (!published.TryGetValue(destination, out var record) || record is null) return;
        record.SupersedencePending = false;
        record.SupersedenceReason = "";
    }

    /// <summary>
    /// Whether the inventory record of a ConfigMgr publish is pending: its last attempt is "Published, supersedence not applied"
    /// for the version that last succeeded. A later attempt that applied the step, or any other attempt, ends it.
    /// </summary>
    public static bool IsSupersedencePending(InventoryOutcome lastAttemptOutcome, string? lastAttemptVersion, string? lastSuccessVersion) =>
        lastAttemptOutcome == InventoryOutcome.PublishedSupersedenceNotApplied
        && !string.IsNullOrWhiteSpace(lastSuccessVersion)
        && string.Equals(lastAttemptVersion, lastSuccessVersion, StringComparison.Ordinal);

    /// <summary>
    /// The reason a publish gave for a supersedence step that did not apply, from the detail the publish recorded
    /// ("Published, supersedence not applied: &lt;reason&gt;"). Empty when the detail names no failed step; a failed step
    /// that gave no reason reads "the site gave no reason."
    /// </summary>
    public static string SupersedenceReasonOf(string? detail)
    {
        const string prefix = "Published, supersedence not applied:";
        if (detail is null) return "";
        var at = detail.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) return "";
        var reason = detail[(at + prefix.Length)..].Trim();
        return reason.Length > 0 ? reason : "the site gave no reason.";
    }

    /// <summary>
    /// Records a refusal. The plan and the run skip that destination for the
    /// same version until Force is set; a newer version is tried again.
    /// </summary>
    public static void SetNotSupported(IDictionary<string, OneClickRefusalRecord> notSupported,
        string destination, string version, string reason, DateTimeOffset at) =>
        notSupported[destination] = new OneClickRefusalRecord { Version = version, Reason = reason, At = Stamp(at) };

    /// <summary>The destination names a record dictionary may hold, in canonical spelling.</summary>
    public static bool IsDestination(string name) => OneClickDestinations.Resolve(name) == name;
}
