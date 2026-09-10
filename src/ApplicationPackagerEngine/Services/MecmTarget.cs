using System.Diagnostics;
using System.Text;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Creates MECM applications from staged content.
/// Shells out to Windows PowerShell 5.1 (powershell.exe) because the CM module
/// requires .NET Framework — it cannot run in a .NET 10 hosted runspace.
/// </summary>
public sealed class MecmTarget
{
    public async Task<PackagingResult> PackageAsync(
        StageManifest manifest,
        string stagedContentPath,
        string siteCode,
        string fileServerPath,
        string comment,
        int estimatedRuntimeMins = 15,
        int maximumRuntimeMins = 30,
        Action<string>? onProgress = null,
        StageCommandOverrides? commandOverrides = null,
        ConditionTemplateDocument? conditionTemplates = null,
        IReadOnlyList<StageRequirement>? requirements = null,
        string? providerMachine = null,
        OnExistingBehavior onExisting = OnExistingBehavior.Skip,
        CancellationToken ct = default,
        string? contentLayout = null,
        string? applicationTitle = null)
    {
        try
        {
            // The title decides the application and its deployment type names;
            // the content folder stays on the manifest name so a title change
            // never moves content already on the share.
            var appName = string.IsNullOrWhiteSpace(applicationTitle) ? manifest.AppName : applicationTitle;
            var version = manifest.SoftwareVersion;
            var publisher = manifest.Publisher;
            var vendorFolder = SanitizeFolderName(publisher);
            var appFolder = manifest.DisplayName ?? manifest.AppName;
            var networkContentPath = ContentLayouts.ContentPath(fileServerPath, contentLayout ?? ContentLayouts.Nested, vendorFolder, appFolder, version);

            // 0. Validate content share accessibility
            var shareRoot = Path.GetPathRoot(fileServerPath) ?? fileServerPath;
            if (fileServerPath.StartsWith(@"\\") && !Directory.Exists(fileServerPath))
            {
                return new PackagingResult
                {
                    Success = false,
                    ErrorMessage = $"Content share not accessible: {fileServerPath}"
                };
            }

            // 0b. Title collision, decided before anything is written. A Skip
            // run that finds the title must leave no copied content behind.
            if (onExisting == OnExistingBehavior.Skip)
            {
                var conflicted = false;
                void WatchProbe(string line)
                {
                    if (OnExistingConflict.TryParse(line, out _, out _, out _)) conflicted = true;
                    onProgress?.Invoke(line);
                }

                var probe = await RunWindowsPowerShellAsync(
                    BuildExistingProbeScript(appName, version, siteCode, providerMachine), WatchProbe, ct);
                if (probe.ExitCode != 0)
                    return new PackagingResult { Success = false, ErrorMessage = probe.StdErr };
                if (conflicted)
                    return new PackagingResult { Success = true, AlreadyExists = true, ApplicationName = appName };
            }

            // 1. Mirror content to the network, refreshing files whose content changed.
            onProgress?.Invoke($"Network content path: {networkContentPath}");

            // The local stage is the source of the copy, so a stage that no longer
            // matches its own manifest is refused before the share is touched.
            var localProblems = ContentSyncService.Verify(stagedContentPath, manifest);
            if (localProblems.Count > 0)
                return new PackagingResult
                {
                    Success = false,
                    ErrorMessage = $"Staged content does not match its manifest: {string.Join("; ", localProblems)}"
                };

            var sync = ContentSyncService.Sync(stagedContentPath, networkContentPath, manifest, onProgress, ct);
            if (sync.UsedSourceHashes)
                onProgress?.Invoke("Content integrity: manifest carries no FileHashes; compared against the staged files.");
            onProgress?.Invoke(
                $"Content synchronized: {sync.Copied.Count} copied, {sync.Refreshed.Count} refreshed, {sync.Unchanged.Count} unchanged.");

            // Verified before the application is created or updated, so CM never points
            // a deployment type at content that does not match the manifest.
            var networkProblems = ContentSyncService.Verify(networkContentPath, manifest);
            if (networkProblems.Count > 0)
                return new PackagingResult
                {
                    Success = false,
                    ErrorMessage = $"Network content verification failed: {string.Join("; ", networkProblems)}"
                };
            if (manifest.FileHashes is { Length: > 0 })
                onProgress?.Invoke($"Content integrity verified: {manifest.FileHashes.Length} file(s).");

            // 2. Build the PowerShell script for CM app creation
            onProgress?.Invoke("Connecting to MECM...");
            // Only a manifest asking for CM's native Appx deployment type takes
            // that path. An MSIX staged with wrappers and a Script detection is
            // a script deployment type like every other product.
            var psScript = manifest.Detection?.Type.Equals("MSIX", StringComparison.OrdinalIgnoreCase) == true
                ? BuildMsixMecmScript(manifest, appName, networkContentPath, siteCode, comment, providerMachine)
                : BuildPackageScript(manifest, appName, networkContentPath,
                    siteCode, comment, estimatedRuntimeMins, maximumRuntimeMins,
                    conditionTemplates, commandOverrides, onProgress, requirements, providerMachine,
                    onExisting);

            // 3. Execute via Windows PowerShell 5.1
            var result = await RunWindowsPowerShellAsync(psScript, onProgress, ct);

            if (result.ExitCode == 0)
            {
                onProgress?.Invoke($"Packaged: {appName}");
                return new PackagingResult
                {
                    Success = true,
                    ApplicationName = appName,
                    NetworkContentPath = networkContentPath
                };
            }
            else
            {
                return new PackagingResult
                {
                    Success = false,
                    ErrorMessage = result.StdErr
                };
            }
        }
        catch (Exception ex)
        {
            return new PackagingResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Builds the CM PowerShell that reports whether the site already holds an
    /// application with this exact title, at any version. Wildcard handling
    /// would match a family of titles, so it is disabled and the results are
    /// filtered to an exact name; only the exact title decides the collision.
    /// Public so the generated script text is assertable without a site.
    /// </summary>
    public static string BuildExistingProbeScript(
        string appName, string incomingVersion, string siteCode, string? providerMachine = null)
    {
        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine($"$existing = @(Get-CMApplication -Name '{Esc(appName)}' -DisableWildcardHandling -ErrorAction Stop | Where-Object {{ $_.LocalizedDisplayName -eq '{Esc(appName)}' -or $_.Name -eq '{Esc(appName)}' }})");
        sb.AppendLine($"if ($existing.Count -gt 1) {{ throw \"Multiple applications have the exact title '{Esc(appName)}'; resolve duplicates before packaging.\" }}");
        sb.AppendLine("if ($existing.Count -eq 1) {");
        AppendConflictMarker(sb, appName, incomingVersion, "    ", "$existing[0]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Emits the conflict marker with the site version read at run time. The
    /// name and staged version are literals inside a single-quoted string, so
    /// a title containing a dollar sign is never interpolated.
    /// </summary>
    private static void AppendConflictMarker(
        StringBuilder sb, string appName, string incomingVersion, string indent, string appVar)
    {
        var prefix = $"{OnExistingConflict.Marker} app='{appName}' version=";
        sb.AppendLine($"{indent}Write-Host ('{Esc(prefix)}' + \"'$({appVar}.SoftwareVersion)'\" + ' incoming=''{Esc(incomingVersion)}''')");
    }

    /// <summary>
    /// Builds the CM PowerShell for one application and every deployment
    /// type its manifest resolves to. Public so the generated script text is
    /// assertable without a site.
    ///
    /// Deployment types are created in spec order because CM assigns
    /// priority by creation order and the client installs the first
    /// deployment type whose requirements pass. Requirement rules resolve
    /// for every spec before any create so a bad spec fails the run without
    /// leaving a partial application behind. On a version change the new set
    /// is created under '(staging)' names first, because a deployed
    /// application refuses to remove its last deployment type; the old set is
    /// removed and the staged names take the canonical names only after every
    /// new deployment type created successfully.
    /// </summary>
    public static string BuildPackageScript(
        StageManifest manifest, string appName, string networkContentPath,
        string siteCode, string comment, int estRuntime, int maxRuntime,
        ConditionTemplateDocument? conditionTemplates = null,
        StageCommandOverrides? commandOverrides = null,
        Action<string>? onLog = null,
        IReadOnlyList<StageRequirement>? requirements = null,
        string? providerMachine = null,
        OnExistingBehavior onExisting = OnExistingBehavior.Skip)
    {
        var specs = DeploymentTypeSpecResolver.Resolve(manifest, networkContentPath, appName);
        var isMultiDt = DeploymentTypeSpecResolver.IsMultiDeploymentType(specs, appName);
        if (isMultiDt)
            onLog?.Invoke($"Deployment types (manifest)  : {string.Join(", ", specs.Select(s => s.DtName))}");

        DeploymentTypeSpecResolver.ApplyCommandOverrides(
            specs, commandOverrides ?? manifest.CommandOverrides, appName, onLog);
        DeploymentTypeSpecResolver.ApplyRequirements(specs, requirements, appName, onLog);

        var templates = conditionTemplates ?? ConditionTemplateService.Defaults();
        var resolvedRules = specs
            .Select(s => RequirementRuleBuilder.Resolve(s.Requirements, templates))
            .ToList();

        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine("Write-Host 'Connected to MECM'");
        if (resolvedRules.Any(r => r.Count > 0))
            sb.AppendLine(RequirementRuleBuilder.BuildGlobalConditionFunction());

        // Task-sequence installation is application-wide, so it is offered only
        // when the deployment types all run as system without a logged-on user.
        var isUserInstall = manifest.InstallContext.Equals("User", StringComparison.OrdinalIgnoreCase);
        var allowTaskSequence = !isUserInstall;

        // Existing application: every title collision takes the operator's
        // policy, at any version. Under Skip, an unchanged application with
        // every expected deployment type present is idempotent; a missing one
        // is a partial prior run and refuses to continue.
        sb.AppendLine($"$expectedDts = @({string.Join(", ", specs.Select(s => $"'{Esc(s.DtName)}'"))})");
        sb.AppendLine("$existing = @()");
        sb.AppendLine($"try {{ $existing = @(Get-CMApplication -Name '{Esc(appName)}' -DisableWildcardHandling -ErrorAction SilentlyContinue) }} catch {{ }}");
        sb.AppendLine($"if ($existing.Count -gt 1) {{ throw \"Multiple existing MECM applications matched '{Esc(appName)}'; refusing to package until the duplicate names are resolved.\" }}");
        sb.AppendLine("$app = $null");
        sb.AppendLine("$replaceExisting = $false");
        sb.AppendLine("$replaceDtNames = @()");
        sb.AppendLine("if ($existing.Count -eq 1) {");
        sb.AppendLine("    $app = $existing[0]");
        sb.AppendLine($"    $currentDts = @(Get-CMDeploymentType -ApplicationName '{Esc(appName)}' -ErrorAction Stop | ForEach-Object {{ [string]$_.LocalizedDisplayName }})");
        sb.AppendLine("    $missingDts = @($expectedDts | Where-Object { $currentDts -notcontains $_ })");
        sb.AppendLine($"    Write-Host 'On-existing behavior         : {onExisting}'");
        switch (onExisting)
        {
            case OnExistingBehavior.Fail:
                sb.AppendLine($"    throw \"Existing MECM application '{Esc(appName)}' already exists at version $($app.SoftwareVersion) and OnExisting=Fail was requested.\"");
                break;

            case OnExistingBehavior.Overwrite:
                // The application object and its deployments survive; only the
                // deployment types and the version are replaced.
                sb.AppendLine("    Write-Host \"Application already exists    : replacing deployment types (v$($app.SoftwareVersion) -> v" + Esc(manifest.SoftwareVersion) + ", operator choice)\"");
                sb.AppendLine("    $replaceExisting = $true");
                break;

            default:
                sb.AppendLine("    if ($missingDts.Count -eq 0) {");
                if (!allowTaskSequence)
                {
                    // Repair an application left task-sequence eligible by an
                    // earlier run, even when its deployment types are retained.
                    AppendTaskSequenceRepair(sb, appName, "        ");
                }
                sb.AppendLine($"        Write-Host \"Application already exists    : {Esc(appName)} (v$($app.SoftwareVersion), unchanged)\"");
                sb.AppendLine("        Write-Host \"Deployment type(s) validated : $($expectedDts -join ', ')\"");
                // Machine-readable companion to the line above: the GUI parses
                // it to offer the operator an overwrite re-run.
                AppendConflictMarker(sb, appName, manifest.SoftwareVersion, "        ", "$app");
                if (!string.IsNullOrWhiteSpace(manifest.Icon))
                {
                    // An idempotent rerun still applies the icon, so a package created
                    // before icon support gains one without a version change.
                    var idemIconPath = Path.Combine(networkContentPath, manifest.Icon);
                    sb.AppendLine($"        $idemIcon = '{Esc(idemIconPath)}'");
                    sb.AppendLine("        if (Test-Path -LiteralPath \"FileSystem::$idemIcon\" -PathType Leaf) {");
                    sb.AppendLine($"            try {{ Set-CMApplication -Name '{Esc(appName)}' -IconLocationFile $idemIcon -ErrorAction Stop; Write-Host 'Application icon set         : {Esc(manifest.Icon)}' }} catch {{ Write-Host \"Application icon not applied : $($_.Exception.Message)\" }}");
                    sb.AppendLine("        }");
                }
                sb.AppendLine("        Write-Host \"CI_ID: $($app.CI_ID)\"");
                sb.AppendLine("        exit 0");
                sb.AppendLine("    }");
                sb.AppendLine($"    throw \"Existing MECM application '{Esc(appName)}' is missing deployment type(s): $($missingDts -join ', '). This looks like a partial prior package run; fix or remove the partial app before packaging again.\"");
                break;
        }
        // One replacement path: the same-version Overwrite branch and the
        // version-change branch share the staging-name create, remove-old,
        // rename sequence below.
        sb.AppendLine("    if ($replaceExisting) { $replaceDtNames = $currentDts }");
        sb.AppendLine("}");

        // Requirement rules build before any create so a provider refusal
        // never leaves a half-built application behind.
        var ruleVars = new List<List<string>>();
        for (var i = 0; i < specs.Count; i++)
            ruleVars.Add(RequirementRuleBuilder.AppendRules(sb, resolvedRules[i], $"dt{i}"));

        // Clear the incompatible flag before the replacement deployment types
        // are added. An operator's own setting on a compatible application is
        // left alone.
        if (!allowTaskSequence)
        {
            sb.AppendLine("if ($app) {");
            AppendTaskSequenceRepair(sb, appName, "    ");
            sb.AppendLine("}");
        }

        sb.AppendLine("if (-not $app) {");
        sb.AppendLine("    Write-Host 'Creating application...'");
        sb.Append($"    $app = New-CMApplication -Name '{Esc(appName)}'");
        sb.Append($" -Publisher '{Esc(manifest.Publisher)}'");
        sb.Append($" -SoftwareVersion '{Esc(manifest.SoftwareVersion)}'");
        sb.Append($" -Description '{Esc(comment)}'");
        sb.Append(allowTaskSequence ? " -AutoInstall $true" : " -AutoInstall $false");
        if (!string.IsNullOrEmpty(manifest.DisplayName))
            sb.Append($" -LocalizedApplicationName '{Esc(manifest.DisplayName)}'");
        sb.AppendLine();
        sb.AppendLine("}");
        sb.AppendLine("Write-Host \"CI_ID: $($app.CI_ID)\"");

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            sb.AppendLine($"$dtName{i} = '{Esc(spec.DtName)}'");
            sb.AppendLine($"$dtCreateName{i} = if ($replaceDtNames.Count -gt 0) {{ \"$dtName{i} (staging)\" }} else {{ $dtName{i} }}");

            // Script detection is a deployment-type parameter, not a clause
            // object, and the two parameter sets are mutually exclusive.
            var isScriptDetection = spec.Detection.Type.Equals("Script", StringComparison.OrdinalIgnoreCase);
            if (isScriptDetection)
            {
                if (string.IsNullOrWhiteSpace(spec.Detection.ScriptText))
                    throw new InvalidOperationException(
                        $"Deployment type '{spec.DtName}' declares Script detection with no ScriptText.");
                sb.AppendLine(BuildDetectionScriptPs(spec.Detection, i));
            }
            else
            {
                // Clause objects are built off the CM PSDrive; the drive context
                // interferes with their parameter binding.
                sb.AppendLine("Set-Location C:");
                sb.AppendLine(BuildDetectionClauseBlockPs(spec.Detection));
                sb.AppendLine($"Set-Location '{siteCode}:'");
            }

            sb.AppendLine($"Write-Host \"Adding deployment type       : $dtCreateName{i}\"");
            sb.Append($"Add-CMScriptDeploymentType -ApplicationName '{Esc(appName)}'");
            sb.Append($" -DeploymentTypeName $dtCreateName{i}");
            sb.Append($" -ContentLocation '{Esc(spec.ContentLocation)}'");
            sb.Append($" -InstallCommand '{Esc(spec.InstallCommand)}'");
            sb.Append($" -UninstallCommand '{Esc(spec.UninstallCommand)}'");
            if (isUserInstall)
            {
                sb.Append(" -InstallationBehaviorType InstallForUser");
                // LogonRequirementType cannot be set when InstallationBehaviorType is InstallForUser
            }
            else
            {
                sb.Append(" -InstallationBehaviorType InstallForSystem");
                sb.Append(" -LogonRequirementType WhetherOrNotUserLoggedOn");
            }
            sb.Append($" -EstimatedRuntimeMins {estRuntime} -MaximumRuntimeMins {maxRuntime}");
            sb.Append(" -ContentFallback -SlowNetworkDeploymentMode Download");
            if (isScriptDetection)
            {
                var lang = string.IsNullOrWhiteSpace(spec.Detection.ScriptLanguage)
                    ? "PowerShell" : spec.Detection.ScriptLanguage!;
                sb.Append($" -ScriptLanguage {ValidateScriptLanguage(lang)} -ScriptText $detectScript{i}");
            }
            else
            {
                sb.Append(" -AddDetectionClause $clauses");
                sb.Append(BuildDetectionClauseConnectorArgs(spec.Detection));
            }
            if (ruleVars[i].Count > 0)
                sb.Append($" -AddRequirement @({string.Join(", ", ruleVars[i])})");
            sb.AppendLine(" | Out-Null");
        }

        sb.AppendLine("foreach ($oldDt in $replaceDtNames) {");
        sb.AppendLine($"    Remove-CMDeploymentType -ApplicationName '{Esc(appName)}' -DeploymentTypeName $oldDt -Force -ErrorAction Stop");
        sb.AppendLine("    Write-Host \"Removed old deployment type  : $oldDt\"");
        sb.AppendLine("}");
        for (var i = 0; i < specs.Count; i++)
        {
            sb.AppendLine($"if ($dtCreateName{i} -ne $dtName{i}) {{");
            sb.AppendLine($"    Set-CMDeploymentType -ApplicationName '{Esc(appName)}' -DeploymentTypeName $dtCreateName{i} -NewDeploymentTypeName $dtName{i} -ErrorAction Stop");
            sb.AppendLine($"    Write-Host \"Renamed deployment type      : $dtName{i}\"");
            sb.AppendLine("}");
        }

        sb.AppendLine("if ($existing.Count -eq 1) {");
        sb.Append($"    Set-CMApplication -Name '{Esc(appName)}' -SoftwareVersion '{Esc(manifest.SoftwareVersion)}'");
        sb.Append($" -Publisher '{Esc(manifest.Publisher)}'");
        if (!string.IsNullOrWhiteSpace(comment))
            sb.Append($" -Description '{Esc(comment)}'");
        sb.AppendLine(" -ErrorAction Stop");
        sb.AppendLine($"    Write-Host 'Updated application version  : {Esc(manifest.SoftwareVersion)}'");
        sb.AppendLine("}");

        // Icon. Set-CMApplication reports success without the provider having
        // accepted the write, and a missing or rejected icon is not a packaging
        // error, so the whole step is advisory.
        if (!string.IsNullOrWhiteSpace(manifest.Icon))
        {
            var iconPath = Path.Combine(networkContentPath, manifest.Icon);
            sb.AppendLine($"$iconPath = '{Esc(iconPath)}'");
            // The script sits on the CMSite provider drive here; a bare UNC
            // Test-Path resolves through the CM provider and fails.
            sb.AppendLine("if (Test-Path -LiteralPath \"FileSystem::$iconPath\" -PathType Leaf) {");
            sb.AppendLine("  try {");
            sb.AppendLine($"    Set-CMApplication -Name '{Esc(appName)}' -IconLocationFile $iconPath -ErrorAction Stop");
            sb.AppendLine($"    Write-Host 'Application icon set         : {Esc(manifest.Icon)}'");
            sb.AppendLine("  } catch { Write-Host \"Application icon not applied : $($_.Exception.Message)\" }");
            sb.AppendLine("} else { Write-Host \"Application icon not found   : $iconPath\" }");
        }

        // Clean up revision history (keep only latest)
        sb.AppendLine("try {");
        sb.AppendLine("  $revisions = Get-CMApplicationRevisionHistory -Id $app.CI_ID -ErrorAction Stop");
        sb.AppendLine("  if (@($revisions).Count -gt 1) {");
        sb.AppendLine("    @($revisions) | Sort-Object -Property @{Expression={if ($_.Revision) {$_.Revision} else {$_.CIVersion}}} -Descending | Select-Object -Skip 1 | ForEach-Object {");
        sb.AppendLine("      Remove-CMApplicationRevisionHistory -InputObject $_ -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-Host 'Cleaned revision history'");
        sb.AppendLine("  }");
        sb.AppendLine("} catch { Write-Host \"Revision cleanup skipped: $($_.Exception.Message)\" }");

        sb.AppendLine("Write-Host 'Application created successfully'");

        return sb.ToString();
    }

    /// <summary>
    /// Emits the detection script into a single-quoted here-string, which
    /// passes the body through with no interpolation. A line that is exactly
    /// the here-string terminator would end it early, so it is refused.
    /// </summary>
    private static string BuildDetectionScriptPs(StageDetection det, int index)
    {
        var text = (det.ScriptText ?? "").Replace("\r\n", "\n").TrimEnd('\n');
        if (text.Split('\n').Any(l => l.TrimEnd() == "'@"))
            throw new InvalidOperationException(
                "Detection script contains a here-string terminator line and cannot be embedded.");

        return $"$detectScript{index} = @'\r\n{text.Replace("\n", "\r\n")}\r\n'@";
    }

    /// <summary>CM accepts only these three script detection languages.</summary>
    private static string ValidateScriptLanguage(string language) =>
        language.ToLowerInvariant() switch
        {
            "powershell" => "PowerShell",
            "vbscript" => "VBScript",
            "javascript" => "JavaScript",
            _ => throw new InvalidOperationException($"Unsupported detection script language: {language}")
        };

    /// <summary>
    /// Emits the $clauses array the deployment type binds to
    /// -AddDetectionClause. A single detection produces a one-element array so
    /// both shapes bind through the same parameter.
    /// </summary>
    public static string BuildDetectionClauseBlockPs(StageDetection det)
    {
        if (!det.Type.Equals("Compound", StringComparison.OrdinalIgnoreCase))
            return $"{BuildDetectionClausePs(det)}\r\n$clauses = @($clause)";

        var clauses = CompoundClauses(det);
        var sb = new StringBuilder();
        for (var i = 0; i < clauses.Length; i++)
        {
            sb.AppendLine(BuildDetectionClausePs(clauses[i]));
            sb.AppendLine($"$clause{i} = $clause");
        }
        sb.Append($"$clauses = @({string.Join(", ", Enumerable.Range(0, clauses.Length).Select(i => $"$clause{i}"))})");
        return sb.ToString();
    }

    /// <summary>
    /// Emits the connector arguments that shape the detection expression.
    /// An AND-joined compound needs none: AND is the cmdlet's default.
    /// GroupSizes passes only the second clause run to -GroupDetectionClauses;
    /// the cmdlet builds the expression left-associatively, so the first run is
    /// already parenthesized on its own and -GroupDetectionClauses holds one
    /// group. OR then attaches to the first clause of the second run.
    /// </summary>
    public static string BuildDetectionClauseConnectorArgs(StageDetection det)
    {
        if (!det.Type.Equals("Compound", StringComparison.OrdinalIgnoreCase)) return "";

        var clauses = CompoundClauses(det);
        var groupSizes = det.GroupSizes;

        if (groupSizes is { Length: > 0 })
        {
            if (groupSizes.Length != 2 || groupSizes.Any(g => g <= 0) || groupSizes.Sum() != clauses.Length)
                throw new InvalidOperationException(
                    $"Detection.GroupSizes must be exactly two non-zero sizes summing to the clause count (got '{string.Join(",", groupSizes)}' for {clauses.Length} clauses).");

            var secondStart = groupSizes[0];
            var group = string.Join(", ", Enumerable.Range(secondStart, clauses.Length - secondStart)
                .Select(i => $"$clause{i}.Setting.LogicalName"));
            return $" -GroupDetectionClauses @({group})"
                 + $" -DetectionClauseConnector @(@{{LogicalName=$clause{secondStart}.Setting.LogicalName;Connector='OR'}})";
        }

        if (!"Or".Equals(det.Connector, StringComparison.OrdinalIgnoreCase)) return "";

        var connectors = string.Join(", ", Enumerable.Range(1, clauses.Length - 1)
            .Select(i => $"@{{LogicalName=$clause{i}.Setting.LogicalName;Connector='OR'}}"));
        return $" -DetectionClauseConnector @({connectors})";
    }

    private static StageDetection[] CompoundClauses(StageDetection det)
    {
        var clauses = det.Clauses ?? [];
        if (clauses.Length < 2)
            throw new InvalidOperationException(
                "A Compound detection requires at least two Clauses.");
        return clauses;
    }

    /// <summary>The -Hive value of the New-CMDetectionClauseRegistry* cmdlets for a detection.</summary>
    private static string CmHive(StageDetection det) => det.IsCurrentUserHive ? "CurrentUser" : "LocalMachine";

    private static string BuildDetectionClausePs(StageDetection det)
    {
        return det.Type switch
        {
            "File" when det.PropertyType == "Existence" =>
                $"$clause = New-CMDetectionClauseFile -Path '{Esc(det.FilePath ?? "")}' -FileName '{Esc(det.FileName ?? "")}' -Existence{(det.Is64Bit ? " -Is64Bit" : "")}",

            "File" =>
                $"$clause = New-CMDetectionClauseFile -Path '{Esc(det.FilePath ?? "")}' -FileName '{Esc(det.FileName ?? "")}' -PropertyType {det.PropertyType ?? "Version"} -Value -ExpressionOperator {det.Operator ?? "GreaterEquals"} -ExpectedValue '{Esc(det.ExpectedValue ?? "")}'{(det.Is64Bit ? " -Is64Bit" : "")}",

            "RegistryKeyValue" =>
                $"$clause = New-CMDetectionClauseRegistryKeyValue -Hive {CmHive(det)} -KeyName '{Esc(det.RegistryKeyRelative ?? "")}' -ValueName '{Esc(det.ValueName ?? "DisplayVersion")}' -PropertyType {det.PropertyType ?? "String"} -Value -ExpressionOperator {det.Operator ?? "IsEquals"} -ExpectedValue '{Esc(det.ExpectedValue ?? "")}'{(det.Is64Bit ? " -Is64Bit" : "")}",

            "RegistryKey" =>
                $"$clause = New-CMDetectionClauseRegistryKey -Hive {CmHive(det)} -KeyName '{Esc(det.RegistryKeyRelative ?? "")}' -Existence{(det.Is64Bit ? " -Is64Bit" : "")}",

            _ => throw new InvalidOperationException($"Unsupported detection type: {det.Type}")
        };
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunWindowsPowerShellAsync(
        string script, Action<string>? onProgress, CancellationToken ct)
    {
        // Write script to temp file to avoid argument escaping issues
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ap-mecm-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, ct);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var outLines = new List<string>();
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                outLines.Add(line);
                onProgress?.Invoke(line);
            }

            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode, string.Join(Environment.NewLine, outLines), stderr);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// Builds the PowerShell script for creating an MSIX deployment type in MECM.
    /// CM handles detection, provisioning, and per-user registration natively for MSIX.
    /// No custom detection clause, install/uninstall commands, or wrappers needed.
    /// </summary>
    private static string BuildMsixMecmScript(
        StageManifest manifest, string appName, string networkContentPath,
        string siteCode, string comment, string? providerMachine = null)
    {
        var sb = new StringBuilder();

        // Import CM module
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$cmModule = Join-Path $env:SMS_ADMIN_UI_PATH '..\\ConfigurationManager.psd1'");
        sb.AppendLine("if (Test-Path $cmModule) { Import-Module $cmModule }");
        sb.AppendLine("else { Import-Module ConfigurationManager }");

        // Create PSDrive if needed
        sb.AppendLine($"if (-not (Get-PSDrive -Name '{siteCode}' -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"    New-PSDrive -Name '{siteCode}' -PSProvider CMSite -Root {CmSiteRoot(providerMachine)} | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine($"Set-Location '{siteCode}:'");
        sb.AppendLine("Write-Host 'Connected to MECM'");

        // Check for existing app
        sb.AppendLine("$existing = $null");
        sb.AppendLine($"try {{ $existing = Get-CMApplication -Name '{Esc(appName)}' -Fast -ErrorAction SilentlyContinue }} catch {{ }}");
        sb.AppendLine("if ($existing) { Write-Host 'Application already exists'; exit 0 }");

        // Create application
        sb.AppendLine("Write-Host 'Creating application...'");
        sb.Append($"$app = New-CMApplication -Name '{Esc(appName)}'");
        sb.Append($" -Publisher '{Esc(manifest.Publisher)}'");
        sb.Append($" -SoftwareVersion '{Esc(manifest.SoftwareVersion)}'");
        sb.Append($" -Description '{Esc(comment)}'");
        sb.Append(" -AutoInstall $true");
        sb.AppendLine();

        sb.AppendLine("Write-Host \"CI_ID: $($app.CI_ID)\"");

        // Find the MSIX file in the content path
        sb.AppendLine($"$msixFile = Get-ChildItem -Path '{Esc(networkContentPath)}' -Filter '*.msix' -ErrorAction SilentlyContinue | Select-Object -First 1");
        sb.AppendLine("if (-not $msixFile) { $msixFile = Get-ChildItem -Path '" + Esc(networkContentPath) + "' -Filter '*.appx' -ErrorAction SilentlyContinue | Select-Object -First 1 }");
        sb.AppendLine("if (-not $msixFile) { throw 'No .msix or .appx file found in content path' }");

        // Add MSIX deployment type — CM handles detection automatically
        sb.AppendLine("Write-Host 'Adding MSIX deployment type...'");
        sb.Append($"Add-CMWindowsAppxDeploymentType -ApplicationName '{Esc(appName)}'");
        sb.Append($" -DeploymentTypeName '{Esc(appName)}'");
        sb.Append(" -ContentLocation $msixFile.FullName");
        sb.Append(" -ContentFallback -SlowNetworkDeploymentMode Download");
        sb.Append(" -Force");
        sb.AppendLine(" | Out-Null");

        // Clean up revision history
        sb.AppendLine("try {");
        sb.AppendLine($"  $revisions = Get-CMApplicationRevisionHistory -Id $app.CI_ID -ErrorAction Stop");
        sb.AppendLine("  if (@($revisions).Count -gt 1) {");
        sb.AppendLine("    @($revisions) | Sort-Object -Property @{Expression={if ($_.Revision) {$_.Revision} else {$_.CIVersion}}} -Descending | Select-Object -Skip 1 | ForEach-Object {");
        sb.AppendLine("      Remove-CMApplicationRevisionHistory -InputObject $_ -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-Host 'Cleaned revision history'");
        sb.AppendLine("  }");
        sb.AppendLine("} catch { Write-Host \"Revision cleanup skipped: $($_.Exception.Message)\" }");

        sb.AppendLine("Write-Host 'Application created successfully'");

        return sb.ToString();
    }

    /// <summary>
    /// Distributes content for a packaged application to a DP group. Call after
    /// PackageAsync succeeds. A group that already holds the content makes the
    /// cmdlet throw "already been targeted"; that is the wanted end state and
    /// counts as success.
    /// </summary>
    public async Task<bool> DistributeAsync(
        string appName, string siteCode, string dpGroupName = "All DPs",
        Action<string>? onProgress = null, string? providerMachine = null,
        CancellationToken ct = default)
    {
        var result = await RunWindowsPowerShellAsync(
            BuildDistributionScript(appName, siteCode, dpGroupName, providerMachine), onProgress, ct);
        if (result.ExitCode != 0)
            onProgress?.Invoke($"Distribution failed: {result.StdErr}");
        return result.ExitCode == 0;
    }

    public static string BuildDistributionScript(string appName, string siteCode, string dpGroupName, string? providerMachine)
    {
        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine("try {");
        sb.AppendLine($"  Start-CMContentDistribution -ApplicationName '{Esc(appName)}' -DistributionPointGroupName '{Esc(dpGroupName)}' -ErrorAction Stop");
        sb.AppendLine($"  Write-Host 'Content distribution initiated: {Esc(appName)} to {Esc(dpGroupName)}'");
        sb.AppendLine("} catch {");
        sb.AppendLine("  if ($_.Exception.Message -match 'already been targeted|already distributed') {");
        sb.AppendLine($"    Write-Host 'Content already targeted: {Esc(appName)} on {Esc(dpGroupName)}'");
        sb.AppendLine("  } else { throw }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Deploys a packaged application to a test collection as Available,
    /// effective immediately, creating the collection (direct membership,
    /// limited to All Systems) when asked. A missing collection without that
    /// permission skips the deployment and reports it; an existing deployment
    /// counts as success.
    /// </summary>
    public async Task<bool> DeployToTestCollectionAsync(
        string appName, string siteCode, string collectionName, bool createIfMissing,
        Action<string>? onProgress = null, string? providerMachine = null,
        CancellationToken ct = default)
    {
        var result = await RunWindowsPowerShellAsync(
            BuildTestCollectionDeploymentScript(appName, siteCode, collectionName, createIfMissing, providerMachine), onProgress, ct);
        if (result.ExitCode != 0)
            onProgress?.Invoke($"Test deployment failed: {result.StdErr}");
        return result.ExitCode == 0;
    }

    public static string BuildTestCollectionDeploymentScript(
        string appName, string siteCode, string collectionName, bool createIfMissing, string? providerMachine)
    {
        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine($"$collection = Get-CMDeviceCollection -Name '{Esc(collectionName)}' -ErrorAction SilentlyContinue");
        if (createIfMissing)
        {
            sb.AppendLine("if (-not $collection) {");
            sb.AppendLine($"  Write-Host 'Creating test collection: {Esc(collectionName)} (direct membership, limited to All Systems)'");
            sb.AppendLine($"  New-CMDeviceCollection -Name '{Esc(collectionName)}' -LimitingCollectionName 'All Systems' -ErrorAction Stop | Out-Null");
            sb.AppendLine($"  $collection = Get-CMDeviceCollection -Name '{Esc(collectionName)}' -ErrorAction SilentlyContinue");
            sb.AppendLine("}");
        }
        sb.AppendLine("if (-not $collection) {");
        sb.AppendLine($"  Write-Host 'Test deployment skipped: collection {Esc(collectionName)} not found'");
        sb.AppendLine("  exit 0");
        sb.AppendLine("}");
        sb.AppendLine("try {");
        sb.AppendLine($"  New-CMApplicationDeployment -Name '{Esc(appName)}' -CollectionName '{Esc(collectionName)}' -DeployAction Install -DeployPurpose Available -AvailableDateTime (Get-Date) -ErrorAction Stop | Out-Null");
        sb.AppendLine($"  Write-Host 'Test deployment created: {Esc(appName)} to {Esc(collectionName)} (Available, immediate)'");
        sb.AppendLine("} catch {");
        sb.AppendLine("  if ($_.Exception.Message -match 'already (exists|deployed|been deployed)') {");
        sb.AppendLine($"    Write-Host 'Test deployment already exists: {Esc(appName)} to {Esc(collectionName)}'");
        sb.AppendLine("  } else { throw }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Creates a deployment for a packaged application to a collection.
    /// Call after DistributeAsync succeeds.
    /// </summary>
    public async Task<bool> DeployAsync(
        string appName, string siteCode, string collectionName,
        string purpose = "Required", bool overrideServiceWindow = true,
        Action<string>? onProgress = null, string? providerMachine = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode, providerMachine);

        sb.Append($"New-CMApplicationDeployment -Name '{Esc(appName)}'");
        sb.Append($" -CollectionName '{Esc(collectionName)}'");
        sb.Append(" -DeployAction Install");
        sb.Append($" -DeployPurpose {purpose}");
        sb.Append(" -UserNotification DisplaySoftwareCenterOnly");
        if (purpose.Equals("Required", StringComparison.OrdinalIgnoreCase) && overrideServiceWindow)
        {
            sb.Append(" -OverrideServiceWindow $true");
            sb.Append(" -RebootOutsideServiceWindow $true");
        }
        sb.AppendLine(" -ErrorAction Stop | Out-Null");
        sb.AppendLine($"Write-Host 'Deployed: {Esc(appName)} to {Esc(collectionName)} ({purpose})'");

        var result = await RunWindowsPowerShellAsync(sb.ToString(), onProgress, ct);
        if (result.ExitCode != 0)
            onProgress?.Invoke($"Deployment failed: {result.StdErr}");
        return result.ExitCode == 0;
    }

    private static void AppendCmPreamble(StringBuilder sb, string siteCode, string? providerMachine = null)
    {
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$cmModule = Join-Path $env:SMS_ADMIN_UI_PATH '..\\ConfigurationManager.psd1'");
        sb.AppendLine("if (Test-Path $cmModule) { Import-Module $cmModule }");
        sb.AppendLine("else { Import-Module ConfigurationManager }");
        sb.AppendLine($"if (-not (Get-PSDrive -Name '{siteCode}' -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"    New-PSDrive -Name '{siteCode}' -PSProvider CMSite -Root {CmSiteRoot(providerMachine)} | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine($"Set-Location '{siteCode}:'");
    }

    /// <summary>
    /// The CMSite drive root. $env:COMPUTERNAME only reaches the site when
    /// the console host is itself the SMS provider, so a configured
    /// provider host replaces it.
    /// </summary>
    private static string CmSiteRoot(string? providerMachine) =>
        string.IsNullOrWhiteSpace(providerMachine)
            ? "$env:COMPUTERNAME"
            : $"'{Esc(providerMachine.Trim())}'";

    /// <summary>
    /// Emits the repair that clears task-sequence eligibility on an existing
    /// application. AutoInstall is application-wide, so one deployment type
    /// needing a user context makes the flag invalid for the whole object.
    /// </summary>
    private static void AppendTaskSequenceRepair(StringBuilder sb, string appName, string indent)
    {
        sb.AppendLine($"{indent}if ($app.AutoInstall -ne $false) {{");
        sb.AppendLine($"{indent}    Set-CMApplication -Name '{Esc(appName)}' -AutoInstall $false -ErrorAction Stop | Out-Null");
        sb.AppendLine($"{indent}    Write-Host 'Task-sequence install        : disabled (deployment type requires a user context)'");
        sb.AppendLine($"{indent}}}");
    }

    private static string Esc(string value) => value.Replace("'", "''");

    /// <summary>
    /// Sanitizes a string for use as a filesystem folder name.
    /// Strips trailing dots/spaces (Windows silently removes these), replaces commas
    /// and other problematic characters. Only allows alphanumeric, space, dot, hyphen, underscore.
    /// </summary>
    /// <summary>Public accessor for BareStaging and other services that need sanitized folder names.</summary>
    internal static string SanitizeFolderNamePublic(string name) => SanitizeFolderName(name);

    private static string SanitizeFolderName(string name)
    {
        // Replace commas and other special chars with nothing
        var sanitized = System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9 .\-_]", "");
        // Collapse multiple spaces
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\s{2,}", " ");
        // Trim trailing dots and spaces (Windows strips these silently, causing path mismatches)
        sanitized = sanitized.TrimEnd('.', ' ');
        return sanitized;
    }
}

public sealed class PackagingResult
{
    public bool Success { get; init; }
    public string ApplicationName { get; init; } = "";
    public string CiId { get; init; } = "";
    public string NetworkContentPath { get; init; } = "";
    public string? ErrorMessage { get; init; }
    public bool AlreadyExists { get; init; }
}
