using System.Globalization;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.OneClick;

/// <summary>The version last published to one destination. At is UTC, yyyy-MM-ddTHH:mm:ssZ.</summary>
public sealed class OneClickPublishRecord
{
    public string Version { get; set; } = "";
    public string At { get; set; } = "";
    public string Id { get; set; } = "";
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

    public string PublishedVersion(string destination) =>
        Published.TryGetValue(destination, out var record) ? record.Version ?? "" : "";

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

    /// <summary>Records a publish. A publish clears that destination's refusal record.</summary>
    public static void SetPublished(IDictionary<string, OneClickPublishRecord> published, IDictionary<string, OneClickRefusalRecord> notSupported,
        string destination, string version, string id, DateTimeOffset at)
    {
        published[destination] = new OneClickPublishRecord { Version = version, Id = id, At = Stamp(at) };
        notSupported.Remove(destination);
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
