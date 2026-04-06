using System.IO.Compression;
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
    private readonly InstallerIdentificationService? _installerIdService;
    private readonly IconExtractionService? _iconService;

    public StagingService(
        ScraperRegistry scrapers,
        DownloadService downloadService,
        MsiInspectionService msiService,
        WrapperGeneratorService wrapperService,
        ManifestService manifestService,
        InstallerIdentificationService? installerIdService = null,
        IconExtractionService? iconService = null)
    {
        _scrapers = scrapers;
        _downloadService = downloadService;
        _msiService = msiService;
        _wrapperService = wrapperService;
        _manifestService = manifestService;
        _iconService = iconService;
        _installerIdService = installerIdService;
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

        var (raw, display) = await _scrapers.GetLatestVersionWithTransformAsync(def.VersionScraper, ct);

        if (!string.IsNullOrEmpty(def.DisplayVersionOverride) && raw is not null)
            return def.DisplayVersionOverride.Replace("{version}", raw, StringComparison.OrdinalIgnoreCase);

        return display;
    }

    /// <summary>
    /// Resolves the download URL, using DownloadUrlQuery if available (single API call for both version and URL).
    /// </summary>
    private async Task<string?> ResolveDownloadUrlFromApiAsync(VersionScraperConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.DownloadUrlQuery)) return null;

        var scraper = _scrapers.Get(config.Type);
        if (scraper is Scrapers.JsonQueryScraper jqs)
        {
            var (_, url) = await jqs.GetVersionAndDownloadUrlAsync(config, ct);
            return url;
        }
        if (scraper is Scrapers.HttpRegexScraper hrs)
        {
            var (_, url) = await hrs.GetVersionAndDownloadUrlAsync(config, ct);
            // If the captured URL is relative (just a filename), prepend the scraper URL's base path
            if (url is not null && !url.Contains("://"))
            {
                var baseUrl = config.Url;
                if (!baseUrl.EndsWith('/')) baseUrl = baseUrl[..(baseUrl.LastIndexOf('/') + 1)];
                url = baseUrl + url;
            }
            return url;
        }
        return null;
    }

    /// <summary>
    /// Runs the full staging pipeline for a JSON-defined app.
    /// </summary>
    public async Task<StagingResult> StageAsync(
        AppDefinition def, string downloadRoot,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? knownVersion = null)
    {
        // Route ODT apps to dedicated pipeline
        if (def.InstallerType.Equals("ODT", StringComparison.OrdinalIgnoreCase))
            return await StageOdtAsync(def, downloadRoot, onProgress, ct, knownVersion);

        try
        {
            // 1. Get version — reuse from Check Latest if available to avoid redundant API calls.
            //    Must re-scrape when a VersionTransform exists, since knownVersion is the display
            //    version and we need the raw version for download URL substitution.
            string? rawVersion, version;
            if (!string.IsNullOrEmpty(knownVersion) && string.IsNullOrEmpty(def.VersionScraper.VersionTransform))
            {
                onProgress?.Invoke($"Checking latest version for {def.Application}...");
                version = knownVersion;
                rawVersion = knownVersion;
            }
            else
            {
                onProgress?.Invoke($"Checking latest version for {def.Application}...");
                (rawVersion, version) = await _scrapers.GetLatestVersionWithTransformAsync(def.VersionScraper, ct);
            }
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(rawVersion))
                return new StagingResult { Success = false, ErrorMessage = "Could not determine latest version" };

            // Apply DisplayVersionOverride for UI/folder/detection display
            // version = transformed (for URL {display_version}), displayVersion = what user sees
            var displayVersion = version;
            if (!string.IsNullOrEmpty(def.DisplayVersionOverride))
                displayVersion = def.DisplayVersionOverride
                    .Replace("{version}", rawVersion, StringComparison.OrdinalIgnoreCase);

            onProgress?.Invoke($"Version: {displayVersion}");

            // 2. Set up versioned content folder (uses display version)
            var appFolder = $"{def.Application.Replace(" ", "")}-{def.Architecture}";
            var baseRoot = Path.Combine(downloadRoot, appFolder);
            var contentPath = Path.Combine(baseRoot, displayVersion);
            Directory.CreateDirectory(contentPath);

            // 3. Resolve download URL — either from API query or template substitution
            string downloadUrl;
            if (!string.IsNullOrEmpty(def.VersionScraper.DownloadUrlQuery))
            {
                var apiUrl = await ResolveDownloadUrlFromApiAsync(def.VersionScraper, ct);
                downloadUrl = apiUrl ?? def.DownloadUrl
                    .Replace("{display_version}", version, StringComparison.OrdinalIgnoreCase)
                    .Replace("{version}", rawVersion, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                downloadUrl = def.DownloadUrl
                    .Replace("{display_version}", version, StringComparison.OrdinalIgnoreCase)
                    .Replace("{version}", rawVersion, StringComparison.OrdinalIgnoreCase);
            }
            var installerFileName = ResolveInstallerFileName(def, downloadUrl, version);
            var installerPath = Path.Combine(contentPath, installerFileName);

            if (!File.Exists(installerPath))
            {
                // Check if FileVersionScraper already downloaded this file during Check Latest
                var cachedPath = def.VersionScraper.Type.Equals("FileVersion", StringComparison.OrdinalIgnoreCase)
                    ? Scrapers.FileVersionScraper.GetCachedPath(def.VersionScraper.Url)
                    : null;

                if (cachedPath is not null)
                {
                    File.Copy(cachedPath, installerPath);
                    onProgress?.Invoke($"Copied from version cache: {installerFileName}");
                }
                else
                {
                    await _downloadService.DownloadAsync(downloadUrl, installerPath,
                        onProgress: onProgress, ct: ct);
                }
            }
            else
            {
                onProgress?.Invoke($"Installer already cached: {installerFileName}");
            }

            // 4. If downloaded file is a ZIP, extract and find the real installer inside
            if (installerPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(installerPath))
            {
                onProgress?.Invoke("Extracting ZIP archive...");
                var extractDir = Path.Combine(contentPath, "_zip_extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(installerPath, extractDir);

                // Find the MSI or EXE inside
                var innerInstaller = Directory.GetFiles(extractDir, "*.msi", SearchOption.AllDirectories).FirstOrDefault()
                    ?? Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();

                if (innerInstaller is not null)
                {
                    var innerName = Path.GetFileName(innerInstaller);
                    var destPath = Path.Combine(contentPath, innerName);
                    if (!File.Exists(destPath))
                        File.Move(innerInstaller, destPath);
                    installerPath = destPath;
                    installerFileName = innerName;
                    onProgress?.Invoke($"Extracted installer: {innerName}");
                }

                // Clean up extract dir and original ZIP
                try { Directory.Delete(extractDir, true); } catch { }
                var originalZip = Path.Combine(contentPath, ResolveInstallerFileName(def, downloadUrl, displayVersion));
                try { if (File.Exists(originalZip) && originalZip != installerPath) File.Delete(originalZip); } catch { }
            }

            // 4b. Inspect installer (MSI only for now)
            string? productCode = null;
            string? productVersion = null;
            string publisher = def.Vendor;

            if (def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase) ||
                installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
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

            // 4c. For non-MSI installers, extract metadata via InstallerIdentificationService
            InstallerMetadata? installerMetadata = null;
            if (productCode is null && _installerIdService is not null &&
                !def.InstallerType.Equals("ODT", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke("Identifying installer framework...");
                var framework = await _installerIdService.IdentifyAsync(
                    installerPath, def.InstallerFramework, ct);
                onProgress?.Invoke($"Framework: {framework}");

                if (framework != "Unknown")
                {
                    onProgress?.Invoke("Extracting installer metadata...");
                    installerMetadata = await _installerIdService.ExtractMetadataAsync(
                        installerPath, framework, ct);

                    // Use extracted ProductCode if available (WiX Burn inner MSI)
                    if (!string.IsNullOrEmpty(installerMetadata.ProductCode))
                    {
                        productCode = installerMetadata.ProductCode;
                        onProgress?.Invoke($"ProductCode (from {installerMetadata.MetadataSource}): {productCode}");
                    }
                    if (!string.IsNullOrEmpty(installerMetadata.ProductVersion))
                        onProgress?.Invoke($"ProductVersion (from {installerMetadata.MetadataSource}): {installerMetadata.ProductVersion}");
                    if (!string.IsNullOrEmpty(installerMetadata.Manufacturer) &&
                        installerMetadata.Manufacturer.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                        publisher = installerMetadata.Manufacturer;
                }
            }

            // 4d. Extract icon from installer
            if (_iconService is not null)
            {
                onProgress?.Invoke("Extracting application icon...");
                var iconResult = await _iconService.ExtractAsync(installerPath, def.Id, ct);
                if (iconResult.Success)
                    onProgress?.Invoke($"Icon cached: {iconResult.CachedPath}");
            }

            // 5. Generate wrappers
            onProgress?.Invoke("Generating install/uninstall wrappers...");
            if (def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase))
            {
                _wrapperService.GenerateMsiWrappers(contentPath, installerFileName, def.UninstallCommand);
            }
            else
            {
                _wrapperService.GenerateExeWrappers(contentPath, installerFileName,
                    def.InstallArgs, def.UninstallArgs, def.InstallerFramework, def.Application, def.UninstallCommand);
            }

            // 6. Build detection config
            // If the definition's detection references {ProductCode} but we have no ProductCode,
            // and we have installer metadata with detection data, use the metadata instead.
            var detection = BuildDetection(def, productCode, displayVersion, rawVersion, installerMetadata);

            // 6b. Detection conformance check — log if definition's detection differs from metadata-derived
            if (installerMetadata?.HasDetectionData == true && detection.Type != "Unknown")
            {
                var metaDerived = BuildDetectionFromMetadata(installerMetadata, displayVersion, def.Detection.Is64Bit);
                if (metaDerived.Type != "Unknown")
                {
                    var defType = detection.Type;
                    var metaType = metaDerived.Type;
                    var defKey = detection.RegistryKeyRelative ?? detection.FilePath ?? "";
                    var metaKey = metaDerived.RegistryKeyRelative ?? metaDerived.FilePath ?? "";

                    if (defType != metaType || (!string.IsNullOrEmpty(metaKey) && defKey != metaKey))
                    {
                        onProgress?.Invoke($"[CONFORMANCE] Definition detection: {defType} on '{defKey}'");
                        onProgress?.Invoke($"[CONFORMANCE] Metadata-derived:     {metaType} on '{metaKey}'");
                        onProgress?.Invoke($"[CONFORMANCE] Source: {installerMetadata.MetadataSource}");
                    }
                    else
                    {
                        onProgress?.Invoke("[CONFORMANCE] Detection matches metadata — OK");
                    }
                }
            }

            // 7. Write manifest
            // ARP-driven naming: use installer metadata when available, fallback to constructed template
            var arpDisplayName = installerMetadata?.ProductName;
            var arpVersion = installerMetadata?.ProductVersion;
            var cmAppName = !string.IsNullOrEmpty(arpDisplayName)
                ? arpDisplayName
                : $"{def.Application} - {displayVersion} ({def.Architecture})";
            var cmDisplayName = !string.IsNullOrEmpty(arpDisplayName)
                ? arpDisplayName
                : $"{def.Application} ({def.Architecture})";
            var cmVersion = !string.IsNullOrEmpty(arpVersion)
                ? arpVersion
                : displayVersion;

            var manifestPath = Path.Combine(contentPath, "stage-manifest.json");
            var manifest = new StageManifest
            {
                AppName = cmAppName,
                DisplayName = cmDisplayName,
                Publisher = publisher,
                SoftwareVersion = cmVersion,
                InstallerFile = installerFileName,
                InstallerType = def.InstallerType,
                InstallContext = def.InstallContext,
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

    // ── ODT staging pipeline ─────────────────────────────────────

    private const string OdtSetupUrl = "https://officecdn.microsoft.com/pr/wsus/setup.exe";

    private async Task<StagingResult> StageOdtAsync(
        AppDefinition def, string downloadRoot,
        Action<string>? onProgress, CancellationToken ct, string? knownVersion)
    {
        try
        {
            var odt = def.Odt;
            if (odt is null || odt.ProductIds.Length == 0)
                return new StagingResult { Success = false, ErrorMessage = "OdtConfig with ProductIds is required for ODT apps" };

            // 1. Get version
            string? version;
            if (!string.IsNullOrEmpty(knownVersion))
            {
                version = knownVersion;
            }
            else
            {
                onProgress?.Invoke($"Checking latest version for {def.Application}...");
                var (_, display) = await _scrapers.GetLatestVersionWithTransformAsync(def.VersionScraper, ct);
                version = display;
            }
            if (string.IsNullOrEmpty(version))
                return new StagingResult { Success = false, ErrorMessage = "Could not determine latest version" };

            // CdnCab scraper strips "16.0." prefix — restore it for ODT
            var fullVersion = version.Contains('.') && !version.StartsWith("16.") ? $"16.0.{version}" : version;

            onProgress?.Invoke($"Version: {version}");

            // 2. Set up folders
            var edition = def.Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "32" : "64";
            var appFolder = $"{def.Application.Replace(" ", "")}-{def.Architecture}";
            var baseRoot = Path.Combine(downloadRoot, appFolder);
            var contentPath = Path.Combine(baseRoot, version);
            Directory.CreateDirectory(contentPath);

            // 3. Download ODT setup.exe
            var setupExe = Path.Combine(baseRoot, "setup.exe");
            if (!File.Exists(setupExe))
            {
                onProgress?.Invoke("Downloading ODT setup.exe...");
                await _downloadService.DownloadAsync(OdtSetupUrl, setupExe, onProgress: onProgress, ct: ct);
            }
            else
            {
                onProgress?.Invoke("ODT setup.exe already cached.");
            }

            // 4. Generate download.xml
            var downloadXml = GenerateOdtXml(edition, fullVersion, odt, contentPath);
            var downloadXmlPath = Path.Combine(baseRoot, "download.xml");
            await File.WriteAllTextAsync(downloadXmlPath, downloadXml, System.Text.Encoding.ASCII, ct);
            onProgress?.Invoke("Generated download.xml");

            // 5. Run setup.exe /download
            onProgress?.Invoke("Downloading Office source files (this may take several minutes)...");
            var dlResult = await RunProcessWithTimeoutAsync(
                setupExe, $"/download \"{downloadXmlPath}\"",
                TimeSpan.FromMinutes(30), ct);
            if (!dlResult)
                return new StagingResult { Success = false, ErrorMessage = "ODT /download failed" };
            onProgress?.Invoke("Office source download complete.");

            // 6. Copy setup.exe into content folder
            var contentSetupExe = Path.Combine(contentPath, "setup.exe");
            if (!File.Exists(contentSetupExe))
            {
                File.Copy(setupExe, contentSetupExe);
                onProgress?.Invoke("Copied setup.exe to content folder.");
            }

            // 7. Generate install.xml (no SourcePath — uses relative at install time)
            var installXml = GenerateOdtXml(edition, fullVersion, odt, sourcePath: null);
            await File.WriteAllTextAsync(Path.Combine(contentPath, "install.xml"), installXml, System.Text.Encoding.ASCII, ct);

            // 8. Generate uninstall.xml
            var uninstallProdId = odt.UninstallProductId ?? odt.ProductIds[^1];
            var uninstallXml = string.Join("\r\n",
                "<Configuration>",
                "  <Remove>",
                $"    <Product ID=\"{uninstallProdId}\" />",
                "  </Remove>",
                "  <Display Level=\"None\" AcceptEULA=\"TRUE\" />",
                "</Configuration>");
            await File.WriteAllTextAsync(Path.Combine(contentPath, "uninstall.xml"), uninstallXml, System.Text.Encoding.ASCII, ct);
            onProgress?.Invoke("Generated install.xml and uninstall.xml");

            // 9. Generate ODT-specific wrappers (setup.exe /configure)
            var installPs1 = string.Join("\r\n",
                "$setupPath = Join-Path $PSScriptRoot 'setup.exe'",
                "$configPath = Join-Path $PSScriptRoot 'install.xml'",
                "$proc = Start-Process -FilePath $setupPath -ArgumentList @('/configure', $configPath) -Wait -PassThru -NoNewWindow -WorkingDirectory $PSScriptRoot",
                "exit $proc.ExitCode");
            var uninstallPs1 = string.Join("\r\n",
                "$setupPath = Join-Path $PSScriptRoot 'setup.exe'",
                "$configPath = Join-Path $PSScriptRoot 'uninstall.xml'",
                "$proc = Start-Process -FilePath $setupPath -ArgumentList @('/configure', $configPath) -Wait -PassThru -NoNewWindow -WorkingDirectory $PSScriptRoot",
                "exit $proc.ExitCode");
            var installBat = "@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"\r\nexit /b %ERRORLEVEL%";
            var uninstallBat = "@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0uninstall.ps1\"\r\nexit /b %ERRORLEVEL%";

            await File.WriteAllTextAsync(Path.Combine(contentPath, "install.ps1"), installPs1, System.Text.Encoding.ASCII, ct);
            await File.WriteAllTextAsync(Path.Combine(contentPath, "uninstall.ps1"), uninstallPs1, System.Text.Encoding.ASCII, ct);
            await File.WriteAllTextAsync(Path.Combine(contentPath, "install.bat"), installBat, System.Text.Encoding.ASCII, ct);
            await File.WriteAllTextAsync(Path.Combine(contentPath, "uninstall.bat"), uninstallBat, System.Text.Encoding.ASCII, ct);
            onProgress?.Invoke("Generated install/uninstall wrappers.");

            // 10. Write manifest
            var detection = BuildDetection(def, null, version);
            var manifestPath = Path.Combine(contentPath, "stage-manifest.json");
            var publisher = def.Vendor;
            var manifest = new StageManifest
            {
                AppName = $"{def.Application} - {version} ({def.Architecture})",
                DisplayName = $"{def.Application} ({def.Architecture})",
                Publisher = publisher,
                SoftwareVersion = version,
                InstallerFile = "setup.exe",
                InstallerType = "ODT",
                InstallArgs = "/configure install.xml",
                UninstallArgs = "/configure uninstall.xml",
                RunningProcess = def.RunningProcess,
                Detection = detection
            };
            _manifestService.Write(manifestPath, manifest);
            onProgress?.Invoke($"Manifest written: {manifestPath}");

            onProgress?.Invoke($"Stage complete: {contentPath}");
            return new StagingResult
            {
                Success = true,
                ContentPath = contentPath,
                ManifestPath = manifestPath
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new StagingResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static string GenerateOdtXml(string edition, string version, OdtConfig odt, string? sourcePath)
    {
        var lines = new List<string> { "<Configuration>" };

        var addAttrs = new List<string>();
        if (!string.IsNullOrEmpty(sourcePath))
            addAttrs.Add($"SourcePath=\"{sourcePath}\"");
        addAttrs.Add($"OfficeClientEdition=\"{edition}\"");
        addAttrs.Add($"Channel=\"{odt.Channel}\"");
        addAttrs.Add("OfficeMgmtCOM=\"TRUE\"");
        addAttrs.Add($"Version=\"{version}\"");
        addAttrs.Add("MigrateArch=\"TRUE\"");
        lines.Add($"  <Add {string.Join(" ", addAttrs)}>");

        foreach (var prodId in odt.ProductIds)
        {
            lines.Add($"    <Product ID=\"{prodId}\">");
            lines.Add("      <Language ID=\"en-us\" />");
            foreach (var exclude in odt.ExcludeApps)
                lines.Add($"      <ExcludeApp ID=\"{exclude}\" />");
            lines.Add("    </Product>");
        }

        lines.Add("  </Add>");
        lines.Add("  <Property Name=\"SharedComputerLicensing\" Value=\"1\" />");
        lines.Add("  <Property Name=\"FORCEAPPSHUTDOWN\" Value=\"TRUE\" />");
        lines.Add("  <Property Name=\"DeviceBasedLicensing\" Value=\"0\" />");
        lines.Add("  <Property Name=\"PinIconsToTaskbar\" Value=\"FALSE\" />");
        lines.Add("  <Property Name=\"SCLCacheOverride\" Value=\"0\" />");
        lines.Add("  <RemoveMSI />");
        lines.Add("  <Display Level=\"None\" AcceptEULA=\"TRUE\" />");
        lines.Add("  <Logging Level=\"Standard\" Path=\"%programdata%\\Appdeploy\\Office2016\" />");
        lines.Add("</Configuration>");

        return string.Join("\r\n", lines);
    }

    private static async Task<bool> RunProcessWithTimeoutAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeout, ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return false;
        }

        return process.ExitCode == 0;
    }

    // ── Standard staging helpers ────────────────────────────────

    /// <summary>
    /// Determines the installer filename: explicit override → URL path → constructed fallback.
    /// </summary>
    private static string ResolveInstallerFileName(AppDefinition def, string downloadUrl, string version)
    {
        // 1. Explicit override in definition (required for fwlinks, query-string URLs, etc.)
        if (!string.IsNullOrEmpty(def.InstallerFileName))
            return def.InstallerFileName
                .Replace("{display_version}", version, StringComparison.OrdinalIgnoreCase)
                .Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

        // 2. Parse from URL path
        var urlFileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
        if (!string.IsNullOrEmpty(urlFileName) && urlFileName.Contains('.'))
            return urlFileName;

        // 3. Fallback: construct from app name + installer type
        var safeName = def.Application.Replace(" ", "").Replace("(", "").Replace(")", "");
        var ext = def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase) ? ".msi" : ".exe";
        return $"{safeName}-{version}{ext}";
    }

    private static StageDetection BuildDetection(AppDefinition def, string? productCode, string version,
        string? rawVersion = null, InstallerMetadata? metadata = null)
    {
        var det = def.Detection;

        // If detection references {ProductCode} but we have no ProductCode, and metadata has detection data,
        // use metadata-derived detection instead of the definition's detection config.
        if (det.RegistryKeyTemplate?.Contains("{ProductCode}", StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrEmpty(productCode)
            && metadata?.HasDetectionData == true)
        {
            return BuildDetectionFromMetadata(metadata, version, def.Detection.Is64Bit);
        }

        if (det.Type.Equals("RegistryKeyValue", StringComparison.OrdinalIgnoreCase))
        {
            // Substitute {ProductCode} and {version} in template
            var regKey = det.RegistryKeyTemplate?
                .Replace("{ProductCode}", productCode ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{version}", version, StringComparison.OrdinalIgnoreCase) ?? "";

            // For Version-type detection, sanitize version to valid System.Version format
            // (e.g., "11.0.30+7" → "11.0.30.7" — CM requires dotted version strings)
            var detValue = version;
            if (det.PropertyType?.Equals("Version", StringComparison.OrdinalIgnoreCase) == true)
                detValue = version.Replace("+", ".").Replace("-", ".");

            return new StageDetection
            {
                Type = "RegistryKeyValue",
                RegistryKeyRelative = regKey,
                ValueName = det.ValueName ?? "DisplayVersion",
                ExpectedValue = detValue,
                Operator = det.Operator ?? "IsEquals",
                PropertyType = det.PropertyType ?? "String",
                Is64Bit = det.Is64Bit
            };
        }

        if (det.Type.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            // For Version-based detection, prefer raw version if it's a valid dotted version format.
            // Raw version may contain prefixes (e.g., "Audacity-3.7.7", "v1.2.3") which are not valid CM versions.
            // For Existence detection, version isn't used for comparison.
            var detVersion = version;
            if (det.PropertyType?.Equals("Version", StringComparison.OrdinalIgnoreCase) == true
                && rawVersion is not null
                && System.Text.RegularExpressions.Regex.IsMatch(rawVersion, @"^\d+[\d.]+$"))
            {
                detVersion = rawVersion;
            }

            return new StageDetection
            {
                Type = "File",
                FilePath = det.FilePath?.Replace("{version}", version, StringComparison.OrdinalIgnoreCase) ?? "",
                FileName = det.FileName ?? "",
                ExpectedValue = detVersion,
                Operator = det.Operator ?? "GreaterEquals",
                PropertyType = det.PropertyType ?? "Version",
                Is64Bit = det.Is64Bit
            };
        }

        if (det.Type.Equals("RegistryKey", StringComparison.OrdinalIgnoreCase))
        {
            var regKey = det.RegistryKeyTemplate?
                .Replace("{version}", version, StringComparison.OrdinalIgnoreCase)
                .Replace("{ProductCode}", productCode ?? "", StringComparison.OrdinalIgnoreCase) ?? "";

            return new StageDetection
            {
                Type = "RegistryKey",
                RegistryKeyRelative = regKey,
                Is64Bit = det.Is64Bit
            };
        }

        return new StageDetection { Type = det.Type };
    }

    /// <summary>
    /// Builds a detection rule from installer metadata extracted by InstallerIdentificationService.
    /// Used as a fallback when the definition's detection config can't be resolved (e.g., {ProductCode} with no MSI).
    /// </summary>
    private static StageDetection BuildDetectionFromMetadata(InstallerMetadata metadata, string version, bool is64Bit)
    {
        if (metadata.DetectionType == "RegistryKeyValue" && !string.IsNullOrEmpty(metadata.DetectionKey))
        {
            var detValue = metadata.DetectionExpectedValue ?? version;
            // Sanitize for Version comparison
            detValue = detValue.Replace("+", ".").Replace("-", ".");

            return new StageDetection
            {
                Type = "RegistryKeyValue",
                RegistryKeyRelative = metadata.DetectionKey,
                ValueName = metadata.DetectionValueName ?? "DisplayVersion",
                ExpectedValue = detValue,
                Operator = "GreaterEquals",
                PropertyType = "Version",
                Is64Bit = is64Bit
            };
        }

        if (metadata.DetectionType == "RegistryKey" && !string.IsNullOrEmpty(metadata.DetectionKey))
        {
            return new StageDetection
            {
                Type = "RegistryKey",
                RegistryKeyRelative = metadata.DetectionKey,
                Is64Bit = is64Bit
            };
        }

        if (metadata.DetectionType == "File" && !string.IsNullOrEmpty(metadata.DetectionFileName))
        {
            return new StageDetection
            {
                Type = "File",
                FilePath = metadata.DetectionFilePath ?? "",
                FileName = metadata.DetectionFileName,
                ExpectedValue = metadata.ProductVersion ?? version,
                Operator = "GreaterEquals",
                PropertyType = "Version",
                Is64Bit = is64Bit
            };
        }

        if (metadata.DetectionType == "MSIX")
        {
            // MSIX detection handled natively by CM — return existence marker
            return new StageDetection
            {
                Type = "MSIX",
                ExpectedValue = metadata.ProductName ?? "",
                Is64Bit = is64Bit
            };
        }

        // Fallback: can't derive detection from metadata
        return new StageDetection { Type = "Unknown" };
    }

    /// <summary>
    /// Resolves InstallerParameter values into a final install arguments string.
    /// Appends CommandFragment for enabled toggles, substitutes {value} for other types.
    /// </summary>
    public static string ResolveInstallerArgs(string baseArgs, InstallerParameter[]? parameters, Dictionary<string, string>? values)
    {
        if (parameters is null || values is null || parameters.Length == 0)
            return baseArgs;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseArgs))
            parts.Add(baseArgs);

        foreach (var param in parameters)
        {
            if (string.IsNullOrEmpty(param.CommandFragment))
                continue;

            if (!values.TryGetValue(param.Key, out var value))
                value = param.DefaultValue;

            if (param.ControlType == "Toggle")
            {
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                    parts.Add(param.CommandFragment.Replace("{value}", "1", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (!string.IsNullOrEmpty(value))
                    parts.Add(param.CommandFragment.Replace("{value}", value, StringComparison.OrdinalIgnoreCase));
            }
        }

        return string.Join(" ", parts);
    }
}
