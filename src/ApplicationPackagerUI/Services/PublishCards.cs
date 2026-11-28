using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>The detail card of each system in the publish review: what a publish creates there, from the settings and the plan.</summary>
public static class PublishCards
{
    public static IReadOnlyList<PublishCard> For(PublishPlan plan, AppSettings settings)
    {
        var scope = OneClickSettingsMap.PlanSettings(settings, consoleFound: true).Scope;
        return plan.Systems.Select(system => new PublishCard(system, Lines(plan, settings, scope, system))).ToList();
    }

    private static List<PublishCardLine> Lines(PublishPlan plan, AppSettings settings, OneClickScopeSettings scope, string system)
    {
        var rows = plan.Rows.Where(r => r.Cells.Any(c => c.Destination == system && c.Kind == PublishCellKind.Create)).Select(r => r.App).ToList();
        // A PS1 packager can distribute and deploy by its own script, so the card does not say that nothing is deployed.
        var scripts = system == OneClickDestinations.ConfigMgr && rows.Any(a => a.ScriptDecides);
        var lines = new List<PublishCardLine>
        {
            new("Scope", !scripts ? OneClickPlanner.Scope(system, scope)
                : rows.All(a => a.ScriptDecides) ? "The PS1 packager script decides distribution and deployment."
                : OneClickPlanner.Scope(system, scope) + ". A PS1 packager can distribute or deploy by its own script.")
        };
        string PerApplication(Func<PublishPlanApp, string> text) =>
            rows.Count == 0 ? "No application is published to this system." : string.Join(Environment.NewLine, rows.Select(a => a.Application + ": " + text(a)));
        switch (system)
        {
            case OneClickDestinations.ConfigMgr:
                lines.Add(new("Content source", string.IsNullOrWhiteSpace(settings.FileShareRoot) ? "File Share Root is not set." : settings.FileShareRoot.Trim()));
                lines.Add(new("Detection method", PerApplication(a => a.Detection.Length > 0 ? a.Detection : "set by the packager")));
                lines.Add(new("Existing application", PerApplication(a => a.Site.Kind switch
                {
                    ConfigMgrFactKind.Found => a.Site.Version.Length > 0 ? "version " + a.Site.Version : "an application with no version",
                    ConfigMgrFactKind.Unreadable => "ConfigMgr was not read",
                    _ => "none found by the last check"
                })));
                lines.Add(new("Distribution and deployment", PerApplication(a => a.ScriptDecides ? "the PS1 packager script decides"
                    : a.ConfigMgrSteps.Length > 0 ? a.ConfigMgrSteps : "none")));
                lines.Add(new("Supersedence", PerApplication(a => a.ScriptDecides ? "set by the PS1 packager script"
                    : a.Supersedes ? "on, the run asks before it applies it" : "off")));
                break;
            case OneClickDestinations.Intune:
                lines.Add(new("Detection method", "From the detection method of the stage."));
                lines.Add(new("Assignments", "App Packager does not create or change assignments."));
                break;
            default:
                lines.Add(new("Applies to", "Computers that have an older version. WSUS cannot give a first install."));
                break;
        }
        return lines;
    }

    /// <summary>The detection type of a JSON definition; empty when the row has none or it cannot be read.</summary>
    public static string DetectionOf(PackagerInfo row)
    {
        if (!row.IsJsonDefined || string.IsNullOrWhiteSpace(row.DefinitionPath) || !File.Exists(row.DefinitionPath)) return "";
        try
        {
            var definition = JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(row.DefinitionPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return definition?.Detection?.Type ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ""; }
    }
}
