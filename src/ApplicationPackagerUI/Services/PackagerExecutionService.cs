using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

public class PackagerExecutionService(
    SettingsService settingsService,
    StagingService stagingService,
    IntuneDeploymentService intuneDeploymentService,
    DeploymentConditionsService deploymentConditionsService)
{
    private static readonly TimeSpan GetLatestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Runs a packager with -GetLatestVersionOnly. Routes JSON apps to engine DLL.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(
        PackagerInfo packager, CancellationToken ct = default)
    {
        // Route JSON-defined apps through the engine DLL
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            var acqMethod = settingsService.Load().AcquisitionMethod;
            return await stagingService.GetLatestVersionAsync(def, acqMethod, ct);
        }

        var settings = settingsService.Load();
        var args = BuildGetLatestArgs(packager, settings);

        // The shared runner drains both streams and terminates the child on cancellation
        // or timeout, including when stderr would otherwise fill its pipe.
        var run = await BoundedProcessRunner.RunAsync(
            new ProcessStartInfo { FileName = "powershell.exe", Arguments = args },
            GetLatestTimeout, ct);
        ct.ThrowIfCancellationRequested();

        if (run.TimedOut || run.ExitCode != 0)
            return null;

        // First non-empty line is the version
        foreach (var line in run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return null;
    }

    /// <summary>
    /// Runs a packager with -StageOnly, streaming stdout line-by-line via the callback.
    /// A snapshot this call creates records <paramref name="runTarget"/>, else the stored
    /// deployment target.
    /// </summary>
    public async Task<PackagerExecutionResult> StageAsync(
        PackagerInfo packager, Action<string>? onOutputLine = null, CancellationToken ct = default,
        Dictionary<string, string>? settingsValues = null, RunSnapshot? snapshot = null, string? runTarget = null)
    {
        // Route JSON-defined apps through the engine DLL
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            var appSettings = settingsService.Load();
            // A grid run builds from the application's active profile; the default
            // profile runs without a snapshot, as every grid run did before profiles.
            var source = ProfileSource.Resolve(WorkbenchStoreOrNull(snapshot), def.Id);
            if (snapshot is null && source.Failure is not null)
                return new PackagerExecutionResult { ExitCode = 1, StdErr = source.RefusalMessage() };
            if (snapshot is null && source.ActiveProfileId is { } activeProfile)
            {
                snapshot = CreateProfileSnapshot(source.ApplicationId, activeProfile, appSettings, out var snapshotRefusal, out var failure,
                    target: runTarget);
                if (snapshotRefusal is not null || failure is not null)
                    return new PackagerExecutionResult { ExitCode = 1, StdErr = snapshotRefusal ?? $"Run snapshot not created for profile {activeProfile}: {failure}" };
                if (snapshot is not null)
                    onOutputLine?.Invoke($"Workbench profile             : {WorkbenchJson.AsText(snapshot.Profile["Name"]) ?? activeProfile} (revision {snapshot.ProfileRevision}, build {snapshot.BuildId})");
            }
            // Reuse version from Check Latest to avoid redundant API calls (GitHub rate limiting)
            var knownVersion = !string.IsNullOrEmpty(packager.LatestVersion) ? packager.LatestVersion : null;
            // The Install for choice comes from the build's profile; the saved per-app
            // entry applies only where the legacy maps still apply. An entry for a
            // definition that no longer declares the modes is ignored.
            var installMode = !def.OffersInstallModes ? null
                : snapshot is not null && !ApplicationIds.IsDefaultProfile(snapshot.ProfileId)
                    ? WorkbenchJson.Get(snapshot.Profile, "InstallMode").Text
                    : source.LegacyMapsApply ? deploymentConditionsService.GetConditions(def.Id)?.InstallMode : null;
            if (string.IsNullOrWhiteSpace(installMode)) installMode = null;
            // Saved install parameters apply when the caller brought none (no review
            // dialog); the M365 channel from Settings rides along for ODT definitions.
            settingsValues ??= appSettings.InstallerSettingValues.TryGetValue(def.Id, out var saved)
                ? new Dictionary<string, string>(saved)
                : null;
            if (def.InstallerType.Equals("ODT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(appSettings.M365Channel))
            {
                settingsValues ??= [];
                settingsValues[StagingService.OdtChannelKey] = appSettings.M365Channel;
            }
            var inputs = new StageInputs
            {
                LocalSourceFolder = appSettings.LocalSourceFolders.TryGetValue(def.Id, out var folder) ? folder : null,
                SideFiles = appSettings.SideFiles,
                CitrixSwitches = appSettings.CitrixSwitches,
                RunSnapshot = snapshot,
                SigningPolicy = appSettings.ScriptSigning
            };
            var result = await stagingService.StageAsync(def, appSettings.DownloadRoot, onOutputLine, ct, knownVersion, settingsValues, installMode, inputs);
            if (result.Success)
            {
                packager.StagedVersion = Path.GetFileName(result.ContentPath);
                await BuildIntuneWinAtStageAsync(result.ManifestPath, onOutputLine, ct);
            }
            return new PackagerExecutionResult
            {
                ExitCode = result.Success ? 0 : 1,
                StdOut = result.Success ? $"Staged to: {result.ContentPath}" : "",
                StdErr = result.ErrorMessage ?? "",
                OutLogPath = result.ManifestPath
            };
        }

        var settings = settingsService.Load();
        var bridge = PrepareChildBridge(packager, settings, onOutputLine, out var refusal, snapshot, runTarget);
        if (refusal is not null) return refusal;
        var (args, logPaths) = BuildStageArgs(packager, settings, bridge?.DownloadRoot);
        var staged = await RunStreamingAsync(args, logPaths, onOutputLine, ct, ChildEnvironment(bridge, settings));
        if (staged.ExitCode != 0) return staged;

        var manifestPath = PackagerStageOutput.ExistingManifest(staged.StdOut);
        if (manifestPath is null)
            return new PackagerExecutionResult
            {
                ExitCode = 1,
                StdOut = staged.StdOut,
                StdErr = PackagerStageOutput.NoManifestMessage,
                OutLogPath = staged.OutLogPath,
                ErrLogPath = staged.ErrLogPath,
                StructuredLogPath = staged.StructuredLogPath
            };
        if (bridge is not null)
            SealPackagerStage(bridge, staged.StdOut, settings.DownloadRoot, onOutputLine);
        await BuildIntuneWinAtStageAsync(manifestPath, onOutputLine, ct);
        return staged;
    }

    /// <summary>The .intunewin of a stage, built when the content prep tool is on this computer; the note goes to the run log.</summary>
    private static async Task BuildIntuneWinAtStageAsync(string? manifestPath, Action<string>? onOutputLine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath) || IntuneDeploymentService.InstalledToolPath() is null) return;
        StageManifest manifest;
        try { manifest = new ManifestService().Read(manifestPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return; }
        void Report(string line)
        {
            if (onOutputLine is not null)
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => onOutputLine(line));
        }
        var note = await IntuneDeploymentService.BuildAtStageAsync(manifest, Path.GetDirectoryName(manifestPath)!, Report, ct);
        if (note.Length > 0) Report($"Intunewin: {note}");
    }

    /// <summary>
    /// The stage manifest a stage run produced: the JSON stage's own manifest, the one
    /// a PS1 packager logged, else the stage that carries the snapshot's BuildId.
    /// </summary>
    public string? StagedManifestPath(PackagerInfo packager, PackagerExecutionResult stageResult, RunSnapshot? snapshot)
    {
        if (packager.StagedManifestPath is { } dropped && File.Exists(dropped)) return dropped;
        if (!string.IsNullOrWhiteSpace(stageResult.OutLogPath) && stageResult.OutLogPath.EndsWith(PackagerStageOutput.ManifestFileName, StringComparison.OrdinalIgnoreCase)
            && File.Exists(stageResult.OutLogPath))
            return stageResult.OutLogPath;
        if (!packager.IsJsonDefined && PackagerStageOutput.ExistingManifest(stageResult.StdOut) is { } logged) return logged;
        if (snapshot is null) return null;
        try
        {
            var settings = settingsService.Load();
            var root = string.IsNullOrWhiteSpace(snapshot.DownloadRoot) ? settings.DownloadRoot : snapshot.DownloadRoot;
            return BuildRecords.ResolveStageManifestForBuild(snapshot.BuildId, root).ManifestPath;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The stage a publish may reuse without staging: with a latest version known, only the
    /// manifest of that version, and for a build sealed under a profile only while its build
    /// record is current (same version, profile revision and signing policy); with none
    /// known, the last staged version. Null when the row has none, the build is stale, or
    /// the row is a PS1 packager whose stage location only its own run reports.
    /// </summary>
    public string? ExistingStageManifest(PackagerInfo packager)
    {
        if (packager.StagedManifestPath is { } dropped) return File.Exists(dropped) ? dropped : null;
        if (!packager.IsJsonDefined || packager.DefinitionPath is null) return null;
        try
        {
            var settings = settingsService.Load();
            var def = LoadDefinition(packager.DefinitionPath);
            var source = ProfileSource.Resolve(WorkbenchStoreOrNull(null), def.Id);
            if (source.Failure is not null) return null;
            var appRoot = Path.Combine(ProfileContentScope.LocalDownloadRoot(settings.DownloadRoot, source.ActiveProfileId),
                $"{def.Application.Replace(" ", "")}-{def.Architecture}");
            if (string.IsNullOrWhiteSpace(packager.LatestVersion))
            {
                if (string.IsNullOrWhiteSpace(packager.StagedVersion)) return null;
                var staged = Path.Combine(appRoot, packager.StagedVersion, "stage-manifest.json");
                return File.Exists(staged) ? staged : null;
            }
            var path = Path.Combine(appRoot, packager.LatestVersion, "stage-manifest.json");
            if (!File.Exists(path)) return null;
            var manifest = new ManifestService().Read(path);
            if (!StageFinalizer.CarriesBuild(manifest)) return path;
            return ResolveBuildPlan(packager) is { } plan
                   && BuildRecords.IsCurrent(plan.DataRoot, plan.ApplicationId, plan.ProfileId, packager.LatestVersion,
                       plan.ProfileRevision, settings.ScriptSigning.PolicyDigest) == true
                ? path
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Publishes the stage a run produced to WSUS. A refusal, a failed server call and
    /// a missing manifest come back as the step result; cancellation propagates.
    /// </summary>
    public async Task<WsusStepResult> PublishWsusAsync(PackagerInfo packager, PackagerExecutionResult stageResult, RunSnapshot? snapshot,
        ApplicationPackagerEngine.Services.Wsus.IWsusPublisher publisher, Action<string>? onOutputLine, CancellationToken ct)
    {
        var manifestPath = StagedManifestPath(packager, stageResult, snapshot);
        if (manifestPath is null)
            return new WsusStepResult(WsusStepStatus.Failed, "No stage manifest was found for this stage. Run Stage again.", [], []);
        StageManifest manifest;
        try { manifest = new ManifestService().Read(manifestPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new WsusStepResult(WsusStepStatus.Failed, $"The stage manifest {manifestPath} could not be read: {ex.Message}", [], []);
        }
        void Report(string line)
        {
            if (onOutputLine is not null)
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => onOutputLine(line));
        }
        var settings = settingsService.Load();
        var supportUrl = string.IsNullOrWhiteSpace(packager.VendorUrl) ? null : packager.VendorUrl;
        return await ApplicationPackagerEngine.Services.Wsus.WsusPublishStep.RunAsync(
            publisher, manifest, Path.GetDirectoryName(manifestPath)!, settings.Wsus, Report, ct, supportUrl);
    }

    /// <summary>Seals the build record of a PS1 stage that ran with a run snapshot; the outcome goes to the run log.</summary>
    private static void SealPackagerStage(PackagerChildLaunch bridge, string stdout, string downloadRoot, Action<string>? onOutputLine)
    {
        string message;
        try
        {
            var searchRoot = string.IsNullOrWhiteSpace(bridge.DownloadRoot) ? downloadRoot : bridge.DownloadRoot;
            PackagerStageSeal.Seal(bridge.Snapshot, PackagerStageSeal.ManifestPathFromOutput(stdout), searchRoot, out message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException or JsonException)
        {
            message = "No build record sealed: " + ex.Message;
        }
        if (onOutputLine is not null)
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => onOutputLine(message));
    }

    /// <summary>
    /// Runs the Package phase for the run's target: the MECM application, an Intune Win32
    /// publish, or both. Without a target the run publishes to ConfigMgr; the stored
    /// deployment target never decides a publish.
    /// </summary>
    public async Task<PackagerExecutionResult> PackageAsync(
        PackagerInfo packager, string comment, Action<string>? onOutputLine = null,
        CancellationToken ct = default, OnExistingBehavior onExisting = OnExistingBehavior.Skip,
        RunSnapshot? snapshot = null, string? targetOverride = null)
    {
        var target = DeploymentTargets.Normalize(targetOverride ?? DeploymentTargets.Mecm);
        if (WsusTargetRefusal(target) is { } wsusRefusal) return wsusRefusal;
        if (packager.StagedManifestPath is not null)
            return await PackageStagedManifestAsync(packager.StagedManifestPath, comment, onOutputLine, ct, onExisting, snapshot, target);

        var settings = settingsService.Load();

        // Route JSON-defined apps through the engine's MecmTarget
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            var source = ProfileSource.Resolve(WorkbenchStoreOrNull(snapshot), def.Id);
            if (snapshot is null && source.Failure is not null)
                return new PackagerExecutionResult { ExitCode = 1, StdErr = source.RefusalMessage() };
            // A grid publish packages the stage of the active profile, under that profile's root.
            var appRoot = Path.Combine(ProfileContentScope.LocalDownloadRoot(settings.DownloadRoot, snapshot?.ProfileId ?? source.ActiveProfileId),
                $"{def.Application.Replace(" ", "")}-{def.Architecture}");
            // With a latest version known, only its stage publishes: an older staged version
            // published as the latest would carry the wrong release to every endpoint.
            var version = !string.IsNullOrWhiteSpace(packager.LatestVersion) ? packager.LatestVersion : packager.StagedVersion ?? "";
            var manifestPath = Path.Combine(appRoot, version, "stage-manifest.json");
            // A snapshot build packages exactly the stage that carries its BuildId, or nothing.
            if (snapshot is not null)
            {
                try { manifestPath = BuildRecords.ResolveStageManifestForBuild(snapshot.BuildId, appRoot).ManifestPath; }
                catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException)
                {
                    return new PackagerExecutionResult { ExitCode = 1, StdErr = ex.Message };
                }
            }

            if (!File.Exists(manifestPath))
                return new PackagerExecutionResult
                {
                    ExitCode = 1,
                    StdErr = version.Length > 0
                        ? $"Stage manifest not found: {manifestPath}. Run Stage for {def.Application} {version} first."
                        : $"Stage manifest not found: {manifestPath}. Run Stage first."
                };

            var manifestService = new ApplicationPackagerEngine.Services.ManifestService();
            var manifest = manifestService.Read(manifestPath);
            var stagedContentPath = Path.GetDirectoryName(manifestPath)!;

            var conflicts = new ConflictWatch();
            void Report(string line)
            {
                conflicts.Observe(line);
                if (onOutputLine is not null)
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => onOutputLine(line));
            }

            var mecmSucceeded = true;
            var mecmAlreadyExists = false;
            var stdOut = "";
            var stdErr = "";

            // Operator selections resolve before any target runs: a variant split rewrites
            // the manifest into gated deployment types, a plain selection stays flat rules
            // on the single deployment type.
            // A build of a non-default profile already carries its profile's conditions,
            // commands and title mode; the saved per-app settings apply only where
            // ProfileSource leaves them in force.
            var profiled = !ProfileSource.LegacyMapsApplyTo(manifest, source);
            var conditions = profiled ? null : deploymentConditionsService.GetConditions(def.Id);
            DeploymentConditionsService.ApplyVariantSplit(manifest, conditions, stagedContentPath, Report);
            var requirements = DeploymentConditionsService.BuildRequirements(conditions);
            // Recorded on the manifest, not just handed to MECM: the Intune publish reads
            // its command lines from the manifest, so a saved override that never lands
            // there publishes install.bat and uninstall.bat instead.
            var commandOverrides = ApplicationPackagerEngine.Services.DeploymentTypeSpecResolver
                .ResolveCommandOverrides(manifest, profiled ? null : deploymentConditionsService.GetCommandOverrides(def.Id));
            var applicationTitle = ApplicationTitle.Resolve(
                manifest.AppName, manifest.SoftwareVersion,
                ApplicationTitle.ResolveMode(manifest.TitleMode, conditions?.TitleMode, settings.IncludeVersionInTitle, settings.RemoveVersionFromTitle));
            var signingPolicy = StageFinalizer.EffectivePolicy(snapshot, settings.ScriptSigning);
            if (applicationTitle != manifest.AppName)
                Report($"Application title            : {applicationTitle}");

            // An Intune-only run stops at the staged content: no site connection, no share
            // copy, no MECM application. The build and publish below work from local content.
            var afterMecm = "";
            if (!DeploymentTargets.SkipsMecm(target))
            {
                var mecmTarget = new ApplicationPackagerEngine.Services.MecmTarget();
                var result = await mecmTarget.PackageAsync(
                    manifest, stagedContentPath, settings.SiteCode, settings.FileShareRoot, comment,
                    settings.EstimatedRuntimeMins, settings.MaximumRuntimeMins,
                    Report,
                    commandOverrides: commandOverrides,
                    conditionTemplates: new ApplicationPackagerEngine.Services.ConditionTemplateService()
                        .Load(warning => onOutputLine?.Invoke(warning)),
                    requirements: requirements,
                    providerMachine: settings.ProviderMachine,
                    onExisting: onExisting,
                    ct: ct,
                    contentLayout: settings.ContentLayout,
                    applicationTitle: applicationTitle,
                    scriptSigning: MecmScriptSigningContext.FromManifest(manifest, signingPolicy),
                    contentScope: ProfileContentScope.ForManifest(manifest),
                    contentOptions: ContentOptions(settings, Report));

                mecmSucceeded = result.Success;
                mecmAlreadyExists = result.AlreadyExists;
                stdOut = result.Success ? $"Packaged: {result.ApplicationName}" : "";
                stdErr = result.ErrorMessage ?? "";
                RecordMecmPublication(manifest, result, settings, Report);
                if (result.Success && !result.AlreadyExists)
                    afterMecm = await RunPostPackageStepsAsync(mecmTarget, result, manifest, stagedContentPath, settings, target, Report, ct);
            }
            else
            {
                Report("Intune only: skipping ConfigMgr application, site connection, and share copy.");
                stdOut = $"Staged content ready: {stagedContentPath}";
            }

            var intuneRequested = DeploymentTargets.PublishesToIntune(target);
            var notes = IntuneRunOutcome.NotRun;
            if (intuneRequested && mecmSucceeded)
            {
                // The Intune display name carries the same title as the MECM
                // application. Every path that reads the manifest name for a
                // content location has already run.
                manifest.AppName = applicationTitle;
                notes = await intuneDeploymentService.BuildAndPublishAsync(
                    manifest, stagedContentPath, Report, comment, ct, signingPolicy, requirements);
            }

            var outcome = PackagingOutcomeComposer.Compose(
                PackagingOutcomeComposer.MecmStatus(
                    !DeploymentTargets.SkipsMecm(target), mecmSucceeded, mecmAlreadyExists),
                stdErr, intuneRequested, notes);

            return new PackagerExecutionResult
            {
                ExitCode = outcome.ExitCode,
                StdOut = stdOut,
                StdErr = outcome.StdErr,
                IntuneWinNote = notes.BuildNote.Length > 0 ? notes.BuildNote : afterMecm,
                IntunePublishNote = notes.PublishNote,
                Conflict = conflicts.Result
            };
        }

        // A PS1 packager owns its own MECM flow and writes no manifest this layer can read, so
        // there is nothing to build a .intunewin from. Under Intune only, running it anyway
        // would create the MECM application the target asked to skip.
        if (DeploymentTargets.SkipsMecm(target))
        {
            return new PackagerExecutionResult
            {
                ExitCode = 1,
                StdErr = "Deployment target is Intune only, and Intune publishing applies to JSON-defined apps. This PS1 packager was not run.",
                IntunePublishNote = "skipped; PS1 packagers publish to ConfigMgr only."
            };
        }
        if (DeploymentTargets.PublishesToIntune(target))
            onOutputLine?.Invoke("Intune publishing applies to JSON-defined apps; this PS1 packager runs the ConfigMgr flow only.");

        var bridge = PrepareChildBridge(packager, settings, onOutputLine, out var refusal, snapshot, target);
        if (refusal is not null) return refusal;
        var (args, logPaths) = BuildPackageArgs(packager, settings, comment, bridge?.DownloadRoot);
        var environment = new Dictionary<string, string>(ChildEnvironment(bridge, settings), StringComparer.OrdinalIgnoreCase)
        {
            [OnExistingVariable] = onExisting.ToString()
        };
        var packaged = await RunStreamingAsync(args, logPaths, onOutputLine, ct, environment);
        foreach (var line in packaged.StdOut.Split('\n'))
        {
            if (!OnExistingConflict.TryParse(line.Trim(), out var app, out var version, out var incoming)) continue;
            return new PackagerExecutionResult
            {
                ExitCode = packaged.ExitCode,
                StdOut = packaged.StdOut,
                StdErr = packaged.StdErr,
                OutLogPath = packaged.OutLogPath,
                ErrLogPath = packaged.ErrLogPath,
                StructuredLogPath = packaged.StructuredLogPath,
                Conflict = new ExistingApplicationConflict(app, version, incoming)
            };
        }
        return packaged;
    }

    /// <summary>
    /// A target that publishes to WSUS is refused here: this path builds the ConfigMgr
    /// application and the Intune app only, and the WSUS publish service carries WSUS.
    /// </summary>
    private static PackagerExecutionResult? WsusTargetRefusal(string target) =>
        DeploymentTargets.PublishesToWsus(target)
            ? new PackagerExecutionResult
            {
                ExitCode = 1,
                StdErr = $"Target {target} publishes to WSUS, which runs through Publish to WSUS; nothing was packaged."
            }
            : null;

    /// <summary>The variable a PS1 packager's common module reads for the existing-application behavior; Skip when unset.</summary>
    public const string OnExistingVariable = "APP_PACKAGER_ON_EXISTING";

    /// <summary>
    /// Writes the run snapshot for a PS1 packager's active workbench profile and returns the
    /// child environment. A signing refusal stops the run before the child starts; any other
    /// failure runs the child without the bridge, as it ran before profiles existed.
    /// </summary>
    private static PackagerChildLaunch? PrepareChildBridge(
        PackagerInfo packager, AppSettings settings, Action<string>? onOutputLine, out PackagerExecutionResult? refusal,
        RunSnapshot? snapshot = null, string? target = null)
    {
        refusal = null;
        try
        {
            if (snapshot is not null)
                return PackagerChildBridge.FromSnapshot(WorkbenchStore.Open(snapshot.DataRoot), snapshot, settings.DownloadRoot,
                    settings.IncludeVersionInTitle, settings.RemoveVersionFromTitle);
            return PackagerChildBridge.Prepare(
                WorkbenchStore.Open(), packager.FullPath, settings.DownloadRoot,
                DeploymentTargets.Normalize(target ?? settings.Intune.DeploymentTarget),
                JsonSerializer.SerializeToNode(settings.ScriptSigning), new ScriptSigningCertificateGate(),
                settings.IncludeVersionInTitle, settings.RemoveVersionFromTitle);
        }
        catch (Exception ex) when (ex.Message.Contains("SigningCertificateUnavailable", StringComparison.Ordinal)
                                   || ex.Message == NoSigningServiceGate.NotLoadedMessage)
        {
            refusal = new PackagerExecutionResult { ExitCode = 1, StdErr = ex.Message };
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or ArgumentException or JsonException)
        {
            var line = $"Run snapshot not created for {Path.GetFileNameWithoutExtension(packager.Script)}: {ex.Message}";
            if (onOutputLine is not null)
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => onOutputLine(line));
            return null;
        }
    }

    /// <summary>
    /// The Intune compatibility findings for the stage a Package run would publish, with
    /// the operator's variant split and requirement selections applied as the run applies
    /// them. Null when the row has no readable staged manifest.
    /// </summary>
    public IReadOnlyList<IntuneCompatibilityFinding>? ReviewIntuneCompatibility(PackagerInfo packager)
    {
        try
        {
            string? manifestPath = packager.StagedManifestPath;
            AppDefinition? def = null;
            ProfileSource? source = null;
            if (manifestPath is null && packager.IsJsonDefined && packager.DefinitionPath is not null)
            {
                def = LoadDefinition(packager.DefinitionPath);
                var settings = settingsService.Load();
                source = ProfileSource.Resolve(WorkbenchStoreOrNull(null), def.Id);
                var appRoot = Path.Combine(ProfileContentScope.LocalDownloadRoot(settings.DownloadRoot, source.ActiveProfileId),
                    $"{def.Application.Replace(" ", "")}-{def.Architecture}");
                manifestPath = Path.Combine(appRoot, packager.LatestVersion ?? "", "stage-manifest.json");
                if (!File.Exists(manifestPath) && !string.IsNullOrWhiteSpace(packager.StagedVersion))
                    manifestPath = Path.Combine(appRoot, packager.StagedVersion, "stage-manifest.json");
            }
            if (manifestPath is null || !File.Exists(manifestPath)) return null;

            var manifest = new ManifestService().Read(manifestPath);
            if (def is null || source is null || !ProfileSource.LegacyMapsApplyTo(manifest, source))
                return IntuneCompatibilityFindings.Build(manifest);
            var conditions = deploymentConditionsService.GetConditions(def.Id);
            DeploymentConditionsService.ApplyVariantSplit(manifest, conditions, Path.GetDirectoryName(manifestPath)!);
            return IntuneCompatibilityFindings.Build(manifest, DeploymentConditionsService.BuildRequirements(conditions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The workbench identity a One Click build of the row is sealed under.</summary>
    public sealed record OneClickBuildPlan(string DataRoot, string ApplicationId, string ProfileId, int ProfileRevision);

    /// <summary>The row's application, active profile and its revision; null when the workbench cannot resolve them.</summary>
    public OneClickBuildPlan? ResolveBuildPlan(PackagerInfo packager)
    {
        try
        {
            var applicationId = packager.IsJsonDefined && packager.DefinitionPath is not null
                ? ApplicationIds.New(ApplicationOrigin.Catalog, name: LoadDefinition(packager.DefinitionPath).Id)
                : ApplicationIds.New(ApplicationOrigin.Catalog, path: packager.FullPath);
            if (!ApplicationIds.IsValid(applicationId)) return null;
            var store = WorkbenchStore.Open();
            var profileId = store.GetApplicationDefinition(applicationId).ActiveProfileId;
            if (string.IsNullOrWhiteSpace(profileId)) profileId = ApplicationIds.DefaultProfileId;
            return new OneClickBuildPlan(store.DataRoot, applicationId, profileId, store.GetProfile(applicationId, profileId).Revision);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or ArgumentException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The run snapshot one One Click row stages from and then publishes from, for a
    /// JSON definition and for a PS1 packager alike. A snapshot that cannot be created
    /// is the row's refusal, with the reason; the row neither stages nor publishes.
    /// </summary>
    public RunSnapshot? CreateRowSnapshot(PackagerInfo packager, OneClickBuildPlan plan, string target, out string? refusal)
    {
        refusal = null;
        var settings = settingsService.Load();
        try
        {
            var store = WorkbenchStore.Open(plan.DataRoot);
            return store.CreateRunSnapshot(new RunSnapshotRequest
            {
                ApplicationId = plan.ApplicationId,
                ProfileId = plan.ProfileId,
                Target = RunTargets.Normalize(target) ?? RunTargets.Mecm,
                SigningPolicy = JsonSerializer.SerializeToNode(settings.ScriptSigning),
                PackagerScriptPath = packager.IsJsonDefined ? packager.DefinitionPath : packager.FullPath,
                DownloadRoot = WorkbenchPaths.GetProfileDownloadRoot(settings.DownloadRoot, plan.ProfileId)
            }, new ScriptSigningCertificateGate());
        }
        catch (Exception ex) when (ex.Message.Contains("SigningCertificateUnavailable", StringComparison.Ordinal)
                                   || ex.Message == NoSigningServiceGate.NotLoadedMessage)
        {
            refusal = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or ArgumentException or JsonException)
        {
            refusal = $"Run snapshot not created for profile {plan.ProfileId}: {ex.Message}";
            return null;
        }
    }

    /// <summary>The workbench store of a snapshot's data root, else the configured one; null when it cannot be opened.</summary>
    private static WorkbenchStore? WorkbenchStoreOrNull(RunSnapshot? snapshot)
    {
        try { return string.IsNullOrWhiteSpace(snapshot?.DataRoot) ? WorkbenchStore.Open() : WorkbenchStore.Open(snapshot.DataRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the run snapshot of one profile of a JSON definition. The signing certificate
    /// resolves here; a policy the certificate cannot meet is the refusal and no snapshot
    /// exists. Any other failure returns null with its message; the caller decides.
    /// </summary>
    private static RunSnapshot? CreateProfileSnapshot(string applicationId, string profileId, AppSettings settings,
        out string? refusal, out string? failure, string? dataRoot = null, string? target = null)
    {
        refusal = null;
        failure = null;
        try
        {
            var store = string.IsNullOrWhiteSpace(dataRoot) ? WorkbenchStore.Open() : WorkbenchStore.Open(dataRoot);
            return store.CreateRunSnapshot(new RunSnapshotRequest
            {
                ApplicationId = applicationId,
                ProfileId = profileId,
                Target = DeploymentTargets.Normalize(target ?? settings.Intune.DeploymentTarget),
                SigningPolicy = JsonSerializer.SerializeToNode(settings.ScriptSigning),
                DownloadRoot = WorkbenchPaths.GetProfileDownloadRoot(settings.DownloadRoot, profileId)
            }, new ScriptSigningCertificateGate());
        }
        catch (Exception ex) when (ex.Message.Contains("SigningCertificateUnavailable", StringComparison.Ordinal)
                                   || ex.Message == NoSigningServiceGate.NotLoadedMessage)
        {
            refusal = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or ArgumentException or JsonException)
        {
            failure = ex.Message;
            return null;
        }
    }

    /// <summary>The deployment type content options the settings store, warnings to the run log.</summary>
    public static DeploymentTypeContentOptions ContentOptions(AppSettings settings, Action<string>? onWarning = null) =>
        DeploymentTypeContentOptions.Resolve(settings.ContentFallback, settings.SlowNetworkDeploymentMode, onWarning);

    /// <summary>
    /// The PS1 child environment: the workbench bridge when one was prepared, the
    /// deployment type content options, and the global title mode when no profile set one.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ChildEnvironment(PackagerChildLaunch? bridge, AppSettings settings)
    {
        var environment = new Dictionary<string, string>(bridge?.Environment ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in ContentOptions(settings).EnvironmentValues)
            environment[name] = value;
        if (!environment.ContainsKey(PackagerChildBridge.TitleModeVariable))
        {
            var token = ApplicationTitle.DefaultModeToken(settings.IncludeVersionInTitle, settings.RemoveVersionFromTitle);
            if (token.Length > 0) environment[PackagerChildBridge.TitleModeVariable] = token;
        }
        return environment;
    }

    // ── Ad-hoc drop: manifest-direct packaging ───────────────────

    /// <summary>
    /// Packages an already-staged content folder from its stage manifest. A dropped installer
    /// has no definition and no acquisition version, so the manifest path is the only handle
    /// on its staged content; everything downstream is the same per-target flow. The run's
    /// <paramref name="target"/> decides ConfigMgr, Intune or both; without one it is ConfigMgr.
    /// </summary>
    public async Task<PackagerExecutionResult> PackageStagedManifestAsync(
        string manifestPath, string comment, Action<string>? onOutputLine = null,
        CancellationToken ct = default, OnExistingBehavior onExisting = OnExistingBehavior.Skip,
        RunSnapshot? snapshot = null, string? target = null)
    {
        target = DeploymentTargets.Normalize(target ?? DeploymentTargets.Mecm);
        if (WsusTargetRefusal(target) is { } wsusRefusal) return wsusRefusal;
        if (!File.Exists(manifestPath))
            return new PackagerExecutionResult { ExitCode = 1, StdErr = $"Stage manifest not found: {manifestPath}. Run Stage first." };

        var settings = settingsService.Load();
        var manifest = new ManifestService().Read(manifestPath);
        var stagedContentPath = Path.GetDirectoryName(manifestPath)!;
        var signingPolicy = StageFinalizer.EffectivePolicy(snapshot, settings.ScriptSigning);

        var conflicts = new ConflictWatch();
        void Report(string line)
        {
            conflicts.Observe(line);
            if (onOutputLine is not null)
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => onOutputLine(line));
        }

        var mecmSucceeded = true;
        var mecmAlreadyExists = false;
        var stdOut = "";
        var stdErr = "";

        var afterMecm = "";
        if (!DeploymentTargets.SkipsMecm(target))
        {
            var mecmTarget = new MecmTarget();
            var result = await mecmTarget.PackageAsync(
                manifest, stagedContentPath, settings.SiteCode, settings.FileShareRoot, comment,
                settings.EstimatedRuntimeMins, settings.MaximumRuntimeMins,
                Report,
                conditionTemplates: new ConditionTemplateService().Load(warning => onOutputLine?.Invoke(warning)),
                providerMachine: settings.ProviderMachine,
                onExisting: onExisting,
                ct: ct,
                contentLayout: settings.ContentLayout,
                scriptSigning: MecmScriptSigningContext.FromManifest(manifest, signingPolicy),
                contentScope: ProfileContentScope.ForManifest(manifest),
                contentOptions: ContentOptions(settings, Report));

            mecmSucceeded = result.Success;
            mecmAlreadyExists = result.AlreadyExists;
            stdOut = result.Success ? $"Packaged: {result.ApplicationName}" : "";
            stdErr = result.ErrorMessage ?? "";
            RecordMecmPublication(manifest, result, settings, Report);
            if (result.Success && !result.AlreadyExists)
                afterMecm = await RunPostPackageStepsAsync(mecmTarget, result, manifest, stagedContentPath, settings, target, Report, ct);
        }
        else
        {
            Report("Intune only: skipping ConfigMgr application, site connection, and share copy.");
            stdOut = $"Staged content ready: {stagedContentPath}";
        }

        var intuneRequested = DeploymentTargets.PublishesToIntune(target);
        var notes = IntuneRunOutcome.NotRun;
        if (intuneRequested && mecmSucceeded)
            notes = await intuneDeploymentService.BuildAndPublishAsync(manifest, stagedContentPath, Report, comment, ct, signingPolicy);

        var outcome = PackagingOutcomeComposer.Compose(
            PackagingOutcomeComposer.MecmStatus(
                !DeploymentTargets.SkipsMecm(target), mecmSucceeded, mecmAlreadyExists),
            stdErr, intuneRequested, notes);

        return new PackagerExecutionResult
        {
            ExitCode = outcome.ExitCode,
            StdOut = stdOut,
            StdErr = outcome.StdErr,
            IntuneWinNote = notes.BuildNote.Length > 0 ? notes.BuildNote : afterMecm,
            IntunePublishNote = notes.PublishNote,
            Conflict = conflicts.Result
        };
    }

    /// <summary>Appends the ConfigMgr publication record of a snapshot build; other builds record nothing.</summary>
    private static void RecordMecmPublication(StageManifest manifest, PackagingResult result, AppSettings settings, Action<string> report) =>
        PublicationRecords.TryAppend(manifest, PublicationRecords.Mecm,
            !result.Success ? PublicationRecords.Failed : result.AlreadyExists ? PublicationRecords.Skipped : PublicationRecords.Succeeded,
            result.ApplicationName, settings.SiteCode, result.Success ? null : result.ErrorMessage, report);

    /// <summary>
    /// The optional steps that follow a new MECM application: content distribution to
    /// the configured DP group, a test-collection deployment once the content is
    /// targeted, and, for a run that does not publish to Intune, the stage's
    /// .intunewin stored beside the network content when the content prep tool is
    /// present. Each step reports its own outcome and none of them fails the run,
    /// because the application already exists. Returns the .intunewin note, or an
    /// empty string when none was stored.
    /// </summary>
    private async Task<string> RunPostPackageStepsAsync(
        MecmTarget mecmTarget, PackagingResult result, StageManifest manifest, string stagedContentPath,
        AppSettings settings, string target, Action<string> report, CancellationToken ct)
    {
        var distribution = settings.ContentDistribution;
        if (distribution.AutoDistribute)
        {
            if (string.IsNullOrWhiteSpace(distribution.DpGroupName))
            {
                report("Auto-distribute is on but no DP group is set; distribution skipped.");
            }
            else
            {
                var distributed = await mecmTarget.DistributeAsync(
                    result.ApplicationName, settings.SiteCode, distribution.DpGroupName.Trim(),
                    report, settings.ProviderMachine, ct);
                if (distributed && distribution.DeployToTestCollection && !string.IsNullOrWhiteSpace(distribution.TestCollectionName))
                {
                    await mecmTarget.DeployToTestCollectionAsync(
                        result.ApplicationName, settings.SiteCode, distribution.TestCollectionName.Trim(),
                        distribution.CreateTestCollectionIfMissing, report, settings.ProviderMachine, ct);
                }
            }
        }

        if (!DeploymentTargets.PublishesToIntune(target))
            return await intuneDeploymentService.BuildBesideContentAsync(manifest, stagedContentPath, result.NetworkContentPath, report, ct, settings.ContentLayout);

        return "";
    }

    /// <summary>
    /// Watches an output stream for the existing-application conflict marker.
    /// The marker is a line among many, so every reported line passes through
    /// here and only a match is retained.
    /// </summary>
    private sealed class ConflictWatch
    {
        public ExistingApplicationConflict? Result { get; private set; }

        public void Observe(string line)
        {
            if (Result is not null) return;
            if (OnExistingConflict.TryParse(line, out var app, out var version, out var incoming))
                Result = new ExistingApplicationConflict(app, version, incoming);
        }
    }

    // ── Process helpers ──────────────────────────────────────────

    private static Process CreateProcess(string arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        PackagerChildBridge.Apply(process.StartInfo, environment);
        return process;
    }

    private async Task<PackagerExecutionResult> RunStreamingAsync(
        string arguments, LogPaths logPaths, Action<string>? onOutputLine, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = CreateProcess(arguments, environment);
        process.Start();

        // Read stderr without the caller's token: abandoning it leaves the child blocked
        // on a full pipe, and terminating the child is what ends the stream.
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var outLines = new List<string>();

        string stderr;
        try
        {
            // Stream stdout line-by-line
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                ct.ThrowIfCancellationRequested();
                outLines.Add(line);
                if (onOutputLine is not null)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => onOutputLine(line));
                }
            }

            await process.WaitForExitAsync(ct);
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            // Disposing the handle ends the wait, not the script. A stage or package run
            // left behind would go on writing to the share long after the operator stopped.
            BoundedProcessRunner.Terminate(process);
            try { _ = await stderrTask; } catch (Exception) { }
            throw;
        }

        // Write log files
        try
        {
            var logDir = Path.GetDirectoryName(logPaths.OutLog);
            if (logDir is not null)
                Directory.CreateDirectory(logDir);

            await File.WriteAllLinesAsync(logPaths.OutLog, outLines, ct);
            if (!string.IsNullOrEmpty(stderr))
                await File.WriteAllTextAsync(logPaths.ErrLog, stderr, ct);
        }
        catch { /* Log write failure is non-fatal */ }

        return new PackagerExecutionResult
        {
            ExitCode = process.ExitCode,
            StdOut = string.Join(Environment.NewLine, outLines),
            StdErr = stderr,
            OutLogPath = logPaths.OutLog,
            ErrLogPath = logPaths.ErrLog,
            StructuredLogPath = logPaths.StructuredLog
        };
    }

    // ── Argument builders ────────────────────────────────────────

    private string BuildGetLatestArgs(PackagerInfo packager, AppSettings settings)
    {
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{packager.FullPath}\" -SiteCode \"{PackagerSiteCode(settings.SiteCode)}\" -GetLatestVersionOnly";

        if (SupportsFileServerPath(packager))
            args += $" -FileServerPath \"{settings.FileShareRoot}\"";
        if (!string.IsNullOrEmpty(settings.DownloadRoot))
            args += $" -DownloadRoot \"{settings.DownloadRoot}\"";
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args += $" -M365Channel \"{settings.M365Channel}\"";

        return args;
    }

    /// <summary>
    /// The site code handed to a PS1 packager's version check. The packagers bind
    /// -SiteCode as a mandatory string, which refuses an empty value; a check
    /// never reads the site, so a workstation without ConfigMgr passes the default.
    /// </summary>
    public static string PackagerSiteCode(string? siteCode) => string.IsNullOrWhiteSpace(siteCode) ? "MCM" : siteCode.Trim();

    private (string args, LogPaths logs) BuildStageArgs(PackagerInfo packager, AppSettings settings, string? profileDownloadRoot)
    {
        var logs = CreateLogPaths(packager, "stage", settings.DownloadRoot);
        var downloadRoot = profileDownloadRoot ?? settings.DownloadRoot;

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{packager.FullPath}\" -StageOnly -LogPath \"{logs.StructuredLog}\"";

        if (!string.IsNullOrEmpty(downloadRoot))
            args += $" -DownloadRoot \"{downloadRoot}\"";
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args += $" -M365Channel \"{settings.M365Channel}\"";

        return (args, logs);
    }

    private (string args, LogPaths logs) BuildPackageArgs(PackagerInfo packager, AppSettings settings, string comment, string? profileDownloadRoot)
    {
        var logs = CreateLogPaths(packager, "package", settings.DownloadRoot);
        var downloadRoot = profileDownloadRoot ?? settings.DownloadRoot;

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{packager.FullPath}\" -PackageOnly";
        args += $" -SiteCode \"{settings.SiteCode}\"";
        args += $" -Comment \"{comment}\"";
        args += $" -LogPath \"{logs.StructuredLog}\"";

        if (SupportsFileServerPath(packager))
            args += $" -FileServerPath \"{settings.FileShareRoot}\"";
        // Only the non-default layout is passed, so a script without the parameter still runs.
        if (ContentLayouts.IsFlat(settings.ContentLayout))
            args += " -ContentLayout Flat";
        if (!string.IsNullOrEmpty(downloadRoot))
            args += $" -DownloadRoot \"{downloadRoot}\"";
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args += $" -M365Channel \"{settings.M365Channel}\"";
        if (settings.EstimatedRuntimeMins > 0)
            args += $" -EstimatedRuntimeMins {settings.EstimatedRuntimeMins}";
        if (settings.MaximumRuntimeMins > 0)
            args += $" -MaximumRuntimeMins {settings.MaximumRuntimeMins}";

        return (args, logs);
    }

    /// <summary>
    /// Checks if a packager script accepts -FileServerPath by scanning the first 120 lines.
    /// </summary>
    private static bool SupportsFileServerPath(PackagerInfo packager)
    {
        try
        {
            var count = 0;
            foreach (var line in File.ReadLines(packager.FullPath))
            {
                if (++count > 120) break;
                if (line.Contains("$FileServerPath", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private sealed record LogPaths(string OutLog, string ErrLog, string StructuredLog);

    private static LogPaths CreateLogPaths(PackagerInfo packager, string phase, string downloadRoot)
    {
        var logDir = Path.Combine(
            string.IsNullOrEmpty(downloadRoot) ? Path.GetTempPath() : downloadRoot,
            "Logs");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = Path.GetFileNameWithoutExtension(packager.Script);

        return new LogPaths(
            OutLog: Path.Combine(logDir, $"{baseName}-{phase}-{stamp}.out.log"),
            ErrLog: Path.Combine(logDir, $"{baseName}-{phase}-{stamp}.err.log"),
            StructuredLog: Path.Combine(logDir, $"{baseName}-{phase}-{stamp}.structured.log")
        );
    }

    private static AppDefinition LoadDefinition(string path)
    {
        var json = File.ReadAllText(path);
        var def = JsonSerializer.Deserialize<AppDefinition>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to load definition: {path}");
        // The icon pack names its files after the definition file, which the
        // stage finds through SourcePath.
        def.SourcePath = path;
        return def;
    }
}
