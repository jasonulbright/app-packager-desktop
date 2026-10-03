using System.Text.Json.Serialization;

namespace ApplicationPackagerEngine.Models;

/// <summary>The destinations a publish reaches, in the order a One Click run publishes them.</summary>
public static class OneClickDestinations
{
    public const string ConfigMgr = "ConfigMgr";
    public const string Wsus = "WSUS";
    public const string Intune = "Intune";

    public static readonly IReadOnlyList<string> All = [ConfigMgr, Wsus, Intune];

    /// <summary>The canonical name of a destination, or null for an unknown one. MECM reads as ConfigMgr.</summary>
    public static string? Resolve(string? name) => name?.Trim() switch
    {
        { } n when n.Equals(ConfigMgr, StringComparison.OrdinalIgnoreCase) || n.Equals("MECM", StringComparison.OrdinalIgnoreCase) => ConfigMgr,
        { } n when n.Equals(Wsus, StringComparison.OrdinalIgnoreCase) => Wsus,
        { } n when n.Equals(Intune, StringComparison.OrdinalIgnoreCase) => Intune,
        _ => null
    };
}

/// <summary>The three destination flags of one application or of the One Click default.</summary>
public sealed class DestinationSet : IEquatable<DestinationSet>
{
    public bool ConfigMgr { get; set; }

    [JsonPropertyName("WSUS")]
    public bool Wsus { get; set; }

    public bool Intune { get; set; }

    public DestinationSet() { }

    public DestinationSet(bool configMgr, bool wsus, bool intune)
    {
        ConfigMgr = configMgr;
        Wsus = wsus;
        Intune = intune;
    }

    [JsonIgnore]
    public bool Any => ConfigMgr || Wsus || Intune;

    /// <summary>The selected destinations in publish order.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Names => OneClickDestinations.All.Where(Has).ToList();

    public bool Has(string destination) => OneClickDestinations.Resolve(destination) switch
    {
        OneClickDestinations.ConfigMgr => ConfigMgr,
        OneClickDestinations.Wsus => Wsus,
        OneClickDestinations.Intune => Intune,
        _ => false
    };

    public DestinationSet Copy() => new(ConfigMgr, Wsus, Intune);

    /// <summary>
    /// The set a deployment target name stands for. An unknown or empty
    /// name is ConfigMgr only.
    /// </summary>
    public static DestinationSet FromTarget(string? target) => target switch
    {
        PublishRunTargets.ConfigMgrAndIntune => new(true, false, true),
        PublishRunTargets.IntuneOnly => new(false, false, true),
        PublishRunTargets.ConfigMgrAndWsus => new(true, true, false),
        PublishRunTargets.WsusOnly => new(false, true, false),
        _ => new(true, false, false)
    };

    /// <summary>
    /// The nearest single target for the code paths that still read one. All
    /// three, or WSUS with Intune, have no name of their own and map to their
    /// ConfigMgr or WSUS part.
    /// </summary>
    public string ToTargetName()
    {
        if (ConfigMgr && Wsus) return PublishRunTargets.ConfigMgrAndWsus;
        if (ConfigMgr && Intune) return PublishRunTargets.ConfigMgrAndIntune;
        if (ConfigMgr) return PublishRunTargets.ConfigMgr;
        if (Wsus) return PublishRunTargets.WsusOnly;
        if (Intune) return PublishRunTargets.IntuneOnly;
        return PublishRunTargets.ConfigMgr;
    }

    /// <summary>"ConfigMgr and WSUS", or "no destination" for an empty set.</summary>
    public string Describe() => Names.Count == 0 ? "no destination" : string.Join(" and ", Names);

    public bool Equals(DestinationSet? other) =>
        other is not null && ConfigMgr == other.ConfigMgr && Wsus == other.Wsus && Intune == other.Intune;

    public override bool Equals(object? obj) => Equals(obj as DestinationSet);

    public override int GetHashCode() => HashCode.Combine(ConfigMgr, Wsus, Intune);
}
