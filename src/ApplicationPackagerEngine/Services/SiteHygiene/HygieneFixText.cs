using System.Globalization;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>The names of the fix parameters a finding carries.</summary>
public static class HygieneFixParameters
{
    /// <summary>The exact identifier the write uses: CI_ID, PackageID, CollectionID, ResourceID, AutoDeploymentID, ModelName or task name.</summary>
    public const string Id = "Id";
    public const string ModelName = "ModelName";
    public const string CollectionId = "CollectionId";
    public const string WindowId = "WindowId";
    public const string RefreshType = "RefreshType";

    /// <summary>Old revision numbers, comma separated.</summary>
    public const string Revisions = "Revisions";

    /// <summary>AssignmentUniqueIDs, comma separated.</summary>
    public const string DeploymentIds = "DeploymentIds";

    public const string SiteCode = "SiteCode";

    /// <summary>The threshold in days the check used; the re-evaluation uses the same value.</summary>
    public const string Days = "Days";
}

/// <summary>
/// One command of a typed fix with its parameters in order. The equivalent PowerShell the plan shows is
/// built from it, and the session script runs the same cmdlet with the same values.
/// </summary>
public sealed record HygieneCommand(string Cmdlet, IReadOnlyList<KeyValuePair<string, string>> Parameters, bool Switch = false)
{
    /// <summary>The window removal has no exact cmdlet; its text is the settings write the script makes.</summary>
    public bool IsWindowRemoval => Cmdlet == HygieneFixText.WindowRemoval;
}

/// <summary>Builds the commands and the read-only PowerShell text of a typed fix.</summary>
public static class HygieneFixText
{
    internal const string WindowRemoval = "SMS_CollectionSettings.ServiceWindows";

    private static KeyValuePair<string, string> P(string name, string value) => new(name, value);

    private static string Get(IReadOnlyDictionary<string, string> p, string name) => p.TryGetValue(name, out var v) ? v ?? "" : "";

    private static IEnumerable<string> List(IReadOnlyDictionary<string, string> p, string name) =>
        Get(p, name).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The commands of a fix, in the order the service runs them; empty for a report-only kind.</summary>
    public static IReadOnlyList<HygieneCommand> Commands(HygieneFixKind kind, IReadOnlyDictionary<string, string> p)
    {
        var id = Get(p, HygieneFixParameters.Id);
        return kind switch
        {
            HygieneFixKind.RemoveApplication => [new("Remove-CMApplication", [P("Id", id), P("Force", "")])],
            HygieneFixKind.RemoveApplicationDeployment => List(p, HygieneFixParameters.DeploymentIds).Select(d => new HygieneCommand("Remove-CMApplicationDeployment", [P("DeploymentId", d), P("Force", "")])).ToList(),
            HygieneFixKind.RemoveApplicationRevisionHistory => List(p, HygieneFixParameters.Revisions).Select(r => new HygieneCommand("Remove-CMApplicationRevisionHistory", [P("Id", id), P("Revision", r), P("Force", "")])).ToList(),
            HygieneFixKind.RemovePackage => [new("Remove-CMPackage", [P("Id", id), P("Force", "")])],
            HygieneFixKind.RemoveCollection => [new("Remove-CMCollection", [P("Id", id), P("Force", "")])],
            HygieneFixKind.RemoveDevice => [new("Remove-CMDevice", [P("ResourceId", id), P("Force", "")])],
            HygieneFixKind.RemoveBootImage => [new("Remove-CMBootImage", [P("Id", id), P("Force", "")])],
            HygieneFixKind.RemoveDriverPackage => [new("Remove-CMDriverPackage", [P("Id", id), P("Force", "")])],
            HygieneFixKind.RemoveAutoDeploymentRule => [new("Remove-CMSoftwareUpdateAutoDeploymentRule", [P("Id", id), P("Force", "")])],
            HygieneFixKind.RemoveMaintenanceWindow => [new(WindowRemoval, [P("CollectionId", Get(p, HygieneFixParameters.CollectionId)), P("ServiceWindowID", Get(p, HygieneFixParameters.WindowId))])],
            HygieneFixKind.SetCollectionRefreshType => [new("Set-CMCollection", [P("CollectionId", id), P("RefreshType", Get(p, HygieneFixParameters.RefreshType))])],
            HygieneFixKind.SetApplication => [new("Set-CMApplication", [P("ModelName", Get(p, HygieneFixParameters.ModelName)), P("AutoInstall", "$true")])],
            HygieneFixKind.SetSiteMaintenanceTask => [new("Set-CMSiteMaintenanceTask", [P("SiteCode", Get(p, HygieneFixParameters.SiteCode)), P("Name", id), P("Enabled", "$true")])],
            HygieneFixKind.RunAutoDeploymentRule => [new("Invoke-CMSoftwareUpdateAutoDeploymentRule", [P("Id", id)])],
            _ => []
        };
    }

    /// <summary>The read-only PowerShell of a fix; null for a report-only kind.</summary>
    public static string? Text(HygieneFixKind kind, IReadOnlyDictionary<string, string> p)
    {
        var commands = Commands(kind, p);
        if (commands.Count == 0) return null;
        return string.Join("\r\n", commands.Select(Line));
    }

    /// <summary>One command as PowerShell: string values single-quoted with every quote character doubled, numbers and $true bare, Force as a switch.</summary>
    public static string Line(HygieneCommand c)
    {
        if (c.IsWindowRemoval)
        {
            var collection = Quote(c.Parameters[0].Value);
            var window = Quote(c.Parameters[1].Value);
            return "$s = Invoke-CMWmiQuery -Query \"SELECT * FROM SMS_CollectionSettings WHERE CollectionID = " + collection + "\" | Select-Object -First 1\r\n"
                + "$s.Get()\r\n"
                + "$keep = New-Object 'System.Collections.Generic.List[Microsoft.ConfigurationManagement.ManagementProvider.IResultObject]'\r\n"
                + "foreach ($item in $s.GetArrayItems('ServiceWindows')) { if ($item['ServiceWindowID'].StringValue -ne " + window + ") { $keep.Add($item) } }\r\n"
                + "$s.SetArrayItems('ServiceWindows', $keep)\r\n"
                + "$s.Put()";
        }
        var text = new StringBuilder(c.Cmdlet);
        foreach (var (name, value) in c.Parameters)
        {
            text.Append(" -").Append(name);
            if (name == "Force") continue;
            text.Append(' ').Append(value == "$true" || IsNumber(value) ? value : Quote(value));
        }
        return text.ToString();
    }

    private static bool IsNumber(string value) => value.Length is > 0 and <= 10 && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static string Quote(string value) => "'" + PowerShellLiteral.EscapeSingleQuoted(value) + "'";
}
