using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

public class PackagerExecutionService(
    ISettingsReader<AppSettings> settingsService,
    StagingService stagingService,
    IntuneDeploymentService intuneDeploymentService,
    DeploymentConditionsService deploymentConditionsService,
    ApplicationInventoryService? inventory = null)
{
    private static readonly TimeSpan GetLatestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Runs a packager with -GetLatestVersionOnly. Routes JSON apps to engine DLL.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(
        IPackagerExecutionRequest packager, CancellationToken ct = default)
    {
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            return await stagingService.GetLatestVersionAsync(def, ct);
        }

        var settings = settingsService.Load();
        var args = BuildGetLatestArgs(packager, settings);

        // The shared runner drains both streams and terminates the child on cancellation
        // or timeout, including when stderr would otherwise fill its pipe.
        var run = await BoundedProcessRunner.RunAsync(ChildStartInfo(args, null), GetLatestTimeout, ct);
        ct.ThrowIfCancellationRequested();

        if (run.TimedOut || run.ExitCode != 0)
            return null;

        return VersionLine(run.StdOut);
    }

    /// <summary>
    /// The first standard-output line shaped like a version, or null when no line is one.
    /// A module the packager loads can write log lines before the version.
    /// </summary>
    public static string? VersionLine(string stdOut)
    {
        foreach (var line in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+(\.\d+){0,3}([+-]\d+)?$"))
                return trimmed;
        }
        return null;
    }

    /// <summary>The configured source that produced a latest-version lookup.</summary>
    public string VersionSource(IPackagerExecutionRequest packager)
    {
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            try
            {
                var scraper = LoadDefinition(packager.DefinitionPath).VersionScraper;
                return string.IsNullOrWhiteSpace(scraper.Url)
                    ? $"{scraper.Type}:{packager.Uid}"
                    : $"{scraper.Type}:{scraper.Url}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                // Keep a stable source label even when a definition became unreadable.
            }
        }
        return "packager:" + packager.Uid;
    }

    /// <summary>
    /// Runs a packager with -StageOnly, streaming stdout line-by-line via the callback.
    /// A snapshot this call creates records <paramref name="runTarget"/>, else the stored
    /// deployment target.
    /// </summary>
    public async Task<PackagerExecutionResult> StageAsync(
        IPackagerExecutionRequest packager, Action<string>? onOutputLine = null, CancellationToken ct = default,
        Dictionary<string, string>? settingsValues = null, RunSnapshot? snapshot = null, string? runTarget = null,
        string? operationId = null, bool recordInventory = true)
    {
        if (!recordInventory)
            return await StageCoreAsync(packager, onOutputLine, ct, settingsValues, snapshot, runTarget);

        var ownsOperation = operationId is null;
        operationId ??= Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var identity = InventoryIdentity(packager, snapshot, ReadInventoryManifest(packager, null, snapshot, out _));
        if (ownsOperation && inventory is not null)
            inventory.BeginOperation(new InventoryOperation(operationId, "Stage", packager.Uid, identity, runTarget, startedAt));

        var outcome = InventoryOutcome.Failed;
        string? detail = null;
        try
        {
            var result = await StageCoreAsync(packager, onOutputLine, ct, settingsValues, snapshot, runTarget);
            if (result.ExitCode == 0)
            {
                outcome = InventoryOutcome.Succeeded;
                RecordStagedBuild(packager, result, snapshot, operationId, onOutputLine);
            }
            else detail = result.StdErr;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = InventoryOutcome.Cancelled;
            detail = "Stage cancelled.";
            throw;
        }
        finally
        {
            if (ownsOperation && inventory is not null)
                CompleteInventoryOperation(operationId, outcome, detail, onOutputLine);
        }
    }

    private async Task<PackagerExecutionResult> StageCoreAsync(
        IPackagerExecutionRequest packager, Action<string>? onOutputLine, CancellationToken ct,
        Dictionary<string, string>? settingsValues, RunSnapshot? snapshot, string? runTarget)
    {
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            var appSettings = settingsService.Load();
            // A grid run builds from the application's active profile; the default
            // profile runs without a snapshot.
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
            var includeArchitectureVariants = snapshot is not null
                && !ApplicationIds.IsDefaultProfile(snapshot.ProfileId)
                    ? string.Equals(
                        WorkbenchFieldMapping.VariantSplitToDisplay(WorkbenchJson.Get(snapshot.Profile, "Variants.Split").Value),
                        "Architecture", StringComparison.OrdinalIgnoreCase)
                    : source.LegacyMapsApply
                        && string.Equals(deploymentConditionsService.GetConditions(def.Id)?.Split, "Architecture", StringComparison.OrdinalIgnoreCase);
            var inputs = new StageInputs
            {
                LocalSourceFolder = appSettings.LocalSourceFolders.TryGetValue(def.Id, out var folder) ? folder : null,
                SideFiles = appSettings.SideFiles,
                CitrixSwitches = appSettings.CitrixSwitches,
                IncludeArchitectureVariants = includeArchitectureVariants,
                RunSnapshot = snapshot,
                SigningPolicy = appSettings.ScriptSigning
            };
            var result = await stagingService.StageAsync(def, appSettings.DownloadRoot, onOutputLine, ct, knownVersion, settingsValues, installMode, inputs);
            if (result.Success)
            {
                packager.StagedVersion = Path.GetFileName(result.ContentPath);
                packager.LocalSourceStagedVersion = result.FromLocalSource ? packager.StagedVersion : null;
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
        try { StagingRootGuard.Prepare(settings.DownloadRoot); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or ArgumentException or NotSupportedException)
        {
            return new PackagerExecutionResult { ExitCode = 1, StdErr = ex.Message };
        }
        var bridge = PrepareChildBridge(packager, settings, onOutputLine, out var refusal, snapshot, runTarget);
        if (refusal is not null) return refusal;
        var (args, logPaths) = StageArguments(packager, settings, bridge?.DownloadRoot, bridge?.Snapshot?.BuildId);
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

    /// <summary>
    /// The refusal text when another account can change the staged content or its parent
    /// folder, which holds the .intunewin a publish reuses; null when both pass.
    /// </summary>
    private static string? StagedContentRefusal(string folder, bool withParent = true)
    {
        try
        {
            StagingRootGuard.Verify(folder);
            if (withParent && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))) is { Length: > 0 } parent)
                StagingRootGuard.Verify(parent);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
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
                OutputLineDispatch.Post(onOutputLine, line);
        }
        var note = await IntuneDeploymentService.BuildAtStageAsync(manifest, Path.GetDirectoryName(manifestPath)!, Report, ct);
        if (note.Length > 0) Report($"Intunewin: {note}");
    }

    /// <summary>
    /// The stage manifest a stage run produced: the JSON stage's own manifest, the one
    /// a PS1 packager logged, else the stage that carries the snapshot's BuildId.
    /// </summary>
    public string? StagedManifestPath(IPackagerExecutionRequest packager, PackagerExecutionResult stageResult, RunSnapshot? snapshot)
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
    /// The version folder a publish of the row uses: the version a local installer staged
    /// under in this session, else the latest version, else the last staged version. An
    /// older staged version is never published in place of a known latest version.
    /// </summary>
    public static string PublishVersion(IPackagerExecutionRequest packager) =>
        !string.IsNullOrWhiteSpace(packager.LocalSourceStagedVersion) ? packager.LocalSourceStagedVersion
        : !string.IsNullOrWhiteSpace(packager.LatestVersion) ? packager.LatestVersion
        : packager.StagedVersion ?? "";

    /// <summary>
    /// The stage a publish may reuse without staging: the manifest of the
    /// <see cref="PublishVersion"/> folder, and for a build sealed under a profile only while
    /// its build record is current (same version, profile revision and signing policy).
    /// Null when the row has none, the build is stale, or the row is a PS1 packager whose
    /// stage location only its own run reports.
    /// </summary>
    public string? ExistingStageManifest(IPackagerExecutionRequest packager)
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
            var version = PublishVersion(packager);
            if (version.Length == 0) return null;
            var path = Path.Combine(appRoot, version, "stage-manifest.json");
            if (!File.Exists(path)) return null;
            if (string.IsNullOrWhiteSpace(packager.LatestVersion) && string.IsNullOrWhiteSpace(packager.LocalSourceStagedVersion)) return path;
            var manifest = new ManifestService().Read(path);
            if (!StageFinalizer.CarriesBuild(manifest)) return path;
            return ResolveBuildPlan(packager) is { } plan
                   && BuildRecords.IsCurrent(plan.DataRoot, plan.ApplicationId, plan.ProfileId, version,
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
    public async Task<WsusStepResult> PublishWsusAsync(IPackagerExecutionRequest packager, PackagerExecutionResult stageResult, RunSnapshot? snapshot,
        ApplicationPackagerEngine.Services.Wsus.IWsusPublisher publisher, Action<string>? onOutputLine, CancellationToken ct,
        string? operationId = null, bool recordInventory = true)
    {
        if (!recordInventory)
            return await PublishWsusCoreAsync(packager, stageResult, snapshot, publisher, onOutputLine, ct);

        var ownsOperation = operationId is null;
        operationId ??= Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var identity = InventoryIdentity(packager, snapshot, ReadInventoryManifest(packager, stageResult, snapshot, out _));
        var settings = settingsService.Load();
        if (ownsOperation && inventory is not null)
            inventory.BeginOperation(new InventoryOperation(operationId, "Publish", packager.Uid, identity, "WSUS", startedAt));

        var outcome = InventoryOutcome.Failed;
        string? detail = null;
        try
        {
            var step = await PublishWsusCoreAsync(packager, stageResult, snapshot, publisher, onOutputLine, ct);
            outcome = RecordWsusOutcome(packager, stageResult, snapshot, settings, step, operationId, startedAt, onOutputLine);
            detail = step.Message;
            return step;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = InventoryOutcome.Cancelled;
            detail = "WSUS publish cancelled.";
            var manifest = ReadInventoryManifest(packager, stageResult, snapshot, out _);
            RecordDestination(packager, InventoryIdentity(packager, snapshot, manifest), settings, "WSUS",
                FirstNonBlank(manifest?.SoftwareVersion, PublishVersion(packager), packager.StagedVersion, "unknown"),
                InventoryOutcome.Cancelled, null, FirstNonBlank(manifest?.BuildId, snapshot?.BuildId),
                operationId, startedAt, detail, onOutputLine);
            throw;
        }
        finally
        {
            if (ownsOperation && inventory is not null)
                CompleteInventoryOperation(operationId, outcome, detail, onOutputLine);
        }
    }

    private async Task<WsusStepResult> PublishWsusCoreAsync(IPackagerExecutionRequest packager, PackagerExecutionResult stageResult, RunSnapshot? snapshot,
        ApplicationPackagerEngine.Services.Wsus.IWsusPublisher publisher, Action<string>? onOutputLine, CancellationToken ct)
    {
        var manifestPath = StagedManifestPath(packager, stageResult, snapshot);
        if (manifestPath is null)
            return new WsusStepResult(WsusStepStatus.Failed, "No stage manifest was found for this stage. Run Stage again.", [], []);
        if (StagedContentRefusal(Path.GetDirectoryName(manifestPath)!) is { } contentRefusal)
            return new WsusStepResult(WsusStepStatus.Failed, contentRefusal, [], []);
        StageManifest manifest;
        try { manifest = new ManifestService().Read(manifestPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new WsusStepResult(WsusStepStatus.Failed, $"The stage manifest {manifestPath} could not be read: {ex.Message}", [], []);
        }
        // A stage made before the manifest carried the exit codes of the definition gets them from the definition.
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            if (string.Equals(manifest.InstallerType, "EXE", StringComparison.OrdinalIgnoreCase) && manifest.RebootExitCodes is null && manifest.SuccessExitCodes is null)
            {
                try { ApplicationPackagerEngine.Services.Wsus.WsusPublishService.CarryExitCodes(manifest, LoadDefinition(packager.DefinitionPath)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    return new WsusStepResult(WsusStepStatus.Failed,
                        $"The definition {packager.DefinitionPath} could not be read, so its installer exit codes could not be added to the update: {ex.Message}", [], []);
                }
            }
        }
        void Report(string line)
        {
            if (onOutputLine is not null)
                OutputLineDispatch.Post(onOutputLine, line);
        }
        var settings = settingsService.Load();
        var supportUrl = string.IsNullOrWhiteSpace(packager.VendorUrl) ? null : packager.VendorUrl;
        return await ApplicationPackagerEngine.Services.Wsus.WsusPublishStep.RunAsync(
            publisher, manifest, Path.GetDirectoryName(manifestPath)!, settings.Wsus, Report, ct, supportUrl);
    }

    private void RecordStagedBuild(IPackagerExecutionRequest row, PackagerExecutionResult result, RunSnapshot? snapshot,
        string operationId, Action<string>? onOutputLine)
    {
        if (inventory is null) return;
        try
        {
            var manifest = ReadInventoryManifest(row, result, snapshot, out var manifestPath);
            if (manifest is null || string.IsNullOrWhiteSpace(manifestPath)
                || string.IsNullOrWhiteSpace(manifest.BuildId) || string.IsNullOrWhiteSpace(manifest.ApplicationId)) return;

            var profileId = string.IsNullOrWhiteSpace(manifest.ProfileId) ? ApplicationIds.DefaultProfileId : manifest.ProfileId;
            var dataRoot = !string.IsNullOrWhiteSpace(snapshot?.DataRoot)
                ? snapshot.DataRoot
                : WorkbenchStoreOrNull(null)?.DataRoot ?? WorkbenchPaths.ResolveDataRoot(create: false);
            var buildRecordPath = Path.Combine(BuildRecords.GetBuildFolder(dataRoot, manifest.ApplicationId, profileId, manifest.BuildId), BuildRecords.FileName);
            if (!File.Exists(buildRecordPath)) return;

            var identity = new InventoryApplicationIdentity(row.Uid, manifest.ApplicationId, profileId, manifest.ProfileRevision);
            inventory.RecordStagedBuild(new InventoryStagedBuild(identity, manifest.SoftwareVersion, manifest.BuildId,
                buildRecordPath, manifestPath, DateTimeOffset.UtcNow, operationId));
        }
        catch (Exception ex)
        {
            onOutputLine?.Invoke("Inventory stage record failed: " + ex.Message);
        }
    }

    /// <summary>Records a successful stage performed by a caller that must use a specialized staging pipeline.</summary>
    public void RecordExternalStage(IPackagerExecutionRequest row, PackagerExecutionResult result, RunSnapshot snapshot,
        Action<string>? onOutputLine = null)
    {
        if (inventory is null) return;
        var operationId = Guid.NewGuid().ToString("N");
        var at = DateTimeOffset.UtcNow;
        var identity = InventoryIdentity(row, snapshot, ReadInventoryManifest(row, result, snapshot, out _));
        var began = false;
        try
        {
            inventory.BeginOperation(new InventoryOperation(operationId, "Stage", row.Uid, identity, snapshot.Target, at));
            began = true;
            if (result.ExitCode == 0)
                RecordStagedBuild(row, result, snapshot, operationId, onOutputLine);
            CompleteInventoryOperation(operationId, result.ExitCode == 0 ? InventoryOutcome.Succeeded : InventoryOutcome.Failed,
                result.StdErr, onOutputLine);
        }
        catch (Exception ex)
        {
            if (began)
            {
                CompleteInventoryOperation(operationId, InventoryOutcome.Failed, ex.Message, onOutputLine);
            }
            onOutputLine?.Invoke("Inventory stage record failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Records the destination outcomes of a package result as a package run does; a ConfigMgr
    /// publish links the record to the CI_ID and ModelName the run read back.
    /// </summary>
    public void RecordPackageOutcomes(IPackagerExecutionRequest row, RunSnapshot? snapshot, string target,
        PackagerExecutionResult result, Action<string>? onOutputLine = null) =>
        PersistPackageOutcomes(row, snapshot, DeploymentTargets.Normalize(target), result, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, onOutputLine);

    private void PersistPackageOutcomes(IPackagerExecutionRequest row, RunSnapshot? snapshot, string target,
        PackagerExecutionResult result, string operationId, DateTimeOffset at, Action<string>? onOutputLine)
    {
        if (inventory is null) return;
        var settings = settingsService.Load();
        var manifest = ReadInventoryManifest(row, null, snapshot, out _);
        var version = FirstNonBlank(result.PublishedVersion, manifest?.SoftwareVersion, PublishVersion(row), row.StagedVersion, "unknown");
        var buildId = FirstNonBlank(result.PublishedBuildId, manifest?.BuildId, snapshot?.BuildId);
        var identity = InventoryIdentity(row, snapshot, manifest);

        if (!DeploymentTargets.SkipsMecm(target))
        {
            var status = result.ConfigMgrOutcome ?? (result.Conflict is not null && result.ExitCode == 0 ? InventoryOutcome.Skipped
                : result.ExitCode == 0 ? InventoryOutcome.Succeeded : InventoryOutcome.Failed);
            RecordDestination(row, identity, settings, "ConfigMgr", version, status,
                result.ConfigMgrObjectId ?? (result.Conflict is null ? ObjectId(result.StdOut, "Packaged: ") : result.Conflict.ApplicationName),
                buildId, operationId, at, result.ConfigMgrDetail ?? result.StdErr, onOutputLine, result.ConfigMgrCiId, result.ConfigMgrModelName);
        }

        if (DeploymentTargets.PublishesToIntune(target))
        {
            var status = result.IntuneOutcome ?? (result.ExitCode == 0 ? InventoryOutcome.Succeeded : InventoryOutcome.Failed);
            RecordDestination(row, identity, settings, "Intune", version, status,
                result.IntuneObjectId ?? IntuneAppId(result.IntunePublishNote), buildId, operationId, at,
                result.IntuneDetail ?? result.IntunePublishNote ?? result.StdErr, onOutputLine);
        }
    }

    private void RecordPackageAttemptOutcomes(IPackagerExecutionRequest row, RunSnapshot? snapshot, string target,
        string operationId, DateTimeOffset at, InventoryOutcome outcome, string detail)
    {
        if (inventory is null) return;
        var settings = settingsService.Load();
        var manifest = ReadInventoryManifest(row, null, snapshot, out _);
        var version = FirstNonBlank(manifest?.SoftwareVersion, PublishVersion(row), row.StagedVersion, "unknown");
        var buildId = FirstNonBlank(manifest?.BuildId, snapshot?.BuildId);
        var identity = InventoryIdentity(row, snapshot, manifest);
        if (!DeploymentTargets.SkipsMecm(target))
            RecordDestination(row, identity, settings, "ConfigMgr", version, outcome, null, buildId, operationId, at, detail, null);
        if (DeploymentTargets.PublishesToIntune(target))
            RecordDestination(row, identity, settings, "Intune", version, outcome, null, buildId, operationId, at, detail, null);
    }

    private InventoryOutcome RecordWsusOutcome(IPackagerExecutionRequest row, PackagerExecutionResult stageResult, RunSnapshot? snapshot,
        AppSettings settings, WsusStepResult step, string operationId, DateTimeOffset at, Action<string>? onOutputLine)
    {
        var manifest = ReadInventoryManifest(row, stageResult, snapshot, out _);
        var outcome = step.Status switch
        {
            WsusStepStatus.Published => InventoryOutcome.Succeeded,
            WsusStepStatus.AlreadyPublished => InventoryOutcome.AlreadyPresent,
            WsusStepStatus.Refused => InventoryOutcome.Refused,
            WsusStepStatus.NotRun => InventoryOutcome.Skipped,
            _ => InventoryOutcome.Failed
        };
        var identity = InventoryIdentity(row, snapshot, manifest);
        RecordDestination(row, identity, settings, "WSUS",
            FirstNonBlank(manifest?.SoftwareVersion, PublishVersion(row), row.StagedVersion, "unknown"), outcome,
            step.PackageId, FirstNonBlank(manifest?.BuildId, snapshot?.BuildId), operationId, at, step.Message, onOutputLine);
        return outcome;
    }

    private void RecordDestination(IPackagerExecutionRequest row, InventoryApplicationIdentity? identity, AppSettings settings,
        string destination, string version, InventoryOutcome outcome, string? objectId, string? buildId,
        string operationId, DateTimeOffset at, string? detail, Action<string>? onOutputLine, string? configMgrCiId = null, string? configMgrModelName = null)
    {
        if (inventory is null) return;
        try
        {
            var environment = ApplicationInventoryProjection.Environments(settings)
                .First(item => string.Equals(item.Destination, destination, StringComparison.OrdinalIgnoreCase)).Environment;
            inventory.RecordDestinationOutcome(new InventoryDestinationOutcome(row.Uid, identity, environment, "Publish",
                version, outcome, objectId, at, buildId, operationId, detail, configMgrCiId, configMgrModelName));
        }
        catch (Exception ex)
        {
            onOutputLine?.Invoke($"Inventory {destination} record failed: {ex.Message}");
        }
    }

    private void CompleteInventoryOperation(string operationId, InventoryOutcome outcome, string? detail, Action<string>? onOutputLine)
    {
        try { inventory?.CompleteOperation(operationId, outcome, DateTimeOffset.UtcNow, detail); }
        catch (Exception ex) { onOutputLine?.Invoke($"Inventory operation completion failed: {ex.Message}"); }
    }

    private InventoryApplicationIdentity InventoryIdentity(IPackagerExecutionRequest row, RunSnapshot? snapshot, StageManifest? manifest)
    {
        if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.ApplicationId))
            return new InventoryApplicationIdentity(row.Uid, manifest.ApplicationId,
                string.IsNullOrWhiteSpace(manifest.ProfileId) ? ApplicationIds.DefaultProfileId : manifest.ProfileId,
                manifest.ProfileRevision);
        if (snapshot is not null)
            return new InventoryApplicationIdentity(row.Uid, snapshot.ApplicationId, snapshot.ProfileId, snapshot.ProfileRevision);
        return ApplicationInventoryProjection.IdentityFor(row, ResolveBuildPlan(row));
    }

    private StageManifest? ReadInventoryManifest(IPackagerExecutionRequest row, PackagerExecutionResult? result,
        RunSnapshot? snapshot, out string? manifestPath)
    {
        manifestPath = result is not null ? StagedManifestPath(row, result, snapshot)
            : snapshot is not null ? StagedManifestPath(row, new PackagerExecutionResult { ExitCode = 0 }, snapshot)
            : row.StagedManifestPath is { } dropped && File.Exists(dropped) ? dropped : ExistingStageManifest(row);
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return null;
        try { return new ManifestService().Read(manifestPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static InventoryOutcome DestinationSummary(PackagerExecutionResult result, string target)
    {
        var outcomes = new List<InventoryOutcome>();
        if (!DeploymentTargets.SkipsMecm(target)) outcomes.Add(result.ConfigMgrOutcome ?? InventoryOutcome.Failed);
        if (DeploymentTargets.PublishesToIntune(target)) outcomes.Add(result.IntuneOutcome ?? InventoryOutcome.Failed);
        return PackagingOutcomeComposer.SummarizeDestinations(outcomes);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string? ObjectId(string text, string prefix) =>
        text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();

    private static string? IntuneAppId(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var marker = "app id ";
        var index = note.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var id = note[(index + marker.Length)..].Trim();
        var end = id.IndexOfAny([')', ' ', '\r', '\n']);
        return end < 0 ? id : id[..end];
    }

    private static InventoryOutcome IntuneOutcome(IntuneRunOutcome outcome) => outcome.Status switch
    {
        IntuneStepStatus.Published => InventoryOutcome.Succeeded,
        IntuneStepStatus.PublishRefused => InventoryOutcome.Refused,
        IntuneStepStatus.BuildFailed or IntuneStepStatus.PublishFailed => InventoryOutcome.Failed,
        _ => InventoryOutcome.Skipped
    };

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
            OutputLineDispatch.Post(onOutputLine, message);
    }

    /// <summary>
    /// Runs the Package phase for the run's target: the MECM application, an Intune Win32
    /// publish, or both. Without a target the run publishes to ConfigMgr; the stored
    /// deployment target never decides a publish.
    /// </summary>
    public async Task<PackagerExecutionResult> PackageAsync(
        IPackagerExecutionRequest packager, string comment, Action<string>? onOutputLine = null,
        CancellationToken ct = default, OnExistingBehavior onExisting = OnExistingBehavior.Skip,
        RunSnapshot? snapshot = null, string? targetOverride = null, string? operationId = null, bool recordInventory = true)
    {
        if (!recordInventory)
            return await PackageCoreAsync(packager, comment, onOutputLine, ct, onExisting, snapshot,
                DeploymentTargets.Normalize(targetOverride ?? DeploymentTargets.Mecm));

        var target = DeploymentTargets.Normalize(targetOverride ?? DeploymentTargets.Mecm);
        var ownsOperation = operationId is null;
        operationId ??= Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var identity = InventoryIdentity(packager, snapshot, ReadInventoryManifest(packager, null, snapshot, out _));
        if (ownsOperation && inventory is not null)
            inventory.BeginOperation(new InventoryOperation(operationId, "Package", packager.Uid, identity, target, startedAt));

        var outcome = InventoryOutcome.Failed;
        string? detail = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await PackageCoreAsync(packager, comment, onOutputLine, ct, onExisting, snapshot, target);
            PersistPackageOutcomes(packager, snapshot, target, result, operationId, startedAt, onOutputLine);
            outcome = result.Conflict is not null && result.ExitCode == 0 ? InventoryOutcome.Skipped
                : result.ExitCode == 0 ? InventoryOutcome.Succeeded
                : DestinationSummary(result, target);
            detail = result.StdErr;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = InventoryOutcome.Cancelled;
            detail = "Package cancelled.";
            RecordPackageAttemptOutcomes(packager, snapshot, target, operationId, startedAt, InventoryOutcome.Cancelled, detail);
            throw;
        }
        catch (Exception ex)
        {
            outcome = InventoryOutcome.Failed;
            detail = ex.Message;
            RecordPackageAttemptOutcomes(packager, snapshot, target, operationId, startedAt, InventoryOutcome.Failed, detail);
            throw;
        }
        finally
        {
            if (ownsOperation && inventory is not null)
                CompleteInventoryOperation(operationId, outcome, detail, onOutputLine);
        }
    }

    private async Task<PackagerExecutionResult> PackageCoreAsync(
        IPackagerExecutionRequest packager, string comment, Action<string>? onOutputLine,
        CancellationToken ct, OnExistingBehavior onExisting, RunSnapshot? snapshot, string target)
    {
        if (WsusTargetRefusal(target) is { } wsusRefusal) return wsusRefusal;
        if (packager.StagedManifestPath is not null)
            return await PackageStagedManifestAsync(packager.StagedManifestPath, comment, onOutputLine, ct, onExisting, snapshot, target);

        var settings = settingsService.Load();

        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            var source = ProfileSource.Resolve(WorkbenchStoreOrNull(snapshot), def.Id);
            if (snapshot is null && source.Failure is not null)
                return new PackagerExecutionResult { ExitCode = 1, StdErr = source.RefusalMessage() };
            // A grid publish packages the stage of the active profile, under that profile's root.
            var appRoot = Path.Combine(ProfileContentScope.LocalDownloadRoot(settings.DownloadRoot, snapshot?.ProfileId ?? source.ActiveProfileId),
                $"{def.Application.Replace(" ", "")}-{def.Architecture}");
            // With a latest version known, only its stage publishes, or the stage a local
            // installer made in this session: an older staged version published as the
            // latest would carry the wrong release to every endpoint.
            var version = PublishVersion(packager);
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

            var stagedContentPath = Path.GetDirectoryName(manifestPath)!;
            if (StagedContentRefusal(stagedContentPath) is { } contentRefusal)
                return new PackagerExecutionResult { ExitCode = 1, StdErr = contentRefusal };
            var manifestService = new ApplicationPackagerEngine.Services.ManifestService();
            var manifest = manifestService.Read(manifestPath);

            var conflicts = new ConflictWatch();
            void Report(string line)
            {
                conflicts.Observe(line);
                if (onOutputLine is not null)
                    OutputLineDispatch.Post(onOutputLine, line);
            }

            var mecmSucceeded = true;
            var mecmAlreadyExists = false;
            var stdOut = "";
            var stdErr = "";
            ApplicationPackagerEngine.Services.PackagingResult? mecmResult = null;

            // Operator selections resolve before any target runs: a variant split rewrites
            // the manifest into gated deployment types, a plain selection stays flat rules
            // on the single deployment type.
            // A build of a non-default profile already carries its profile's conditions,
            // commands and title mode; the saved per-app settings apply only where
            // ProfileSource leaves them in force.
            var profiled = !ProfileSource.LegacyMapsApplyTo(manifest, source);
            var conditions = profiled ? null : deploymentConditionsService.GetConditions(def.Id);
            if (!profiled && manifest.OptionalArchitecturePayloadsStaged == true
                && !string.Equals(conditions?.Split, "Architecture", StringComparison.OrdinalIgnoreCase))
                return new PackagerExecutionResult
                {
                    ExitCode = 1,
                    StdErr = "This stage includes the optional ARM64 payload, and the Architecture split is no longer selected. Re-stage before publishing."
                };
            DeploymentConditionsService.ApplyVariantSplit(manifest, conditions, stagedContentPath, Report,
                definitionDeclaresOptionalArchitecturePayloads: def.PayloadVariants is { Length: > 0 });
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
            string? contentFailure = null;
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

                mecmResult = result;
                mecmSucceeded = result.Success;
                mecmAlreadyExists = result.AlreadyExists;
                stdOut = result.Success ? $"Packaged: {result.ApplicationName}" : "";
                stdErr = result.ErrorMessage ?? "";
                RecordMecmPublication(manifest, result, settings, Report);
                if (result.Success && !result.AlreadyExists)
                    (afterMecm, contentFailure) = await RunPostPackageStepsAsync(mecmTarget, result, manifest, stagedContentPath, settings, target, Report, ct);
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
                stdErr, intuneRequested, notes, contentFailure);

            return new PackagerExecutionResult
            {
                ExitCode = outcome.ExitCode,
                StdOut = stdOut,
                StdErr = outcome.StdErr,
                IntuneWinNote = notes.BuildNote.Length > 0 ? notes.BuildNote : afterMecm,
                IntunePublishNote = notes.PublishNote,
                Conflict = conflicts.Result,
                ConfigMgrOutcome = PackagingOutcomeComposer.ConfigMgrInventoryOutcome(
                    !DeploymentTargets.SkipsMecm(target), mecmSucceeded, mecmAlreadyExists, contentFailure),
                ConfigMgrObjectId = stdOut.StartsWith("Packaged: ", StringComparison.OrdinalIgnoreCase) ? stdOut[10..].Trim() : null,
                ConfigMgrDetail = FirstNonBlank(stdErr, contentFailure),
                ConfigMgrCiId = NonBlank(mecmResult?.CiId),
                ConfigMgrModelName = NonBlank(mecmResult?.ModelName),
                IntuneOutcome = !intuneRequested ? null : IntuneOutcome(notes),
                IntuneObjectId = IntuneAppId(notes.PublishNote),
                IntuneDetail = FirstNonBlank(notes.FailureNote, outcome.StdErr),
                PublishedVersion = manifest.SoftwareVersion,
                PublishedBuildId = manifest.BuildId
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
                IntunePublishNote = "skipped; PS1 packagers publish to ConfigMgr only.",
                IntuneOutcome = InventoryOutcome.Refused,
                IntuneDetail = "PS1 packagers publish to ConfigMgr only."
            };
        }
        if (DeploymentTargets.PublishesToIntune(target))
            onOutputLine?.Invoke("Intune publishing applies to JSON-defined apps; this PS1 packager runs the ConfigMgr flow only.");

        if (StagedContentRefusal(settings.DownloadRoot, withParent: false) is { } rootRefusal)
            return new PackagerExecutionResult { ExitCode = 1, StdErr = rootRefusal };
        var bridge = PrepareChildBridge(packager, settings, onOutputLine, out var refusal, snapshot, target);
        if (refusal is not null) return refusal;
        var (args, logPaths) = PackageArguments(packager, settings, comment, bridge?.DownloadRoot, bridge?.Snapshot?.BuildId);
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
                Conflict = new ExistingApplicationConflict(app, version, incoming),
                ConfigMgrOutcome = packaged.ExitCode == 0 ? InventoryOutcome.Skipped : InventoryOutcome.Failed,
                ConfigMgrObjectId = app,
                ConfigMgrDetail = packaged.StdErr,
                IntuneOutcome = DeploymentTargets.PublishesToIntune(target) ? InventoryOutcome.Refused : null,
                IntuneDetail = DeploymentTargets.PublishesToIntune(target) ? "PS1 packagers publish to ConfigMgr only." : null,
                PublishedVersion = PublishVersion(packager),
                PublishedBuildId = bridge?.Snapshot?.BuildId ?? snapshot?.BuildId
            };
        }
        return new PackagerExecutionResult
        {
            ExitCode = packaged.ExitCode,
            StdOut = packaged.StdOut,
            StdErr = packaged.StdErr,
            OutLogPath = packaged.OutLogPath,
            ErrLogPath = packaged.ErrLogPath,
            StructuredLogPath = packaged.StructuredLogPath,
            ConfigMgrOutcome = packaged.ExitCode == 0 ? InventoryOutcome.Succeeded : InventoryOutcome.Failed,
            ConfigMgrObjectId = ObjectId(packaged.StdOut, "Packaged: "),
            ConfigMgrDetail = packaged.StdErr,
            IntuneOutcome = DeploymentTargets.PublishesToIntune(target) ? InventoryOutcome.Refused : null,
            IntuneDetail = DeploymentTargets.PublishesToIntune(target) ? "PS1 packagers publish to ConfigMgr only." : null,
            PublishedVersion = PublishVersion(packager),
            PublishedBuildId = bridge?.Snapshot?.BuildId ?? snapshot?.BuildId
        };
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
    /// failure runs the child without the bridge.
    /// </summary>
    private static PackagerChildLaunch? PrepareChildBridge(
        IPackagerExecutionRequest packager, AppSettings settings, Action<string>? onOutputLine, out PackagerExecutionResult? refusal,
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
                OutputLineDispatch.Post(onOutputLine, line);
            return null;
        }
    }

    /// <summary>The saved condition templates, or null (the built-in ones) when the file cannot be read.</summary>
    private static ConditionTemplateDocument? ConditionTemplatesOrNull()
    {
        try { return new ConditionTemplateService().Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The Intune compatibility findings for the stage a Package run would publish, with
    /// the operator's variant split and requirement selections applied as the run applies
    /// them. Null when the row has no readable staged manifest.
    /// </summary>
    public IReadOnlyList<IntuneCompatibilityFinding>? ReviewIntuneCompatibility(IPackagerExecutionRequest packager)
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
                manifestPath = Path.Combine(appRoot, PublishVersion(packager), "stage-manifest.json");
                if (!File.Exists(manifestPath) && !string.IsNullOrWhiteSpace(packager.StagedVersion))
                    manifestPath = Path.Combine(appRoot, packager.StagedVersion, "stage-manifest.json");
            }
            if (manifestPath is null || !File.Exists(manifestPath)) return null;

            var manifest = new ManifestService().Read(manifestPath);
            if (def is null || source is null || !ProfileSource.LegacyMapsApplyTo(manifest, source))
                return IntuneCompatibilityFindings.Build(manifest, null, ConditionTemplatesOrNull(), settingsService.Load().Intune.AcceptedRequirementDrops);
            var conditions = deploymentConditionsService.GetConditions(def.Id);
            DeploymentConditionsService.ApplyVariantSplit(manifest, conditions, Path.GetDirectoryName(manifestPath)!,
                definitionDeclaresOptionalArchitecturePayloads: def.PayloadVariants is { Length: > 0 });
            return IntuneCompatibilityFindings.Build(manifest, DeploymentConditionsService.BuildRequirements(conditions),
                ConditionTemplatesOrNull(), settingsService.Load().Intune.AcceptedRequirementDrops);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The workbench identity a One Click build of the row is sealed under.</summary>
    public sealed record OneClickBuildPlan(string DataRoot, string ApplicationId, string ProfileId, int ProfileRevision);

    /// <summary>The row's application, active profile and its revision; null when the workbench cannot resolve them.</summary>
    public OneClickBuildPlan? ResolveBuildPlan(IPackagerExecutionRequest packager)
    {
        try
        {
            var applicationId = WorkbenchApplicationCatalog.ApplicationIdFor(packager);
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
    public RunSnapshot? CreateRowSnapshot(IPackagerExecutionRequest packager, OneClickBuildPlan plan, string target, out string? refusal)
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
        var stagedContentPath = Path.GetDirectoryName(manifestPath)!;
        if (StagedContentRefusal(stagedContentPath) is { } contentRefusal)
            return new PackagerExecutionResult { ExitCode = 1, StdErr = contentRefusal };
        var manifest = new ManifestService().Read(manifestPath);
        var signingPolicy = StageFinalizer.EffectivePolicy(snapshot, settings.ScriptSigning);

        var conflicts = new ConflictWatch();
        void Report(string line)
        {
            conflicts.Observe(line);
            if (onOutputLine is not null)
                OutputLineDispatch.Post(onOutputLine, line);
        }

        var mecmSucceeded = true;
        var mecmAlreadyExists = false;
        var stdOut = "";
        var stdErr = "";
        PackagingResult? mecmResult = null;

        var afterMecm = "";
            string? contentFailure = null;
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

            mecmResult = result;
            mecmSucceeded = result.Success;
            mecmAlreadyExists = result.AlreadyExists;
            stdOut = result.Success ? $"Packaged: {result.ApplicationName}" : "";
            stdErr = result.ErrorMessage ?? "";
            RecordMecmPublication(manifest, result, settings, Report);
            if (result.Success && !result.AlreadyExists)
                (afterMecm, contentFailure) = await RunPostPackageStepsAsync(mecmTarget, result, manifest, stagedContentPath, settings, target, Report, ct);
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
            stdErr, intuneRequested, notes, contentFailure);

        return new PackagerExecutionResult
        {
            ExitCode = outcome.ExitCode,
            StdOut = stdOut,
            StdErr = outcome.StdErr,
            IntuneWinNote = notes.BuildNote.Length > 0 ? notes.BuildNote : afterMecm,
            IntunePublishNote = notes.PublishNote,
            Conflict = conflicts.Result,
            ConfigMgrOutcome = PackagingOutcomeComposer.ConfigMgrInventoryOutcome(
                !DeploymentTargets.SkipsMecm(target), mecmSucceeded, mecmAlreadyExists, contentFailure),
            ConfigMgrObjectId = stdOut.StartsWith("Packaged: ", StringComparison.OrdinalIgnoreCase) ? stdOut[10..].Trim() : null,
            ConfigMgrDetail = FirstNonBlank(stdErr, contentFailure),
            ConfigMgrCiId = NonBlank(mecmResult?.CiId),
            ConfigMgrModelName = NonBlank(mecmResult?.ModelName),
            IntuneOutcome = !intuneRequested ? null : IntuneOutcome(notes),
            IntuneObjectId = IntuneAppId(notes.PublishNote),
            IntuneDetail = FirstNonBlank(notes.FailureNote, outcome.StdErr),
            PublishedVersion = manifest.SoftwareVersion,
            PublishedBuildId = manifest.BuildId
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
    /// present. Each step reports its own outcome in the log. A distribution or a test
    /// deployment that fails does not undo the application, but it fails the run: the
    /// returned ContentFailure is the sentence for it, and null when no step failed. Also
    /// returns the .intunewin note, or an empty string when none was stored.
    /// </summary>
    private async Task<(string IntuneNote, string? ContentFailure)> RunPostPackageStepsAsync(
        MecmTarget mecmTarget, PackagingResult result, StageManifest manifest, string stagedContentPath,
        AppSettings settings, string target, Action<string> report, CancellationToken ct)
    {
        var distribution = settings.ContentDistribution;
        string? contentFailure = null;
        if (distribution.AutoDistribute)
        {
            if (string.IsNullOrWhiteSpace(distribution.DpGroupName))
            {
                report("Auto-distribute is on but no DP group is set; distribution skipped.");
            }
            else
            {
                var deployToTest = distribution.DeployToTestCollection && !string.IsNullOrWhiteSpace(distribution.TestCollectionName);
                contentFailure = await MecmTarget.RunContentStepsAsync(
                    () => mecmTarget.DistributeAsync(
                        result.ApplicationName, settings.SiteCode, distribution.DpGroupName.Trim(),
                        report, settings.ProviderMachine, ct),
                    deployToTest
                        ? () => mecmTarget.DeployToTestCollectionAsync(
                            result.ApplicationName, settings.SiteCode, distribution.TestCollectionName.Trim(),
                            distribution.CreateTestCollectionIfMissing, report, settings.ProviderMachine, ct,
                            userCollection: distribution.TestCollectionIsUser)
                        : null);
            }
        }

        if (!DeploymentTargets.PublishesToIntune(target))
            return (await intuneDeploymentService.BuildBesideContentAsync(manifest, stagedContentPath, result.NetworkContentPath, report, ct, settings.ContentLayout), contentFailure);

        return ("", contentFailure);
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

    /// <summary>
    /// The start of one PS1 packager child. Every argument goes through ArgumentList, so a
    /// value with quotes or a trailing backslash arrives as one intact argument. Standard
    /// input is redirected and closed at start: a packager that prompts reads end of input
    /// and fails instead of waiting on a console nobody watches.
    /// </summary>
    public static ProcessStartInfo ChildStartInfo(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        WindowsPowerShellHost.ApplyLogonModulePath(startInfo);
        PackagerChildBridge.Apply(startInfo, environment);
        return startInfo;
    }

    private async Task<PackagerExecutionResult> RunStreamingAsync(
        IReadOnlyList<string> arguments, LogPaths logPaths, Action<string>? onOutputLine, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process { StartInfo = ChildStartInfo(arguments, environment) };
        process.Start();
        process.StandardInput.Close();

        // Read stderr without the caller's token: abandoning it leaves the child blocked
        // on a full pipe, and terminating the child is what ends the stream.
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var outLines = new List<string>();

        string stderr;
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                ct.ThrowIfCancellationRequested();
                outLines.Add(line);
                if (onOutputLine is not null)
                {
                    await OutputLineDispatch.SendAsync(onOutputLine, line);
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

    private static List<string> BuildGetLatestArgs(IPackagerExecutionRequest packager, AppSettings settings)
    {
        var args = new List<string>
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", packager.FullPath,
            "-SiteCode", PackagerSiteCode(settings.SiteCode), "-GetLatestVersionOnly"
        };

        if (SupportsFileServerPath(packager))
            args.AddRange(["-FileServerPath", settings.FileShareRoot ?? ""]);
        if (!string.IsNullOrEmpty(settings.DownloadRoot))
            args.AddRange(["-DownloadRoot", settings.DownloadRoot]);
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args.AddRange(["-M365Channel", settings.M365Channel]);

        return args;
    }

    /// <summary>
    /// The site code handed to a PS1 packager's version check. The packagers bind
    /// -SiteCode as a mandatory string, which refuses an empty value; a check
    /// never reads the site, so a workstation without ConfigMgr passes the default.
    /// </summary>
    public static string PackagerSiteCode(string? siteCode) => string.IsNullOrWhiteSpace(siteCode) ? "MCM" : siteCode.Trim();

    /// <summary>The -StageOnly arguments of a PS1 packager child and its log paths, named with the build id when there is one.</summary>
    public static (IReadOnlyList<string> Arguments, LogPaths Logs) StageArguments(
        IPackagerExecutionRequest packager, AppSettings settings, string? profileDownloadRoot, string? buildId)
    {
        var logs = CreateLogPaths(packager, "stage", settings.DownloadRoot, buildId);
        var downloadRoot = profileDownloadRoot ?? settings.DownloadRoot;

        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", packager.FullPath, "-StageOnly", "-LogPath", logs.StructuredLog };

        if (!string.IsNullOrEmpty(downloadRoot))
            args.AddRange(["-DownloadRoot", downloadRoot]);
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args.AddRange(["-M365Channel", settings.M365Channel]);

        return (args, logs);
    }

    /// <summary>The -PackageOnly arguments of a PS1 packager child and its log paths, named with the build id when there is one.</summary>
    public static (IReadOnlyList<string> Arguments, LogPaths Logs) PackageArguments(
        IPackagerExecutionRequest packager, AppSettings settings, string comment, string? profileDownloadRoot, string? buildId)
    {
        var logs = CreateLogPaths(packager, "package", settings.DownloadRoot, buildId);
        var downloadRoot = profileDownloadRoot ?? settings.DownloadRoot;

        var args = new List<string>
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", packager.FullPath, "-PackageOnly",
            "-SiteCode", settings.SiteCode ?? "", "-Comment", comment ?? "", "-LogPath", logs.StructuredLog
        };

        if (SupportsFileServerPath(packager))
            args.AddRange(["-FileServerPath", settings.FileShareRoot ?? ""]);
        // Only the non-default layout is passed, so a script without the parameter still runs.
        if (ContentLayouts.IsFlat(settings.ContentLayout))
            args.AddRange(["-ContentLayout", "Flat"]);
        if (!string.IsNullOrEmpty(downloadRoot))
            args.AddRange(["-DownloadRoot", downloadRoot]);
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args.AddRange(["-M365Channel", settings.M365Channel]);
        if (settings.EstimatedRuntimeMins > 0)
            args.AddRange(["-EstimatedRuntimeMins", settings.EstimatedRuntimeMins.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (settings.MaximumRuntimeMins > 0)
            args.AddRange(["-MaximumRuntimeMins", settings.MaximumRuntimeMins.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        return (args, logs);
    }

    /// <summary>
    /// Checks if a packager script accepts -FileServerPath by scanning the first 120 lines.
    /// </summary>
    private static bool SupportsFileServerPath(IPackagerExecutionRequest packager)
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

    public sealed record LogPaths(string OutLog, string ErrLog, string StructuredLog);

    /// <summary>
    /// The log files of one child run. Two runs of one script and phase in the same second
    /// would share a name, so the name carries the build id, or a random suffix for a run
    /// without one.
    /// </summary>
    private static LogPaths CreateLogPaths(IPackagerExecutionRequest packager, string phase, string downloadRoot, string? buildId)
    {
        var logDir = Path.Combine(
            string.IsNullOrEmpty(downloadRoot) ? Path.GetTempPath() : downloadRoot,
            "Logs");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var run = !string.IsNullOrWhiteSpace(buildId) && ApplicationIds.IsPlainFolderName(buildId) && buildId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            ? buildId
            : Guid.NewGuid().ToString("N")[..8];
        var baseName = $"{Path.GetFileNameWithoutExtension(packager.Script)}-{phase}-{stamp}-{run}";

        return new LogPaths(
            OutLog: Path.Combine(logDir, $"{baseName}.out.log"),
            ErrLog: Path.Combine(logDir, $"{baseName}.err.log"),
            StructuredLog: Path.Combine(logDir, $"{baseName}.structured.log")
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
