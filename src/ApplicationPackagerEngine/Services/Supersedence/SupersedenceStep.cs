using System.Text;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>How the supersedence step of a publish ended.</summary>
public enum SupersedenceStepStatus
{
    /// <summary>The mode is Off, the application has no marker, or the combination does not apply; nothing was asked of the site.</summary>
    NotRun,
    /// <summary>The lookup found no older application, or none that can be paired. Not a failure.</summary>
    NoOlderVersion,
    /// <summary>Every pair is superseded.</summary>
    Applied,
    /// <summary>The lookup or a cmdlet failed. The new application stays; the outcome is "Published, supersedence not applied".</summary>
    NotApplied
}

public sealed record SupersedenceStepResult(SupersedenceStepStatus Status, string Message, IReadOnlyList<string> Warnings)
{
    public static SupersedenceStepResult NotRun { get; } = new(SupersedenceStepStatus.NotRun, "", []);

    /// <summary>The sentence of a failed step, or null.</summary>
    public string? Failure => Status == SupersedenceStepStatus.NotApplied ? Message : null;
}

/// <summary>
/// The scripts of the supersedence step. The lookup reads the applications with the marker; the step sets one relation for each pair.
/// The relations of a deployment type are read first, and a relation that exists is not written again. Names and not identifiers go
/// to the cmdlet: every save of an application makes a new revision with a new CI_ID, so an identifier that the first call read is
/// stale for the second.
/// </summary>
public static class SupersedenceScripts
{
    /// <summary>The applications of the site whose keywords hold the marker, with their deployment types.</summary>
    public static string BuildLookupScript(string siteCode, string? providerMachine, string marker, string newApplicationName)
    {
        var sb = new StringBuilder();
        MecmTarget.AppendCmPreamble(sb, siteCode, providerMachine);
        var needle = PowerShellLiteral.EscapeSingleQuoted(">" + marker.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") + "<");
        sb.AppendLine($"$markerNeedle = '{needle}'");
        // The new application is found by its name: every save makes a new revision with a new CI_ID (checked on the lab site), so an
        // identifier that the package script read is not reliable here.
        sb.AppendLine($"$newApplicationName = '{PowerShellLiteral.EscapeSingleQuoted(newApplicationName)}'");
        sb.AppendLine("foreach ($candidate in @(Get-CMApplication)) {");
        // The new application is always listed, with its deployment types, even when it holds no marker (it may have keywords of its own).
        sb.AppendLine("    if ($candidate.LocalizedDisplayName -ne $newApplicationName -and (-not $candidate.SDMPackageXML -or -not $candidate.SDMPackageXML.Contains($markerNeedle))) { continue }");
        sb.AppendLine("    $titleB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$candidate.LocalizedDisplayName))");
        sb.AppendLine("    $expired = if ($candidate.IsExpired) { 1 } else { 0 }");
        sb.AppendLine($"    Write-Host (\"{SupersedenceLookup.ApplicationLine}|{{0}}|{{1}}|{{2}}|{{3}}\" -f $candidate.CI_ID, $expired, $candidate.SoftwareVersion, $titleB64)");
        sb.AppendLine("    foreach ($candidateDt in @(Get-CMDeploymentType -ApplicationName $candidate.LocalizedDisplayName -DisableWildcardHandling)) {");
        sb.AppendLine("        $dtB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$candidateDt.LocalizedDisplayName))");
        sb.AppendLine($"        Write-Host (\"{SupersedenceLookup.DeploymentTypeLine}|{{0}}|{{1}}|{{2}}\" -f $candidate.CI_ID, $candidateDt.CI_ID, $dtB64)");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// One relation for each pair: the new deployment type supersedes the old one. <paramref name="uninstall"/> is the uninstall option of
    /// Replace and uninstall. A relation that exists (read before the write, and read again after a failed write) is a success. Any other
    /// failure is printed, and the script exits 1 after it tried every pair. A relation that exists is not changed, so a change of the
    /// mode does not rewrite its uninstall option.
    /// </summary>
    public static string BuildStepScript(
        string siteCode, string? providerMachine, string newApplicationName, string oldApplicationName,
        IReadOnlyList<DeploymentTypePair> pairs, bool uninstall)
    {
        static string Q(string text) => PowerShellLiteral.EscapeSingleQuoted(text);
        var sb = new StringBuilder();
        MecmTarget.AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine("function Test-Supersedes([string]$appName, [string]$dtName, [string]$oldDtName) {");
        sb.AppendLine("    try {");
        sb.AppendLine("        $dt = Get-CMDeploymentType -ApplicationName $appName -DeploymentTypeName $dtName -DisableWildcardHandling -ErrorAction Stop");
        sb.AppendLine("        foreach ($relation in @(Get-CMDeploymentTypeSupersedence -InputObject $dt -ErrorAction Stop)) {");
        sb.AppendLine("            if ((($relation | Out-String)).Contains($oldDtName)) { return $true }");
        sb.AppendLine("        }");
        sb.AppendLine("    } catch { }");
        sb.AppendLine("    return $false");
        sb.AppendLine("}");
        sb.AppendLine($"$newApplicationName = '{Q(newApplicationName)}'");
        sb.AppendLine($"$oldApplicationName = '{Q(oldApplicationName)}'");
        sb.AppendLine("$supersedenceFailed = $false");
        foreach (var pair in pairs)
        {
            var label = Q($"{pair.New.Name} supersedes {pair.Old.Name}");
            sb.AppendLine("$newDtName = '" + Q(pair.New.Name) + "'");
            sb.AppendLine("$oldDtName = '" + Q(pair.Old.Name) + "'");
            sb.AppendLine("if (Test-Supersedes $newApplicationName $newDtName $oldDtName) {");
            sb.AppendLine($"    Write-Host 'Supersedence already exists: {label}'");
            sb.AppendLine("} else {");
            sb.AppendLine("    try {");
            sb.AppendLine("        Set-CMApplicationSupersedence -Name $newApplicationName -CurrentDeploymentTypeName $newDtName " +
                          "-SupersededApplicationName $oldApplicationName -OldDeploymentTypeName $oldDtName " +
                          $"-IsUninstall:${(uninstall ? "true" : "false")} -DisableWildcardHandling -ErrorAction Stop | Out-Null");
            sb.AppendLine($"        Write-Host 'Supersedence added          : {label}'");
            sb.AppendLine("    } catch {");
            sb.AppendLine("        $writeError = $_.Exception.Message");
            sb.AppendLine("        if (Test-Supersedes $newApplicationName $newDtName $oldDtName) {");
            sb.AppendLine($"            Write-Host 'Supersedence already exists: {label}'");
            sb.AppendLine("        } else {");
            sb.AppendLine("            $supersedenceFailed = $true");
            sb.AppendLine($"            Write-Host ('Supersedence failed         : {{0}}: {{1}}' -f '{label}', $writeError)");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        sb.AppendLine("if ($supersedenceFailed) { exit 1 }");
        return sb.ToString();
    }

    /// <summary>The first failure line of the step script, or the whole text when there is no such line.</summary>
    public static string FailureText(string output, string error)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Supersedence failed", StringComparison.Ordinal));
        if (line is not null) return line[(line.IndexOf(':') + 1)..].Trim();
        var text = error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return text.Length > 0 ? text : "the site gave no reason.";
    }
}
