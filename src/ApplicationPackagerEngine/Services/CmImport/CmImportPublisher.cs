using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.CmImport;

public sealed record CmPackagedApp(string Name, string IntuneName, string IntuneWinPath, string AppId);

/// <summary>
/// Builds the .intunewin file of each staged app and publishes it through the existing Intune
/// publisher. The finished Graph rules, return codes, restart behavior, minimums and architectures
/// of the plan replace the values a stage derives from a detection rule.
/// </summary>
public sealed class CmImportPublisher(IntuneWinToolService tool, IntunePublishService publisher)
{
    /// <summary>The fields of a converted manifest that the publisher reads from a stage manifest.</summary>
    public static StageManifest ToStageManifest(CmIntuneManifest m) => new()
    {
        AppName = m.AppName,
        Publisher = m.Publisher,
        SoftwareVersion = m.SoftwareVersion,
        ApplicationId = m.ApplicationId,
        ProfileId = m.ProfileId.Length > 0 ? m.ProfileId : "default",
        InstallerType = "EXE",
        SetupFile = m.SetupFile,
        InstallationBehaviorType = m.InstallationBehaviorType,
        Icon = m.Icon,
        CommandOverrides = new StageCommandOverrides { Install = m.InstallCommandLine, Uninstall = m.UninstallCommandLine }
    };

    public static IntuneConversionOverrides ToOverrides(CmIntuneManifest m) =>
        new(m.IntuneRules, m.IntuneReturnCodes, m.IntuneRestartBehavior, m.IntuneMinimums, m.IntuneAllowedArchitectures);

    /// <summary>
    /// Packages every staged app, and publishes it when <paramref name="options"/> is given. Publishing
    /// needs the content, so it cannot follow a stage that skipped the copy.
    /// </summary>
    public async Task<IReadOnlyList<CmPackagedApp>> PackageAsync(CmImportPlan plan, CmStageResult stage, string toolPath,
        IntunePublishOptions? options, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var results = new List<CmPackagedApp>();
        var missing = stage.Staged.FirstOrDefault(s => !Directory.Exists(s.ContentFolder) || !Directory.EnumerateFileSystemEntries(s.ContentFolder).Any());
        if (missing is not null)
            throw new InvalidOperationException($"The content folder of '{missing.Name}' is missing or empty: {missing.ContentFolder}. The stage skipped the content copy, so the app cannot be built or published.");
        foreach (var item in stage.Staged)
        {
            var dt = plan.DeploymentTypes.FirstOrDefault(d => d.Convert && d.Name == item.Name)
                ?? throw new InvalidOperationException($"The plan has no convertible deployment type named '{item.Name}'.");
            var manifest = dt.Manifest!;
            var built = await tool.BuildAsync(toolPath, item.ContentFolder, item.SetupFile, Path.Combine(item.Folder, "Package"),
                CmImportStage.SafeName(item.Name) + ".intunewin", onProgress: onProgress, ct: ct).ConfigureAwait(false);
            var appId = "";
            if (options is not null)
            {
                var perApp = new IntunePublishOptions
                {
                    TenantId = options.TenantId, ClientId = options.ClientId, ClientSecret = options.ClientSecret,
                    MinimumSupportedWindowsRelease = options.MinimumSupportedWindowsRelease,
                    GraphBase = options.GraphBase, TokenAuthority = options.TokenAuthority,
                    PollTimeoutSec = options.PollTimeoutSec, PollInterval = options.PollInterval,
                    CommitPollInterval = options.CommitPollInterval, BlockSizeBytes = options.BlockSizeBytes,
                    Description = string.IsNullOrWhiteSpace(manifest.Description) ? options.Description : manifest.Description
                };
                appId = await publisher.PublishAsync(built.IntuneWinPath, ToStageManifest(manifest), perApp, onProgress, ct,
                    conversion: ToOverrides(manifest)).ConfigureAwait(false);
            }
            results.Add(new CmPackagedApp(item.Name, item.IntuneName, built.IntuneWinPath, appId));
        }
        return results;
    }
}
