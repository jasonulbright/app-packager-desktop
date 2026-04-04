namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Deployment target that stages content to a network share without creating a CM Application.
/// Used for PSADT wrapper generation, Intune prep, manual packaging workflows,
/// or any scenario where you want staged content without CM integration.
/// </summary>
public sealed class BareStaging
{
    /// <summary>
    /// Copies staged content to the network share in the standard vendor/app/version folder structure.
    /// No CM connection, no PowerShell, no detection rules — just file copy with sanitized paths.
    /// </summary>
    public Task<PackagingResult> StageToNetworkAsync(
        Models.StageManifest manifest,
        string stagedContentPath,
        string fileServerPath,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        try
        {
            var vendorFolder = MecmTarget.SanitizeFolderNamePublic(manifest.Publisher);
            var appFolder = manifest.DisplayName ?? manifest.AppName;
            var networkRoot = Path.Combine(fileServerPath, "Applications", vendorFolder, appFolder);
            var networkContentPath = Path.Combine(networkRoot, manifest.SoftwareVersion);

            onProgress?.Invoke($"Staging to: {networkContentPath}");
            Directory.CreateDirectory(networkContentPath);

            var copied = 0;
            foreach (var file in Directory.GetFiles(stagedContentPath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(stagedContentPath, file);
                if (relativePath.Equals("stage-manifest.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dest = Path.Combine(networkContentPath, relativePath);
                var destDir = Path.GetDirectoryName(dest)!;
                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (!File.Exists(dest))
                {
                    File.Copy(file, dest);
                    copied++;
                }
            }

            onProgress?.Invoke($"Staged {copied} files to {networkContentPath}");

            return Task.FromResult(new PackagingResult
            {
                Success = true,
                ApplicationName = manifest.AppName,
                NetworkContentPath = networkContentPath
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PackagingResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }
}
