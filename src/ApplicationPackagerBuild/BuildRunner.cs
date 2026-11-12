using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerBuild;

/// <summary>One phase of a build and how it ended.</summary>
public sealed record BuildPhase(string Phase, int ExitCode, string Detail);

/// <summary>The WSUS half of a build, as the command line reports it.</summary>
public sealed record BuildWsusOutcome(bool Ok, string PackageId, string Outcome, IReadOnlyList<string> Warnings, string Message);

/// <summary>What one build did; the exit code is the process exit code.</summary>
public sealed class BuildResult
{
    public string ApplicationId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string BuildId { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string Target { get; set; } = "";
    public string DownloadRoot { get; set; } = "";
    public List<BuildPhase> Phases { get; } = [];
    public BuildWsusOutcome? Wsus { get; set; }
    public int ExitCode { get; set; }

    public IEnumerable<string> SummaryLines()
    {
        yield return $"ApplicationId : {ApplicationId}";
        yield return $"ProfileId     : {ProfileId}";
        yield return $"BuildId       : {BuildId}";
        yield return $"Snapshot      : {Snapshot}";
        yield return $"Target        : {Target}";
        yield return $"DownloadRoot  : {DownloadRoot}";
        yield return $"Phases        : {(Phases.Count == 0 ? "none" : string.Join("; ", Phases.Select(p => $"{p.Phase} exit {p.ExitCode}")))}";
        yield return Wsus is null
            ? "Wsus          : not run"
            : $"Wsus          : {Wsus.Outcome}{(Wsus.PackageId.Length > 0 ? $", package {Wsus.PackageId}" : "")}"
              + (Wsus.Warnings.Count > 0 ? $", warnings: {string.Join(" | ", Wsus.Warnings)}" : "");
        yield return $"ExitCode      : {ExitCode}";
    }
}

/// <summary>
/// Runs one command-line build through the services the window uses: the application
/// and profile resolve from the arguments, Stage writes a run
/// snapshot through the workbench store and stages through the execution service (a BYO
/// application through the ad-hoc stage), and Package publishes to each destination of
/// the target through the publish-run service, ConfigMgr first. Nothing prompts: every
/// decision comes from an argument or a saved setting, or the run stops with a message
/// naming the argument.
/// </summary>
public sealed class BuildRunner(
    IRunSettingsReader<AppSettings> settingsService,
    PackagerDiscoveryService discoveryService,
    WorkbenchApplicationCatalog applicationCatalog,
    PackagerExecutionService executionService,
    PublishRunService publishRuns,
    StagingService stagingService,
    DroppedInstallerAnalysisService dropAnalysisService,
    IBuildOutput output)
{
    private const string ConfigMgr = "ConfigMgr";
    private const string Intune = "Intune";
    private const string Wsus = "WSUS";

    private BuildOptions _options = new();
    private string? _localSourceDefinitionId;

    /// <summary>The publish destinations of a target, in run order.</summary>
    public static IReadOnlyList<string> Destinations(string target) => RunTargets.Normalize(target) switch
    {
        RunTargets.Mecm => [ConfigMgr],
        RunTargets.MecmAndIntune => [ConfigMgr, Intune],
        RunTargets.IntuneOnly => [Intune],
        RunTargets.MecmAndWsus => [ConfigMgr, Wsus],
        RunTargets.WsusOnly => [Wsus],
        _ => []
    };

    /// <summary>The run overrides of the arguments, applied over every settings load of this process and never saved.</summary>
    public static void ApplyOverrides(AppSettings settings, BuildOptions options, string? localSourceDefinitionId = null)
    {
        if (options.DownloadRoot is { } downloadRoot) settings.DownloadRoot = downloadRoot;
        if (options.SiteCode is { } siteCode) settings.SiteCode = siteCode;
        if (options.ProviderMachineName is { } provider) settings.ProviderMachine = provider;
        if (options.FileServerPath is { } share) settings.FileShareRoot = share;
        if (options.PackagersRoot is { } packagers) settings.PackagersRoot = packagers;
        if (options.EstimatedMinutes is { } estimated) settings.EstimatedRuntimeMins = estimated;
        if (options.MaximumMinutes is { } maximum) settings.MaximumRuntimeMins = maximum;
        settings.Wsus = WsusSettingsResolver.Resolve(settings.Wsus, options);
        if (options.LocalSourceFolder is { } folder && localSourceDefinitionId is not null)
            settings.LocalSourceFolders[localSourceDefinitionId] = folder;
    }

    /// <summary>The run overrides the snapshot records: timing, and the pinned version of a run that stages one.</summary>
    public static JsonObject RunOverrides(BuildOptions options, bool pinVersion)
    {
        var overrides = new JsonObject();
        if (options.EstimatedMinutes is { } estimated) overrides["EstimatedMinutes"] = estimated;
        if (options.MaximumMinutes is { } maximum) overrides["MaximumMinutes"] = maximum;
        if (pinVersion && !string.IsNullOrWhiteSpace(options.Version)) overrides["PinnedVersion"] = options.Version;
        return overrides;
    }

    public async Task<BuildResult> RunAsync(BuildOptions options, CancellationToken ct)
    {
        _options = options;
        var result = new BuildResult { Target = options.Target };
        settingsService.ApplyRunOverrides(s => ApplyOverrides(s, _options, _localSourceDefinitionId));
        settingsService.ReadFileOncePerRun();
        try
        {
            settingsService.Load();
            if (settingsService.TakeLoadWarning() is { } warning) output.Error(warning);
            await RunCoreAsync(result, ct);
        }
        catch (BuildRefusal ex)
        {
            output.Error(ex.Message);
            result.ExitCode = 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            output.Error("Cancelled.");
            result.ExitCode = 1;
        }
        return result;
    }

    private async Task RunCoreAsync(BuildResult result, CancellationToken ct)
    {
        var o = _options;
        var settings = settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.DownloadRoot))
            throw new BuildRefusal("DownloadRoot is not configured: pass --download-root or set it in Settings.");

        var store = WorkbenchStore.Open();
        var discovered = store.DiscoverApplications(applicationCatalog.Roots());
        var applicationId = ApplicationResolver.Resolve(o.Application, discovered);
        result.ApplicationId = applicationId;
        var profileId = Refuse(() => ProfileResolver.Resolve(store, applicationId, o.Profile));
        result.ProfileId = profileId;
        result.DownloadRoot = WorkbenchPaths.GetProfileDownloadRoot(settings.DownloadRoot, profileId);

        var isByo = ApplicationIds.IsByo(applicationId);
        PackagerInfo? row = null;
        if (isByo) Refuse(() => store.ResolveByoSource(applicationId, o.Version));
        else row = await FindRowAsync(applicationId, discovered, ct);

        var destinations = o.Package ? Destinations(o.Target) : [];
        var stageThisRun = o.Stage || destinations.Contains(Wsus);
        if (row is { IsJsonDefined: true } && stageThisRun && o.Version is not null)
            throw new BuildRefusal("--version does not apply to staging a definition: a definition stages its latest version. Use --version with --package alone to publish the sealed build of that version.");

        if (row is not null && LoadDefinitionOrNull(row) is { LocalSource.IsRequired: true } definition)
        {
            _localSourceDefinitionId = definition.Id;
            var kept = LocalSourceFolderPrompt.Confirm([row], LoadDefinitionOrNull, settingsService.Load(), _ => null, output.Info, out _);
            if (kept.Count == 0)
                throw new BuildRefusal($"{row.Application} stages from a local installer folder, and no folder exists for it: pass --local-source-folder <folder>, or set the folder in Settings.");
        }

        if (o.Package && destinations.Count == 0)
            output.Info($"Package skipped: target {o.Target} publishes to no destination.");
        CheckGates(destinations, settingsService.Load());

        RunSnapshot? snapshot = null;
        PackagerExecutionResult? stage = null;
        if (stageThisRun)
        {
            (snapshot, row, stage) = await StageAsync(store, applicationId, profileId, row, result, ct);
            if (stage.ExitCode != 0) return;
        }
        else if (destinations.Count > 0)
        {
            (snapshot, row) = isByo
                ? await ByoPackageSourceAsync(store, applicationId, profileId, result, ct)
                : (SealedBuildSnapshot(store, applicationId, profileId, result), row);
            if (row is null) return;
        }

        foreach (var destination in destinations)
        {
            var exitCode = destination == Wsus
                ? await PublishWsusAsync(row!, snapshot, result, ct)
                : await PackageAsync(row!, destination, snapshot, result, ct);
            if (exitCode != 0) return;
        }
    }

    private async Task<PackagerInfo> FindRowAsync(string applicationId, IReadOnlyList<WorkbenchApplication> discovered, CancellationToken ct)
    {
        var source = ApplicationResolver.Source(applicationId, discovered);
        if (source?.SourcePath is null)
        {
            var roots = applicationCatalog.Roots();
            var searched = string.Join(", ", new[] { roots.CatalogDefinitionsRoot, roots.CatalogOverridesRoot, roots.PackagersRoot, roots.CustomDefinitionsRoot ?? WorkbenchStore.Open().CustomDefinitionsRoot }
                .Where(r => !string.IsNullOrWhiteSpace(r)));
            throw new BuildRefusal($"No definition or packager script for application '{applicationId}' under {searched}.");
        }
        if (!source.Runnable) throw new BuildRefusal($"Application '{applicationId}' is not runnable: {source.SourcePath}");

        if (source.SourcePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var definition = Refuse(() => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(source.SourcePath), DefinitionJson))
                             ?? throw new BuildRefusal($"Failed to load definition: {source.SourcePath}");
            definition.SourcePath = source.SourcePath;
            definition.SourceOrigin = source.Origin;
            return PackagerDiscoveryService.DefinitionRow(definition, source.SourcePath, source.Origin);
        }

        var scripts = await discoveryService.DiscoverAllAsync(ct);
        return scripts.FirstOrDefault(p => !p.IsJsonDefined && string.Equals(p.FullPath, source.SourcePath, StringComparison.OrdinalIgnoreCase))
               ?? throw new BuildRefusal($"The packager script {source.SourcePath} has no UID header, so the App Packager page does not list it.");
    }

    private static readonly JsonSerializerOptions DefinitionJson = new() { PropertyNameCaseInsensitive = true };

    private static AppDefinition? LoadDefinitionOrNull(PackagerInfo p)
    {
        if (!p.IsJsonDefined || string.IsNullOrEmpty(p.DefinitionPath) || !File.Exists(p.DefinitionPath)) return null;
        try { return JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(p.DefinitionPath), DefinitionJson); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Every destination's gate runs before anything stages, as the publish buttons gate a
    /// run: ConfigMgr needs the console, the site code and the share, Intune its
    /// credentials, WSUS a valid server and the administration API.
    /// </summary>
    private void CheckGates(IReadOnlyList<string> destinations, AppSettings settings)
    {
        if (GateRefusal(destinations, settings, WsusProbe) is { } refusal) throw new BuildRefusal(refusal);
    }

    /// <summary>Reads whether this computer has the WSUS administration API; replaceable so the gate is testable either way.</summary>
    public Func<WsusApiStatus> WsusProbe { get; set; } = WsusApiProbe.Probe;

    /// <summary>The first destination gate that refuses, or null when every destination may run.</summary>
    public static string? GateRefusal(IReadOnlyList<string> destinations, AppSettings settings, Func<WsusApiStatus> wsusProbe)
    {
        foreach (var destination in destinations)
        {
            switch (destination)
            {
                case ConfigMgr:
                    if (PublishRunService.RunGate(PublishRunTargets.ConfigMgr, settings) is { } mecmBlocked) return mecmBlocked;
                    break;
                case Intune:
                    if (PublishRunService.RunGate(PublishRunTargets.IntuneOnly, settings) is { } intuneBlocked) return intuneBlocked;
                    break;
                default:
                    if (!settings.Wsus.IsConfigured)
                        return "No WSUS server: pass --wsus-server or set the server in Settings, WSUS Publishing.";
                    try { settings.Wsus.Normalize(); }
                    catch (WsusSettingsException ex) { return ex.Message; }
                    var api = wsusProbe();
                    if (!api.Available) return $"WSUS publishing is unavailable: {api.Reason}";
                    break;
            }
        }
        return null;
    }

    private async Task<(RunSnapshot? Snapshot, PackagerInfo? Row, PackagerExecutionResult Stage)> StageAsync(
        WorkbenchStore store, string applicationId, string profileId, PackagerInfo? row, BuildResult result, CancellationToken ct)
    {
        var settings = settingsService.Load();
        var isByo = row is null;
        var snapshot = Refuse(() => WorkbenchRunSnapshots.Create(store, settings, applicationId, profileId, _options.Target,
            isByo ? null : row!.IsJsonDefined ? row.DefinitionPath : row.FullPath,
            RunOverrides(_options, pinVersion: true)), "Run snapshot not available: ");
        result.BuildId = snapshot.BuildId;
        result.Snapshot = snapshot.Path ?? "";
        var profileName = WorkbenchJson.AsText(snapshot.Profile["Name"]) ?? snapshot.ProfileId;
        output.Info($"Build: {applicationId} (profile {profileName}, revision {snapshot.ProfileRevision}, build {snapshot.BuildId})");

        if (isByo)
        {
            var (byoRow, staged) = await StageByoAsync(store, applicationId, snapshot, settings, result, ct);
            return (snapshot, byoRow, staged);
        }

        output.Info($"Stage: {row!.Application} ({row.Script})");
        var stage = await executionService.StageAsync(row, line => output.Info($"  {line}"), ct, null, snapshot, _options.Target);
        result.Phases.Add(new BuildPhase("Stage", stage.ExitCode, stage.StdOut));
        if (stage.ExitCode == 0)
        {
            output.Info($"Staged: {stage.StdOut}");
        }
        else
        {
            output.Error($"Stage FAILED (exit {stage.ExitCode})");
            WriteStdErr(stage.StdErr);
            result.ExitCode = stage.ExitCode;
        }
        return (snapshot, row, stage);
    }

    private async Task<(PackagerInfo? Row, PackagerExecutionResult Stage)> StageByoAsync(
        WorkbenchStore store, string applicationId, RunSnapshot snapshot, AppSettings settings, BuildResult result, CancellationToken ct)
    {
        var plan = await RefuseAsync(() => ByoStaging.PlanAsync(WorkbenchStore.Open(snapshot.DataRoot), dropAnalysisService, applicationId,
            _options.Version, deepInspect: true, ct), "Stage refused: ");
        output.Info($"Stage: {plan.Manifest.AppName} {plan.Manifest.SoftwareVersion} (source revision {plan.Source.Revision}, {plan.Source.FileName})");
        var inputs = new StageInputs { RunSnapshot = snapshot, SigningPolicy = settings.ScriptSigning };
        var staged = await ByoStaging.StageAsync(stagingService, plan, settings.DownloadRoot, line => output.Info($"  {line}"), ct, inputs);
        var pendingRow = WorkbenchRunSnapshots.ByoRow(plan.Manifest, plan.Analysis, "", applicationId);
        if (!staged.Success)
        {
            output.Error($"Stage FAILED: {staged.ErrorMessage}");
            result.Phases.Add(new BuildPhase("Stage", 1, staged.ErrorMessage ?? ""));
            result.ExitCode = 1;
            executionService.RecordExternalStage(pendingRow,
                new PackagerExecutionResult { ExitCode = 1, StdErr = staged.ErrorMessage ?? "Stage failed." }, snapshot, output.Info);
            return (null, new PackagerExecutionResult { ExitCode = 1, StdErr = staged.ErrorMessage ?? "" });
        }
        output.Info($"Staged: {staged.ContentPath}");
        result.Phases.Add(new BuildPhase("Stage", 0, staged.ContentPath));
        var row = WorkbenchRunSnapshots.ByoRow(plan.Manifest, plan.Analysis, staged.ManifestPath, applicationId);
        var stageResult = new PackagerExecutionResult { ExitCode = 0, StdOut = $"Staged to: {staged.ContentPath}", OutLogPath = staged.ManifestPath };
        executionService.RecordExternalStage(row, stageResult, snapshot, output.Info);
        return (row, stageResult);
    }

    /// <summary>
    /// The snapshot of the newest sealed build of the profile, or of the newest one of
    /// --version: Package consumes an exact BuildId, as the Workbench Package does.
    /// </summary>
    private RunSnapshot SealedBuildSnapshot(WorkbenchStore store, string applicationId, string profileId, BuildResult result)
    {
        var record = SelectSealedBuild(Refuse(() => BuildRecords.List(store.DataRoot, applicationId, profileId)), _options.Version);
        if (record is null)
            throw new BuildRefusal(_options.Version is null
                ? WorkbenchBuildLauncher.NoSealedBuildMessage
                : $"No sealed build of version '{_options.Version}' exists for this profile. Stage it first; Package consumes an exact build id, not the newest manifest on disk.");

        var buildId = WorkbenchJson.Get(record, "BuildId").Text ?? "";
        RefuseTimingOverride(buildId, record["Timing"]?.Deserialize<StageTiming>());
        var snapshot = Refuse(() => WorkbenchStore.ReadRunSnapshot(Path.Combine(store.RunsRoot, buildId + ".json"))
                                    ?? throw new FileNotFoundException($"Run snapshot for build '{buildId}' was not found."));
        result.BuildId = snapshot.BuildId;
        result.Snapshot = snapshot.Path ?? "";
        output.Info($"Package: build {buildId} ({WorkbenchJson.Get(record, "SoftwareVersion").Text}, sealed {WorkbenchJson.Get(record, "SealedAt").Text})");
        return snapshot;
    }

    /// <summary>
    /// A sealed build publishes with the runtime minutes its manifest records, which win
    /// over any caller value. A package-only run that passes minutes for such a build is
    /// refused, so the minutes are never dropped without notice.
    /// </summary>
    private void RefuseTimingOverride(string buildId, StageTiming? timing)
    {
        if (_options.EstimatedMinutes is null && _options.MaximumMinutes is null) return;
        if (timing is null || (timing.EstimatedMinutes is null && timing.MaximumMinutes is null)) return;
        throw new BuildRefusal(
            $"--estimated-minutes and --maximum-minutes do not apply to build {buildId}: its sealed manifest records " +
            $"estimated {Minutes(timing.EstimatedMinutes)} and maximum {Minutes(timing.MaximumMinutes)} minutes. Stage with them, or leave them out.");

        static string Minutes(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no";
    }

    /// <summary>The newest record of the list (newest BuildId first), or the newest whose SoftwareVersion is <paramref name="version"/>.</summary>
    public static JsonObject? SelectSealedBuild(IReadOnlyList<JsonObject> records, string? version) =>
        version is null
            ? records.FirstOrDefault()
            : records.FirstOrDefault(r => string.Equals(WorkbenchJson.Get(r, "SoftwareVersion").Text, version, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A BYO package without Stage reuses the manifest the last stage of this revision
    /// left while it is still current, and stages first otherwise.
    /// </summary>
    private async Task<(RunSnapshot? Snapshot, PackagerInfo? Row)> ByoPackageSourceAsync(
        WorkbenchStore store, string applicationId, string profileId, BuildResult result, CancellationToken ct)
    {
        var settings = settingsService.Load();
        var plan = await RefuseAsync(() => ByoStaging.PlanAsync(store, dropAnalysisService, applicationId, _options.Version, deepInspect: true, ct), "Package refused: ");
        var current = new ByoStageCurrency(Refuse(() => store.GetProfile(applicationId, profileId)).Revision, settings.ScriptSigning.PolicyDigest);
        var manifestPath = ByoStaging.FindPriorStageManifest(plan, settings.DownloadRoot, profileId, _options.Target, current);
        if (manifestPath is not null)
        {
            output.Info($"Package: {plan.Manifest.AppName} reuses the prior stage {manifestPath}");
            var manifest = new ManifestService().Read(manifestPath);
            RefuseTimingOverride(manifest.BuildId ?? "", manifest.Timing);
            result.BuildId = manifest.BuildId ?? "";
            return (null, WorkbenchRunSnapshots.ByoRow(plan.Manifest, plan.Analysis, manifestPath, applicationId));
        }
        var (snapshot, row, stage) = await StageAsync(store, applicationId, profileId, null, result, ct);
        return stage.ExitCode == 0 ? (snapshot, row) : (snapshot, null);
    }

    private async Task<int> PackageAsync(PackagerInfo row, string destination, RunSnapshot? snapshot, BuildResult result, CancellationToken ct)
    {
        var target = destination == Intune ? PublishRunTargets.IntuneOnly : PublishRunTargets.ConfigMgr;
        var label = destination == Intune ? "Publish to Intune" : "Publish to ConfigMgr";
        output.Info($"{label}: {row.Application} ({row.Script})");
        var onExisting = destination == Intune ? OnExistingBehavior.Skip : _options.OnExisting;
        executionService.CommandLineSupersedence = _options.VersionRule is null && _options.Supersedence is null
            ? null
            : new ApplicationPackagerEngine.Services.Supersedence.SupersedenceCommandLine(_options.VersionRule, _options.Supersedence);
        var packaged = await executionService.PackageAsync(row, _options.Comment, line => output.Info($"  {line}"), ct, onExisting, snapshot, target);

        if (packaged.Conflict is { } conflict && packaged.ExitCode == 0)
            output.Info($"Existing application kept: {conflict.ApplicationName} v{conflict.Version}");
        else if (packaged.ExitCode == 0)
            output.Info(PublishRunService.PackagedLine(packaged));
        else
        {
            output.Error($"Package FAILED (exit {packaged.ExitCode})");
            WriteStdErr(packaged.StdErr);
        }
        PublishRunService.LogIntuneNotes(row, packaged, output.Info);

        result.Phases.Add(new BuildPhase($"Package {destination}", packaged.ExitCode, PublishRunService.RowStatus(target, packaged.ExitCode)));
        if (packaged.ExitCode != 0) result.ExitCode = packaged.ExitCode;
        return packaged.ExitCode;
    }

    private async Task<int> PublishWsusAsync(PackagerInfo row, RunSnapshot? snapshot, BuildResult result, CancellationToken ct)
    {
        output.Info($"Publish to WSUS: {row.Application} ({row.Script})");
        WsusRowRun run;
        try
        {
            run = await publishRuns.PublishWsusRowAsync(row, snapshot, output.Info, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or ArgumentException or JsonException)
        {
            output.Error($"ERROR: {ex.Message}");
            result.Wsus = new BuildWsusOutcome(false, "", "Failed", [], ex.Message);
            result.ExitCode = 1;
            return 1;
        }
        if (run.StageFailed)
        {
            output.Error($"Stage FAILED (exit {run.Stage.ExitCode})");
            WriteStdErr(run.Stage.StdErr);
            result.Wsus = new BuildWsusOutcome(false, "", "Failed", [], run.Stage.StdErr.Trim());
            result.ExitCode = 1;
            return 1;
        }

        var step = run.Step!;
        result.Wsus = new BuildWsusOutcome(step.Ok, step.PackageId, step.Status.ToString(), step.Warnings, step.Message);
        foreach (var warning in step.Warnings) output.Error($"WSUS warning: {warning}");
        if (step.Refused) output.Error($"WSUS: {step.NotSupportedReason}");
        else if (!step.Ok) output.Error($"WSUS publish failed: {step.Message}");
        // A failed approval or decline leaves the update published; the run still reports it through the exit code.
        var exitCode = step.Ok && step.Warnings.Count == 0 ? 0 : 1;
        result.Phases.Add(new BuildPhase("Package WSUS", exitCode, run.Outcome!.Status));
        if (exitCode != 0) result.ExitCode = 1;
        return exitCode;
    }

    private void WriteStdErr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return;
        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            output.Error($"  {line.TrimEnd('\r')}");
    }

    private static T Refuse<T>(Func<T> action, string prefix = "")
    {
        try { return action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or ArgumentException or JsonException or InstallerAnalysisException)
        {
            throw new BuildRefusal(prefix + ex.Message);
        }
    }

    private static async Task<T> RefuseAsync<T>(Func<Task<T>> action, string prefix = "")
    {
        try { return await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or ArgumentException or JsonException or InstallerAnalysisException)
        {
            throw new BuildRefusal(prefix + ex.Message);
        }
    }
}
