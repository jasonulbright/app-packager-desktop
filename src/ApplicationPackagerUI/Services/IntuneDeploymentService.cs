using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Builds a .intunewin from staged content and publishes it as an Intune Win32 app.
/// Failures never throw to the caller: by the time this runs the MECM application (when one
/// was requested) already exists, so the outcome rides back as a status the caller decides
/// on, carrying the notes the operator sees. Cancellation still propagates.
/// </summary>
public sealed class IntuneDeploymentService(SettingsService settingsService)
{
    private static readonly string ToolFolder = Path.Combine(AppDataPaths.LegacyRoot, "Tools");

    private const string SetupFile = "install.bat";

    public async Task<IntuneRunOutcome> BuildAndPublishAsync(
        StageManifest manifest,
        string stagedContentPath,
        Action<string>? log = null,
        string description = "",
        CancellationToken ct = default,
        SigningPolicy? signingPolicy = null,
        IReadOnlyList<StageRequirement>? untranslatedRequirements = null)
    {
        string intuneWinPath;
        bool reused;
        try
        {
            (intuneWinPath, reused) = await BuildOrReuseAsync(manifest, stagedContentPath, log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return IntuneRunOutcome.BuildFailed($"build failed: {ex.Message}");
        }

        var winNote = $"{(reused ? "reused" : "built")} {Path.GetFileName(intuneWinPath)}";

        // Reading settings and resolving options can both fail on stored data; neither is
        // a published app, so both answer through the same failure path.
        IntunePublishOptions? options;
        try
        {
            var settings = settingsService.Load();
            options = BuildOptions(settings.Intune, manifest, description);
            signingPolicy ??= settings.ScriptSigning;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return IntuneRunOutcome.PublishFailed(winNote, ex.Message);
        }
        if (options is null)
            return IntuneRunOutcome.PublishRefused(winNote,
                "Intune credentials not configured; set Tenant ID, Client ID, and Client Secret in Settings.");

        var findings = IntuneCompatibilityFindings.Build(manifest, untranslatedRequirements);
        foreach (var finding in findings) log?.Invoke(IntuneCompatibilityFindings.LogLine(finding));
        if (IntuneCompatibilityFindings.RefusalMessage(findings) is { } refusal)
            return IntuneRunOutcome.PublishRefused(winNote, refusal);

        try
        {
            using var publisher = new IntunePublishService();
            var appId = await publisher.PublishAsync(intuneWinPath, manifest, options, log, ct,
                IntuneScriptRuleContext.FromManifest(manifest, signingPolicy, stagedContentPath), findings).ConfigureAwait(false);
            PublicationRecords.TryAppend(manifest, PublicationRecords.Intune, PublicationRecords.Succeeded, appId, options.TenantId, onLog: log);
            return IntuneRunOutcome.Published(winNote, $"published (app id {appId})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PublicationRecords.TryAppend(manifest, PublicationRecords.Intune, PublicationRecords.Failed, null, options.TenantId, ex.Message, log);
            return IntuneRunOutcome.PublishFailed(winNote, $"publish failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the .intunewin of a fresh stage when the content prep tool is already
    /// on this computer; the tool is never downloaded here. Returns the note, or an
    /// empty string when the tool is absent. A failed build does not fail the stage.
    /// </summary>
    public static async Task<string> BuildAtStageAsync(
        StageManifest manifest,
        string stagedContentPath,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var toolPath = InstalledToolPath();
        if (toolPath is null) return "";
        try
        {
            var build = await new IntuneWinToolService().BuildAsync(
                toolPath, stagedContentPath, SetupFileOf(manifest), IntuneWinArtifact.OutputFolder(stagedContentPath),
                IntuneWinArtifact.FileName(manifest), onProgress: log, ct: ct).ConfigureAwait(false);
            return $"built {Path.GetFileName(build.IntuneWinPath)}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"build failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Stores the stage's .intunewin beside the network content version folder of a
    /// ConfigMgr run: the one Stage built when it is newer than the manifest, else a
    /// fresh build. Runs only when the content prep tool is on this computer; the
    /// note is empty otherwise. Failures ride back as the note.
    /// </summary>
    public async Task<string> BuildBesideContentAsync(
        StageManifest manifest,
        string stagedContentPath,
        string networkContentPath,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (InstalledToolPath() is null && IntuneWinArtifact.Reusable(manifest, stagedContentPath) is null) return "";
        try
        {
            var (intuneWinPath, reused) = await BuildOrReuseAsync(manifest, stagedContentPath, log, ct).ConfigureAwait(false);
            var verb = reused ? "reused" : "built";

            var networkParent = Path.GetDirectoryName(networkContentPath.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(networkParent))
                return $"{verb} {Path.GetFileName(intuneWinPath)}; no network folder to copy beside";

            var target = Path.Combine(networkParent, Path.GetFileName(intuneWinPath));
            File.Copy(intuneWinPath, target, overwrite: true);
            log?.Invoke($"Intunewin stored: {target}");
            return $"{verb} {Path.GetFileName(intuneWinPath)}, stored beside the content";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"build failed: {ex.Message}";
        }
    }

    /// <summary>The content prep tool path when it is already present, else null.</summary>
    public static string? InstalledToolPath()
    {
        var toolPath = Path.Combine(ToolFolder, "IntuneWinAppUtil.exe");
        if (File.Exists(toolPath)) return toolPath;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "IntuneWinAppUtil.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Downloads the content prep tool into the tool folder; returns its path.</summary>
    public static Task<string> DownloadToolAsync(Action<string>? log = null, CancellationToken ct = default) =>
        EnsureToolAsync(log, ct);

    /// <summary>
    /// Returns publish options, or null when any credential is missing. The secret is
    /// unprotected here and never leaves this call path.
    /// </summary>
    private static IntunePublishOptions? BuildOptions(IntuneSettings stored, StageManifest manifest, string description)
    {
        var secret = IntuneSecretProtector.Unprotect(stored.ClientSecretProtected);
        if (string.IsNullOrWhiteSpace(stored.TenantId) ||
            string.IsNullOrWhiteSpace(stored.ClientId) ||
            string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        return new IntunePublishOptions
        {
            TenantId = stored.TenantId.Trim(),
            ClientId = stored.ClientId.Trim(),
            ClientSecret = secret,
            Description = description,
            // The payload's own architecture, never the detection's registry view: a
            // per-user x64 product detects through the 32-bit view.
            Architecture = InstallerArchitectures.Resolve(manifest)
        };
    }

    /// <summary>Resolves the content prep tool, downloading it into the tool folder on first use.</summary>
    private static async Task<string> EnsureToolAsync(Action<string>? log, CancellationToken ct)
    {
        var toolPath = Path.Combine(ToolFolder, "IntuneWinAppUtil.exe");
        if (File.Exists(toolPath))
            return toolPath;

        log?.Invoke("Downloading Microsoft Win32 Content Prep Tool...");
        var service = new IntuneWinToolService();
        return await service.InstallAsync(ToolFolder, onProgress: log, ct: ct).ConfigureAwait(false);
    }

    /// <summary>The entry file the content prep tool records: the manifest's resolved setup file, else install.bat.</summary>
    private static string SetupFileOf(StageManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.SetupFile) ? SetupFile : manifest.SetupFile;

    /// <summary>The stage's .intunewin when it is newer than the manifest, else a fresh build (downloading the tool on first use).</summary>
    private static async Task<(string Path, bool Reused)> BuildOrReuseAsync(
        StageManifest manifest, string stagedContentPath, Action<string>? log, CancellationToken ct)
    {
        if (IntuneWinArtifact.Reusable(manifest, stagedContentPath) is { } existing)
        {
            log?.Invoke($"Intunewin reused: {existing}");
            return (existing, true);
        }
        var toolPath = await EnsureToolAsync(log, ct).ConfigureAwait(false);
        var build = await new IntuneWinToolService().BuildAsync(
            toolPath, stagedContentPath, SetupFileOf(manifest), IntuneWinArtifact.OutputFolder(stagedContentPath),
            IntuneWinArtifact.FileName(manifest), onProgress: log, ct: ct).ConfigureAwait(false);
        return (build.IntuneWinPath, false);
    }
}
