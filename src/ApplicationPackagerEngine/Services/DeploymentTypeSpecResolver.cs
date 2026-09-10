using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>One resolved deployment type to create, in creation order.</summary>
public sealed class DeploymentTypeSpec
{
    public string DtName { get; set; } = "";
    public string ContentLocation { get; set; } = "";
    public string InstallCommand { get; set; } = "";
    public string UninstallCommand { get; set; } = "";
    public StageDetection Detection { get; set; } = new();
    /// <summary>
    /// Authoritative for this deployment type. Empty means an unconditional
    /// fallback, never inherited rules.
    /// </summary>
    public StageRequirement[] Requirements { get; set; } = [];
}

/// <summary>
/// Resolves a stage manifest into an ordered deployment type spec list.
/// A manifest without a DeploymentTypes array yields one spec built from the
/// base fields (single-deployment-type behavior). A DeploymentTypes array
/// yields one spec per entry in manifest order; CM assigns priority by
/// creation order and the client installs the first deployment type whose
/// requirements pass, so authors list the most specific variant first and
/// the unconditional fallback last.
/// </summary>
public static class DeploymentTypeSpecResolver
{
    public const string DefaultInstallCommand = "install.bat";
    public const string DefaultUninstallCommand = "uninstall.bat";

    public static List<DeploymentTypeSpec> Resolve(
        StageManifest manifest, string networkContentPath, string appName)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var entries = manifest.DeploymentTypes ?? [];
        if (entries.Length == 0)
        {
            return
            [
                new DeploymentTypeSpec
                {
                    DtName = appName,
                    ContentLocation = networkContentPath,
                    InstallCommand = DefaultInstallCommand,
                    UninstallCommand = DefaultUninstallCommand,
                    Detection = manifest.Detection,
                    Requirements = []
                }
            ];
        }

        var specs = new List<DeploymentTypeSpec>(entries.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.NameSuffix))
                throw new InvalidOperationException(
                    "Every DeploymentTypes entry requires a NameSuffix; the deployment type name becomes '<AppName> - <NameSuffix>'.");

            var dtName = $"{appName} - {entry.NameSuffix}";
            if (!seen.Add(dtName))
                throw new InvalidOperationException(
                    $"DeploymentTypes entries produce a duplicate deployment type name '{dtName}'.");

            var content = networkContentPath;
            if (!string.IsNullOrWhiteSpace(entry.ContentSubpath))
                content = Path.Combine(networkContentPath, entry.ContentSubpath);

            var detection = IsUsable(entry.Detection) ? entry.Detection!
                : IsUsable(manifest.Detection) ? manifest.Detection
                : throw new InvalidOperationException(
                    $"DeploymentTypes entry '{entry.NameSuffix}' has no Detection and the manifest has no base Detection.");

            specs.Add(new DeploymentTypeSpec
            {
                DtName = dtName,
                ContentLocation = content,
                InstallCommand = string.IsNullOrWhiteSpace(entry.InstallCommandLine)
                    ? DefaultInstallCommand : entry.InstallCommandLine!,
                UninstallCommand = string.IsNullOrWhiteSpace(entry.UninstallCommandLine)
                    ? DefaultUninstallCommand : entry.UninstallCommandLine!,
                Detection = detection,
                // Requirements resolve strictly from the entry: a variant
                // without rules is the unconditional fallback by design, and
                // inheriting the base rules here would gate the fallback by
                // accident. Nulls inside the array are dropped so a rule-less
                // entry carries zero rules, not one phantom rule.
                Requirements = (entry.Requirements ?? []).Where(r => r is not null).ToArray()
            });
        }

        return specs;
    }

    /// <summary>
    /// True when the spec list describes deployment type variants rather
    /// than the single base deployment type.
    /// </summary>
    public static bool IsMultiDeploymentType(IReadOnlyList<DeploymentTypeSpec> specs, string appName) =>
        specs.Count > 1 || (specs.Count == 1 && !string.Equals(specs[0].DtName, appName, StringComparison.Ordinal));

    /// <summary>
    /// Settles which commands both targets see and records them on the manifest. A saved
    /// operator override replaces the manifest's own commands outright, which is the
    /// precedence the MECM path already applies to its argument; with no saved override
    /// the manifest keeps what it carries, so an ad-hoc or manifest-direct stage still
    /// supplies its own commands. The manifest is updated in memory only — the staged
    /// file is not rewritten, and neither are the stored settings.
    /// </summary>
    public static StageCommandOverrides? ResolveCommandOverrides(
        StageManifest manifest, StageCommandOverrides? saved)
    {
        var effective = saved ?? manifest.CommandOverrides;
        manifest.CommandOverrides = effective;
        return effective;
    }

    /// <summary>
    /// Applies operator command overrides to the single deployment type.
    /// A multi-deployment-type manifest carries per-variant commands, so the
    /// flat override is refused rather than applied to every variant.
    /// </summary>
    public static void ApplyCommandOverrides(
        IReadOnlyList<DeploymentTypeSpec> specs,
        StageCommandOverrides? overrides,
        string appName,
        Action<string>? onLog = null)
    {
        if (overrides is null) return;
        if (string.IsNullOrWhiteSpace(overrides.Install) && string.IsNullOrWhiteSpace(overrides.Uninstall))
            throw new InvalidOperationException(
                "Command overrides carry neither an Install nor an Uninstall command.");

        if (IsMultiDeploymentType(specs, appName))
        {
            onLog?.Invoke("Command overrides ignored: a DeploymentTypes manifest carries per-deployment-type commands.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(overrides.Install))
        {
            specs[0].InstallCommand = overrides.Install!.Trim();
            onLog?.Invoke($"Install command (override)   : {specs[0].InstallCommand}");
        }
        if (!string.IsNullOrWhiteSpace(overrides.Uninstall))
        {
            specs[0].UninstallCommand = overrides.Uninstall!.Trim();
            onLog?.Invoke($"Uninstall command (override) : {specs[0].UninstallCommand}");
        }
    }

    /// <summary>
    /// Applies operator requirement specs to the single deployment type.
    /// A multi-deployment-type manifest carries per-variant requirements
    /// that are authoritative, so the flat set is refused rather than
    /// applied to every variant.
    /// </summary>
    public static void ApplyRequirements(
        IReadOnlyList<DeploymentTypeSpec> specs,
        IReadOnlyList<StageRequirement>? requirements,
        string appName,
        Action<string>? onLog = null)
    {
        if (requirements is null || requirements.Count == 0) return;

        if (IsMultiDeploymentType(specs, appName))
        {
            onLog?.Invoke("Requirement rules ignored: a DeploymentTypes manifest carries per-deployment-type Requirements.");
            return;
        }

        specs[0].Requirements = [.. requirements.Where(r => r is not null)];
    }

    /// <summary>
    /// A detection is usable when it names a type and carries at least one
    /// locator field. The C# model always materializes a Detection object,
    /// so emptiness stands in for the PowerShell null check.
    /// </summary>
    private static bool IsUsable(StageDetection? det)
    {
        if (det is null || string.IsNullOrWhiteSpace(det.Type)) return false;
        return !string.IsNullOrWhiteSpace(det.RegistryKeyRelative)
            || !string.IsNullOrWhiteSpace(det.FilePath)
            || !string.IsNullOrWhiteSpace(det.FileName)
            || !string.IsNullOrWhiteSpace(det.ScriptText)
            || det.Clauses is { Length: > 0 };
    }
}
