using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>The parse diagnostics lists of the Install and uninstall and Detection pages.</summary>
public static class WorkbenchScriptDiagnostics
{
    public const string ScriptClean = "No parse errors. Validation inspects the script; it never runs it.";
    public const string DetectionWrites = "No parse errors; the script writes to STDOUT.";
    public const string DetectionSilent = "No parse errors, but nothing is written to STDOUT: Intune would read this as not installed.";

    /// <summary>The parser's lines for an install or uninstall script; an empty script lists nothing.</summary>
    public static IReadOnlyList<string> ForScript(string? text, ScriptInspection inspection)
    {
        if (inspection.Diagnostics.Count > 0) return inspection.Diagnostics;
        return string.IsNullOrWhiteSpace(text) ? [] : [ScriptClean];
    }

    /// <summary>The parser's lines for a detection script, with the STDOUT verdict when it parses.</summary>
    public static IReadOnlyList<string> ForDetection(string? text, ScriptInspection inspection)
    {
        if (inspection.Diagnostics.Count > 0) return inspection.Diagnostics;
        if (string.IsNullOrWhiteSpace(text)) return [];
        return [inspection.WritesOutput ? DetectionWrites : DetectionSilent];
    }
}

/// <summary>
/// The Intune compatibility findings the Workbench shows for one sealed build: the
/// findings a publish of that build's staged manifest would log, with an INTUNE- code
/// prefix so the footer counts them against the Intune chip only.
/// </summary>
public static class WorkbenchIntuneFindings
{
    public const string NoBuildCode = "INTUNE-NO-BUILD";

    public static IReadOnlyList<WorkbenchFinding> ForBuild(JsonObject? record)
    {
        if (record is null)
            return [new WorkbenchFinding(WorkbenchFinding.Info, NoBuildCode,
                "No sealed build is selected. The Intune compatibility check reads a staged build; stage this profile, then validate again.")];
        var stageRoot = WorkbenchJson.Get(record, "StageRoot").Text;
        var manifestPath = string.IsNullOrWhiteSpace(stageRoot) ? null : Path.Combine(stageRoot, "stage-manifest.json");
        if (manifestPath is null || !File.Exists(manifestPath))
            return [new WorkbenchFinding(WorkbenchFinding.Info, NoBuildCode,
                $"The staged content of build {WorkbenchJson.Get(record, "BuildId").Text} is no longer at {stageRoot}; the Intune compatibility check did not run.")];
        try
        {
            var manifest = new ManifestService().Read(manifestPath);
            return IntuneCompatibilityFindings.Build(manifest)
                .Select(f => new WorkbenchFinding(f.Severity, "INTUNE-" + f.Code, f.Message))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [new WorkbenchFinding(WorkbenchFinding.Info, NoBuildCode, $"The stage manifest of the selected build could not be read: {ex.Message}")];
        }
    }
}
