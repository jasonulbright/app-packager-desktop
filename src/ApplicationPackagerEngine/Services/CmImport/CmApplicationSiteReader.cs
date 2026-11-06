using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Services.CmImport;

/// <summary>
/// Reads one application from the site, read-only, through the ConfigMgr session. The stored
/// document of the application is the source: it holds every deployment type, where a
/// deployment type cmdlet returns the first deployment type's document for each type.
/// </summary>
public sealed class CmApplicationSiteReader(IConfigMgrSession session)
{
    internal const string DigestScript = """
        param([string]$ApplicationName)
        $app = @(Get-CMApplication -Name $ApplicationName -DisableWildcardHandling -WarningAction SilentlyContinue -ErrorAction Stop)
        if ($app.Count -eq 0) { throw "ConfigMgr application '$ApplicationName' was not found." }
        if ($app.Count -gt 1) { throw "More than one ConfigMgr application is named '$ApplicationName'." }
        $xml = [string]$app[0].SDMPackageXML
        if ([string]::IsNullOrWhiteSpace($xml)) { throw "ConfigMgr application '$ApplicationName' returned no stored document." }
        $xml
        """;

    internal const string NamesScript = """
        param([string]$TargetApplication, [string]$TargetDeploymentType)
        $applications = @(Get-CMApplication -Fast -ErrorAction Stop | Where-Object { $_.ModelName -like ('*' + $TargetApplication) })
        if ($applications.Count -eq 1) {
            $applicationName = [string]$applications[0].LocalizedDisplayName
            $deploymentTypeName = ''
            $deploymentTypes = @(Get-CMDeploymentType -ApplicationName $applicationName -ErrorAction Stop | Where-Object { $_.ModelName -like ('*' + $TargetDeploymentType) })
            if ($deploymentTypes.Count -eq 1) { $deploymentTypeName = [string]$deploymentTypes[0].LocalizedDisplayName }
            [pscustomobject]@{ ApplicationName = $applicationName; DeploymentTypeName = $deploymentTypeName }
        }
        """;

    private sealed record Names(string ApplicationName, string DeploymentTypeName);

    /// <summary>The stored document of the application with this exact name.</summary>
    public async Task<string> ReadDigestAsync(string applicationName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(applicationName)) throw new ArgumentException("The application name is empty.", nameof(applicationName));
        var result = await session.InvokeAsync(ConfigMgrScript.FromText("GetApplicationDigest", DigestScript),
            new ConfigMgrParameters { ["ApplicationName"] = applicationName }, ct).ConfigureAwait(false);
        var xml = result.Lines.FirstOrDefault(l => l.TrimStart().StartsWith('<') && l.Contains("AppMgmtDigest", StringComparison.Ordinal));
        return xml ?? throw new InvalidDataException($"ConfigMgr application '{applicationName}' returned no stored document.");
    }

    /// <summary>
    /// Fills the names of the dependency and supersedence targets. Best effort: a name that cannot
    /// be read stays as the logical name, and the failure goes to <paramref name="onWarning"/>.
    /// </summary>
    public async Task ResolveRelationshipNamesAsync(CmImportPlan plan, Action<string>? onWarning = null, CancellationToken ct = default)
    {
        foreach (var relationship in plan.Relationships)
        {
            try
            {
                var result = await session.InvokeAsync(ConfigMgrScript.FromText("GetRelationshipNames", NamesScript),
                    new ConfigMgrParameters
                    {
                        ["TargetApplication"] = relationship.TargetApplication,
                        ["TargetDeploymentType"] = relationship.TargetDeploymentType
                    }, ct).ConfigureAwait(false);
                if (result.As<Names>().FirstOrDefault() is { } names)
                {
                    relationship.TargetApplicationName = names.ApplicationName;
                    relationship.TargetDeploymentTypeName = names.DeploymentTypeName;
                }
            }
            catch (Exception ex) when (ex is ConfigMgrSessionException or System.Text.Json.JsonException)
            {
                onWarning?.Invoke($"Could not resolve the name of {relationship.TargetApplication}: {ex.Message}");
            }
        }
    }
}
