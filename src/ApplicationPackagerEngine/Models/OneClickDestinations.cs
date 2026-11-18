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
    /// The set a deployment target name stands for, or null for a name that is not one (an empty
    /// or unknown name). ContentOnly is the empty set. The earlier MECM spellings read as ConfigMgr.
    /// </summary>
    public static DestinationSet? TryFromTarget(string? target) => DeploymentTargets.Canonical(target) switch
    {
        PublishRunTargets.ConfigMgr => new(true, false, false),
        PublishRunTargets.ConfigMgrAndIntune => new(true, false, true),
        PublishRunTargets.IntuneOnly => new(false, false, true),
        PublishRunTargets.ConfigMgrAndWsus => new(true, true, false),
        PublishRunTargets.WsusOnly => new(false, true, false),
        PublishRunTargets.IntuneAndWsus => new(false, true, true),
        PublishRunTargets.ConfigMgrAndIntuneAndWsus => new(true, true, true),
        DeploymentTargets.ContentOnly => new(false, false, false),
        _ => null
    };

    /// <summary>
    /// The set a deployment target name stands for. An unknown or empty name is the empty set:
    /// it never becomes ConfigMgr. A caller that has saved destinations reads those first.
    /// </summary>
    public static DestinationSet FromTarget(string? target) => TryFromTarget(target) ?? new(false, false, false);

    /// <summary>
    /// The target name of this set: each of the seven non-empty combinations has its own name.
    /// The empty set has no publish name; the stored legacy value keeps its earlier spelling,
    /// ConfigMgr, and the saved destinations decide what a run publishes to.
    /// </summary>
    public string ToTargetName()
    {
        if (ConfigMgr && Wsus && Intune) return PublishRunTargets.ConfigMgrAndIntuneAndWsus;
        if (ConfigMgr && Wsus) return PublishRunTargets.ConfigMgrAndWsus;
        if (ConfigMgr && Intune) return PublishRunTargets.ConfigMgrAndIntune;
        if (ConfigMgr) return PublishRunTargets.ConfigMgr;
        if (Wsus && Intune) return PublishRunTargets.IntuneAndWsus;
        if (Wsus) return PublishRunTargets.WsusOnly;
        if (Intune) return PublishRunTargets.IntuneOnly;
        return PublishRunTargets.ConfigMgr;
    }

    /// <summary>
    /// The destinations as a list in words: "ConfigMgr", "ConfigMgr and WSUS", "Intune and WSUS", "ConfigMgr, Intune and WSUS",
    /// or "no destination" for an empty set. The order is ConfigMgr, Intune, WSUS.
    /// </summary>
    public string Describe()
    {
        var names = new List<string>();
        if (ConfigMgr) names.Add(OneClickDestinations.ConfigMgr);
        if (Intune) names.Add(OneClickDestinations.Intune);
        if (Wsus) names.Add(OneClickDestinations.Wsus);
        return names.Count switch
        {
            0 => "no destination",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
        };
    }

    public bool Equals(DestinationSet? other) =>
        other is not null && ConfigMgr == other.ConfigMgr && Wsus == other.Wsus && Intune == other.Intune;

    public override bool Equals(object? obj) => Equals(obj as DestinationSet);

    public override int GetHashCode() => HashCode.Combine(ConfigMgr, Wsus, Intune);
}
