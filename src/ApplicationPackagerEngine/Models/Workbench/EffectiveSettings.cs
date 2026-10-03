using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Models.Workbench;

/// <summary>The precedence level that supplied an effective value, lowest first.</summary>
public enum SettingSource
{
    Global,
    Packager,
    Profile,
    Variant,
    Target,
    Run,

    /// <summary>Nothing supplied the field and only a completed Stage can resolve it.</summary>
    NeedsStaging
}

/// <summary>
/// One field of the precedence table: where the Global, Packager and Profile levels
/// store it. The Variant, Target and Run levels are dictionaries keyed by Field.
/// </summary>
public sealed record WorkbenchField(
    string Field,
    string? GlobalPath,
    string? PackagerPath,
    string ProfilePath,
    bool NeedsStaging);

/// <summary>One resolved field: the value and the level that supplied it.</summary>
public sealed record EffectiveSetting(string Field, JsonNode? Value, SettingSource Source);

/// <summary>Every field of the precedence table, resolved, in table order.</summary>
public sealed class EffectiveSettings
{
    private readonly Dictionary<string, EffectiveSetting> _byField;

    public EffectiveSettings(IReadOnlyList<EffectiveSetting> fields)
    {
        Fields = fields;
        _byField = fields.ToDictionary(f => f.Field, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<EffectiveSetting> Fields { get; }

    public EffectiveSetting this[string field] => _byField[field];

    public bool TryGet(string field, out EffectiveSetting setting) => _byField.TryGetValue(field, out setting!);
}
