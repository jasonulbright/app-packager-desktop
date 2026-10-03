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

    /// <summary>The Type combo text for a stored rule type: Script shows as PowerShell.</summary>
    public static string DetectionTypeDisplay(string? storedType) =>
        string.Equals(storedType, "Script", StringComparison.OrdinalIgnoreCase) ? "PowerShell"
        : string.IsNullOrWhiteSpace(storedType) ? "Registry" : storedType;

    private static int SourceFileCount(WorkbenchProfile profile) =>
        WorkbenchProfileValidator.Items(profile.Data["SourceFiles"]).Count(e => e is not null);
}
