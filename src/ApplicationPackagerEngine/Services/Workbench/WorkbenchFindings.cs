using System.Globalization;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>One compatibility finding: Blocking, Review or Info, a stable code and a message.</summary>
public sealed record WorkbenchFinding(string Severity, string Code, string Message)
{
    public const string Blocking = "Blocking";
    public const string Review = "Review";
    public const string Info = "Info";
}

/// <summary>The three independent footer results.</summary>
public sealed record WorkbenchChipState(string Content, string ConfigMgr, string Intune)
{
    public const string NotValidated = "Not validated";
    public static readonly WorkbenchChipState Unvalidated = new(NotValidated, NotValidated, NotValidated);
}

/// <summary>
/// Local validation of a profile. It inspects the profile only: nothing runs and no
/// target is contacted.
/// </summary>
public static class WorkbenchFindings
{
    /// <summary>True when the profile's custom detection is a script, whose text <see cref="Local"/> needs inspected.</summary>
    public static bool HasCustomDetectionScript(WorkbenchProfile profile) =>
        string.Equals(profile.Get("Detection.Mode").Text, "Custom", StringComparison.OrdinalIgnoreCase)
        && string.Equals(profile.Get("Detection.Rule.Type").Text, "Script", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The profile's own findings, before any target adapter adds its own. A custom script
    /// detection is judged on <paramref name="detectionScript"/>, the parser's inspection of
    /// its text; without one the script findings are not produced.
    /// </summary>
    public static IReadOnlyList<WorkbenchFinding> Local(WorkbenchProfile profile, WorkbenchInheritedSettings inherited,
        ScriptInspection? detectionScript = null)
    {
        var findings = new List<WorkbenchFinding>();
        void Add(string severity, string code, string message) => findings.Add(new WorkbenchFinding(severity, code, message));

        var estimated = Minutes(profile.Get("Timing.EstimatedMinutes"), inherited.EstimatedMinutes);
        var maximum = Minutes(profile.Get("Timing.MaximumMinutes"), inherited.MaximumMinutes);
        if (estimated < 1 || maximum < 1)
            Add(WorkbenchFinding.Blocking, "TIMING-RANGE", "Estimated duration and maximum runtime must both be at least one minute.");
        else if (estimated > maximum)
            Add(WorkbenchFinding.Blocking, "TIMING-ORDER", "Estimated install duration is greater than the maximum runtime.");
        if (maximum >= 1 && (maximum < DeploymentTypeTiming.MinimumMaximumRuntimeMinutes || maximum > DeploymentTypeTiming.MaximumAllowedMinutes))
            Add(WorkbenchFinding.Review, "TIMING-CONFIGMGR-RANGE",
                $"ConfigMgr accepts a maximum runtime from {DeploymentTypeTiming.MinimumMaximumRuntimeMinutes} to {DeploymentTypeTiming.MaximumAllowedMinutes} minutes. A ConfigMgr publish with this value stops before it writes anything.");
        Add(WorkbenchFinding.Info, "TIMING-INTUNE", "Intune install timeout is not sent by this publisher; the value is recorded in the definition only.");

        foreach (var section in new[] { "Install", "Uninstall" })
        {
            if (string.Equals(profile.Get(section + ".Mode").Text, "Custom", StringComparison.OrdinalIgnoreCase))
                Add(WorkbenchFinding.Review, "INSTALL-CUSTOM",
                    section + " uses a custom script, so later changes to the packager wrapper no longer reach this profile and need review.");
        }

        if (string.Equals(profile.Get("Detection.Mode").Text, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            if (detectionScript is not null && HasCustomDetectionScript(profile))
            {
                if (!detectionScript.WritesOutput)
                    Add(WorkbenchFinding.Blocking, "DETECT-NO-OUTPUT",
                        "The detection script writes nothing to STDOUT, so an installed endpoint still reports not installed.");
                if (detectionScript.Diagnostics.Count > 0)
                    Add(WorkbenchFinding.Blocking, "DETECT-PARSE", "The detection script does not parse: " + detectionScript.Diagnostics[0]);
            }
            var logic = profile.Get("Detection.Rule.Logic").Text ?? "";
            var conversion = WorkbenchJson.IsTruthy(profile.Get("Detection.IntuneScriptConversion").Value);
            if ((logic.Equals("Or", StringComparison.OrdinalIgnoreCase) || logic.Equals("TwoGroup", StringComparison.OrdinalIgnoreCase)) && !conversion)
                Add(WorkbenchFinding.Blocking, "DETECT-INTUNE-COMPOUND",
                    "Intune cannot express this grouped or OR detection; choose script conversion explicitly, or publish to ConfigMgr only.");
        }

        if (WorkbenchProfileValidator.Items(profile.Get("Requirements.Operations").Value).Any(o => o is not null))
            Add(WorkbenchFinding.Review, "REQ-INTUNE",
                "The Intune publisher turns each requirement rule into a PowerShell requirement rule. A rule that has no Intune form blocks the Intune publish until its drop is accepted for the application.");

        if (WorkbenchJson.IsTruthy(profile.Get("Variants.Split").Value))
            Add(WorkbenchFinding.Blocking, "VARIANT-INTUNE",
                "A variant split produces several deployment types; Intune publishing of split applications is not supported.");

        foreach (var entry in WorkbenchProfileValidator.Items(profile.Data["SourceFiles"]))
        {
            var destination = WorkbenchJson.Get(entry, "Destination").Text ?? "";
            if (EscapesContentRoot(destination))
                Add(WorkbenchFinding.Blocking, "SOURCE-PATH", "Source file destination escapes the content root: " + destination);
        }

        return findings;
    }

    /// <summary>
    /// The footer chips. Content and ConfigMgr count Blocking and Review findings outside
    /// the Intune codes; Intune is Unsupported on an Intune blocker and Needs review on an
    /// Intune review item. An Intune finding never changes the other two.
    /// </summary>
    public static WorkbenchChipState Chips(IEnumerable<WorkbenchFinding> findings, bool validated)
    {
        if (!validated) return WorkbenchChipState.Unvalidated;
        var all = findings.ToList();
        static bool Actionable(WorkbenchFinding f) => f.Severity is WorkbenchFinding.Blocking or WorkbenchFinding.Review;
        static bool IsIntune(WorkbenchFinding f) => f.Code.Contains("INTUNE", StringComparison.OrdinalIgnoreCase);

        var content = all.Any(f => Actionable(f) && (f.Code.StartsWith("SOURCE-", StringComparison.OrdinalIgnoreCase)
                                                     || f.Code.StartsWith("INSTALL-", StringComparison.OrdinalIgnoreCase)));
        var configMgr = all.Any(f => Actionable(f) && !IsIntune(f));
        var intune = all.Any(f => f.Severity == WorkbenchFinding.Blocking && IsIntune(f)) ? "Unsupported"
            : all.Any(f => f.Severity == WorkbenchFinding.Review && IsIntune(f)) ? "Needs review"
            : "Ready";
        return new WorkbenchChipState(content ? "Needs review" : "Ready", configMgr ? "Needs review" : "Ready", intune);
    }

    /// <summary>
    /// The findings that stop a run: every Blocking finding for an Intune-only publish,
    /// and the Blocking findings outside the Intune codes for any other target.
    /// </summary>
    public static IReadOnlyList<WorkbenchFinding> BlockingFor(IEnumerable<WorkbenchFinding> findings, string target) =>
        findings.Where(f => f.Severity == WorkbenchFinding.Blocking
                            && (string.Equals(target, RunTargets.IntuneOnly, StringComparison.OrdinalIgnoreCase)
                                || !f.Code.Contains("INTUNE", StringComparison.OrdinalIgnoreCase)))
                .ToList();

    private static int Minutes(WorkbenchMember member, int inherited)
    {
        if (!member.Found || member.Value is null) return inherited;
        return int.TryParse(member.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ? minutes : 0;
    }

    private static bool EscapesContentRoot(string destination)
    {
        if (destination.Length == 0) return false;
        if (Path.IsPathRooted(destination)) return true;
        return destination.Split('\\', '/').Any(segment => segment == "..");
    }
}
