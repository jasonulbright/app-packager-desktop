using System.Globalization;
using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Reads the per-app deployment condition selections and translates them
/// into the manifest semantics the Package phase consumes: requirement
/// specs on the single deployment type, or a DeploymentTypes variant set
/// when a split is selected.
/// </summary>
public sealed class DeploymentConditionsService(SettingsService settingsService)
{
    /// <summary>The stored selections for one definition id, or null when it has none.</summary>
    public DeploymentConditionEntry? GetConditions(string definitionId)
    {
        var settings = settingsService.Load();
        return settings.DeploymentConditions.TryGetValue(definitionId, out var entry) ? entry : null;
    }

    /// <summary>The stored command overrides for one definition id, or null when both fields are empty.</summary>
    public StageCommandOverrides? GetCommandOverrides(string definitionId)
    {
        var settings = settingsService.Load();
        if (!settings.CommandOverrides.TryGetValue(definitionId, out var entry)) return null;

        var install = (entry.Install ?? "").Trim();
        var uninstall = (entry.Uninstall ?? "").Trim();
        if (install.Length == 0 && uninstall.Length == 0) return null;

        return new StageCommandOverrides
        {
            Install = install.Length > 0 ? install : null,
            Uninstall = uninstall.Length > 0 ? uninstall : null
        };
    }

    /// <summary>
    /// The flat requirement specs for a selection without a split. A split
    /// selection carries its gating on the variant entries instead, so it
    /// contributes no flat rules.
    /// </summary>
    public static List<StageRequirement> BuildRequirements(DeploymentConditionEntry? entry)
    {
        var rules = new List<StageRequirement>();
        if (entry is null || DeploymentConditionValues.IsSplit(entry.Split)) return rules;

        if (DeploymentConditionValues.IsArchitecture(entry.Architecture))
            rules.Add(new StageRequirement { ConditionId = "cpu-arch", Value = entry.Architecture });

        var cultures = NormalizeCultures(entry.Languages);
        if (cultures.Length > 0)
            rules.Add(new StageRequirement { ConditionId = "os-language", Cultures = cultures });

        if (entry.Network == DeploymentConditionValues.VpnOnly)
            rules.Add(new StageRequirement { ConditionId = "vpn-connected", Value = true });
        else if (entry.Network == DeploymentConditionValues.OnSiteOnly)
            rules.Add(new StageRequirement { ConditionId = "vpn-connected", Value = false });

        return rules;
    }

    /// <summary>
    /// Rewrites the manifest into a variant set when a split is selected.
    /// Entries are emitted most-specific-first because the client installs
    /// the first deployment type whose requirements pass. A content
    /// subpath is set only where the staged content actually carries that
    /// folder; otherwise the variant runs from the content root.
    /// </summary>
    public static void ApplyVariantSplit(
        StageManifest manifest, DeploymentConditionEntry? entry,
        string stagedContentPath, Action<string>? onLog = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (entry is null || !DeploymentConditionValues.IsSplit(entry.Split)) return;

        // A manifest that already declares its own variants is authoritative.
        if (manifest.DeploymentTypes is { Length: > 0 })
        {
            onLog?.Invoke("Variant split ignored: the stage manifest already declares DeploymentTypes.");
            return;
        }

        string? Subpath(string folder) =>
            Directory.Exists(Path.Combine(stagedContentPath, folder)) ? folder : null;

        switch (entry.Split)
        {
            case "Architecture":
                // Both entries are gated: a machine that is neither x64 nor
                // ARM64 installs neither variant.
                manifest.DeploymentTypes =
                [
                    new StageDeploymentType
                    {
                        NameSuffix = "ARM64",
                        ContentSubpath = Subpath("arm64"),
                        Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "ARM64" }]
                    },
                    new StageDeploymentType
                    {
                        NameSuffix = "x64",
                        Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "x64" }]
                    }
                ];
                break;

            case "Network":
                // The VPN-gated online variant first, then the unconditional fallback.
                manifest.DeploymentTypes =
                [
                    new StageDeploymentType
                    {
                        NameSuffix = "Online",
                        ContentSubpath = Subpath("online"),
                        Requirements = [new StageRequirement { ConditionId = "vpn-connected", Value = true }]
                    },
                    new StageDeploymentType { NameSuffix = "Managed" }
                ];
                break;

            case "Language":
            {
                var cultures = NormalizeCultures(entry.Languages)
                    .Where(c => !IsEnglishFallback(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (cultures.Length == 0)
                {
                    onLog?.Invoke("Language split ignored: every configured language reduces to the en-US fallback.");
                    return;
                }

                var entries = cultures
                    .Select(culture => new StageDeploymentType
                    {
                        NameSuffix = culture,
                        ContentSubpath = Subpath(culture),
                        Requirements = [new StageRequirement { ConditionId = "os-language", Cultures = [culture] }]
                    })
                    .ToList();
                // The content root payload is the unconditional fallback.
                entries.Add(new StageDeploymentType { NameSuffix = "en-US" });
                manifest.DeploymentTypes = [.. entries];
                break;
            }
        }

        if (manifest.DeploymentTypes is { Length: > 0 })
            onLog?.Invoke($"Variant split ({entry.Split}) : {string.Join(", ", manifest.DeploymentTypes.Select(d => d.NameSuffix))}");
    }

    /// <summary>
    /// Culture codes in their canonical form. An unparseable code is
    /// dropped here rather than failing the package run downstream.
    /// </summary>
    public static string[] NormalizeCultures(IEnumerable<string>? values)
    {
        if (values is null) return [];
        var result = new List<string>();
        foreach (var raw in values)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) continue;
            try { result.Add(CultureInfo.GetCultureInfo(text).Name); }
            catch (CultureNotFoundException) { }
        }
        return [.. result];
    }

    private static bool IsEnglishFallback(string culture) =>
        culture.Equals("en", StringComparison.OrdinalIgnoreCase)
        || culture.Equals("en-US", StringComparison.OrdinalIgnoreCase);
}
