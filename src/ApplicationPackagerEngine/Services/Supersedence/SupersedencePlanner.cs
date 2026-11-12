using System.Text;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// What a publish does with the version rule: the manifest that each target publishes, the identity marker, the lines for the log and
/// the refusal when the row cannot be published. A refused row creates nothing.
/// </summary>
public sealed record SupersedencePlan(
    StageManifest ConfigMgrManifest,
    StageManifest IntuneManifest,
    string? Marker,
    string? Refusal,
    IReadOnlyList<string> Log,
    SupersedenceMode Mode = SupersedenceMode.Off,
    VersionRule ConfigMgrRule = VersionRule.Minimum);

public static class SupersedencePlanner
{
    public static SupersedencePlan Plan(
        StageManifest manifest, SupersedenceSettings settings, SupersedenceCommandLine? commandLine,
        bool publishesToConfigMgr, bool publishesToIntune, Func<StageManifest, string?> msiProductVersion,
        string? fallbackApplicationId = null)
    {
        // A grid stage of the default profile runs without a snapshot, so its manifest has no application identifier; the run knows it.
        var applicationId = string.IsNullOrWhiteSpace(manifest.ApplicationId) ? fallbackApplicationId : manifest.ApplicationId;
        var log = new List<string>();
        ResolvedSupersedence resolved;
        try
        {
            resolved = SupersedenceResolver.Resolve(settings, applicationId, commandLine);
        }
        catch (InvalidOperationException ex)
        {
            return new SupersedencePlan(manifest, manifest, null, ex.Message, log);
        }

        string Where(SettingSource source) => source switch
        {
            SettingSource.CommandLine => "command line",
            SettingSource.TitleOverride => "title override",
            _ => "site default"
        };

        if (publishesToConfigMgr)
        {
            log.Add($"Version rule (ConfigMgr)    : {SupersedenceNames.Text(resolved.ConfigMgrRule.Value)} ({Where(resolved.ConfigMgrRule.Source)})");
            log.Add($"Supersedence mode           : {SupersedenceNames.Text(resolved.Mode.Value)} ({Where(resolved.Mode.Source)})");
            if (resolved.Blocked is { } blocked)
                return new SupersedencePlan(manifest, manifest, null, blocked, log);
        }
        if (publishesToIntune)
            log.Add($"Version rule (Intune)       : {SupersedenceNames.Text(resolved.IntuneRule.Value)} ({Where(resolved.IntuneRule.Source)})");

        // A title with no identifier has no key: nothing is recorded for it and no block can be accepted (a line name comes later).
        var titleKey = SupersedenceResolver.OverrideKey(applicationId);
        var guard = new ExactGuard(msiProductVersion, settings.InstalledValues, settings.AcceptedExactBlocks);
        Func<StageDetection, ExactGuardVerdict> verdictFor = clause => guard.Evaluate(manifest, clause, titleKey);
        var acceptedBlock = false;

        var refusal = new StringBuilder();
        StageManifest ForTarget(string target, VersionRule rule)
        {
            var (copy, outcomes) = VersionRuleApplier.ApplyToManifest(manifest, rule, verdictFor);
            foreach (var outcome in outcomes)
            {
                switch (outcome.Result)
                {
                    case ClauseResult.ExactApplied:
                        log.Add($"Version rule ({target})".PadRight(28) + $": Exact written to {outcome.Clause}");
                        break;
                    case ClauseResult.ExactAcceptedBlock:
                        acceptedBlock = true;
                        log.Add($"Version rule ({target})".PadRight(28) + $": Exact not applied to {outcome.Clause} (block accepted for this version). {outcome.Detail}");
                        break;
                    default:
                        if (refusal.Length > 0) refusal.Append(' ');
                        refusal.Append($"The version rule Exact cannot be applied for {target}: {outcome.Detail}");
                        break;
                }
            }
            return copy;
        }

        var configMgr = publishesToConfigMgr ? ForTarget("ConfigMgr", resolved.ConfigMgrRule.Value) : manifest;
        var intune = publishesToIntune ? ForTarget("Intune", resolved.IntuneRule.Value) : manifest;
        if (refusal.Length > 0)
        {
            refusal.Append(" Set the version rule of this title to Minimum, or accept the block for this version.");
            return new SupersedencePlan(manifest, manifest, null, refusal.ToString(), log);
        }

        // Exact guard rule 5: a title whose Exact was not applied cannot be uninstalled by supersedence.
        if (publishesToConfigMgr && acceptedBlock && resolved.Mode.Value == SupersedenceMode.ReplaceAndUninstall)
            return new SupersedencePlan(manifest, manifest, null,
                "The version rule Exact was not applied to this title (the block is accepted), so Replace and uninstall is blocked for it. Set the supersedence mode of the title to Replace or Off.", log);

        var marker = publishesToConfigMgr ? IdentityMarker.For(manifest, applicationId) : null;
        return new SupersedencePlan(configMgr, intune, marker, null, log, resolved.Mode.Value, resolved.ConfigMgrRule.Value);
    }
}
