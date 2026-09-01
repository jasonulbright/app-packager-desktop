using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Outcome of one Intune post-step. Both notes mirror the lines the operator sees for a run;
/// an empty note means that half of the step never ran.
/// </summary>
public sealed record IntuneRunNotes(string IntuneWinNote, string PublishNote)
{
    public static readonly IntuneRunNotes None = new("", "");
}

/// <summary>
/// Builds a .intunewin from staged content and publishes it as an Intune Win32 app.
/// Failures never throw to the caller: by the time this runs the MECM application (when one
/// was requested) already exists, so the outcome rides back as notes for the run log.
/// </summary>
public sealed class IntuneDeploymentService(SettingsService settingsService)
{
    private static readonly string ToolFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SRLPackr", "Tools");

    private const string SetupFile = "install.bat";

    public async Task<IntuneRunNotes> BuildAndPublishAsync(
        StageManifest manifest,
        string stagedContentPath,
        Action<string>? log = null,
        string description = "",
        CancellationToken ct = default)
    {
        string intuneWinPath;
        try
        {
            var toolPath = await EnsureToolAsync(log, ct).ConfigureAwait(false);
            var outputFolder = Path.GetDirectoryName(stagedContentPath.TrimEnd(Path.DirectorySeparatorChar))
                ?? throw new InvalidOperationException($"Staged content folder has no parent: {stagedContentPath}");

            var toolService = new IntuneWinToolService();
            var build = await toolService.BuildAsync(
                toolPath, stagedContentPath, SetupFile, outputFolder,
                OutputName(manifest), onProgress: log, ct: ct).ConfigureAwait(false);

            intuneWinPath = build.IntuneWinPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new IntuneRunNotes($"build failed: {ex.Message}", "");
        }

        var winNote = $"built {Path.GetFileName(intuneWinPath)}";

        var settings = settingsService.Load();
        var options = BuildOptions(settings.Intune, manifest, description);
        if (options is null)
            return new IntuneRunNotes(winNote, "Intune credentials not configured; set Tenant ID, Client ID, and Client Secret in Settings.");

        if (manifest.DeploymentTypes is { Length: > 0 })
            return new IntuneRunNotes(winNote, "variant-split app; publish skipped (single-payload apps only for now).");

        try
        {
            using var publisher = new IntunePublishService();
            var appId = await publisher.PublishAsync(intuneWinPath, manifest, options, log, ct).ConfigureAwait(false);
            return new IntuneRunNotes(winNote, $"published (app id {appId})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new IntuneRunNotes(winNote, $"publish failed: {ex.Message}");
        }
    }

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
            Architecture = manifest.Detection.Is64Bit ? "x64" : "x86"
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

    private static string OutputName(StageManifest manifest)
    {
        var raw = $"{manifest.AppName}-{manifest.SoftwareVersion}";
        foreach (var c in Path.GetInvalidFileNameChars())
            raw = raw.Replace(c, '_');
        return raw.Replace(' ', '_') + ".intunewin";
    }
}
