using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
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
    /// </summary>
    public async Task<PackagerExecutionResult> StageAsync(
        PackagerInfo packager, Action<string>? onOutputLine = null, CancellationToken ct = default,
        Dictionary<string, string>? settingsValues = null)
    {
        // Route JSON-defined apps through the engine DLL
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            var appSettings = settingsService.Load();
            // Reuse version from Check Latest to avoid redundant API calls (GitHub rate limiting)
            var knownVersion = !string.IsNullOrEmpty(packager.LatestVersion) ? packager.LatestVersion : null;
            // The operator's Install for choice reaches staging here; an entry
            // for a definition that no longer declares the modes is ignored.
            var installMode = def.OffersInstallModes
                ? deploymentConditionsService.GetConditions(def.Id)?.InstallMode
                : null;
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
            var result = await stagingService.StageAsync(def, appSettings.DownloadRoot, onOutputLine, ct, knownVersion, settingsValues, installMode);
            return new PackagerExecutionResult
            {
                ExitCode = result.Success ? 0 : 1,
                StdOut = result.Success ? $"Staged to: {result.ContentPath}" : "",
                StdErr = result.ErrorMessage ?? "",
                OutLogPath = result.ManifestPath
            };
        }

        var settings = settingsService.Load();
        var (args, logPaths) = BuildStageArgs(packager, settings);
        return await RunStreamingAsync(args, logPaths, onOutputLine, ct);
    }

    /// <summary>
    /// Runs the Package phase for the configured deployment target: the MECM application,
    /// an Intune Win32 publish, or both.
    /// </summary>
    public async Task<PackagerExecutionResult> PackageAsync(
        PackagerInfo packager, string comment, Action<string>? onOutputLine = null,
        CancellationToken ct = default, OnExistingBehavior onExisting = OnExistingBehavior.Skip)
    {
        if (packager.StagedManifestPath is not null)
            return await PackageStagedManifestAsync(packager.StagedManifestPath, comment, onOutputLine, ct, onExisting);

        var settings = settingsService.Load();
        var target = DeploymentTargets.Normalize(settings.Intune.DeploymentTarget);

        // Route JSON-defined apps through the engine's MecmTarget
        if (packager.IsJsonDefined && packager.DefinitionPath is not null)
        {
            var def = LoadDefinition(packager.DefinitionPath);
            var manifestPath = Path.Combine(settings.DownloadRoot,
                $"{def.Application.Replace(" ", "")}-{def.Architecture}",
                packager.LatestVersion, "stage-manifest.json");

            if (!File.Exists(manifestPath))
                return new PackagerExecutionResult { ExitCode = 1, StdErr = $"Stage manifest not found: {manifestPath}. Run Stage first." };

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
            var conditions = deploymentConditionsService.GetConditions(def.Id);
            DeploymentConditionsService.ApplyVariantSplit(manifest, conditions, stagedContentPath, Report);
            var requirements = DeploymentConditionsService.BuildRequirements(conditions);
            // Recorded on the manifest, not just handed to MECM: the Intune publish reads
            // its command lines from the manifest, so a saved override that never lands
            // there publishes install.bat and uninstall.bat instead.
            var commandOverrides = ApplicationPackagerEngine.Services.DeploymentTypeSpecResolver
                .ResolveCommandOverrides(manifest, deploymentConditionsService.GetCommandOverrides(def.Id));
            var applicationTitle = ApplicationTitle.Resolve(
                manifest.AppName, manifest.SoftwareVersion, ApplicationTitle.Parse(conditions?.TitleMode));
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
                    applicationTitle: applicationTitle);

                mecmSucceeded = result.Success;
                mecmAlreadyExists = result.AlreadyExists;
                stdOut = result.Success ? $"Packaged: {result.ApplicationName}" : "";
                stdErr = result.ErrorMessage ?? "";
                if (result.Success && !result.AlreadyExists)
                    afterMecm = await RunPostPackageStepsAsync(mecmTarget, result, manifest, stagedContentPath, settings, target, Report, ct);
            }
            else
            {
                Report("Intune only: skipping MECM application, site connection, and share copy.");
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
                    manifest, stagedContentPath, Report, comment, ct);
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
                IntunePublishNote = "skipped; PS1 packagers publish to MECM only."
            };
        }
        if (DeploymentTargets.PublishesToIntune(target))
            onOutputLine?.Invoke("Intune publishing applies to JSON-defined apps; this PS1 packager runs the MECM flow only.");

        var (args, logPaths) = BuildPackageArgs(packager, settings, comment);
        return await RunStreamingAsync(args, logPaths, onOutputLine, ct);
    }

    // ── Ad-hoc drop: manifest-direct packaging ───────────────────

    /// <summary>
    /// Packages an already-staged content folder from its stage manifest. A dropped installer
    /// has no definition and no acquisition version, so the manifest path is the only handle
    /// on its staged content; everything downstream is the same per-target flow.
    /// </summary>
    public async Task<PackagerExecutionResult> PackageStagedManifestAsync(
        string manifestPath, string comment, Action<string>? onOutputLine = null,
        CancellationToken ct = default, OnExistingBehavior onExisting = OnExistingBehavior.Skip)
    {
        if (!File.Exists(manifestPath))
            return new PackagerExecutionResult { ExitCode = 1, StdErr = $"Stage manifest not found: {manifestPath}. Run Stage first." };

        var settings = settingsService.Load();
        var target = DeploymentTargets.Normalize(settings.Intune.DeploymentTarget);
        var manifest = new ManifestService().Read(manifestPath);
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
                contentLayout: settings.ContentLayout);

            mecmSucceeded = result.Success;
            mecmAlreadyExists = result.AlreadyExists;
            stdOut = result.Success ? $"Packaged: {result.ApplicationName}" : "";
            stdErr = result.ErrorMessage ?? "";
            if (result.Success && !result.AlreadyExists)
                afterMecm = await RunPostPackageStepsAsync(mecmTarget, result, manifest, stagedContentPath, settings, target, Report, ct);
        }
        else
        {
            Report("Intune only: skipping MECM application, site connection, and share copy.");
            stdOut = $"Staged content ready: {stagedContentPath}";
        }

        var intuneRequested = DeploymentTargets.PublishesToIntune(target);
        var notes = IntuneRunOutcome.NotRun;
        if (intuneRequested && mecmSucceeded)
            notes = await intuneDeploymentService.BuildAndPublishAsync(manifest, stagedContentPath, Report, comment, ct);

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

    /// <summary>
    /// The optional steps that follow a new MECM application: content distribution to
    /// the configured DP group, a test-collection deployment once the content is
    /// targeted, and, for an MECM-only target that asked for it, a .intunewin stored
    /// beside the network content. Each step reports its own outcome and none of
    /// them fails the run, because the application already exists. Returns the
    /// .intunewin note, or an empty string when none was built.
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

        if (settings.Intune.CreateIntuneWin && !DeploymentTargets.PublishesToIntune(target))
            return await intuneDeploymentService.BuildBesideContentAsync(manifest, stagedContentPath, result.NetworkContentPath, report, ct);

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

    private static Process CreateProcess(string arguments)
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
        return process;
    }

    private async Task<PackagerExecutionResult> RunStreamingAsync(
        string arguments, LogPaths logPaths, Action<string>? onOutputLine, CancellationToken ct)
    {
        using var process = CreateProcess(arguments);
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
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{packager.FullPath}\" -SiteCode \"{settings.SiteCode}\" -GetLatestVersionOnly";

        if (SupportsFileServerPath(packager))
            args += $" -FileServerPath \"{settings.FileShareRoot}\"";
        if (!string.IsNullOrEmpty(settings.DownloadRoot))
            args += $" -DownloadRoot \"{settings.DownloadRoot}\"";
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args += $" -M365Channel \"{settings.M365Channel}\"";

        return args;
    }

    private (string args, LogPaths logs) BuildStageArgs(PackagerInfo packager, AppSettings settings)
    {
        var logs = CreateLogPaths(packager, "stage", settings.DownloadRoot);

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{packager.FullPath}\" -StageOnly -LogPath \"{logs.StructuredLog}\"";

        if (!string.IsNullOrEmpty(settings.DownloadRoot))
            args += $" -DownloadRoot \"{settings.DownloadRoot}\"";
        if (!string.IsNullOrEmpty(settings.M365Channel))
            args += $" -M365Channel \"{settings.M365Channel}\"";

        return (args, logs);
    }

    private (string args, LogPaths logs) BuildPackageArgs(PackagerInfo packager, AppSettings settings, string comment)
    {
        var logs = CreateLogPaths(packager, "package", settings.DownloadRoot);

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{packager.FullPath}\" -PackageOnly";
        args += $" -SiteCode \"{settings.SiteCode}\"";
        args += $" -Comment \"{comment}\"";
        args += $" -LogPath \"{logs.StructuredLog}\"";

        if (SupportsFileServerPath(packager))
            args += $" -FileServerPath \"{settings.FileShareRoot}\"";
        // Only the non-default layout is passed, so a script without the parameter still runs.
        if (ContentLayouts.IsFlat(settings.ContentLayout))
            args += " -ContentLayout Flat";
        if (!string.IsNullOrEmpty(settings.DownloadRoot))
            args += $" -DownloadRoot \"{settings.DownloadRoot}\"";
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
        return JsonSerializer.Deserialize<AppDefinition>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to load definition: {path}");
    }
}
