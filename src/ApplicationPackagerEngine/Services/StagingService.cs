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
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(AppDefinition def, CancellationToken ct = default)
    {
        return await _scrapers.GetLatestVersionAsync(def.VersionScraper, ct);
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
            // 1. Scrape version
            onProgress?.Invoke($"Checking latest version for {def.Application}...");
            var version = await _scrapers.GetLatestVersionAsync(def.VersionScraper, ct);
            if (string.IsNullOrEmpty(version))
                return new StagingResult { Success = false, ErrorMessage = "Could not determine latest version" };

            onProgress?.Invoke($"Version: {version}");

            // 2. Set up versioned content folder
            var appFolder = $"{def.Application.Replace(" ", "")}-{def.Architecture}";
            var baseRoot = Path.Combine(downloadRoot, appFolder);
            var contentPath = Path.Combine(baseRoot, version);
            Directory.CreateDirectory(contentPath);

            // 3. Download installer
            var installerFileName = Path.GetFileName(new Uri(def.DownloadUrl).AbsolutePath);
            var installerPath = Path.Combine(contentPath, installerFileName);

            if (!File.Exists(installerPath))
            {
                await _downloadService.DownloadAsync(def.DownloadUrl, installerPath,
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
