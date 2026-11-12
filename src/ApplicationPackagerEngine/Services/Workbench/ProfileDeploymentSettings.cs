using System.Globalization;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>One entry of the return codes list of an install step in a profile.</summary>
public sealed record ProfileReturnCode(int Code, string Type);

/// <summary>
/// The install step fields of a profile that are not part of a command: the return codes, the reboot policy and the working directory.
/// The text is checked when the stage is finalized, so a mistake stops the stage with a clear message and does not publish a
/// deployment that ignores what the profile says.
/// </summary>
public static class ProfileDeploymentSettings
{
    /// <summary>The types of a return code and the Graph spelling of each. FastRetry is a ConfigMgr type; Intune has retry.</summary>
    private static readonly (string Name, string Graph)[] CodeTypes =
    [
        ("Success", "success"), ("SoftReboot", "softReboot"), ("HardReboot", "hardReboot"),
        ("Retry", "retry"), ("FastRetry", "retry"), ("Failed", "failed")
    ];

    /// <summary>The codes that every Intune app has before a profile adds to them.</summary>
    public static IReadOnlyList<(int Code, string Graph)> IntuneDefaults { get; } =
        [(0, "success"), (1707, "success"), (3010, "softReboot"), (1641, "hardReboot"), (1618, "retry")];

    /// <summary>
    /// Parses a list such as <c>0,3010=SoftReboot,1641=HardReboot</c>. A bare number is a success. An empty text has no entries.
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry is not a number, has an unknown type, or repeats a code with another type.</exception>
    public static IReadOnlyList<ProfileReturnCode> ParseReturnCodes(string? text, string stepName = "Install")
    {
        var result = new List<ProfileReturnCode>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Split(',', ';', '\r', '\n'))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            var parts = entry.Split('=', 2);
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                throw new InvalidOperationException(
                    $"{stepName}.ReturnCodes entry '{entry}' does not start with a whole number. Write the list as 0,3010=SoftReboot,1641=HardReboot.");
            var typeName = parts.Length == 2 ? parts[1].Trim() : "Success";
            var type = CodeTypes.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (type.Name is null)
                throw new InvalidOperationException(
                    $"{stepName}.ReturnCodes entry '{entry}' has the type '{typeName}'. The types are {string.Join(", ", CodeTypes.Select(t => t.Name))}.");
            var earlier = result.FirstOrDefault(r => r.Code == code);
            if (earlier is not null && !earlier.Type.Equals(type.Name, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{stepName}.ReturnCodes lists {code} as both {earlier.Type} and {type.Name}.");
            if (earlier is null) result.Add(new ProfileReturnCode(code, type.Name));
        }
        return result;
    }

    /// <summary>
    /// The table of an Intune app: the defaults, where a code of the profile replaces the default of the same code and the
    /// other codes of the profile are added.
    /// </summary>
    public static IReadOnlyList<(int Code, string Graph)> IntuneReturnCodes(IEnumerable<ProfileReturnCode> profileCodes)
    {
        var table = IntuneDefaults.ToList();
        foreach (var entry in profileCodes)
        {
            var graph = CodeTypes.First(t => t.Name.Equals(entry.Type, StringComparison.OrdinalIgnoreCase)).Graph;
            var at = table.FindIndex(t => t.Code == entry.Code);
            if (at >= 0) table[at] = (entry.Code, graph); else table.Add((entry.Code, graph));
        }
        return table;
    }

    /// <summary>
    /// The post-execution behavior of the deployment type for a reboot policy of the profile; null for Inherit or no value, which
    /// leaves the behavior of the definition. A soft reboot is what the return codes already say, so it is BasedOnExitCode.
    /// </summary>
    /// <exception cref="InvalidOperationException">The text is not a reboot policy.</exception>
    public static string? PostExecutionBehaviorFor(string? policy, string stepName = "Install")
    {
        if (string.IsNullOrWhiteSpace(policy)) return null;
        switch (policy.Trim().ToLowerInvariant().Replace("-", " ").Replace("_", " "))
        {
            case "inherit": case "packager default": return null;
            case "no reboot": case "noaction": return "NoAction";
            case "soft reboot": case "basedonexitcode": return "BasedOnExitCode";
            case "hard reboot": case "programreboot": return "ProgramReboot";
            case "force restart": case "forcereboot": return "ForceReboot";
            default:
                throw new InvalidOperationException(
                    $"{stepName}.RebootPolicy '{policy}' is not one of Inherit, No reboot, Soft reboot, Hard reboot, Force restart.");
        }
    }

    /// <summary>The working directory of a step; null when empty. A line break or another control character is refused.</summary>
    public static string? WorkingDirectoryFor(string? text, string stepName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (value.Any(char.IsControl))
            throw new InvalidOperationException($"{stepName}.WorkingDirectory holds a line break or another control character.");
        return value;
    }

    /// <summary>
    /// Applies the install fields that the manifest carries: the reboot policy sets the post-execution behavior, and the success and
    /// reboot codes of the return code list join the exit code lists that the WSUS update uses.
    /// </summary>
    public static void Apply(StageManifest manifest)
    {
        var codes = ParseReturnCodes(WorkbenchJson.AsText(manifest.InstallReturnCodes));
        var behavior = PostExecutionBehaviorFor(WorkbenchJson.AsText(manifest.InstallRebootPolicy));
        _ = WorkingDirectoryFor(WorkbenchJson.AsText(manifest.InstallWorkingDirectory), "Install");
        _ = WorkingDirectoryFor(WorkbenchJson.AsText(manifest.UninstallWorkingDirectory), "Uninstall");
        if (behavior is not null) manifest.PostExecutionBehavior = behavior;

        var success = (manifest.SuccessExitCodes ?? []).ToList();
        var reboot = (manifest.RebootExitCodes ?? []).ToList();
        foreach (var entry in codes)
        {
            if (entry.Code == 0) continue;
            if (entry.Type == "Success" && !success.Contains(entry.Code)) success.Add(entry.Code);
            else if (entry.Type is "SoftReboot" or "HardReboot" && !reboot.Contains(entry.Code)) reboot.Add(entry.Code);
        }
        if (success.Count > 0) manifest.SuccessExitCodes = success.ToArray();
        if (reboot.Count > 0) manifest.RebootExitCodes = reboot.ToArray();
    }

    /// <summary>The Graph returnCodes array of the app: the defaults and the install return codes of the manifest.</summary>
    public static JsonArray IntuneReturnCodeArray(StageManifest manifest) =>
        new(IntuneReturnCodes(ParseReturnCodes(WorkbenchJson.AsText(manifest.InstallReturnCodes)))
            .Select(t => (JsonNode)new JsonObject { ["returnCode"] = t.Code, ["type"] = t.Graph }).ToArray());
}
