using System.Diagnostics;
using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Creates MECM applications from staged content.
/// Shells out to Windows PowerShell 5.1 (powershell.exe) because the CM module
/// requires .NET Framework — it cannot run in a .NET 10 hosted runspace.
/// </summary>
public sealed partial class MecmTarget
{
    /// <summary>
    /// Get-CMApplication prints "Search query parameter 'LocalizedDisplayName' supports wildcards and -DisableWildcardHandling
    /// is defined" on every call that passes -DisableWildcardHandling, so each lookup of a publish wrote the same line to the log.
    /// The generated scripts stay byte for byte as recorded; the line is dropped where the log receives it.
    /// </summary>
    internal static bool IsWildcardNotice(string? line) =>
        line is not null
        && line.Contains("Search query parameter", StringComparison.Ordinal)
        && line.Contains("-DisableWildcardHandling is defined", StringComparison.Ordinal);

    internal static Action<string>? WithoutCmdletNotice(Action<string>? onProgress) =>
        onProgress is null ? null : line => { if (!IsWildcardNotice(line)) onProgress(line); };

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
        string? applicationTitle = null,
        MecmScriptSigningContext? scriptSigning = null,
        Workbench.ProfileContentScope? contentScope = null,
        DeploymentTypeContentOptions? contentOptions = null)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            onProgress = WithoutCmdletNotice(onProgress);
            if (NativeMsixRefusal(manifest) is { } nativeMsix)
                return new PackagingResult { Success = false, ErrorMessage = nativeMsix };
            // The title decides the application and its deployment type names;
            // the content folder stays on the manifest name so a title change
            // never moves content already on the share.
            var appName = string.IsNullOrWhiteSpace(applicationTitle) ? manifest.AppName : applicationTitle;
            var version = manifest.SoftwareVersion;
            var publisher = manifest.Publisher;
            var vendorFolder = SanitizeFolderName(publisher);
            var appFolder = manifest.DisplayName ?? manifest.AppName;
            // Two profiles of one application at one version never share a network folder.
            var versionFolder = (contentScope ?? Workbench.ProfileContentScope.Default).ScopedVersionFolder(version);
            var networkContentPath = ContentLayouts.ContentPath(fileServerPath, contentLayout ?? ContentLayouts.Nested, vendorFolder, appFolder, versionFolder);

            var shareRoot = Path.GetPathRoot(fileServerPath) ?? fileServerPath;
            if (fileServerPath.StartsWith(@"\\") && !Directory.Exists(fileServerPath))
            {
                return new PackagingResult
                {
                    Success = false,
                    ErrorMessage = $"Content share not accessible: {fileServerPath}"
                };
            }

            // Title collision, decided before anything is written. A Skip
            // or Fail run that finds the title must leave the share content of
            // the existing application unchanged.
            if (ProbesBeforeContentCopy(onExisting))
            {
                var conflicted = false;
                var siteVersion = "";
                void WatchProbe(string line)
                {
                    if (OnExistingConflict.TryParse(line, out _, out var found, out _)) { conflicted = true; siteVersion = found; }
                    onProgress?.Invoke(line);
                }

                var probe = await WindowsPowerShellHost.RunAsync(
                    BuildExistingProbeScript(appName, version, siteCode, providerMachine), WatchProbe, ct);
                if (probe.ExitCode != 0)
                    return new PackagingResult { Success = false, ErrorMessage = probe.StdErr };
                var decision = DecideAfterProbe(onExisting, conflicted, appName, siteVersion, version);
                if (decision is not null)
                {
                    if (decision.LogLine is not null) onProgress?.Invoke(decision.LogLine);
                    return decision.Result;
                }
            }

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

            if (scriptSigning is not null)
                VerifyRequiredDeploymentSigning(scriptSigning.Policy, scriptSigning.Record, stagedContentPath);

            // A file on the share that the manifest does not list fails the verification
            // below; it is refused here, before the first write to the share.
            var unexpected = ContentSyncService.UnexpectedFiles(networkContentPath, manifest);
            if (unexpected.Count > 0)
                return new PackagingResult
                {
                    Success = false,
                    ErrorMessage = $"Network content folder holds files the stage manifest does not list; nothing was written to the share: {string.Join("; ", unexpected.Select(f => "unexpected file: " + f))}"
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

            onProgress?.Invoke("Connecting to ConfigMgr...");
            // An MSIX is a script deployment type like every other product: the stage wraps it
            // and gives it a script or provisioning-key detection.
            var psScript = BuildPackageScript(manifest, appName, networkContentPath,
                siteCode, comment, estimatedRuntimeMins, maximumRuntimeMins,
                conditionTemplates, commandOverrides, onProgress, requirements, providerMachine,
                onExisting, scriptSigning, contentOptions);

            var result = await WindowsPowerShellHost.RunAsync(psScript, onProgress, ct);

            if (result.ExitCode == 0)
            {
                onProgress?.Invoke($"Packaged: {appName}");
                return new PackagingResult
                {
                    Success = true,
                    ApplicationName = appName,
                    NetworkContentPath = networkContentPath,
                    CiId = ReadCiId(result.StdOut),
                    ModelName = ReadModelName(result.StdOut)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PackagingResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// True for the behaviors that leave an existing application alone; their
    /// existing-application probe runs before any content reaches the share.
    /// </summary>
    public static bool ProbesBeforeContentCopy(OnExistingBehavior onExisting) =>
        onExisting is OnExistingBehavior.Skip or OnExistingBehavior.Fail;

    /// <summary>
    /// What a Skip run says when it finds the title on the site. A title without a version matches every version, so a new
    /// version is skipped as well; the line says so instead of leaving the run to look like a publish.
    /// </summary>
    public static string ExistingApplicationSkipMessage(string appName, string siteVersion, string incomingVersion)
    {
        var site = siteVersion.Trim();
        var incoming = incomingVersion.Trim();
        if (site.Length > 0 && string.Equals(site, incoming, StringComparison.Ordinal))
            return $"ConfigMgr already holds '{appName}' at version {site}. Nothing was changed (OnExisting=Skip).";
        var held = site.Length > 0 ? $"ConfigMgr holds '{appName}' at version {site}" : $"ConfigMgr holds '{appName}'";
        var staged = incoming.Length > 0 ? $"version {incoming}" : "the staged version";
        return $"{held}; {staged} was NOT published by this Skip run. Choose Overwrite to replace the deployment types.";
    }

    /// <summary>What a run does after the existing-application probe: its result and an optional log line.</summary>
    public sealed record ProbeDecision(PackagingResult Result, string? LogLine);

    /// <summary>
    /// The decision after the probe. A title found under Fail ends the run with the refusal. A title found under Skip ends it
    /// successfully, leaves the application alone and logs which version was not published. No title found returns null and
    /// the run goes on.
    /// </summary>
    public static ProbeDecision? DecideAfterProbe(OnExistingBehavior onExisting, bool conflicted, string appName, string siteVersion, string incomingVersion)
    {
        // Overwrite never probes (ProbesBeforeContentCopy), so it has no decision here.
        if (!conflicted || onExisting == OnExistingBehavior.Overwrite) return null;
        if (onExisting == OnExistingBehavior.Fail)
            return new ProbeDecision(new PackagingResult { Success = false, ErrorMessage = ExistingApplicationFailMessage(appName, siteVersion) }, null);
        return new ProbeDecision(
            new PackagingResult { Success = true, AlreadyExists = true, ApplicationName = appName },
            ExistingApplicationSkipMessage(appName, siteVersion, incomingVersion));
    }

    /// <summary>The refusal of a Fail run that found the title on the site.</summary>
    public static string ExistingApplicationFailMessage(string appName, string siteVersion) =>
        $"Existing ConfigMgr application '{appName}' already exists at version {siteVersion} and OnExisting=Fail was requested.";

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
        sb.AppendLine($"if ($existing.Count -gt 1) {{ throw \"Multiple applications have the exact title '{EscDq(appName)}'; resolve duplicates before packaging.\" }}");
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
        OnExistingBehavior onExisting = OnExistingBehavior.Skip,
        MecmScriptSigningContext? scriptSigning = null,
        DeploymentTypeContentOptions? contentOptions = null)
    {
        var content = contentOptions ?? DeploymentTypeContentOptions.Default;
        onLog?.Invoke(content.LogLine);
        var specs = DeploymentTypeSpecResolver.Resolve(manifest, networkContentPath, appName);
        var isMultiDt = DeploymentTypeSpecResolver.IsMultiDeploymentType(specs, appName);
        if (isMultiDt)
            onLog?.Invoke($"Deployment types (manifest)  : {string.Join(", ", specs.Select(s => s.DtName))}");

        DeploymentTypeSpecResolver.ApplyCommandOverrides(
            specs, commandOverrides ?? manifest.CommandOverrides, appName, onLog);
        DeploymentTypeSpecResolver.ApplyRequirements(specs, requirements, appName, onLog);

        var signingPolicy = SigningPolicy.Normalize(scriptSigning?.Policy);
        var signingRecord = scriptSigning?.Record;
        CheckResolvedDeploymentCommands(specs, signingPolicy, signingRecord);
        VerifyRequiredDeploymentSigning(signingPolicy, signingRecord, null);

        var templates = conditionTemplates ?? ConditionTemplateService.Defaults();
        var resolvedRules = specs
            .Select(s => RequirementRuleBuilder.Resolve(s.Requirements, templates))
            .ToList();

        // One identity per template per run: a timestamped signature differs on
        // every signing, and two deployment types must reference one condition.
        var identities = new Dictionary<string, ConditionScriptIdentity>(StringComparer.OrdinalIgnoreCase);
        ConditionScriptIdentity ScriptIdentity(ConditionTemplate template)
        {
            if (!identities.TryGetValue(template.Id, out var identity))
            {
                identity = ConditionScriptIdentity.Resolve(template, signingPolicy, scriptSigning?.Signer);
                identities[template.Id] = identity;
            }
            return identity;
        }

        var detectionStatus = signingRecord?.Detection.Status ?? "";
        var detectionSigned = SigningStatus.IsSigned(detectionStatus);
        var detectionThumbprint = detectionSigned ? signingRecord!.Detection.Thumbprint : "";
        var finalizedDetectionFile = signingRecord?.Detection.File;
        var hasScriptDetection = specs.Any(s => s.Detection.Type.Equals("Script", StringComparison.OrdinalIgnoreCase));
        var hasScriptConditions = RequirementRuleBuilder.UsesScriptConditions(resolvedRules);

        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine("Write-Host 'Connected to MECM'");
        if (resolvedRules.Any(r => r.Count > 0))
            sb.AppendLine(RequirementRuleBuilder.BuildGlobalConditionFunction());
        if (hasScriptDetection || hasScriptConditions)
            sb.AppendLine(MecmScriptSigningPs.ReadBackHelpers());
        if (hasScriptDetection)
            sb.AppendLine(MecmScriptSigningPs.DetectionFunctions());
        if (hasScriptConditions)
            sb.AppendLine(MecmScriptSigningPs.ScriptGlobalConditionFunctions());

        // Task-sequence installation is application-wide, so it is offered only
        // when the deployment types all run as system without a logged-on user.
        var isUserInstall = manifest.InstallationBehaviorType is { } manifestBehavior
            ? string.Equals(manifestBehavior, InstallForUser, StringComparison.OrdinalIgnoreCase)
            : manifest.InstallContext.Equals("User", StringComparison.OrdinalIgnoreCase);
        var allowTaskSequence = !isUserInstall;

        // Existing application: every title collision takes the operator's
        // policy, at any version. Under Skip, an unchanged application with
        // every expected deployment type present is idempotent; a missing one
        // is a partial prior run and refuses to continue.
        sb.AppendLine($"$expectedDts = @({string.Join(", ", specs.Select(s => $"'{Esc(s.DtName)}'"))})");
        sb.AppendLine("$existing = @()");
        sb.AppendLine($"try {{ $existing = @(Get-CMApplication -Name '{Esc(appName)}' -DisableWildcardHandling -ErrorAction SilentlyContinue) }} catch {{ }}");
        sb.AppendLine($"if ($existing.Count -gt 1) {{ throw \"Multiple existing MECM applications matched '{EscDq(appName)}'; refusing to package until the duplicate names are resolved.\" }}");
        sb.AppendLine("$app = $null");
        sb.AppendLine("$replaceExisting = $false");
        sb.AppendLine("$createdApp = $false");
        sb.AppendLine("$createdStaging = @()");
        sb.AppendLine("$oldDtRemoved = $false");
        sb.AppendLine("$replaceDtNames = @()");
        sb.AppendLine("if ($existing.Count -eq 1) {");
        sb.AppendLine("    $app = $existing[0]");
        sb.AppendLine($"    $currentDts = @(Get-CMDeploymentType -ApplicationName '{Esc(appName)}' -ErrorAction Stop | ForEach-Object {{ [string]$_.LocalizedDisplayName }})");
        sb.AppendLine("    $missingDts = @($expectedDts | Where-Object { $currentDts -notcontains $_ })");
        sb.AppendLine($"    Write-Host 'On-existing behavior         : {onExisting}'");
        switch (onExisting)
        {
            case OnExistingBehavior.Fail:
                sb.AppendLine($"    throw \"Existing MECM application '{EscDq(appName)}' already exists at version $($app.SoftwareVersion) and OnExisting=Fail was requested.\"");
                break;

            case OnExistingBehavior.Overwrite:
                // The application object and its deployments survive; only the
                // deployment types and the version are replaced.
                sb.AppendLine("    Write-Host \"Application already exists    : replacing deployment types (v$($app.SoftwareVersion) -> v" + EscDq(manifest.SoftwareVersion) + ", operator choice)\"");
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
                sb.AppendLine($"        Write-Host \"Application already exists    : {EscDq(appName)} (v$($app.SoftwareVersion), unchanged)\"");
                sb.AppendLine("        Write-Host \"Deployment type(s) validated : $($expectedDts -join ', ')\"");
                // Machine-readable companion to the line above: the GUI parses
                // it to offer the operator an overwrite re-run.
                AppendConflictMarker(sb, appName, manifest.SoftwareVersion, "        ", "$app");
                if (!string.IsNullOrWhiteSpace(manifest.Icon))
                {
                    // An idempotent rerun still applies the icon, so an application
                    // without one gains it without a version change.
                    var idemIconPath = Path.Combine(networkContentPath, manifest.Icon);
                    sb.AppendLine($"        $idemIcon = '{Esc(idemIconPath)}'");
                    sb.AppendLine("        if (Test-Path -LiteralPath \"FileSystem::$idemIcon\" -PathType Leaf) {");
                    sb.AppendLine($"            try {{ Set-CMApplication -Name '{Esc(appName)}' -IconLocationFile $idemIcon -ErrorAction Stop; Write-Host 'Application icon set         : {Esc(manifest.Icon)}' }} catch {{ Write-Host \"Application icon not applied : $($_.Exception.Message)\" }}");
                    sb.AppendLine("        }");
                }
                sb.AppendLine("        Write-Host \"CI_ID: $($app.CI_ID)\"");
                sb.AppendLine("        exit 0");
                sb.AppendLine("    }");
                sb.AppendLine($"    throw \"Existing MECM application '{EscDq(appName)}' is missing deployment type(s): $($missingDts -join ', '). This looks like a partial prior package run; fix or remove the partial app before packaging again.\"");
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
            ruleVars.Add(RequirementRuleBuilder.AppendRules(sb, resolvedRules[i], $"dt{i}", ScriptIdentity));

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
        sb.AppendLine("    $createdApp = $true");
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

        // From here the site can refuse a step (a value it does not accept, a content path it cannot read). The steps run in a
        // try block so that a failure does not leave a half-built application: one that this run created is removed, and the
        // staging deployment types of this run are removed from one that was already there.
        sb.AppendLine(IncompleteWorkCleanupFunction.ReplaceLineEndings());
        sb.AppendLine("try {");
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            sb.AppendLine($"$dtName{i} = '{Esc(spec.DtName)}'");
            sb.AppendLine($"$dtCreateName{i} = if ($replaceDtNames.Count -gt 0) {{ \"$dtName{i} (staging)\" }} else {{ $dtName{i} }}");

            // Script detection is a deployment-type parameter, not a clause
            // object, and the two parameter sets are mutually exclusive.
            var isScriptDetection = spec.Detection.Type.Equals("Script", StringComparison.OrdinalIgnoreCase);
            var fileTransport = false;
            if (isScriptDetection)
            {
                // Only the manifest's own detection has a finalized file; a
                // deployment type that carries its own detection travels as text.
                fileTransport = !string.IsNullOrWhiteSpace(finalizedDetectionFile)
                    && ReferenceEquals(spec.Detection, manifest.Detection);
                if (!fileTransport && detectionSigned)
                    throw new InvalidOperationException(
                        "The detection script is signed but the manifest names no Detection.ScriptFile; a signed script cannot be imported as script text. Re-run the Stage phase so the finalized script is written to the content folder.");
                if (signingPolicy.RequireDetection && !detectionSigned)
                    throw new InvalidOperationException(SigningStatus.RefusalMessage(SigningCategory.Detection, detectionStatus));

                if (fileTransport)
                {
                    // The finalized file sits at the stage root, which is the
                    // network content root, whatever the deployment type's subpath.
                    var absolute = Path.IsPathRooted(finalizedDetectionFile!)
                        ? finalizedDetectionFile!
                        : Path.Combine(networkContentPath, finalizedDetectionFile!);
                    sb.Append($"$detectTransport{i} = {MecmScriptSigningPs.DetectionFileFunction}");
                    sb.Append($" -Path '{Esc(absolute)}'");
                    sb.Append($" -ScriptFile '{Esc(finalizedDetectionFile!)}'");
                    sb.Append($" -ContentLocation '{Esc(networkContentPath)}'");
                    sb.Append($" -Signed ${(detectionSigned ? "true" : "false")}");
                    sb.Append($" -VerifySignature ${(detectionSigned || signingPolicy.RequireDetection ? "true" : "false")}");
                    sb.AppendLine($" -ExpectedThumbprint '{Esc(detectionThumbprint)}'");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(spec.Detection.ScriptText))
                        throw new InvalidOperationException(
                            $"Deployment type '{spec.DtName}' declares Script detection with no ScriptText.");
                    sb.AppendLine(BuildDetectionScriptPs(spec.Detection, i));
                    sb.AppendLine($"$detectTransport{i} = [pscustomobject]@{{ Transport = 'Text'; Path = ''; TempPath = ''; Text = $detectScript{i}; Bytes = [System.Text.Encoding]::UTF8.GetBytes($detectScript{i}); Signed = $false }}");
                }
            }
            else
            {
                // Clause objects are built off the CM PSDrive; the drive context
                // interferes with their parameter binding.
                sb.AppendLine("Set-Location C:");
                sb.AppendLine(BuildDetectionClauseBlockPs(spec.Detection));
                sb.AppendLine($"Set-Location '{Esc(siteCode)}:'");
            }

            sb.AppendLine($"Write-Host \"Adding deployment type       : $dtCreateName{i}\"");
            sb.Append($"Add-CMScriptDeploymentType -ApplicationName '{Esc(appName)}'");
            sb.Append($" -DeploymentTypeName $dtCreateName{i}");
            sb.Append($" -ContentLocation '{Esc(spec.ContentLocation)}'");
            sb.Append($" -InstallCommand '{Esc(spec.InstallCommand)}'");
            sb.Append($" -UninstallCommand '{Esc(spec.UninstallCommand)}'");
            var behavior = ValidateBehavior(spec.InstallationBehaviorType ?? manifest.InstallationBehaviorType
                ?? (isUserInstall ? InstallForUser : "InstallForSystem"));
            sb.Append($" -InstallationBehaviorType {behavior}");
            // LogonRequirementType cannot be set when InstallationBehaviorType is InstallForUser
            if (behavior != InstallForUser)
                sb.Append($" -LogonRequirementType {ValidateLogonRequirement(spec.LogonRequirementType ?? manifest.LogonRequirementType ?? "WhetherOrNotUserLoggedOn")}");
            var timing = DeploymentTypeTiming.Resolve(manifest.Timing, estRuntime, maxRuntime, $"deployment type '{spec.DtName}'", onLog);
            if (timing.Source == "manifest")
                onLog?.Invoke($"Runtime minutes (manifest)   : estimated {timing.EstimatedRuntimeMins}, maximum {timing.MaximumRuntimeMins}");
            sb.Append($" -EstimatedRuntimeMins {timing.EstimatedRuntimeMins} -MaximumRuntimeMins {timing.MaximumRuntimeMins}");
            // A switch: '-RequireUserInteraction $true' binds $true positionally and the cmdlet refuses the call.
            if (manifest.RequireUserInteraction == true)
                sb.Append(" -RequireUserInteraction:$true");
            sb.Append(content.CmdletArguments);
            sb.Append($" -RebootBehavior {ResolveRebootBehavior(manifest.PostExecutionBehavior)}");
            sb.Append(" -UserInteractionMode Hidden");
            if (isScriptDetection)
            {
                var lang = string.IsNullOrWhiteSpace(spec.Detection.ScriptLanguage)
                    ? "PowerShell" : spec.Detection.ScriptLanguage!;
                sb.Append(fileTransport
                    ? $" -ScriptLanguage {ValidateScriptLanguage(lang)} -ScriptFile $detectTransport{i}.Path"
                    : $" -ScriptLanguage {ValidateScriptLanguage(lang)} -ScriptText $detectScript{i}");
            }
            else
            {
                sb.Append(" -AddDetectionClause $clauses");
                sb.Append(BuildDetectionClauseConnectorArgs(spec.Detection));
            }
            if (ruleVars[i].Count > 0)
                sb.Append($" -AddRequirement @({string.Join(", ", ruleVars[i])})");
            sb.AppendLine(" | Out-Null");
            // The cleanup removes only the deployment types that this run created.
            sb.AppendLine($"$createdStaging += $dtCreateName{i}");

            if (isScriptDetection)
            {
                sb.Append($"{MecmScriptSigningPs.DetectionReadBackFunction} -ApplicationName '{Esc(appName)}'");
                sb.Append($" -DeploymentTypeName $dtCreateName{i} -Transport $detectTransport{i}");
                sb.Append($" -ExpectedThumbprint '{Esc(fileTransport ? detectionThumbprint : "")}'");
                sb.AppendLine($" -RequireDetection ${(signingPolicy.RequireDetection ? "true" : "false")}");
            }
        }

        sb.AppendLine("foreach ($oldDt in $replaceDtNames) {");
        sb.AppendLine($"    Remove-CMDeploymentType -ApplicationName '{Esc(appName)}' -DeploymentTypeName $oldDt -Force -ErrorAction Stop");
        sb.AppendLine("    $oldDtRemoved = $true");
        sb.AppendLine("    Write-Host \"Removed old deployment type  : $oldDt\"");
        sb.AppendLine("}");
        for (var i = 0; i < specs.Count; i++)
        {
            sb.AppendLine($"if ($dtCreateName{i} -ne $dtName{i}) {{");
            sb.AppendLine($"    Set-CMDeploymentType -ApplicationName '{Esc(appName)}' -DeploymentTypeName $dtCreateName{i} -NewDeploymentTypeName $dtName{i} -ErrorAction Stop");
            sb.AppendLine($"    Write-Host \"Renamed deployment type      : $dtName{i}\"");
            sb.AppendLine("}");
        }
        sb.AppendLine("} catch {");
        sb.AppendLine("    $stepError = $_");
        sb.AppendLine($"    Remove-ApkgIncompleteWork -ApplicationName '{Esc(appName)}' -SiteCode '{Esc(siteCode)}' -CreatedApp $createdApp -CreatedStaging $createdStaging -OldDtRemoved $oldDtRemoved");
        sb.AppendLine("    throw $stepError");
        sb.AppendLine("}");

        sb.AppendLine("if ($existing.Count -eq 1) {");
        sb.Append($"    Set-CMApplication -Name '{Esc(appName)}' -SoftwareVersion '{Esc(manifest.SoftwareVersion)}'");
        sb.Append($" -Publisher '{Esc(manifest.Publisher)}'");
        // The Software Center name follows the manifest, as it does for a new application. The comment is written only when there
        // is one: the command line and One Click pass an empty comment when the operator gave none, and a description that an
        // administrator typed in the console must survive that.
        if (!string.IsNullOrWhiteSpace(comment))
            sb.Append($" -Description '{Esc(comment)}'");
        if (!string.IsNullOrEmpty(manifest.DisplayName))
            sb.Append($" -LocalizedApplicationName '{Esc(manifest.DisplayName)}'");
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

        sb.AppendLine("try {");
        sb.AppendLine("  $revisions = Get-CMApplicationRevisionHistory -Id $app.CI_ID -ErrorAction Stop");
        sb.AppendLine("  if (@($revisions).Count -gt 1) {");
        sb.AppendLine("    @($revisions) | Sort-Object -Property @{Expression={if ($_.Revision) {$_.Revision} else {$_.CIVersion}}} -Descending | Select-Object -Skip 1 | ForEach-Object {");
        sb.AppendLine("      Remove-CMApplicationRevisionHistory -InputObject $_ -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-Host 'Cleaned revision history'");
        sb.AppendLine("  }");
        sb.AppendLine("} catch { Write-Host \"Revision cleanup skipped: $($_.Exception.Message)\" }");

        AppendFinalCiId(sb, appName);
        sb.AppendLine("Write-Host 'Application created successfully'");

        return sb.ToString();
    }

    /// <summary>
    /// Re-reads the application after the revision cleanup and prints its CI_ID and ModelName:
    /// every revision carries its own CI_ID, so the one printed at creation is not the one the
    /// site keeps.
    /// </summary>
    private static void AppendFinalCiId(System.Text.StringBuilder sb, string appName)
    {
        sb.AppendLine($"$finalApp = @(Get-CMApplication -Name '{Esc(appName)}' -DisableWildcardHandling -ErrorAction SilentlyContinue) | Select-Object -First 1");
        sb.AppendLine("if ($finalApp) { Write-Host \"Final CI_ID: $($finalApp.CI_ID)\"; Write-Host \"Final ModelName: $($finalApp.ModelName)\" }");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:Final )?CI_ID: (\d+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex CiIdLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"^Final ModelName: (ScopeId_[A-Za-z0-9-]+/Application_[A-Za-z0-9-]+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex ModelNameLine();

    /// <summary>The ModelName the final re-read printed last; empty when none.</summary>
    public static string ReadModelName(string? output) =>
        ModelNameLine().Matches(output ?? "").LastOrDefault()?.Groups[1].Value ?? "";

    /// <summary>The CI_ID the script printed last: the final re-read when present, the creation line otherwise; empty when none.</summary>
    public static string ReadCiId(string? output)
    {
        var lines = CiIdLine().Matches(output ?? "");
        var final = lines.LastOrDefault(m => m.Value.StartsWith("Final", StringComparison.Ordinal));
        return (final ?? lines.LastOrDefault())?.Groups[1].Value ?? "";
    }

    private const string InstallForUser = "InstallForUser";

    private static readonly string[] InstallationBehaviors =
        [InstallForUser, "InstallForSystem", "InstallForSystemIfResourceIsDeviceOtherwiseInstallForUser"];

    private static readonly string[] LogonRequirements =
        ["OnlyWhenUserLoggedOn", "WhetherOrNotUserLoggedOn", "OnlyWhenNoUserLoggedOn"];

    /// <summary>The cmdlet spelling of an InstallationBehaviorType; anything else is refused before it reaches the script.</summary>
    public static string ValidateBehavior(string value) =>
        InstallationBehaviors.FirstOrDefault(b => string.Equals(b, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"Unsupported InstallationBehaviorType '{value}'; use {string.Join(", ", InstallationBehaviors)}.");

    /// <summary>The cmdlet spelling of a LogonRequirementType; anything else is refused before it reaches the script.</summary>
    public static string ValidateLogonRequirement(string value) =>
        LogonRequirements.FirstOrDefault(l => string.Equals(l, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"Unsupported LogonRequirementType '{value}'; use {string.Join(", ", LogonRequirements)}.");

    /// <summary>
    /// Re-checks the resolved deployment-type commands for execution-policy
    /// relaxation before the first site write. Operator overrides and
    /// per-variant commands resolve after the stage was audited. Enforcement
    /// follows the manifest's own signed state as well as the policy, so a
    /// signed build is refused even when no policy is in scope.
    /// </summary>
    public static LauncherChainResult? CheckResolvedDeploymentCommands(
        IReadOnlyList<DeploymentTypeSpec> specs, SigningPolicy policy, ScriptSigningRecord? record)
    {
        var signed = SigningStatus.IsSigned(record?.Deployment.Status);
        var normalized = SigningPolicy.Normalize(policy);
        if (!signed && !normalized.SignDeployment && !normalized.RequireDeployment) return null;

        var effective = signed
            ? new SigningPolicy { SignDeployment = true, RequireDeployment = true }
            : normalized;
        var commands = specs.SelectMany((s, i) => new[]
        {
            new LauncherCommand($"DeploymentTypes[{i + 1}].InstallCommand", s.InstallCommand),
            new LauncherCommand($"DeploymentTypes[{i + 1}].UninstallCommand", s.UninstallCommand)
        });
        // The stage tree was audited at stage time; a root that cannot exist
        // limits this pass to the commands.
        var commandsOnlyRoot = Path.Combine(Path.GetTempPath(), "ap-commands-only-" + Guid.NewGuid().ToString("N"));
        return DeploymentLauncher.AuditChain(commandsOnlyRoot, commands, effective);
    }

    /// <summary>
    /// The RequireDeployment check: a missing record or a Deployment status
    /// other than SignedAndVerified or ExistingSignatureValid is refused. With
    /// a stage root, every recorded deployment file is verified there: it
    /// exists, its signature is intact, its SHA-256 equals the recorded hash
    /// and its signer equals the recorded thumbprint.
    /// </summary>
    public static void VerifyRequiredDeploymentSigning(SigningPolicy? policy, ScriptSigningRecord? record, string? stageRoot)
    {
        if (!SigningPolicy.Normalize(policy).RequireDeployment) return;
        var status = record?.Deployment.Status;
        if (!SigningStatus.IsSigned(status))
            throw new InvalidOperationException(SigningStatus.RefusalMessage(SigningCategory.Deployment, status));
        if (stageRoot is null) return;

        foreach (var file in record!.Deployment.Files)
        {
            var path = Path.Combine(stageRoot, file.RelativePath);
            string? problem = null;
            if (!File.Exists(path))
            {
                problem = "is missing from the staged content";
            }
            else
            {
                var verify = ScriptSignatureVerifier.Verify(path);
                var hash = ScriptSignatureVerifier.FileSha256Hex(path);
                if (!verify.SignatureIntact)
                    problem = $"does not carry an intact signature ({verify.Status})";
                else if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    problem = $"has hash {hash}, not the recorded {file.Sha256}";
                else if (file.Thumbprint.Length > 0 && !string.Equals(verify.Thumbprint, file.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    problem = $"is signed by {verify.Thumbprint}, not by the recorded {file.Thumbprint}";
            }
            if (problem is not null)
                throw new InvalidOperationException(
                    $"RequireDeployment is set but deployment script '{file.RelativePath}' {problem}; refusing to publish.");
        }
    }

    /// <summary>
    /// Emits the detection script into a single-quoted here-string, which
    /// passes the body through with no interpolation. A line that is exactly
    /// the here-string terminator would end it early, so it is refused.
    /// </summary>
    private static string BuildDetectionScriptPs(StageDetection det, int index)
    {
        var text = (det.ScriptText ?? "").Replace("\r\n", "\n").TrimEnd('\n');
        if (text.Split('\n').Any(l => l.Length >= 2 && PowerShellLiteral.IsSingleQuote(l[0]) && l[1] == '@'))
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
    /// group. OR then attaches to the first clause of the second run. A group
    /// holds two or more clauses: a one-clause second run takes the OR
    /// connector alone.
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
            var connector = $" -DetectionClauseConnector @(@{{LogicalName=$clause{secondStart}.Setting.LogicalName;Connector='OR'}})";
            return groupSizes[1] >= 2 ? $" -GroupDetectionClauses @({group})" + connector : connector;
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

    // The accepted values of -PropertyType and -ExpressionOperator of New-CMDetectionClauseFile
    // and New-CMDetectionClauseRegistryKeyValue.
    private static readonly string[] FilePropertyTypes = ["DateCreated", "DateModified", "Version", "Size"];
    private static readonly string[] RegistryPropertyTypes = ["Version", "Integer", "String"];
    // Between takes two values and OneOf and NoneOf a list, and the manifest carries one ExpectedValue, so they are not offered.
    private static readonly string[] FileOperators =
        ["IsEquals", "NotEquals", "GreaterThan", "LessThan", "GreaterEquals", "LessEquals"];
    private static readonly string[] RegistryOperators =
        [.. FileOperators, "BeginsWith", "NotBeginsWith", "EndsWith", "NotEndsWith", "Contains", "NotContains"];

    /// <summary>
    /// A detection value that the script carries as a bare word: the accepted value in the
    /// cmdlet's spelling. A manifest is a file an operator can edit; any other text would
    /// run as script in the publishing session.
    /// </summary>
    private static string CmdletWord(string? value, string fallback, string[] accepted, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return accepted.FirstOrDefault(a => a.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"Detection {field} '{value}' is not a value the ConfigMgr cmdlet accepts. Use one of: {string.Join(", ", accepted)}.");
    }

    /// <summary>The property type Existence, compared without regard to case like the detection tester and the WSUS rules do.</summary>
    private static bool IsExistence(StageDetection det) =>
        string.Equals(det.PropertyType?.Trim(), "Existence", StringComparison.OrdinalIgnoreCase);

    private static string BuildDetectionClausePs(StageDetection det)
    {
        return det.Type switch
        {
            "File" when IsExistence(det) =>
                $"$clause = New-CMDetectionClauseFile -Path '{Esc(det.FilePath ?? "")}' -FileName '{Esc(det.FileName ?? "")}' -Existence{(det.Is64Bit ? " -Is64Bit" : "")}",

            "File" =>
                $"$clause = New-CMDetectionClauseFile -Path '{Esc(det.FilePath ?? "")}' -FileName '{Esc(det.FileName ?? "")}' -PropertyType {CmdletWord(det.PropertyType, "Version", FilePropertyTypes, "File PropertyType")} -Value -ExpressionOperator {CmdletWord(det.Operator, "GreaterEquals", FileOperators, "File Operator")} -ExpectedValue '{Esc(det.ExpectedValue ?? "")}'{(det.Is64Bit ? " -Is64Bit" : "")}",

            // The existence form of the cmdlet has no operator and no expected value, and it still needs a property type.
            "RegistryKeyValue" when IsExistence(det) =>
                $"$clause = New-CMDetectionClauseRegistryKeyValue -Hive {CmHive(det)} -KeyName '{Esc(det.RegistryKeyRelative ?? "")}' -ValueName '{Esc(det.ValueName ?? "DisplayVersion")}' -PropertyType String -Existence{(det.Is64Bit ? " -Is64Bit" : "")}",

            "RegistryKeyValue" =>
                $"$clause = New-CMDetectionClauseRegistryKeyValue -Hive {CmHive(det)} -KeyName '{Esc(det.RegistryKeyRelative ?? "")}' -ValueName '{Esc(det.ValueName ?? "DisplayVersion")}' -PropertyType {CmdletWord(det.PropertyType, "String", RegistryPropertyTypes, "RegistryKeyValue PropertyType")} -Value -ExpressionOperator {CmdletWord(det.Operator, "IsEquals", RegistryOperators, "RegistryKeyValue Operator")} -ExpectedValue '{Esc(det.ExpectedValue ?? "")}'{(det.Is64Bit ? " -Is64Bit" : "")}",

            "RegistryKey" =>
                $"$clause = New-CMDetectionClauseRegistryKey -Hive {CmHive(det)} -KeyName '{Esc(det.RegistryKeyRelative ?? "")}' -Existence{(det.Is64Bit ? " -Is64Bit" : "")}",

            _ => throw new InvalidOperationException($"Unsupported detection type: {det.Type}")
        };
    }

    /// <summary>
    /// The refusal of a manifest that asks for ConfigMgr's native Appx deployment type (the detection
    /// type MSIX), or null. App Packager publishes an MSIX as a script deployment type, as it does every
    /// other product, so a manifest like this one is from an earlier stage: the native path failed on a
    /// UNC content path and did not read the behavior for an existing application.
    /// </summary>
    public static string? NativeMsixRefusal(StageManifest manifest) =>
        manifest.Detection?.Type.Equals("MSIX", StringComparison.OrdinalIgnoreCase) == true
            ? "This stage asks for the native MSIX deployment type of ConfigMgr, which App Packager no longer publishes. "
              + "An MSIX is published as a script deployment type. Stage the application again from its definition (Stage), then publish. If the definition itself has a detection rule of the type MSIX, change that rule to a registry key or a script rule first."
            : null;
    /// <summary>
    /// Distributes content for a packaged application to a DP group. Call after
    /// PackageAsync succeeds. A group that already holds the content makes the
    /// cmdlet throw ("already been targeted", or on the tested site "No content destination was found"); that is the wanted end state and
    /// counts as success when the group exists.
    /// </summary>
    public async Task<ContentStepResult> DistributeAsync(
        string appName, string siteCode, string dpGroupName = "All DPs",
        Action<string>? onProgress = null, string? providerMachine = null,
        CancellationToken ct = default)
    {
        var result = await WindowsPowerShellHost.RunAsync(
            BuildDistributionScript(appName, siteCode, dpGroupName, providerMachine), onProgress, ct);
        if (result.ExitCode == 0) return ContentStepResult.Ok;
        onProgress?.Invoke($"Distribution failed: {result.StdErr}");
        return ContentStepResult.Failed(ContentStepFailureText("distribution", dpGroupName, result.StdErr));
    }

    /// <summary>The outcome of a step after the application exists: distribution or test deployment.</summary>
    public sealed record ContentStepResult(bool Succeeded, string? Failure)
    {
        public static ContentStepResult Ok { get; } = new(true, null);
        public static ContentStepResult Failed(string failure) => new(false, failure);
    }

    /// <summary>
    /// Runs the content steps in their order: distribution first, then the test deployment when there is one. A distribution
    /// that fails skips the deployment. Returns the sentence of the first step that failed, or null when all steps succeeded.
    /// </summary>
    public static async Task<string?> RunContentStepsAsync(Func<Task<ContentStepResult>> distribute, Func<Task<ContentStepResult>>? deployToTest)
    {
        var distributed = await distribute().ConfigureAwait(false);
        if (!distributed.Succeeded) return distributed.Failure;
        if (deployToTest is null) return null;
        var deployed = await deployToTest().ConfigureAwait(false);
        return deployed.Succeeded ? null : deployed.Failure;
    }

    /// <summary>The sentence that names a failed content step, its target and the reason of the provider.</summary>
    public static string ContentStepFailureText(string step, string target, string reason)
    {
        // The first line of the provider text is the reason; the rest is position information, and the log has all of it.
        var text = reason.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (text.Length > 300) text = text[..300] + "...";
        if (text.Length == 0) text = "the provider gave no reason.";
        var title = step.Equals("distribution", StringComparison.Ordinal) ? "Content distribution" : "Test deployment";
        return $"{title} to '{target}' failed: {text}";
    }

    public static string BuildDistributionScript(string appName, string siteCode, string dpGroupName, string? providerMachine)
    {
        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine("try {");
        sb.AppendLine($"  Start-CMContentDistribution -ApplicationName '{Esc(appName)}' -DistributionPointGroupName '{Esc(dpGroupName)}' -ErrorAction Stop");
        sb.AppendLine($"  Write-Host 'Content distribution initiated: {Esc(appName)} to {Esc(dpGroupName)}'");
        sb.AppendLine("} catch {");
        // The site answers a second distribution of content that a group already holds with "No content destination was found
        // ... has already been distributed" (lab: ConfigMgr 5.00.9146). It gives the same answer for a group that does not
        // exist, so the group is looked up before the answer counts as done.
        sb.AppendLine("  if ($_.Exception.Message -match 'already been targeted|already distributed|already been distributed|No content destination was found') {");
        sb.AppendLine($"    if (-not (Get-CMDistributionPointGroup -Name '{Esc(dpGroupName)}' -ErrorAction Stop)) {{ throw \"Distribution point group '{EscDq(dpGroupName)}' was not found.\" }}");
        sb.AppendLine($"    Write-Host 'Content already targeted: {Esc(appName)} on {Esc(dpGroupName)}'");
        sb.AppendLine("  } else { throw }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Deploys a packaged application to a test collection as Available,
    /// effective immediately, creating an empty collection limited to All Systems
    /// or All Users when asked. A missing collection without that
    /// permission skips the deployment and reports it; an existing deployment
    /// counts as success.
    /// </summary>
    public async Task<ContentStepResult> DeployToTestCollectionAsync(
        string appName, string siteCode, string collectionName, bool createIfMissing,
        Action<string>? onProgress = null, string? providerMachine = null,
        CancellationToken ct = default, bool userCollection = false)
    {
        var result = await WindowsPowerShellHost.RunAsync(
            BuildTestCollectionDeploymentScript(appName, siteCode, collectionName, createIfMissing, providerMachine, userCollection), onProgress, ct);
        if (result.ExitCode == 0) return ContentStepResult.Ok;
        onProgress?.Invoke($"Test deployment failed: {result.StdErr}");
        return ContentStepResult.Failed(ContentStepFailureText("test deployment", collectionName, result.StdErr));
    }

    public static string BuildTestCollectionDeploymentScript(
        string appName, string siteCode, string collectionName, bool createIfMissing, string? providerMachine,
        bool userCollection = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        var collectionType = userCollection ? "User" : "Device";
        var limitingId = userCollection ? "SMS00002" : "SMS00001";
        var limitingName = userCollection ? "All Users" : "All Systems";
        var sb = new StringBuilder();
        AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine($"$collections = @(Get-CMCollection -CollectionType {collectionType} -Name '{Esc(collectionName)}' -DisableWildcardHandling -ErrorAction Stop)");
        if (createIfMissing)
        {
            sb.AppendLine("if ($collections.Count -eq 0) {");
            sb.AppendLine($"  Write-Host 'Creating {collectionType.ToLowerInvariant()} test collection: {Esc(collectionName)} (empty, limited to {limitingName})'");
            sb.AppendLine($"  $collections = @(New-CM{collectionType}Collection -Name '{Esc(collectionName)}' -LimitingCollectionId '{limitingId}' -ErrorAction Stop)");
            sb.AppendLine("}");
        }
        sb.AppendLine("if ($collections.Count -eq 0) {");
        sb.AppendLine($"  Write-Host 'Test deployment skipped: collection {Esc(collectionName)} not found'");
        sb.AppendLine("  exit 0");
        sb.AppendLine("}");
        sb.AppendLine($"if ($collections.Count -ne 1) {{ throw 'Multiple {collectionType.ToLowerInvariant()} collections named {Esc(collectionName)}; use a unique test collection name.' }}");
        sb.AppendLine("$collection = $collections[0]");
        sb.AppendLine("if ([string]::IsNullOrWhiteSpace([string]$collection.CollectionID)) { throw 'The test collection returned no collection ID.' }");
        sb.AppendLine("try {");
        sb.AppendLine($"  New-CMApplicationDeployment -Name '{Esc(appName)}' -CollectionId $collection.CollectionID -DeployAction Install -DeployPurpose Available -AvailableDateTime (Get-Date) -ErrorAction Stop | Out-Null");
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

        var result = await WindowsPowerShellHost.RunAsync(sb.ToString(), onProgress, ct);
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
        sb.AppendLine($"if (-not (Get-PSDrive -Name '{Esc(siteCode)}' -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"    New-PSDrive -Name '{Esc(siteCode)}' -PSProvider CMSite -Root {CmSiteRoot(providerMachine)} | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine($"Set-Location '{Esc(siteCode)}:'");
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

    /// <summary>
    /// What the package script does when the site refuses a deployment-type step. An application that the run created goes
    /// away. For an application that was already there, only the deployment types that this run created go, and only while no
    /// old deployment type has been removed yet: after that the new ones are the replacement and stay. The detection clauses are
    /// built on the file system drive, so the function goes back to the site drive first.
    /// </summary>
    internal const string IncompleteWorkCleanupFunction = """
        function Remove-ApkgIncompleteWork {
            param([string]$ApplicationName, [string]$SiteCode, [bool]$CreatedApp, [string[]]$CreatedStaging, [bool]$OldDtRemoved)
            try { Set-Location "${SiteCode}:" -ErrorAction Stop }
            catch { Write-Host "Cleanup could not return to the site drive: $($_.Exception.Message)"; return }
            if ($CreatedApp) {
                try { Remove-CMApplication -Name $ApplicationName -Force -ErrorAction Stop; Write-Host 'Removed the incomplete application' }
                catch { Write-Host "The incomplete application was not removed: $($_.Exception.Message)" }
                return
            }
            if ($OldDtRemoved) {
                Write-Host 'An old deployment type was already removed, so the new deployment types stay. Publish again after the cause is fixed.'
                return
            }
            foreach ($name in @($CreatedStaging)) {
                try { Remove-CMDeploymentType -ApplicationName $ApplicationName -DeploymentTypeName $name -Force -ErrorAction Stop; Write-Host "Removed deployment type of this run: $name" }
                catch { Write-Host "The deployment type '$name' of this run was not removed: $($_.Exception.Message)" }
            }
        }
        """;

    private static string Esc(string value) => PowerShellLiteral.EscapeSingleQuoted(value);

    private static string EscDq(string value) => PowerShellLiteral.EscapeDoubleQuoted(value);

    /// <summary>
    /// The -RebootBehavior a script deployment type is created with. A manifest that
    /// names none takes BasedOnExitCode, so a 3010 exit reaches the client as a restart.
    /// </summary>
    public static string ResolveRebootBehavior(string? postExecutionBehavior) =>
        StagingService.NormalizePostExecutionBehavior(postExecutionBehavior) ?? "BasedOnExitCode";

    internal static string SanitizeFolderNamePublic(string name) => SanitizeFolderName(name);

    /// <summary>
    /// Sanitizes a string for use as a filesystem folder name.
    /// Strips trailing dots/spaces (Windows silently removes these), replaces commas
    /// and other problematic characters. Only allows alphanumeric, space, dot, hyphen, underscore.
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9 .\-_]", "");
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

    /// <summary>The ModelName of the application, the same for all its revisions and kept on a rename; empty when not read.</summary>
    public string ModelName { get; init; } = "";
    public string NetworkContentPath { get; init; } = "";
    public string? ErrorMessage { get; init; }
    public bool AlreadyExists { get; init; }
}
