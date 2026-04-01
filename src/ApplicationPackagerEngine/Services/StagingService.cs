using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Orchestrates the full staging pipeline for JSON-defined apps:
/// scrape version → download → inspect → create versioned folder → generate wrappers → write manifest.
/// </summary>
public sealed class StagingService
{
    private readonly ScraperRegistry _scrapers;
    private readonly DownloadService _downloadService;
    private readonly MsiInspectionService _msiService;
    private readonly WrapperGeneratorService _wrapperService;
    private readonly ManifestService _manifestService;

    public StagingService(
        ScraperRegistry scrapers,
        DownloadService downloadService,
        MsiInspectionService msiService,
        WrapperGeneratorService wrapperService,
        ManifestService manifestService)
    {
        _scrapers = scrapers;
        _downloadService = downloadService;
        _msiService = msiService;
        _wrapperService = wrapperService;
        _manifestService = manifestService;
    }

    /// <summary>
    /// Gets the latest version for a JSON-defined app without downloading.
    /// Routes through winget or static URL based on preference and availability.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(
        AppDefinition def, string acquisitionMethod = "StaticUrl", CancellationToken ct = default)
    {
        // If user prefers winget and this app has a WingetId, try winget first
        if (acquisitionMethod == "Winget" && def.Acquisition.HasWinget)
        {
            var wingetConfig = new Models.VersionScraperConfig
            {
                Type = "Winget",
                Url = def.Acquisition.WingetId ?? ""
            };
            var wingetVersion = await _scrapers.GetLatestVersionAsync(wingetConfig, ct);
            if (wingetVersion is not null)
                return wingetVersion;
        }

        var (_, display) = await _scrapers.GetLatestVersionWithTransformAsync(def.VersionScraper, ct);
        return display;
    }

    /// <summary>
    /// Runs the full staging pipeline for a JSON-defined app.
    /// </summary>
    public async Task<StagingResult> StageAsync(
        AppDefinition def, string downloadRoot,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        try
        {
            // 1. Scrape version (raw for download URLs, display for folder/manifest/detection)
            onProgress?.Invoke($"Checking latest version for {def.Application}...");
            var (rawVersion, version) = await _scrapers.GetLatestVersionWithTransformAsync(def.VersionScraper, ct);
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(rawVersion))
                return new StagingResult { Success = false, ErrorMessage = "Could not determine latest version" };

            onProgress?.Invoke($"Version: {version}");

            // 2. Set up versioned content folder (uses display version)
            var appFolder = $"{def.Application.Replace(" ", "")}-{def.Architecture}";
            var baseRoot = Path.Combine(downloadRoot, appFolder);
            var contentPath = Path.Combine(baseRoot, version);
            Directory.CreateDirectory(contentPath);

            // 3. Download installer — {version} = raw (for URLs that need original format),
            //    {display_version} = transformed (for URLs that need the display format)
            var downloadUrl = def.DownloadUrl
                .Replace("{display_version}", version, StringComparison.OrdinalIgnoreCase)
                .Replace("{version}", rawVersion, StringComparison.OrdinalIgnoreCase);
            var installerFileName = ResolveInstallerFileName(def, downloadUrl, version);
            var installerPath = Path.Combine(contentPath, installerFileName);

            if (!File.Exists(installerPath))
            {
                await _downloadService.DownloadAsync(downloadUrl, installerPath,
                    onProgress: onProgress, ct: ct);
            }
            else
            {
                onProgress?.Invoke($"Installer already cached: {installerFileName}");
            }

            // 4. Inspect installer (MSI only for now)
            string? productCode = null;
            string? productVersion = null;
            string publisher = def.Vendor;

            if (def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke("Reading MSI properties...");
                var msiProps = _msiService.ReadProperties(installerPath);
                productCode = msiProps.ProductCode;
                productVersion = msiProps.ProductVersion;
                if (!string.IsNullOrWhiteSpace(msiProps.Manufacturer) &&
                    msiProps.Manufacturer.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                    publisher = msiProps.Manufacturer;
                onProgress?.Invoke($"ProductCode: {productCode}");
                onProgress?.Invoke($"ProductVersion: {productVersion}");
            }

            // 5. Generate wrappers
            onProgress?.Invoke("Generating install/uninstall wrappers...");
            if (def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase))
            {
                _wrapperService.GenerateMsiWrappers(contentPath, installerFileName);
            }
            else
            {
                _wrapperService.GenerateExeWrappers(contentPath, installerFileName,
                    def.InstallArgs, def.UninstallArgs);
            }

            // 6. Build detection config
            var detection = BuildDetection(def, productCode, version);

            // 7. Write manifest
            var manifestPath = Path.Combine(contentPath, "stage-manifest.json");
            var manifest = new StageManifest
            {
                AppName = $"{def.Application} - {version} ({def.Architecture})",
                DisplayName = $"{def.Application} ({def.Architecture})",
                Publisher = publisher,
                SoftwareVersion = version,
                InstallerFile = installerFileName,
                InstallerType = def.InstallerType,
                InstallArgs = def.InstallArgs,
                UninstallArgs = def.UninstallArgs,
                ProductCode = productCode,
                RunningProcess = def.RunningProcess,
                Detection = detection
            };
            _manifestService.Write(manifestPath, manifest);
            onProgress?.Invoke($"Manifest written: {manifestPath}");

            onProgress?.Invoke($"Stage complete: {contentPath}");

            return new StagingResult
            {
                Success = true,
                Version = version,
                ContentPath = contentPath,
                ManifestPath = manifestPath
            };
        }
        catch (OperationCanceledException)
        {
            return new StagingResult { Success = false, ErrorMessage = "Cancelled" };
        }
        catch (Exception ex)
        {
            return new StagingResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Determines the installer filename: explicit override → URL path → constructed fallback.
    /// </summary>
    private static string ResolveInstallerFileName(AppDefinition def, string downloadUrl, string version)
    {
        // 1. Explicit override in definition (required for fwlinks, query-string URLs, etc.)
        if (!string.IsNullOrEmpty(def.InstallerFileName))
            return def.InstallerFileName.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

        // 2. Parse from URL path
        var urlFileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
        if (!string.IsNullOrEmpty(urlFileName) && urlFileName.Contains('.'))
            return urlFileName;

        // 3. Fallback: construct from app name + installer type
        var safeName = def.Application.Replace(" ", "").Replace("(", "").Replace(")", "");
        var ext = def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase) ? ".msi" : ".exe";
        return $"{safeName}-{version}{ext}";
    }

    private static StageDetection BuildDetection(AppDefinition def, string? productCode, string version)
    {
        var det = def.Detection;

        if (det.Type.Equals("RegistryKeyValue", StringComparison.OrdinalIgnoreCase))
        {
            // Substitute {ProductCode} in template
            var regKey = det.RegistryKeyTemplate?
                .Replace("{ProductCode}", productCode ?? "", StringComparison.OrdinalIgnoreCase) ?? "";

            return new StageDetection
            {
                Type = "RegistryKeyValue",
                RegistryKeyRelative = regKey,
                ValueName = det.ValueName ?? "DisplayVersion",
                ExpectedValue = version,
                Operator = det.Operator ?? "IsEquals",
                PropertyType = det.PropertyType ?? "String",
                Is64Bit = det.Is64Bit
            };
        }

        if (det.Type.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            return new StageDetection
            {
                Type = "File",
                FilePath = det.FilePath ?? "",
                FileName = det.FileName ?? "",
                ExpectedValue = version,
                Operator = det.Operator ?? "GreaterEquals",
                PropertyType = det.PropertyType ?? "Version",
                Is64Bit = det.Is64Bit
            };
        }

        return new StageDetection { Type = det.Type };
    }
}
