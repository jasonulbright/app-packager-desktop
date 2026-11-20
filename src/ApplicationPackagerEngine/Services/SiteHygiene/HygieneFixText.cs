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

    /// <summary>The ModelName of the application that a supersedence rule points at (a deleted application).</summary>
    public const string ReferencedModel = "ReferencedModel";

    /// <summary>Supersedes or DependsOn: the kind of relation that ClearRetiredRelation removes.</summary>
    public const string RelationKind = "RelationKind";

    /// <summary>APP-07: the ModelNames of the active applications a retired application supersedes, comma separated.</summary>
    public const string SupersedesModels = "SupersedesModels";

    /// <summary>APP-07: the ModelNames of the active applications a retired application depends on, comma separated.</summary>
    public const string DependsOnModels = "DependsOnModels";

    /// <summary>APP-08: the ModelNames of the applications that supersede the superseded application, comma separated.</summary>
    public const string ReplacementModels = "ReplacementModels";

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

    /// <summary>The supersedence removal has no cmdlet that can do it for a deleted application; its text is the document edit the script makes.</summary>
    public bool IsSupersedenceRemoval => Cmdlet == HygieneFixText.SupersedenceRemoval;

    /// <summary>The removal of a rule or a dependency toward a retired application, with the supported cmdlets.</summary>
    public bool IsRetiredRelationRemoval => Cmdlet == HygieneFixText.RetiredRelationRemoval;

    /// <summary>The removal of a rule of a retired application: reinstate, remove, retire again.</summary>
    public bool IsRetiredApplicationRulesRemoval => Cmdlet == HygieneFixText.RetiredApplicationRulesRemoval;

    /// <summary>The delete of a superseded application after the removal of the supersedence rules of its replacements.</summary>
    public bool IsSupersededApplicationRemoval => Cmdlet == HygieneFixText.SupersededApplicationRemoval;
}

/// <summary>Builds the commands and the read-only PowerShell text of a typed fix.</summary>
public static class HygieneFixText
{
    internal const string WindowRemoval = "SMS_CollectionSettings.ServiceWindows";

    /// <summary>
    /// Set-CMApplicationSupersedence -RemoveSupersedence (the replacement of the deprecated Remove-CMDeploymentTypeSupersedence) needs the
    /// superseded application and its deployment type as parameters. A deleted application has neither, so the fix edits the application
    /// document of the superseding application.
    /// </summary>
    internal const string SupersedenceRemoval = "SMS_Application.SDMPackageXML.Supersedes";

    internal const string RetiredRelationRemoval = "Set-CMApplicationSupersedence or Remove-CMDeploymentTypeDependency";

    /// <summary>
    /// APP-07: a retired application cannot be changed (the cmdlets run and change nothing), so the fix reinstates it, removes the rule and retires it
    /// again.
    /// </summary>
    internal const string RetiredApplicationRulesRemoval = "Resume-CMApplication, Set-CMApplicationSupersedence or Remove-CMDeploymentTypeDependency, Suspend-CMApplication";

    /// <summary>APP-08: the supersedence rules of the replacements go, then the superseded application.</summary>
    internal const string SupersededApplicationRemoval = "Set-CMApplicationSupersedence and Remove-CMApplication";

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
            HygieneFixKind.ClearRetiredRelation => [new(RetiredRelationRemoval, [P("ModelName", Get(p, HygieneFixParameters.ModelName)), P("ReferencedModel", Get(p, HygieneFixParameters.ReferencedModel)), P("RelationKind", Get(p, HygieneFixParameters.RelationKind))])],
            HygieneFixKind.ClearRetiredApplicationRules => RetiredRules(p),
            HygieneFixKind.RemoveSupersededApplication => [new(SupersededApplicationRemoval, [P("ModelName", Get(p, HygieneFixParameters.ModelName)), P("ReplacementModels", Get(p, HygieneFixParameters.ReplacementModels))])],
            HygieneFixKind.ClearBrokenSupersedence => [new(SupersedenceRemoval, [P("ModelName", Get(p, HygieneFixParameters.ModelName)), P("ReferencedModel", Get(p, HygieneFixParameters.ReferencedModel))])],
            _ => []
        };
    }

    /// <summary>
    /// APP-07: one command for each rule toward an active application. A parameter set that names one target (a write of the plan) gives that one
    /// command; the finding gives one command for each supersedence target and each dependency target.
    /// </summary>
    private static IReadOnlyList<HygieneCommand> RetiredRules(IReadOnlyDictionary<string, string> p)
    {
        var model = Get(p, HygieneFixParameters.ModelName);
        HygieneCommand One(string reference, string kind) => new(RetiredApplicationRulesRemoval, [P("ModelName", model), P("ReferencedModel", reference), P("RelationKind", kind)]);
        if (Get(p, HygieneFixParameters.ReferencedModel).Length > 0) return [One(Get(p, HygieneFixParameters.ReferencedModel), Get(p, HygieneFixParameters.RelationKind))];
        return List(p, HygieneFixParameters.SupersedesModels).Select(m => One(m, "Supersedes"))
            .Concat(List(p, HygieneFixParameters.DependsOnModels).Select(m => One(m, "DependsOn"))).ToList();
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
                + "if ($keep.Count -eq @($s.GetArrayItems('ServiceWindows')).Count) { throw 'No object corresponds to the specified parameters.' }\r\n"
                + "$s.SetArrayItems('ServiceWindows', $keep)\r\n"
                + "$s.Put()";
        }
        if (c.IsRetiredRelationRemoval)
        {
            var from = Quote(c.Parameters[0].Value);
            var to = Quote(c.Parameters[1].Value);
            return c.Parameters[2].Value == "Supersedes"
                ? "$f = Get-CMApplication -ModelName " + from + "\r\n$t = Get-CMApplication -ModelName " + to + "\r\n"
                    + "# For each supersedence rule of $f that names $t (the deployment types are read from the application definition):\r\n"
                    + "Set-CMApplicationSupersedence -Id $f.CI_ID -CurrentDeploymentTypeId $fd.CI_ID -SupersededApplicationId $t.CI_ID -OldDeploymentTypeId $td.CI_ID -RemoveSupersedence -Force"
                : "$f = Get-CMApplication -ModelName " + from + "\r\n"
                    + "# For each deployment type $dt of $f, each dependency group $g and each dependency $d whose AppModelName is " + to + ":\r\n"
                    + "Remove-CMDeploymentTypeDependency -InputObject $g -DeploymentTypeDependency $d -Force";
        }
        if (c.IsRetiredApplicationRulesRemoval)
        {
            var from = Quote(c.Parameters[0].Value);
            var to = Quote(c.Parameters[1].Value);
            var change = c.Parameters[2].Value == "Supersedes"
                ? "# For each supersedence rule of $f that names $t (the deployment types are read from SMS_AppRelation_Flat):\r\nSet-CMApplicationSupersedence -Id $f.CI_ID -CurrentDeploymentTypeId $fd.CI_ID -SupersededApplicationId $t.CI_ID -OldDeploymentTypeId $td.CI_ID -RemoveSupersedence -Force"
                : "# For each deployment type $dt of $f, each dependency group $g and each dependency $d whose AppModelName is " + to + ":\r\nRemove-CMDeploymentTypeDependency -InputObject $g -DeploymentTypeDependency $d -Force";
            return "$f = Get-CMApplication -ModelName " + from + "\r\n$t = Get-CMApplication -ModelName " + to + "\r\n"
                + "# A retired application cannot be changed: reinstate it, make the change, retire it again.\r\n"
                + "Resume-CMApplication -InputObject $f\r\n" + change + "\r\nSuspend-CMApplication -InputObject (Get-CMApplication -ModelName " + from + ")";
        }
        if (c.IsSupersededApplicationRemoval)
        {
            var model = Quote(c.Parameters[0].Value);
            var names = string.Join(", ", c.Parameters[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Quote));
            return "$t = Get-CMApplication -ModelName " + model + "\r\n"
                + "# For each supersedence rule of the replacements (" + names + ") that names $t (the deployment types are read from SMS_AppRelation_Flat):\r\n"
                + "Set-CMApplicationSupersedence -Id $f.CI_ID -CurrentDeploymentTypeId $fd.CI_ID -SupersededApplicationId $t.CI_ID -OldDeploymentTypeId $td.CI_ID -RemoveSupersedence -Force\r\n"
                + "# Only when no deployment, dependency, task sequence or stale reference row refers to the application:\r\n"
                + "Remove-CMApplication -Id $t.CI_ID -Force";
        }
        if (c.IsSupersedenceRemoval)
        {
            var model = Quote(c.Parameters[0].Value);
            var reference = c.Parameters[1].Value;
            var slash = reference.IndexOf('/', StringComparison.Ordinal);
            var scope = Quote(slash > 0 ? reference[..slash] : reference);
            var logical = Quote(slash > 0 ? reference[(slash + 1)..] : "");
            return "$app = Get-CMApplication -ModelName " + model + " -ErrorAction Stop\r\n"
                + "$serializer = [Microsoft.ConfigurationManagement.ApplicationManagement.Serialization.SccmSerializer]\r\n"
                + "$object = $serializer::DeserializeFromString($app.SDMPackageXML, $true)\r\n"
                + "foreach ($dt in $object.DeploymentTypes) { $rules = New-Object System.Collections.ArrayList; foreach ($r in $dt.Supersedes) { [void]$rules.Add($r) }\r\n"
                + "  foreach ($r in $rules) { if ($r.Expression.ApplicationAuthoringScopeId -eq " + scope + " -and $r.Expression.ApplicationLogicalName -eq " + logical + ") { [void]$dt.Supersedes.Remove($r) } } }\r\n"
                + "$app.SDMPackageXML = $serializer::SerializeToString($object, $true)\r\n"
                + "$app.Put()";
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
