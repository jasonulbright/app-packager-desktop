namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Identifies installer framework technology and extracts metadata.
/// Uses ArchiveExtractionService for EXE introspection, MsiInspectionService for MSI files.
/// </summary>
public sealed class InstallerIdentificationService
{
    private readonly ArchiveExtractionService _archiveService;
    private readonly MsiInspectionService _msiService;

    public InstallerIdentificationService(ArchiveExtractionService archiveService, MsiInspectionService msiService)
    {
        _archiveService = archiveService;
        _msiService = msiService;
    }

    /// <summary>
    /// Identifies the installer framework for a given installer file.
    /// If the definition already has InstallerFramework set, returns that.
    /// Otherwise, inspects the file to determine the framework.
    /// </summary>
    public async Task<string> IdentifyAsync(string installerPath, string? knownFramework = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(knownFramework) && knownFramework != "Unknown")
            return knownFramework;

        var ext = Path.GetExtension(installerPath).ToLowerInvariant();

        return ext switch
        {
            ".msi" => "NativeMSI",
            ".msix" or ".appx" => "MSIX",
            ".zip" => "ZIP",
            ".exe" => await _archiveService.IdentifyFrameworkAsync(installerPath, ct) ?? "Unknown",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Extracts detection-relevant metadata from an installer based on its framework.
    /// Returns structured metadata that can be used to build detection rules.
    /// </summary>
    public async Task<InstallerMetadata> ExtractMetadataAsync(
        string installerPath, string framework, CancellationToken ct = default)
    {
        return framework switch
        {
            "NativeMSI" => ExtractMsiMetadata(installerPath),
            "NSIS" => await ExtractNsisMetadataAsync(installerPath, ct),
            "InnoSetup" => await ExtractInnoSetupMetadataAsync(installerPath, ct),
            "WixBurn" => await ExtractWixBurnMetadataAsync(installerPath, ct),
            "MSIX" => await ExtractMsixMetadataAsync(installerPath, ct),
            "ZIP" => await ExtractZipMetadataAsync(installerPath, ct),
            _ => new InstallerMetadata { Framework = framework }
        };
    }

    private InstallerMetadata ExtractMsiMetadata(string msiPath)
    {
        var props = _msiService.ReadProperties(msiPath);
        return new InstallerMetadata
        {
            Framework = "NativeMSI",
            ProductCode = props.ProductCode,
            ProductVersion = props.ProductVersion,
            ProductName = props.ProductName,
            Manufacturer = props.Manufacturer,
            DetectionType = "RegistryKeyValue",
            DetectionKey = $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{props.ProductCode}",
            DetectionValueName = "DisplayVersion",
            DetectionExpectedValue = props.ProductVersion
        };
    }

    private async Task<InstallerMetadata> ExtractNsisMetadataAsync(string exePath, CancellationToken ct)
    {
        using var result = await _archiveService.ExtractAsync(exePath, ct);
        if (!result.Success)
            return new InstallerMetadata { Framework = "NSIS" };
        var tempDir = result.OutputDirectory!;
        {

            // JetBrains product-info.json
            var productInfoPath = Path.Combine(tempDir, "product-info.json");
            if (File.Exists(productInfoPath))
            {
                var json = await File.ReadAllTextAsync(productInfoPath, ct);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
                var buildNumber = root.TryGetProperty("buildNumber", out var b) ? b.GetString() : null;
                var vendor = root.TryGetProperty("productVendor", out var pv) ? pv.GetString() : null;

                return new InstallerMetadata
                {
                    Framework = "NSIS",
                    ProductName = name,
                    ProductVersion = version,
                    BuildNumber = buildNumber,
                    Manufacturer = vendor,
                    DetectionType = "RegistryKey",
                    DetectionKey = $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{name} {version}",
                    MetadataSource = "product-info.json"
                };
            }

            var exes = Directory.GetFiles(tempDir, "*.exe", SearchOption.AllDirectories)
                .Where(f => !f.Contains("unins", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains("$PLUGINSDIR", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (exes.Length > 0)
            {
                var mainExe = exes.OrderByDescending(f => new FileInfo(f).Length).First();
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(mainExe);
                return new InstallerMetadata
                {
                    Framework = "NSIS",
                    ProductName = vi.ProductName,
                    ProductVersion = vi.ProductVersion,
                    Manufacturer = vi.CompanyName,
                    DetectionType = "File",
                    DetectionFilePath = Path.GetDirectoryName(mainExe)?.Replace(tempDir, "C:\\Program Files"),
                    DetectionFileName = Path.GetFileName(mainExe),
                    MetadataSource = "primary-exe-version"
                };
            }

            return new InstallerMetadata { Framework = "NSIS" };
        }
    }

    private async Task<InstallerMetadata> ExtractInnoSetupMetadataAsync(string exePath, CancellationToken ct)
    {
        // An Inno Setup uninstall key is <AppId>_is1, and AppId defaults to AppName.
        using var result = await _archiveService.ExtractAsync(exePath, ct);
        if (!result.Success)
            return new InstallerMetadata { Framework = "InnoSetup" };
        var tempDir = result.OutputDirectory!;
        {

            var appDir = Path.Combine(tempDir, "{app}");
            if (Directory.Exists(appDir))
            {
                var exes = Directory.GetFiles(appDir, "*.exe")
                    .Where(f => !f.Contains("unins", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (exes.Length > 0)
                {
                    var mainExe = exes.OrderByDescending(f => new FileInfo(f).Length).First();
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(mainExe);
                    return new InstallerMetadata
                    {
                        Framework = "InnoSetup",
                        ProductName = vi.ProductName,
                        ProductVersion = vi.ProductVersion,
                        Manufacturer = vi.CompanyName,
                        DetectionType = "RegistryKey",
                        DetectionKey = $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{vi.ProductName}_is1",
                        MetadataSource = "innosetup-app-exe"
                    };
                }
            }

            return new InstallerMetadata { Framework = "InnoSetup" };
        }
    }

    private async Task<InstallerMetadata> ExtractWixBurnMetadataAsync(string exePath, CancellationToken ct)
    {
        // WiX Burn: extract EXE → parse BurnManifest XML for ProductCode,
        // or find inner .msi and read MSI tables as fallback.
        using var result = await _archiveService.ExtractAsync(exePath, ct);
        if (!result.Success)
            return new InstallerMetadata { Framework = "WixBurn" };
        var tempDir = result.OutputDirectory!;
        {

            // Strategy 1: Parse BurnManifest XML (file "0") for MsiPackage ProductCode.
            // Burn EXEs extract to numbered files without extensions. File "0" is the manifest.
            var burnManifestPath = Path.Combine(tempDir, "0");
            if (File.Exists(burnManifestPath))
            {
                try
                {
                    var doc = new System.Xml.XmlDocument();
                    doc.Load(burnManifestPath);
                    var nsMgr = new System.Xml.XmlNamespaceManager(doc.NameTable);
                    nsMgr.AddNamespace("burn", "http://schemas.microsoft.com/wix/2008/Burn");

                    // Find MsiPackage elements — pick the non-Permanent one (main product, not prereqs)
                    var msiPackages = doc.SelectNodes("//burn:MsiPackage", nsMgr);
                    if (msiPackages != null)
                    {
                        foreach (System.Xml.XmlNode pkg in msiPackages)
                        {
                            var permanent = pkg.Attributes?["Permanent"]?.Value;
                            var productCode = pkg.Attributes?["ProductCode"]?.Value;
                            var version = pkg.Attributes?["Version"]?.Value;
                            if (permanent == "yes" || string.IsNullOrEmpty(productCode))
                                continue;

                            var arpNode = doc.SelectSingleNode("//burn:Registration/burn:Arp", nsMgr);
                            var publisher = arpNode?.Attributes?["Publisher"]?.Value;
                            var displayName = arpNode?.Attributes?["DisplayName"]?.Value;

                            return new InstallerMetadata
                            {
                                Framework = "WixBurn",
                                ProductCode = productCode,
                                ProductVersion = version,
                                ProductName = displayName,
                                Manufacturer = publisher,
                                DetectionType = "RegistryKeyValue",
                                DetectionKey = $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{productCode}",
                                DetectionValueName = "DisplayVersion",
                                DetectionExpectedValue = version,
                                MetadataSource = "BurnManifest-MsiPackage"
                            };
                        }
                    }
                }
                catch { /* Fall through to MSI file search */ }
            }

            // Strategy 2: Find inner .msi files (works when Burn extracts with extensions)
            var msiFiles = Directory.GetFiles(tempDir, "*.msi", SearchOption.AllDirectories);
            if (msiFiles.Length == 0)
                return new InstallerMetadata { Framework = "WixBurn", MetadataSource = "no-inner-msi-no-manifest" };

            var mainMsi = msiFiles.OrderByDescending(f => new FileInfo(f).Length).First();
            var msiProps = _msiService.ReadProperties(mainMsi);

            return new InstallerMetadata
            {
                Framework = "WixBurn",
                ProductCode = msiProps.ProductCode,
                ProductVersion = msiProps.ProductVersion,
                ProductName = msiProps.ProductName,
                Manufacturer = msiProps.Manufacturer,
                DetectionType = "RegistryKeyValue",
                DetectionKey = $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{msiProps.ProductCode}",
                DetectionValueName = "DisplayVersion",
                DetectionExpectedValue = msiProps.ProductVersion,
                MetadataSource = $"inner-msi:{Path.GetFileName(mainMsi)}"
            };
        }
    }

    private static Task<InstallerMetadata> ExtractMsixMetadataAsync(string msixPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        MsixIdentity identity;
        try { identity = new MsixPackageReader().ReadIdentity(msixPath); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or System.Xml.XmlException or InstallerAnalysisException)
        {
            return Task.FromResult(new InstallerMetadata { Framework = "MSIX" });
        }
        return Task.FromResult(new InstallerMetadata
        {
            Framework = "MSIX",
            ProductName = identity.Name,
            ProductVersion = identity.Version,
            Manufacturer = identity.Publisher,
            DetectionType = "MSIX",
            MetadataSource = "AppxManifest.xml"
        });
    }

    private async Task<InstallerMetadata> ExtractZipMetadataAsync(string zipPath, CancellationToken ct)
    {
        using var result = await _archiveService.ExtractAsync(zipPath, ct);
        if (!result.Success)
            return new InstallerMetadata { Framework = "ZIP" };
        var tempDir = result.OutputDirectory!;
        {

            var exes = Directory.GetFiles(tempDir, "*.exe", SearchOption.AllDirectories);
            if (exes.Length > 0)
            {
                var mainExe = exes.OrderByDescending(f => new FileInfo(f).Length).First();
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(mainExe);
                return new InstallerMetadata
                {
                    Framework = "ZIP",
                    ProductName = vi.ProductName,
                    ProductVersion = vi.FileVersion,
                    DetectionType = "File",
                    DetectionFileName = Path.GetFileName(mainExe),
                    MetadataSource = "zip-primary-exe"
                };
            }

            return new InstallerMetadata { Framework = "ZIP" };
        }
    }
}

/// <summary>
/// Metadata extracted from an installer, used to auto-derive detection rules.
/// </summary>
public sealed class InstallerMetadata
{
    public string Framework { get; init; } = "Unknown";
    public string? ProductCode { get; init; }
    public string? ProductVersion { get; init; }
    public string? ProductName { get; init; }
    public string? Manufacturer { get; init; }
    public string? BuildNumber { get; init; }

    /// <summary>Recommended detection type: RegistryKeyValue, RegistryKey, File, MSIX</summary>
    public string? DetectionType { get; init; }
    /// <summary>Registry key path for RegistryKeyValue/RegistryKey detection</summary>
    public string? DetectionKey { get; init; }
    /// <summary>Registry value name (e.g., DisplayVersion)</summary>
    public string? DetectionValueName { get; init; }
    /// <summary>Expected value for comparison</summary>
    public string? DetectionExpectedValue { get; init; }
    /// <summary>File path for File detection</summary>
    public string? DetectionFilePath { get; init; }
    /// <summary>File name for File detection</summary>
    public string? DetectionFileName { get; init; }

    /// <summary>Where the metadata was sourced from (for diagnostics)</summary>
    public string? MetadataSource { get; init; }

    /// <summary>Whether enough metadata was extracted to build a detection rule automatically</summary>
    public bool HasDetectionData =>
        !string.IsNullOrEmpty(DetectionType) &&
        (!string.IsNullOrEmpty(DetectionKey) || !string.IsNullOrEmpty(DetectionFileName));
}
