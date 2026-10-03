using System.Globalization;
using System.Text;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>One row of the Default versus custom grid.</summary>
public sealed record WorkbenchDiffRow(string Field, string Default, string Custom);

/// <summary>The Review and build page: the default-versus-custom diff and the resolved plan text.</summary>
public static class WorkbenchReview
{
    /// <summary>The overridable fields the diff reports, in page order.</summary>
    public static readonly IReadOnlyList<string> DiffPaths =
    [
        "Application.DisplayName", "Application.Publisher", "Application.Description", "Application.TitleMode",
        "Install.Mode", "Install.Command", "Uninstall.Mode", "Uninstall.Command",
        "Detection.Mode", "InstallMode", "Timing.EstimatedMinutes", "Timing.MaximumMinutes",
        "Execution.Context", "Execution.ScriptHost"
    ];

    /// <summary>Every overridden field with its inherited and custom value; source files as a count.</summary>
    public static IReadOnlyList<WorkbenchDiffRow> Diff(WorkbenchEditorSession session)
    {
        var rows = new List<WorkbenchDiffRow>();
        foreach (var path in DiffPaths)
        {
            if (!session.IsOverridden(path)) continue;
            var custom = session.Get(path).Value;
            rows.Add(new WorkbenchDiffRow(path, session.Inherited.ForPath(path),
                custom is null ? "(removed)" : WorkbenchJson.AsText(custom) ?? ""));
        }
        var files = SourceFileCount(session.Profile);
        if (files > 0) rows.Add(new WorkbenchDiffRow("SourceFiles", "(none)", $"{files} file(s)"));
        return rows;
    }

    /// <summary>The read-only plan: what a build of this profile resolves to before staging.</summary>
    public static string Plan(WorkbenchEditorSession session, string signingLine)
    {
        var profile = session.Profile;
        var inherited = session.Inherited;
        string Effective(string path, string fallback)
        {
            var member = profile.Get(path);
            return member.Found && !string.IsNullOrEmpty(member.Text) ? member.Text! : fallback;
        }

        var detection = string.Equals(profile.Get("Detection.Mode").Text, "Custom", StringComparison.OrdinalIgnoreCase)
            ? "Custom " + DetectionTypeDisplay(profile.Get("Detection.Rule.Type").Text)
            : inherited.DetectionSummary;

        var plan = new StringBuilder();
        plan.AppendLine("Application  : " + session.ApplicationId);
        plan.AppendLine($"Profile      : {profile.Name} ({session.ProfileId}, revision {profile.Revision})");
        plan.AppendLine("Title        : " + Effective("Application.DisplayName", inherited.DisplayName));
        plan.AppendLine("Install      : " + Effective("Install.Command", inherited.InstallCommand));
        plan.AppendLine("Detection    : " + detection);
        plan.AppendLine($"Timing       : estimated {Effective("Timing.EstimatedMinutes", inherited.EstimatedMinutes.ToString(CultureInfo.InvariantCulture))} min, maximum {Effective("Timing.MaximumMinutes", inherited.MaximumMinutes.ToString(CultureInfo.InvariantCulture))} min");
        plan.AppendLine($"Execution    : {Effective("Execution.Context", inherited.ExecutionContext)}, script host {Effective("Execution.ScriptHost", inherited.ScriptHost)}");
        plan.AppendLine("Source files : " + SourceFileCount(profile));
        plan.AppendLine("Signing      : " + signingLine);
        return plan.ToString();
    }

    /// <summary>
    /// The plan's signing line for the policy a build of this profile resolves at Stage:
    /// the global policy from Settings, which the run snapshot freezes.
    /// </summary>
    public static string SigningLine(Models.Signing.SigningPolicy? stored)
    {
        var policy = Models.Signing.SigningPolicy.Normalize(stored);
        if (!policy.AnySwitch) return "off; no script is signed in this build";
        static string List(params (bool On, string Name)[] items) => string.Join(", ", items.Where(i => i.On).Select(i => i.Name));
        var signs = List((policy.SignDetection, "detection"), (policy.SignRequirements, "requirement"), (policy.SignDeployment, "install and uninstall"));
        var requires = List((policy.RequireDetection, "detection"), (policy.RequireRequirements, "requirement"), (policy.RequireDeployment, "install and uninstall"));
        var parts = new List<string>();
        if (signs.Length > 0) parts.Add("signs " + signs + " scripts");
        if (requires.Length > 0) parts.Add("requires valid " + requires + " signatures");
        parts.Add(string.IsNullOrWhiteSpace(policy.CertificateThumbprint)
            ? "no certificate selected"
            : $"certificate {policy.CertificateThumbprint} ({policy.StoreLocation})");
        parts.Add(string.IsNullOrWhiteSpace(policy.TimestampServer)
            ? "no timestamp"
            : "timestamp " + policy.TimestampServer + (policy.TimestampRequired ? " (required)" : ""));
        parts.Add(policy.HashAlgorithm);
        return string.Join("; ", parts) + "; applied at build time";
    }

    /// <summary>The Type combo text for a stored rule type: Script shows as PowerShell.</summary>
    public static string DetectionTypeDisplay(string? storedType) =>
        string.Equals(storedType, "Script", StringComparison.OrdinalIgnoreCase) ? "PowerShell"
        : string.IsNullOrWhiteSpace(storedType) ? "Registry" : storedType;

    private static int SourceFileCount(WorkbenchProfile profile) =>
        WorkbenchProfileValidator.Items(profile.Data["SourceFiles"]).Count(e => e is not null);
}
