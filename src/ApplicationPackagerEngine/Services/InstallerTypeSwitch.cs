using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The per-application installer type switch (EXE or MSI). The definition file is the default type; <see cref="AppDefinition.InstallerTypeOverrides"/>
/// holds the properties of each other type. <see cref="Apply"/> returns the definition as it stages for the chosen type, so each type carries its
/// own download, silent switches, detection and local source. The setting key is InstallerType; empty means the default type.
/// </summary>
public static class InstallerTypeSwitch
{
    public const string Exe = "EXE";
    public const string Msi = "MSI";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>True for EXE or MSI (any case).</summary>
    public static bool IsType(string? value) => Normalize(value) is not null;

    public static string? Normalize(string? value) =>
        string.Equals(value?.Trim(), Exe, StringComparison.OrdinalIgnoreCase) ? Exe
        : string.Equals(value?.Trim(), Msi, StringComparison.OrdinalIgnoreCase) ? Msi : null;

    /// <summary>The default type first, then the declared others; empty when the definition declares no other type.</summary>
    public static IReadOnlyList<string> Choices(AppDefinition def)
    {
        if (def.InstallerTypeOverrides is not { Count: > 0 }) return [];
        var own = Normalize(def.InstallerType);
        if (own is null) return [];
        var list = new List<string> { own };
        foreach (var key in def.InstallerTypeOverrides.Keys)
            if (Normalize(key) is { } type && !list.Contains(type)) list.Add(type);
        return list.Count >= 2 ? list : [];
    }

    /// <summary>
    /// The definition for the chosen type. An empty or unknown choice, the default type, and a definition that does not offer the choice return
    /// the definition itself. The result keeps the source path and origin.
    /// </summary>
    public static AppDefinition Apply(AppDefinition def, string? chosen)
    {
        var type = Normalize(chosen);
        if (type is null || !Choices(def).Contains(type) || string.Equals(type, Normalize(def.InstallerType), StringComparison.Ordinal)) return def;
        var overlay = def.InstallerTypeOverrides!.First(p => Normalize(p.Key) == type).Value;
        var node = JsonSerializer.SerializeToNode(def, Options)!.AsObject();
        node.Remove(nameof(AppDefinition.InstallerTypeOverrides));
        foreach (var (name, value) in overlay)
        {
            var existing = node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) node.Remove(existing);
            node[name] = value?.DeepClone();
        }
        node[nameof(AppDefinition.InstallerType)] = type;
        var result = node.Deserialize<AppDefinition>(Options)!;
        result.SourcePath = def.SourcePath;
        result.SourceOrigin = def.SourceOrigin;
        result.SourceLabel = def.SourceLabel;
        return result;
    }
}
