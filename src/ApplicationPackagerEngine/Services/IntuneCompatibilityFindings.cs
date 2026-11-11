using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>One thing an Intune publish of a manifest would or would not do.</summary>
public sealed record IntuneCompatibilityFinding(string Severity, string Code, string Message);

/// <summary>
/// What an Intune publish of a manifest would and would not do, decided before any
/// Graph call so a blocking gap never leaves a half-created app in the tenant.
/// Blocking refuses the publish; Review needs an operator decision; Info records a
/// capability the publish does not carry. Building the list never throws.
/// </summary>
public static partial class IntuneCompatibilityFindings
{
    public const string Blocking = "Blocking";
    public const string Review = "Review";
    public const string Info = "Info";

    [GeneratedRegex("^(x64|amd64|64|64-bit|x86_64)$", RegexOptions.IgnoreCase)]
    private static partial Regex X64Names();

    [GeneratedRegex("^(x86|intel|32|32-bit)$", RegexOptions.IgnoreCase)]
    private static partial Regex X86Names();

    [GeneratedRegex("^(arm64|aarch64)$", RegexOptions.IgnoreCase)]
    private static partial Regex Arm64Names();

    /// <summary>The Graph windowsArchitecture value for a manifest architecture; empty when none maps.</summary>
    public static string AllowedArchitecture(string? architecture)
    {
        var value = architecture ?? "";
        if (X64Names().IsMatch(value)) return "x64";
        if (X86Names().IsMatch(value)) return "x86";
        if (Arm64Names().IsMatch(value)) return "arm64";
        return "";
    }

    /// <param name="manifest">The manifest as the publish would read it.</param>
    /// <param name="untranslatedRequirements">ConfigMgr requirement rules the run applies outside the manifest.</param>
    /// <param name="templates">The condition templates the requirement specs bind to; the built-in ones when null.</param>
    /// <param name="acceptedDrops">The <see cref="IntuneRequirementPlan.DropKey"/> values of the rules whose drop the administrator accepted.</param>
    public static IReadOnlyList<IntuneCompatibilityFinding> Build(
        StageManifest manifest, IReadOnlyList<StageRequirement>? untranslatedRequirements = null,
        ConditionTemplateDocument? templates = null, IEnumerable<string>? acceptedDrops = null)
    {
        var findings = new List<IntuneCompatibilityFinding>();

        var isMultiDt = false;
        if (manifest.DeploymentTypes is { Length: > 0 })
        {
            try
            {
                var specs = DeploymentTypeSpecResolver.Resolve(manifest, ".", manifest.AppName);
                isMultiDt = DeploymentTypeSpecResolver.IsMultiDeploymentType(specs, manifest.AppName);
            }
            catch (InvalidOperationException)
            {
                isMultiDt = true;
            }
        }
        if (isMultiDt)
            findings.Add(new(Blocking, "MultipleDeploymentTypes",
                "This application has deployment-type variants. Intune has no equivalent priority and fallback model, so the variant set cannot be published as one Win32 app."));
        if (manifest.RequireUserInteraction == true)
            findings.Add(new(Blocking, "InteractiveInstall",
                "This deployment type allows the user to interact with the installation. Intune runs Win32 installs silently, so an interactive profile cannot be published."));

        try
        {
            IntunePublishService.BuildRules(manifest.Detection);
        }
        catch (InvalidOperationException ex)
        {
            findings.Add(new(Blocking, "DetectionNotMappable", ex.Message));
        }

        var requirementPlan = IntuneRequirementPlan.Of(untranslatedRequirements, templates, acceptedDrops);
        foreach (var translation in IntuneRequirementRules.Translate(requirementPlan.AllSpecs(manifest), requirementPlan.Templates))
        {
            var id = translation.Spec.ConditionId ?? "";
            if (translation.Translated)
                findings.Add(new(Info, "RequirementTranslated",
                    $"The requirement '{id}' is published as a PowerShell requirement rule."));
            else if (requirementPlan.AcceptedDrops.Contains(IntuneRequirementPlan.DropKey(manifest.AppName, id)))
                findings.Add(new(Review, "RequirementDropped",
                    $"The requirement '{id}' has no Intune rule ({translation.Gap}) and its drop is accepted for this application, so the published app is not gated by it."));
            else
                findings.Add(new(Blocking, "RequirementNotTranslatable",
                    $"The requirement '{id}' has no Intune rule: {translation.Gap} The app would install on devices that ConfigMgr skips. Accept the drop of this rule for this application, or remove the requirement."));
        }

        if (string.IsNullOrWhiteSpace(manifest.Architecture))
            findings.Add(new(Review, "ArchitectureUnresolved",
                "The manifest declares no architecture, so the published app keeps the publisher default rather than a value resolved from the installer."));
        else if (AllowedArchitecture(manifest.Architecture).Length == 0)
            findings.Add(new(Blocking, "ArchitectureUnsupported",
                $"Architecture '{manifest.Architecture}' has no allowedArchitectures value in the Graph v1.0 Win32 app resource (x86, x64, arm64)."));

        if (manifest.Timing is { } timing && (timing.EstimatedMinutes is not null || timing.MaximumMinutes is not null))
            findings.Add(new(Info, "TimingNotApplied",
                "Estimated and maximum runtime minutes are recorded in the build but not sent to Intune: the Graph v1.0 install-experience resource has no runtime property (Timing.IntuneApplied = false)."));
        if (string.IsNullOrWhiteSpace(manifest.SetupFile))
            findings.Add(new(Info, "SetupFileFromPackage",
                "The manifest names no setup file, so the setup reference comes from the .intunewin package metadata."));

        return findings;
    }

    /// <summary>The run-log line for one finding.</summary>
    public static string LogLine(IntuneCompatibilityFinding finding) =>
        $"Intune compatibility         : [{finding.Severity}] {finding.Code} - {finding.Message}";

    public static bool HasBlocking(IEnumerable<IntuneCompatibilityFinding> findings) =>
        findings.Any(f => f.Severity == Blocking);

    /// <summary>The refusal message for the blocking findings, or null when none blocks.</summary>
    public static string? RefusalMessage(IEnumerable<IntuneCompatibilityFinding> findings)
    {
        var blocking = findings.Where(f => f.Severity == Blocking).ToList();
        return blocking.Count == 0
            ? null
            : "This application cannot be published to Intune: " + string.Join(" | ", blocking.Select(f => $"{f.Code}: {f.Message}"));
    }
}
