using System.IO.Compression;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.InstallerScripts;
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
    private readonly MsixPackageReader _msixReader = new();

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
    /// <summary>
    /// A vendor endpoint that answers HTTP 200 with an HTML interstitial
    /// would otherwise stage a web page as the installer (seen live with
    /// CPU-Z). EXE payloads must start MZ; MSI payloads must start with
    /// the OLE compound-file magic.
    /// </summary>
    public static void ValidateInstallerMagic(string path, string installerType)
    {
        var type = installerType?.ToUpperInvariant();
        if (type == "MSIX")
        {
            MsixPackageReader.ValidateContainer(path);
            return;
        }
        if (type is not ("EXE" or "MSI")) return;
        if (!File.Exists(path)) return;

        Span<byte> head = stackalloc byte[8];
        using (var fs = File.OpenRead(path))
        {
            var read = fs.Read(head);
            if (read < 8) throw new InvalidOperationException($"Downloaded installer is only {read} bytes: {Path.GetFileName(path)}");
        }

        var ok = type == "EXE"
            ? head[0] == (byte)'M' && head[1] == (byte)'Z'
            : head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;
        if (!ok)
        {
            var preview = System.Text.Encoding.ASCII.GetString(head.ToArray()).Replace('\0', '.');
            throw new InvalidOperationException(
                $"Downloaded file is not a valid {type} (starts with '{preview}') - the vendor endpoint likely returned an HTML page: {Path.GetFileName(path)}");
        }
    }

    /// <summary>
    /// Computes the SHA256 of a staged file as an uppercase hex string.
    /// </summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>
    /// Verifies a downloaded payload against a vendor-published SHA256. A
    /// mismatch means the payload is not the build the feed described — a
    /// truncated transfer, a mirror serving a different build, or a
    /// substituted file — and staging must not continue on it.
    /// A null or empty expected hash is not a verification and passes.
    /// </summary>
    public static void VerifyPayloadHash(string path, string? expectedSha256, Action<string>? onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return;

        var expected = expectedSha256.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(expected, "^[0-9A-F]{64}$"))
            throw new InvalidOperationException(
                $"Expected payload SHA256 is not a 64-character hex digest: '{expectedSha256}'.");

        if (!File.Exists(path))
            throw new FileNotFoundException($"Cannot verify SHA256 of a missing payload: {path}", path);

        var actual = ComputeSha256(path);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Payload SHA256 '{actual}' does not match the expected value '{expected}': {Path.GetFileName(path)}");

        onProgress?.Invoke($"Payload SHA256 verified: {actual}");
    }

    private static readonly HttpClient HashFeedClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string HashFeedUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>
    /// Resolves the vendor-published payload hash from the version feed.
    /// The feed is re-read rather than threaded through the scraper so the
    /// scraper contract stays version-and-URL only.
    /// Returns null when the definition declares no hash capture.
    /// </summary>
    public static async Task<string?> ResolveFeedHashAsync(
        VersionScraperConfig config, string? version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.PayloadHashQuery)) return null;
        if (string.IsNullOrWhiteSpace(config.Url)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, config.Url);
        if (!config.NoUserAgent)
            request.Headers.TryAddWithoutValidation("User-Agent", HashFeedUserAgent);

        using var response = await HashFeedClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(ct);

        return ExtractFeedHash(content, config, version);
    }

    /// <summary>
    /// Applies the hash capture to already-fetched feed content. JsonQuery
    /// definitions resolve through the JSON path evaluator; every other
    /// scraper type treats the capture as a regex over the raw text.
    /// </summary>
    public static string? ExtractFeedHash(string content, VersionScraperConfig config, string? version)
    {
        var query = config.PayloadHashQuery;
        if (string.IsNullOrWhiteSpace(query)) return null;

        if (version is not null)
            query = query.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

        if (config.Type.Equals("JsonQuery", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            return JsonQueryScraper.EvaluateQuery(doc.RootElement, query);
        }

        var match = System.Text.RegularExpressions.Regex.Match(content, query);
        if (!match.Success) return null;

        var named = match.Groups["hash"];
        if (named.Success) return named.Value;
        return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
    }

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
        if (scraper is Scrapers.FileVersionScraper && config.Hops is { Count: > 0 })
            return await Scrapers.FileVersionScraper.ResolveDownloadUrlAsync(config, ct);
        if (scraper is Scrapers.JsonQueryScraper jqs)
        {
            var (_, url) = await jqs.GetVersionAndDownloadUrlAsync(config, ct);
            return url;
        }
        if (scraper is Scrapers.HttpRegexScraper hrs)
        {
            var (_, url) = await hrs.GetVersionAndDownloadUrlAsync(config, ct);
            // A captured relative URL is resolved against the scraper URL:
            // a leading '/' is origin-relative, anything else is directory-relative.
            if (url is not null && !url.Contains("://"))
            {
                if (url.StartsWith('/'))
                {
                    var origin = new Uri(config.Url).GetLeftPart(UriPartial.Authority);
                    url = origin + url;
                }
                else
                {
                    var baseUrl = config.Url;
                    if (!baseUrl.EndsWith('/')) baseUrl = baseUrl[..(baseUrl.LastIndexOf('/') + 1)];
                    url = baseUrl + url;
                }
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
        string? knownVersion = null,
        Dictionary<string, string>? settingsValues = null,
        string? installMode = null)
    {
        // Route ODT apps to dedicated pipeline
        if (def.InstallerType.Equals("ODT", StringComparison.OrdinalIgnoreCase))
            return await StageOdtAsync(def, downloadRoot, onProgress, ct, knownVersion, settingsValues);

        if (def.BootstrapperLayout is not null)
            return await StageBootstrapperLayoutAsync(def, downloadRoot, onProgress, ct, knownVersion);

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
                // Check if FileVersionScraper already downloaded this file during Check Latest.
                // A hop-resolved scrape cached the file under the resolved URL's name.
                var cachedPath = def.VersionScraper.Type.Equals("FileVersion", StringComparison.OrdinalIgnoreCase)
                    ? Scrapers.FileVersionScraper.GetCachedPath(
                        def.VersionScraper.Hops is { Count: > 0 } ? downloadUrl : def.VersionScraper.Url)
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
                    ValidateInstallerMagic(installerPath, def.InstallerType);
                }
            }
            else
            {
                onProgress?.Invoke($"Installer already cached: {installerFileName}");
            }

            // 3b. Payload integrity. A pinned hash wins over the feed value:
            // it is the stronger statement, and a definition carrying both is
            // asserting the pin. A cached payload is verified too, so a bad
            // prior download does not stage on a later run.
            var expectedHash = def.PayloadSha256;
            if (string.IsNullOrWhiteSpace(expectedHash))
                expectedHash = await ResolveFeedHashAsync(def.VersionScraper, rawVersion, ct);
            try
            {
                VerifyPayloadHash(installerPath, expectedHash, onProgress);
            }
            catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(expectedHash))
            {
                // A torn download (a CDN once served a short body) fails the
                // hash on every later run while the file sits in the cache.
                // One fresh download separates that from a vendor-side change.
                onProgress?.Invoke($"Payload SHA256 mismatch; discarding the cached file and downloading once more: {installerFileName}");
                File.Delete(installerPath);
                await _downloadService.DownloadAsync(downloadUrl, installerPath,
                    onProgress: onProgress, ct: ct);
                ValidateInstallerMagic(installerPath, def.InstallerType);
                VerifyPayloadHash(installerPath, expectedHash, onProgress);
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

            var isMsix = def.InstallerType.Equals("MSIX", StringComparison.OrdinalIgnoreCase);

            // 4b. Inspect installer (MSI only for now)
            string? productCode = null;
            string? productVersion = null;
            string publisher = def.Vendor;

            StageMsixPackage? msixPackage = null;
            if (isMsix)
            {
                onProgress?.Invoke("Reading MSIX package identity...");
                MsixPackageReader.ValidateContainer(installerPath);
                var identity = _msixReader.ReadIdentity(installerPath);
                onProgress?.Invoke($"MSIX identity name           : {identity.Name}");
                onProgress?.Invoke($"MSIX identity version        : {identity.Version}");
                onProgress?.Invoke($"MSIX architecture            : {identity.ProcessorArchitecture}");

                if (!string.IsNullOrWhiteSpace(identity.ProcessorArchitecture)
                    && !string.IsNullOrWhiteSpace(def.Architecture)
                    && !identity.ProcessorArchitecture.Equals(def.Architecture, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"MSIX architecture is '{identity.ProcessorArchitecture}' but the definition declares '{def.Architecture}'.");

                // The AppX cmdlets refuse an unsigned or untrusted package, so
                // the signer is pinned into the install wrapper at stage time.
                var signature = _msixReader.ReadSignature(installerPath)
                    ?? throw new InvalidOperationException("MSIX package carries no signature; refusing to stage an unsigned package.");
                if (!signature.SignatureValid)
                    throw new InvalidOperationException("MSIX signature did not verify against the package contents.");
                onProgress?.Invoke($"MSIX signer thumbprint       : {signature.Thumbprint}");
                if (!signature.ChainTrusted)
                    onProgress?.Invoke("MSIX signer does not chain to a trusted root on this machine; the target device must trust it.");

                msixPackage = new StageMsixPackage
                {
                    Name = identity.Name,
                    Version = identity.Version,
                    Publisher = identity.Publisher,
                    ProcessorArchitecture = identity.ProcessorArchitecture,
                    SignerThumbprint = signature.Thumbprint,
                    SignerSubject = signature.Subject,
                    Provisioned = true
                };
            }

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
            if (productCode is null && _installerIdService is not null && !isMsix &&
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
                    // A manufacturer read off the wrong bundled EXE is as wrong as its name.
                    if (!string.IsNullOrEmpty(installerMetadata.Manufacturer) &&
                        installerMetadata.Manufacturer.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                        IsProductNamePlausible(installerMetadata, def))
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
            // InstallerSettings are the single source for the switches they own: the
            // definition's InstallArgs carries only the switches no parameter covers.
            var installArgs = ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, settingsValues);
            var uninstallArgs = ResolveUninstallArgs(
                ApplySettingTokens(def.UninstallArgs, def.InstallerSettings, settingsValues) ?? def.UninstallArgs,
                def.InstallerSettings, settingsValues);
            var uninstallCommand = ApplySettingTokens(def.UninstallCommand, def.InstallerSettings, settingsValues);

            // 6. Build detection config
            // If the definition's detection references {ProductCode} but we have no ProductCode,
            // and we have installer metadata with detection data, use the metadata instead.
            // Built ahead of the wrappers: the EXE uninstall wrapper takes its
            // Add/Remove Programs lookup hints from the resolved detection rule.
            var detection = msixPackage is not null
                ? BuildMsixScriptDetection(msixPackage)
                : BuildDetection(def, productCode, displayVersion, productVersion ?? rawVersion, installerMetadata, settingsValues);

            // 6a. Install mode. A definition that declares both modes lets the
            // operator pick System or User; the staged installer's compiled
            // script supplies that branch, and switch, uninstaller, detection
            // and context move together with it.
            var installContext = def.InstallContext;
            string? modeUninstallCommandLine = null;
            if (!string.IsNullOrWhiteSpace(installMode))
            {
                var applied = ApplyInstallMode(def, installerPath, installMode, installArgs, detection, onProgress, uninstallCommand);
                installArgs = applied.InstallArgs;
                if (applied.UninstallExpression is not null) uninstallCommand = applied.UninstallExpression;
                modeUninstallCommandLine = applied.UninstallCommandLine;
                detection = applied.Detection;
                installContext = applied.InstallContext;
            }

            onProgress?.Invoke("Generating install/uninstall wrappers...");
            if (isMsix)
            {
                _wrapperService.GenerateMsixWrappers(contentPath, installerFileName,
                    msixPackage!.SignerThumbprint, msixPackage.Provisioned);
            }
            else if (def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase))
            {
                _wrapperService.GenerateMsiWrappers(contentPath, installerFileName, uninstallCommand, installArgs);
            }
            else if (def.ExtractSetup is not null)
            {
                _wrapperService.GenerateExtractSetupWrappers(contentPath, installerFileName,
                    def.ExtractSetup, installArgs, uninstallArgs);
            }
            else
            {
                var (arpKeyName, installFolder) = WrapperGeneratorService.ResolveUninstallHints(detection);
                _wrapperService.GenerateExeWrappers(contentPath, installerFileName,
                    installArgs, uninstallArgs, def.InstallerFramework, def.Application, uninstallCommand,
                    ResolvePostInstallScript(def, settingsValues), arpKeyName, installFolder);
            }

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
            // ARP-driven naming: use installer metadata when available, fallback to constructed template.
            // Metadata read off the largest bundled EXE names whatever that EXE is
            // (a bundled 7-Zip, a GnuPG component), so it is used only when it
            // names this product.
            var trustMetadataName = installerMetadata is not null
                && IsProductNamePlausible(installerMetadata, def);
            var arpDisplayName = trustMetadataName ? installerMetadata!.ProductName : null;
            var arpVersion = trustMetadataName ? installerMetadata!.ProductVersion : null;
            if (installerMetadata?.ProductName is { Length: > 0 } && !trustMetadataName)
                onProgress?.Invoke($"Installer metadata names '{installerMetadata.ProductName}' ({installerMetadata.MetadataSource}); keeping the definition's name.");
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
                InstallContext = installContext,
                InstallMode = string.IsNullOrWhiteSpace(installMode) ? null : installMode,
                InstallArgs = installArgs,
                UninstallArgs = uninstallArgs,
                UninstallCommand = modeUninstallCommandLine,
                ProductCode = productCode,
                RunningProcess = def.RunningProcess,
                Detection = detection,
                MsixPackage = msixPackage
            };
            IconExtractionService.StageIcon(def, contentPath, manifest, onProgress);
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

    // ── Bootstrapper /layout staging pipeline ────────────────────

    /// <summary>
    /// Stages a vendor bootstrapper as a complete offline layout: download the
    /// bootstrapper, run it with the layout switch, and ship the resulting tree
    /// as the package content so no client reaches the vendor at install time.
    /// Detection comes from a designated MSI inside the layout, whose
    /// ProductVersion is the kit version and whose ProductCode is the ARP key,
    /// so no probe install is needed.
    /// </summary>
    private async Task<StagingResult> StageBootstrapperLayoutAsync(
        AppDefinition def, string downloadRoot,
        Action<string>? onProgress, CancellationToken ct, string? knownVersion)
    {
        try
        {
            var layout = def.BootstrapperLayout!;
            ValidateBootstrapperLayout(layout);

            string? version = knownVersion;
            if (string.IsNullOrEmpty(version))
            {
                onProgress?.Invoke($"Checking latest version for {def.Application}...");
                var (_, display) = await _scrapers.GetLatestVersionWithTransformAsync(def.VersionScraper, ct);
                version = display;
            }
            if (string.IsNullOrEmpty(version))
                return new StagingResult { Success = false, ErrorMessage = "Could not determine latest version" };
            onProgress?.Invoke($"Version: {version}");

            var appFolder = $"{def.Application.Replace(" ", "")}-{def.Architecture}";
            var baseRoot = Path.Combine(downloadRoot, appFolder);
            var contentPath = Path.Combine(baseRoot, version);

            AssertLayoutPathLength(contentPath, layout.MaxLayoutPathLength);
            Directory.CreateDirectory(contentPath);

            // The bundle refuses a layout target beneath the folder it runs from
            // (exit 2002), so the bootstrapper is kept outside the layout root.
            var bootstrapperFolder = Path.Combine(baseRoot, "bootstrapper");
            Directory.CreateDirectory(bootstrapperFolder);

            var bootstrapperFileName = WrapperGeneratorService.ResolveBootstrapperFileName(layout);
            var bootstrapperPath = Path.Combine(bootstrapperFolder, bootstrapperFileName);
            var bootstrapperUrl = !string.IsNullOrWhiteSpace(layout.BootstrapperUrl)
                ? layout.BootstrapperUrl
                : def.DownloadUrl;

            if (!File.Exists(bootstrapperPath))
            {
                var cachedPath = def.VersionScraper.Type.Equals("FileVersion", StringComparison.OrdinalIgnoreCase)
                    ? Scrapers.FileVersionScraper.GetCachedPath(def.VersionScraper.Url)
                    : null;

                if (cachedPath is not null)
                {
                    File.Copy(cachedPath, bootstrapperPath);
                    onProgress?.Invoke($"Copied bootstrapper from version cache: {bootstrapperFileName}");
                }
                else
                {
                    await _downloadService.DownloadAsync(bootstrapperUrl, bootstrapperPath, onProgress: onProgress, ct: ct);
                }
                ValidateInstallerMagic(bootstrapperPath, "EXE");
            }
            else
            {
                onProgress?.Invoke($"Bootstrapper already cached: {bootstrapperFileName}");
            }

            // 2. Build the offline layout. The bundle re-verifies every payload
            //    over an existing layout, so a complete one is left alone.
            var layoutSetup = Path.Combine(contentPath, bootstrapperFileName);
            var layoutInstallers = Path.Combine(contentPath, "Installers");
            if (File.Exists(layoutSetup) && Directory.Exists(layoutInstallers))
            {
                onProgress?.Invoke("Offline layout exists. Skipping layout build.");
            }
            else
            {
                onProgress?.Invoke("Building offline layout (this downloads the full kit)...");
                var args = $"{layout.QuietSwitch} {layout.LayoutSwitch} \"{contentPath}\"";
                var exitCode = await RunInstallerProcessAsync(bootstrapperPath, args, TimeSpan.FromHours(2), ct, onProgress);
                if (exitCode is null)
                    return new StagingResult { Success = false, ErrorMessage = $"{bootstrapperFileName} {layout.LayoutSwitch} timed out" };
                if (exitCode != 0)
                    return new StagingResult { Success = false, ErrorMessage = $"{bootstrapperFileName} {layout.LayoutSwitch} failed (exit {exitCode})" };

                if (!File.Exists(layoutSetup))
                    return new StagingResult { Success = false, ErrorMessage = $"Layout completed but {bootstrapperFileName} is missing from {contentPath}" };
                if (!Directory.Exists(layoutInstallers))
                    return new StagingResult { Success = false, ErrorMessage = $"Layout completed but the Installers folder is missing from {contentPath}" };
            }

            // 3. Derive detection from the layout's own feature MSI.
            var versionMsiPath = ResolveVersionSourceMsi(contentPath, layout.VersionSourceMsi);
            onProgress?.Invoke($"Version source MSI: {Path.GetFileName(versionMsiPath)}");
            var msiProps = _msiService.ReadProperties(versionMsiPath);
            if (string.IsNullOrWhiteSpace(msiProps.ProductVersion))
                return new StagingResult { Success = false, ErrorMessage = "Version source MSI carries no ProductVersion." };
            if (string.IsNullOrWhiteSpace(msiProps.ProductCode))
                return new StagingResult { Success = false, ErrorMessage = "Version source MSI carries no ProductCode." };
            onProgress?.Invoke($"ProductVersion: {msiProps.ProductVersion}");
            onProgress?.Invoke($"ProductCode: {msiProps.ProductCode}");

            var publisher = !string.IsNullOrWhiteSpace(msiProps.Manufacturer) &&
                            msiProps.Manufacturer.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                ? msiProps.Manufacturer
                : def.Vendor;

            onProgress?.Invoke("Generating install/uninstall wrappers...");
            _wrapperService.GenerateBootstrapperLayoutWrappers(contentPath, layout);

            var detection = BuildDetection(def, msiProps.ProductCode, msiProps.ProductVersion, msiProps.ProductVersion);

            var manifestPath = Path.Combine(contentPath, "stage-manifest.json");
            var manifest = new StageManifest
            {
                AppName = $"{def.Application} - {version} ({def.Architecture})",
                DisplayName = $"{def.Application} ({def.Architecture})",
                Publisher = publisher,
                SoftwareVersion = version,
                InstallerFile = bootstrapperFileName,
                InstallerType = "EXE",
                InstallContext = def.InstallContext,
                InstallArgs = BuildLayoutInstallArgs(layout),
                UninstallArgs = $"{layout.QuietSwitch} {layout.UninstallSwitch}",
                UninstallCommand = $"{bootstrapperFileName} {layout.QuietSwitch} {layout.UninstallSwitch}",
                ProductCode = msiProps.ProductCode,
                RunningProcess = def.RunningProcess,
                Detection = detection
            };
            IconExtractionService.StageIcon(def, contentPath, manifest, onProgress);
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
    /// The install command line recorded in the manifest, matching what the
    /// generated install wrapper runs.
    /// </summary>
    public static string BuildLayoutInstallArgs(BootstrapperLayoutConfig layout)
    {
        var parts = new List<string> { layout.QuietSwitch };
        if (!string.IsNullOrWhiteSpace(layout.InstallPath))
        {
            parts.Add("/installpath");
            parts.Add($"\"{layout.InstallPath}\"");
        }
        if (layout.Features.Length > 0)
        {
            parts.Add("/features");
            parts.AddRange(layout.Features);
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The bundle validates the layout root against the longest payload name it
    /// will write and refuses with exit 2002 when the result would approach
    /// MAX_PATH. Failing here names the cause instead.
    /// </summary>
    public static void AssertLayoutPathLength(string layoutPath, int maxLength)
    {
        if (layoutPath.Length > maxLength)
            throw new InvalidOperationException(
                $"Layout path is too long for the bundle ({layoutPath.Length} chars, limit {maxLength}): {layoutPath}. Use a shorter download root.");
    }

    public static void ValidateBootstrapperLayout(BootstrapperLayoutConfig layout)
    {
        if (string.IsNullOrWhiteSpace(layout.BootstrapperFileName))
            throw new InvalidOperationException("BootstrapperLayout.BootstrapperFileName is required.");
        if (string.IsNullOrWhiteSpace(layout.VersionSourceMsi))
            throw new InvalidOperationException("BootstrapperLayout.VersionSourceMsi is required: detection is anchored to it.");
        if (string.IsNullOrWhiteSpace(layout.LayoutSwitch))
            throw new InvalidOperationException("BootstrapperLayout.LayoutSwitch is required.");
        if (layout.MaxLayoutPathLength <= 0)
            throw new InvalidOperationException("BootstrapperLayout.MaxLayoutPathLength must be positive.");
    }

    /// <summary>
    /// Resolves the detection anchor inside a built layout. A pattern must
    /// match exactly one file: several matches mean the anchor no longer
    /// identifies a single feature MSI and the derived detection would be
    /// whichever the file system happened to return first.
    /// </summary>
    public static string ResolveVersionSourceMsi(string layoutPath, string relativePathOrPattern)
    {
        var normalized = relativePathOrPattern.Replace('/', Path.DirectorySeparatorChar);

        if (!normalized.Contains('*') && !normalized.Contains('?'))
        {
            var direct = Path.Combine(layoutPath, normalized);
            if (!File.Exists(direct))
                throw new InvalidOperationException($"Detection anchor missing from the layout: {direct}");
            return direct;
        }

        var searchDir = Path.Combine(layoutPath, Path.GetDirectoryName(normalized) ?? "");
        var pattern = Path.GetFileName(normalized);
        var matches = Directory.Exists(searchDir)
            ? Directory.GetFiles(searchDir, pattern, SearchOption.TopDirectoryOnly)
            : [];

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"Detection anchor pattern matched no file in the layout: {Path.Combine(searchDir, pattern)}"),
            _ => throw new InvalidOperationException(
                $"Detection anchor pattern matched {matches.Length} files in the layout; it must identify exactly one: {Path.Combine(searchDir, pattern)}")
        };
    }

    // ── ODT staging pipeline ─────────────────────────────────────

    private const string OdtSetupUrl = "https://officecdn.microsoft.com/pr/wsus/setup.exe";

    /// <summary>Setting keys the ODT pipeline reads from the chosen values.</summary>
    public const string OdtChannelKey = "Channel";
    public const string OdtDeployModeKey = "DeployMode";
    public const string OdtExcludePrefix = "Exclude_";

    /// <summary>
    /// The definition's ODT configuration with the chosen values applied: the
    /// update channel and deploy mode when chosen, and the excluded apps taken
    /// from the Exclude_* toggles whenever the definition declares any.
    /// </summary>
    public static OdtConfig ResolveOdtConfig(AppDefinition def, Dictionary<string, string>? values)
    {
        var odt = def.Odt ?? new OdtConfig();
        var resolved = new OdtConfig
        {
            ProductIds = odt.ProductIds,
            UninstallProductId = odt.UninstallProductId,
            Channel = odt.Channel,
            ExcludeApps = odt.ExcludeApps,
            DeploymentMode = odt.DeploymentMode,
            UpdateChannel = odt.UpdateChannel,
            EnableUpdates = odt.EnableUpdates,
            AcceptEula = odt.AcceptEula,
            PinToTaskbar = odt.PinToTaskbar,
            SharedComputerLicensing = odt.SharedComputerLicensing
        };
        if (values is null) return resolved;

        if (values.TryGetValue(OdtChannelKey, out var channel) && !string.IsNullOrWhiteSpace(channel))
            resolved.Channel = channel.Trim();

        var modeParam = def.InstallerSettings?.FirstOrDefault(p => p.Key.Equals(OdtDeployModeKey, StringComparison.OrdinalIgnoreCase));
        var mode = values.TryGetValue(OdtDeployModeKey, out var chosenMode) ? chosenMode : modeParam?.DefaultValue;
        if (!string.IsNullOrWhiteSpace(mode))
            resolved.DeploymentMode = mode.Equals("Online", StringComparison.OrdinalIgnoreCase) ? "Online" : "Offline";

        var excludeParams = def.InstallerSettings?
            .Where(p => p.Key.StartsWith(OdtExcludePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (excludeParams is { Count: > 0 })
        {
            resolved.ExcludeApps = excludeParams
                .Where(p => (values.TryGetValue(p.Key, out var v) ? v : p.DefaultValue).Equals("true", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key[OdtExcludePrefix.Length..])
                .ToArray();
        }
        return resolved;
    }

    private async Task<StagingResult> StageOdtAsync(
        AppDefinition def, string downloadRoot,
        Action<string>? onProgress, CancellationToken ct, string? knownVersion,
        Dictionary<string, string>? settingsValues = null)
    {
        try
        {
            var odt = def.Odt is null ? null : ResolveOdtConfig(def, settingsValues);
            if (odt is null || odt.ProductIds.Length == 0)
                return new StagingResult { Success = false, ErrorMessage = "OdtConfig with ProductIds is required for ODT apps" };
            var online = odt.DeploymentMode.Equals("Online", StringComparison.OrdinalIgnoreCase);
            onProgress?.Invoke($"ODT channel {odt.Channel}, {(online ? "Online (CDN install)" : "Offline (source downloaded)")}, excluded: {(odt.ExcludeApps.Length == 0 ? "none" : string.Join(", ", odt.ExcludeApps))}");

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

            // 4 and 5. Offline: download.xml and setup.exe /download bring the source into
            // the content folder. Online: the client installs from the CDN, so the content
            // is setup.exe and the xml files only.
            if (!online)
            {
                var downloadXml = GenerateOdtXml(edition, fullVersion, odt, contentPath, pinVersion: true);
                var downloadXmlPath = Path.Combine(baseRoot, "download.xml");
                await File.WriteAllTextAsync(downloadXmlPath, downloadXml, System.Text.Encoding.ASCII, ct);
                onProgress?.Invoke("Generated download.xml");

                onProgress?.Invoke("Downloading Office source files (this may take several minutes)...");
                var dlResult = await RunProcessWithTimeoutAsync(
                    setupExe, $"/download \"{downloadXmlPath}\"",
                    TimeSpan.FromMinutes(30), ct);
                if (!dlResult)
                    return new StagingResult { Success = false, ErrorMessage = "ODT /download failed" };
                onProgress?.Invoke("Office source download complete.");
            }

            // 6. Copy setup.exe into content folder
            var contentSetupExe = Path.Combine(contentPath, "setup.exe");
            if (!File.Exists(contentSetupExe))
            {
                File.Copy(setupExe, contentSetupExe);
                onProgress?.Invoke("Copied setup.exe to content folder.");
            }

            // 7. Generate install.xml: no SourcePath, so an offline install reads the source
            // beside it and an online install reads the CDN. Offline pins the downloaded
            // build; online takes the channel's current build.
            var installXml = GenerateOdtXml(edition, fullVersion, odt, sourcePath: null, pinVersion: !online);
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
            IconExtractionService.StageIcon(def, contentPath, manifest, onProgress);
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

    public static string GenerateOdtXml(string edition, string version, OdtConfig odt, string? sourcePath, bool pinVersion = true)
    {
        var lines = new List<string> { "<Configuration>" };

        var addAttrs = new List<string>();
        if (!string.IsNullOrEmpty(sourcePath))
            addAttrs.Add($"SourcePath=\"{sourcePath}\"");
        addAttrs.Add($"OfficeClientEdition=\"{edition}\"");
        addAttrs.Add($"Channel=\"{odt.Channel}\"");
        addAttrs.Add("OfficeMgmtCOM=\"TRUE\"");
        if (pinVersion)
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
        => await RunProcessForExitCodeAsync(fileName, arguments, timeout, ct) == 0;

    /// <summary>Windows Installer's "another installation is in progress".</summary>
    public const int InstallerBusyExitCode = 1618;

    /// <summary>
    /// Runs a vendor bootstrapper whose payload step is itself a Windows
    /// Installer operation, retrying while another installer holds the
    /// mutex. Returns the final exit code, or null on timeout.
    /// </summary>
    public static async Task<int?> RunInstallerProcessAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct,
        Action<string>? onProgress = null, int busyRetries = 6, TimeSpan? busyDelay = null)
    {
        var delay = busyDelay ?? TimeSpan.FromSeconds(60);
        for (var attempt = 1; ; attempt++)
        {
            var exitCode = await RunProcessForExitCodeAsync(fileName, arguments, timeout, ct);
            if (exitCode != InstallerBusyExitCode || attempt > busyRetries)
                return exitCode;
            onProgress?.Invoke($"Another installation is in progress (exit {InstallerBusyExitCode}); retry {attempt} of {busyRetries} in {delay.TotalSeconds:0}s...");
            await Task.Delay(delay, ct);
        }
    }

    private static async Task<int?> RunProcessForExitCodeAsync(
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
            return null;
        }

        return process.ExitCode;
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

    /// <summary>
    /// Script detection for a provisioned MSIX. A provisioned package lands
    /// under Program Files\WindowsApps in a directory named with the publisher
    /// hash, so detection queries the provisioning store instead of a path.
    /// The script writes to stdout only when an installed version is at or
    /// above the staged one, which is how CM reads "installed".
    /// </summary>
    public static StageDetection BuildMsixScriptDetection(StageMsixPackage package)
    {
        var name = package.Name.Replace("'", "''");
        var version = package.Version.Replace("'", "''");

        var script = string.Join("\r\n",
            $"$wanted = [version]'{version}'",
            "$prov = Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |",
            $"    Where-Object {{ $_.DisplayName -eq '{name}' }}",
            "foreach ($p in $prov) {",
            "    $found = $null",
            "    if (-not [version]::TryParse([string]$p.Version, [ref]$found)) { continue }",
            "    if ($found -ge $wanted) { Write-Output 'Installed'; exit 0 }",
            "}",
            "exit 0");

        return new StageDetection
        {
            Type = "Script",
            ScriptLanguage = "PowerShell",
            ScriptText = script,
            ExpectedValue = package.Version,
            Operator = "GreaterEquals",
            Is64Bit = !package.ProcessorArchitecture.Equals("x86", StringComparison.OrdinalIgnoreCase)
        };
    }

    /// <summary>The staged fields an install mode rewrites.</summary>
    public sealed record InstallModeResult(
        string InstallArgs,
        string? UninstallCommandLine,
        string? UninstallExpression,
        StageDetection Detection,
        string InstallContext);

    private static readonly string[] ModeSwitches = ["/allusers", "/currentuser"];

    /// <summary>
    /// Applies the operator's install mode to a definition stage. The staged
    /// installer's compiled script must offer the mode; the definition must
    /// declare it. The mode switch replaces any mode switch already in the
    /// arguments, the uninstaller and detection follow the branch, and a File
    /// detection moves between the two branch folders.
    /// </summary>
    public static InstallModeResult ApplyInstallMode(
        AppDefinition def, string installerPath, string installMode, string installArgs,
        StageDetection detection, Action<string>? onProgress = null, string? uninstallExpression = null)
    {
        var mode = installMode.Equals("AllUsers", StringComparison.OrdinalIgnoreCase) ? "AllUsers"
            : installMode.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase) ? "CurrentUser"
            : throw new InvalidOperationException($"Install mode '{installMode}' is not AllUsers or CurrentUser.");
        if (!def.OffersInstallModes)
            throw new InvalidOperationException($"{def.Application} does not declare SupportsInstallModes; an install mode cannot be applied.");

        var engine = InstallerSignatureScanner.Identify(installerPath);
        var facts = CompiledScriptFacts.TryRead(engine, installerPath)
            ?? throw new InvalidOperationException($"The {engine} script of {Path.GetFileName(installerPath)} could not be read; the {mode} install mode cannot be applied.");
        if (!facts.Branches.TryGetValue(mode, out var branch))
            throw new InvalidOperationException(
                $"{Path.GetFileName(installerPath)} offers no {mode} install mode; the script accepts {string.Join(" and ", facts.InstallModes)}.");
        var defaultBranch = facts.DefaultBranch;

        // The switch is the difference between the branch and the default
        // branch; the definition's other switches stay.
        var defaultTokens = WrapperGeneratorService.Tokenize(defaultBranch?.InstallArgs ?? "");
        var modeSwitch = WrapperGeneratorService.Tokenize(branch.InstallArgs)
            .FirstOrDefault(t => !defaultTokens.Contains(t, StringComparer.OrdinalIgnoreCase)) ?? "";
        var kept = WrapperGeneratorService.Tokenize(installArgs)
            .Where(t => !ModeSwitches.Contains(t, StringComparer.OrdinalIgnoreCase));
        var newInstallArgs = string.Join(" ", kept.Append(modeSwitch).Where(t => t.Length > 0));

        var uninstallLine = string.IsNullOrWhiteSpace(branch.SilentUninstallCommand) ? null : branch.SilentUninstallCommand;
        var newUninstallExpression = uninstallLine is null ? null : WrapperGeneratorService.UninstallExpressionFromCommandLine(uninstallLine);
        if (newUninstallExpression == "") newUninstallExpression = null;
        // A definition's own expression keeps its surrounding statements when
        // it names the default uninstaller; otherwise the branch's uninstall
        // command replaces it.
        if (newUninstallExpression is not null && uninstallLine is not null
            && !string.IsNullOrWhiteSpace(uninstallExpression) && defaultBranch is not null)
        {
            var moved = WrapperGeneratorService.MoveUninstallExpressionToBranch(
                uninstallExpression, UninstallerPathForms(defaultBranch.UninstallerPathWindows), uninstallLine);
            if (moved is not null) newUninstallExpression = moved;
            else if (uninstallExpression.Contains("$proc", StringComparison.Ordinal))
                onProgress?.Invoke("The definition's uninstall command does not name the default uninstaller; the branch's own uninstall command is used.");
        }
        uninstallExpression = newUninstallExpression;

        var newDetection = detection;
        if (detection.Type is "RegistryKeyValue" or "RegistryKey" && !string.IsNullOrEmpty(branch.UninstallRegistryKey))
        {
            newDetection = CloneForMode(detection);
            var relative = branch.UninstallRegistryKeyRelative;
            var wow = relative.Contains("WOW6432Node\\", StringComparison.OrdinalIgnoreCase);
            newDetection.RegistryKeyRelative = wow ? relative.Replace("WOW6432Node\\", "", StringComparison.OrdinalIgnoreCase) : relative;
            newDetection.Is64Bit = !wow && branch.Is64BitView;
            newDetection.Hive = branch.RegistryHive == "HKCU" ? "CurrentUser" : "LocalMachine";
        }
        else if (detection.Type == "File" && defaultBranch is not null && !string.IsNullOrEmpty(detection.FilePath))
        {
            var from = ExpandKnownFolders(defaultBranch.InstallDirWindows).TrimEnd('\\');
            var to = branch.InstallDirWindows.TrimEnd('\\');
            var current = detection.FilePath.TrimEnd('\\');
            if (from.Length > 0 && current.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                newDetection = CloneForMode(detection);
                newDetection.FilePath = to + current[from.Length..];
            }
            else
            {
                onProgress?.Invoke($"File detection path {detection.FilePath} is not under the default install folder {from}; left unchanged.");
            }
        }

        var context = branch.InstallContext == "PerUser" ? "User" : "Machine";
        onProgress?.Invoke($"Install mode {mode}: arguments '{newInstallArgs}', context {context}" +
            (uninstallLine is null ? "" : $", uninstall {uninstallLine}"));
        return new InstallModeResult(newInstallArgs, uninstallLine, uninstallExpression, newDetection, context);
    }

    private static StageDetection CloneForMode(StageDetection d) => new()
    {
        Type = d.Type,
        RegistryKeyRelative = d.RegistryKeyRelative,
        ValueName = d.ValueName,
        ExpectedValue = d.ExpectedValue,
        Operator = d.Operator,
        PropertyType = d.PropertyType,
        FilePath = d.FilePath,
        FileName = d.FileName,
        Is64Bit = d.Is64Bit,
        Hive = d.Hive,
        ScriptLanguage = d.ScriptLanguage,
        ScriptText = d.ScriptText,
        Connector = d.Connector,
        Clauses = d.Clauses,
        GroupSizes = d.GroupSizes
    };

    /// <summary>
    /// Definitions write literal Program Files paths where the script reader
    /// writes environment form; the machine folders are expanded to their
    /// literal x64-host values so the two can be compared.
    /// </summary>
    /// <summary>
    /// The spellings a definition may use for an uninstaller the script reader
    /// writes in environment form: the literal x64-host path, and the
    /// PowerShell $env: and ${env:} forms of the leading variable.
    /// </summary>
    private static IEnumerable<string> UninstallerPathForms(string windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath)) yield break;
        yield return windowsPath;
        yield return ExpandKnownFolders(windowsPath);
        var variable = System.Text.RegularExpressions.Regex.Match(windowsPath, @"^%([^%]+)%");
        if (variable.Success)
        {
            var rest = windowsPath[variable.Length..];
            yield return "$env:" + variable.Groups[1].Value + rest;
            yield return "${env:" + variable.Groups[1].Value + "}" + rest;
        }
    }

    private static string ExpandKnownFolders(string path) => path
        .Replace("%ProgramW6432%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles(x86)%", @"C:\Program Files (x86)", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%CommonProgramW6432%", @"C:\Program Files\Common Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%CommonProgramFiles(x86)%", @"C:\Program Files (x86)\Common Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%CommonProgramFiles%", @"C:\Program Files\Common Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramData%", @"C:\ProgramData", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether metadata ProductName may name the staged application. Sources
    /// that read a manifest or table (Burn manifest, inner MSI, product-info.json,
    /// AppxManifest) are taken as written. A version resource read off the
    /// largest extracted EXE is taken only when it and the definition name
    /// share their alphanumeric text in either direction.
    /// </summary>
    public static bool IsProductNamePlausible(InstallerMetadata metadata, AppDefinition def)
    {
        if (string.IsNullOrWhiteSpace(metadata.ProductName)) return false;

        var source = metadata.MetadataSource ?? "";
        if (source is "product-info.json" or "BurnManifest-MsiPackage" or "AppxManifest.xml"
            || source.StartsWith("inner-msi:", StringComparison.Ordinal))
            return true;

        var found = Normalize(metadata.ProductName);
        if (found.Length == 0) return false;
        foreach (var candidate in new[] { def.CMName, def.Application })
        {
            var wanted = Normalize(candidate);
            if (wanted.Length == 0) continue;
            if (found.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(found, StringComparison.Ordinal))
                return true;
        }
        return false;

        static string Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            // The definition's architecture tag is not part of the vendor's name.
            var trimmed = System.Text.RegularExpressions.Regex.Replace(value, @"\s*\(x\d+\)\s*$", "");
            return new string(trimmed.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }
    }

    public static StageDetection BuildDetection(AppDefinition def, string? productCode, string version,
        string? rawVersion = null, InstallerMetadata? metadata = null,
        Dictionary<string, string>? settingsValues = null)
    {
        var det = ApplySettingTokens(def.Detection, def.InstallerSettings, settingsValues);

        // If detection references {ProductCode} but we have no ProductCode, and metadata has detection data,
        // use metadata-derived detection instead of the definition's detection config.
        if (det.RegistryKeyTemplate?.Contains("{ProductCode}", StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrEmpty(productCode)
            && metadata?.HasDetectionData == true)
        {
            return BuildDetectionFromMetadata(metadata, version, def.Detection.Is64Bit);
        }

        if (det.Type.Equals("Script", StringComparison.OrdinalIgnoreCase))
            return BuildScriptDetection(det, productCode, version);

        if (det.Type.Equals("Compound", StringComparison.OrdinalIgnoreCase))
            return BuildCompoundDetection(det, productCode, version, rawVersion);

        return BuildSingleDetection(det, productCode, version, rawVersion);
    }

    /// <summary>
    /// Materializes a definition's Script detection, substituting the staged
    /// version into the body so the shipped script compares against the build
    /// that was actually staged.
    /// </summary>
    private static StageDetection BuildScriptDetection(DetectionConfig det, string? productCode, string version)
    {
        if (string.IsNullOrWhiteSpace(det.ScriptText))
            throw new InvalidOperationException("Detection type 'Script' requires a ScriptText body.");

        return new StageDetection
        {
            Type = "Script",
            ScriptLanguage = string.IsNullOrWhiteSpace(det.ScriptLanguage) ? "PowerShell" : det.ScriptLanguage,
            ScriptText = det.ScriptText
                .Replace("{version}", version, StringComparison.OrdinalIgnoreCase)
                .Replace("{ProductCode}", productCode ?? "", StringComparison.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Materializes a definition's Compound detection. Clauses resolve
    /// individually through the single-clause builder, so a clause carries the
    /// same version and ProductCode substitution a standalone detection would.
    /// </summary>
    private static StageDetection BuildCompoundDetection(
        DetectionConfig det, string? productCode, string version, string? rawVersion)
    {
        var clauses = det.Clauses ?? [];
        if (clauses.Length < 2)
            throw new InvalidOperationException(
                "Detection type 'Compound' requires at least two Clauses.");
        if (clauses.Any(c => c.Type.Equals("Compound", StringComparison.OrdinalIgnoreCase)
                          || c.Type.Equals("Script", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "A Compound detection clause must be a single registry or file clause.");

        var connector = string.IsNullOrWhiteSpace(det.Connector) ? "And" : det.Connector!;
        if (!connector.Equals("And", StringComparison.OrdinalIgnoreCase)
            && !connector.Equals("Or", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Compound detection Connector must be 'And' or 'Or' (got '{connector}').");

        var groupSizes = det.GroupSizes;
        if (groupSizes is { Length: > 0 }
            && (groupSizes.Length != 2 || groupSizes.Any(g => g <= 0) || groupSizes.Sum() != clauses.Length))
        {
            throw new InvalidOperationException(
                $"Detection.GroupSizes must be exactly two non-zero sizes summing to the clause count (got '{string.Join(",", groupSizes)}' for {clauses.Length} clauses).");
        }

        return new StageDetection
        {
            Type = "Compound",
            Connector = connector,
            GroupSizes = groupSizes is { Length: > 0 } ? groupSizes : null,
            Clauses = [.. clauses.Select(c => BuildSingleDetection(c, productCode, version, rawVersion))]
        };
    }

    /// <summary>
    /// Resolves one registry or file detection config into its staged form.
    /// </summary>
    /// <summary>
    /// Maps a definition's RegistryHive spelling (HKCU, HKEY_CURRENT_USER,
    /// CurrentUser) onto the manifest's two values. Anything else, including
    /// an absent value, is the machine hive.
    /// </summary>
    public static string ResolveHive(string? registryHive)
    {
        var hive = (registryHive ?? "").Trim();
        return hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            || hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
            || hive.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase)
            ? "CurrentUser"
            : "LocalMachine";
    }

    private static StageDetection BuildSingleDetection(
        DetectionConfig det, string? productCode, string version, string? rawVersion)
    {
        if (det.Type.Equals("RegistryKeyValue", StringComparison.OrdinalIgnoreCase))
        {
            // Substitute {ProductCode} and {version} in template
            var regKey = det.RegistryKeyTemplate?
                .Replace("{ProductCode}", productCode ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{version}", version, StringComparison.OrdinalIgnoreCase) ?? "";

            // For Version-type detection, sanitize version to valid System.Version format
            // (e.g., "11.0.30+7" → "11.0.30.7" — CM requires dotted version strings)
            // Use rawVersion (MSI ProductVersion) when available — ARP DisplayVersion comes from the MSI,
            // not from the scraped marketing version (e.g., XenCenter: scraped 2026.1.0, MSI 26.1.0).
            var detValue = rawVersion ?? version;
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
                Is64Bit = det.Is64Bit,
                Hive = ResolveHive(det.RegistryHive)
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
                Is64Bit = det.Is64Bit,
                Hive = ResolveHive(det.RegistryHive)
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

    // ── ad-hoc (dropped installer) staging ───────────────────────

    /// <summary>
    /// Stages an already-analysed local installer into the same versioned content
    /// layout the definition pipeline produces, so packaging consumes it unchanged.
    /// There is no version scrape or download — the file on disk is the source.
    /// </summary>
    public Task<StagingResult> StageDroppedInstallerAsync(
        StageManifest manifest, string sourceInstallerPath, string downloadRoot,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(sourceInstallerPath))
                return Task.FromResult(new StagingResult { Success = false, ErrorMessage = $"Installer not found: {sourceInstallerPath}" });

            // Wrapper .ps1 files are written for Windows PowerShell defaults on the
            // target device; a non-ASCII installer filename is mangled inside the
            // wrapper and fails only at deployment time.
            if (manifest.InstallerFile.Any(c => c > 0x7F))
                return Task.FromResult(new StagingResult
                {
                    Success = false,
                    ErrorMessage = $"Installer filename contains non-ASCII characters; rename it before staging: {manifest.InstallerFile}"
                });

            var vendorFolder = MecmTarget.SanitizeFolderNamePublic(manifest.Publisher);
            var appFolder = MecmTarget.SanitizeFolderNamePublic(manifest.DisplayName ?? manifest.AppName);
            var versionFolder = MecmTarget.SanitizeFolderNamePublic(manifest.SoftwareVersion);

            if (string.IsNullOrWhiteSpace(appFolder))
                return Task.FromResult(new StagingResult { Success = false, ErrorMessage = "Application name contains no usable path characters." });
            if (string.IsNullOrWhiteSpace(versionFolder))
                return Task.FromResult(new StagingResult { Success = false, ErrorMessage = "Version contains no usable path characters." });
            if (string.IsNullOrWhiteSpace(vendorFolder))
                vendorFolder = "Unknown";

            var contentPath = Path.Combine(downloadRoot, vendorFolder, appFolder, versionFolder);
            Directory.CreateDirectory(contentPath);
            ct.ThrowIfCancellationRequested();

            var stagedInstaller = Path.Combine(contentPath, manifest.InstallerFile);
            File.Copy(sourceInstallerPath, stagedInstaller, true);
            onProgress?.Invoke($"Staged installer: {stagedInstaller}");

            onProgress?.Invoke("Generating install/uninstall wrappers...");
            if (manifest.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase))
                _wrapperService.GenerateMsiWrappers(contentPath, manifest.InstallerFile, null, manifest.InstallArgs);
            else
            {
                var (arpKeyName, installFolder) = WrapperGeneratorService.ResolveUninstallHints(manifest.Detection);
                var uninstallExpression = string.IsNullOrWhiteSpace(manifest.UninstallCommand)
                    ? null
                    : WrapperGeneratorService.UninstallExpressionFromCommandLine(manifest.UninstallCommand);
                if (uninstallExpression == "") uninstallExpression = null;
                _wrapperService.GenerateExeWrappers(contentPath, manifest.InstallerFile,
                    manifest.InstallArgs, manifest.UninstallArgs, manifest.DetectedEngine, manifest.AppName, uninstallExpression,
                    null, arpKeyName, installFolder);
            }

            var manifestPath = Path.Combine(contentPath, "stage-manifest.json");
            _manifestService.Write(manifestPath, manifest);
            onProgress?.Invoke($"Manifest written: {manifestPath}");

            return Task.FromResult(new StagingResult
            {
                Success = true,
                Version = manifest.SoftwareVersion,
                ContentPath = contentPath,
                ManifestPath = manifestPath
            });
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new StagingResult { Success = false, ErrorMessage = "Cancelled" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new StagingResult { Success = false, ErrorMessage = ex.Message });
        }
    }

    /// <summary>
    /// Resolves InstallerParameter values into a final install arguments string.
    /// Appends CommandFragment for enabled toggles, substitutes {value} for other types.
    /// </summary>
    public static string ResolveInstallerArgs(string baseArgs, InstallerParameter[]? parameters, Dictionary<string, string>? values)
        => ResolveArgs(baseArgs, parameters, values, p => p.CommandFragment);

    /// <summary>Same appending rules as the install arguments, driven by UninstallFragment.</summary>
    public static string ResolveUninstallArgs(string baseArgs, InstallerParameter[]? parameters, Dictionary<string, string>? values)
        => ResolveArgs(baseArgs, parameters, values, p => p.UninstallFragment);

    private static string ResolveArgs(string baseArgs, InstallerParameter[]? parameters, Dictionary<string, string>? values,
        Func<InstallerParameter, string?> fragmentOf)
    {
        if (parameters is null || parameters.Length == 0)
            return baseArgs;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseArgs))
            parts.Add(baseArgs);

        foreach (var param in parameters)
        {
            var fragment = fragmentOf(param);
            if (string.IsNullOrEmpty(fragment))
                continue;

            if (values is null || !values.TryGetValue(param.Key, out var value))
                value = param.DefaultValue;

            if (param.ControlType == "Toggle")
            {
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                    parts.Add(fragment.Replace("{value}", MapValue(param, null, "1"), StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (!string.IsNullOrEmpty(value))
                    parts.Add(fragment.Replace("{value}", MapValue(param, null, value), StringComparison.OrdinalIgnoreCase));
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Substitutes {setting:Key} tokens with the effective value of the matching
    /// InstallerParameter (chosen value, else DefaultValue). A token whose key
    /// matches no parameter is left in place, so a typo surfaces instead of
    /// silently producing an empty registry key or uninstall switch.
    /// </summary>
    public static string? ApplySettingTokens(string? text, InstallerParameter[]? parameters, Dictionary<string, string>? values)
    {
        if (string.IsNullOrEmpty(text) || parameters is null || parameters.Length == 0)
            return text;
        if (!text.Contains("{setting:", StringComparison.OrdinalIgnoreCase))
            return text;

        foreach (var param in parameters)
        {
            if (values is null || !values.TryGetValue(param.Key, out var value))
                value = param.DefaultValue;
            if (param.ValueMaps is not null)
            {
                foreach (var map in param.ValueMaps)
                    text = text!.Replace($"{{setting:{param.Key}:{map.Key}}}", MapValue(param, map.Key, value),
                        StringComparison.OrdinalIgnoreCase);
            }

            text = text!.Replace($"{{setting:{param.Key}}}", MapValue(param, null, value), StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>
    /// Applies a parameter's substitution table to a chosen value. A named map
    /// selects ValueMaps[mapName]; a null name selects ValueMap. A value the
    /// table does not list substitutes itself, so a table only has to spell out
    /// the values it rewrites.
    /// </summary>
    private static string MapValue(InstallerParameter param, string? mapName, string value)
    {
        var table = mapName is null
            ? param.ValueMap
            : param.ValueMaps is not null && param.ValueMaps.TryGetValue(mapName, out var named) ? named : null;

        return table is not null && table.TryGetValue(value, out var mapped) ? mapped : value;
    }

    /// <summary>
    /// Returns the post-install PowerShell for a definition with its setting
    /// tokens resolved, or null when the definition has none or the gating
    /// setting is not at its enabling value.
    /// </summary>
    public static string? ResolvePostInstallScript(AppDefinition def, Dictionary<string, string>? values)
    {
        var config = def.PostInstallScript;
        if (config is null || string.IsNullOrWhiteSpace(config.Script))
            return null;

        if (!string.IsNullOrEmpty(config.WhenSetting))
        {
            var gate = def.InstallerSettings?.FirstOrDefault(p =>
                p.Key.Equals(config.WhenSetting, StringComparison.OrdinalIgnoreCase));
            if (gate is null)
                return null;

            if (values is null || !values.TryGetValue(gate.Key, out var chosen))
                chosen = gate.DefaultValue;

            if (!string.Equals(chosen, config.WhenValue, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return ApplySettingTokens(config.Script, def.InstallerSettings, values);
    }

    /// <summary>
    /// Returns a detection config with {setting:Key} tokens resolved. Returns the
    /// original instance when no token is present, so definitions without settings
    /// tokens are not cloned.
    /// </summary>
    public static DetectionConfig ApplySettingTokens(DetectionConfig det, InstallerParameter[]? parameters, Dictionary<string, string>? values)
    {
        if (parameters is null || parameters.Length == 0)
            return det;

        if (!HasSettingToken(det))
            return det;

        return new DetectionConfig
        {
            Type = det.Type,
            RegistryHive = det.RegistryHive,
            RegistryKeyTemplate = ApplySettingTokens(det.RegistryKeyTemplate, parameters, values),
            ValueName = det.ValueName,
            Operator = det.Operator,
            FilePath = ApplySettingTokens(det.FilePath, parameters, values),
            FileName = ApplySettingTokens(det.FileName, parameters, values),
            PropertyType = det.PropertyType,
            Is64Bit = det.Is64Bit,
            ScriptLanguage = det.ScriptLanguage,
            ScriptText = ApplySettingTokens(det.ScriptText, parameters, values),
            Connector = det.Connector,
            Clauses = det.Clauses?.Select(c => ApplySettingTokens(c, parameters, values)).ToArray(),
            GroupSizes = det.GroupSizes
        };
    }

    private static bool HasSettingToken(DetectionConfig det) =>
        det.RegistryKeyTemplate?.Contains("{setting:", StringComparison.OrdinalIgnoreCase) == true
        || det.FilePath?.Contains("{setting:", StringComparison.OrdinalIgnoreCase) == true
        || det.FileName?.Contains("{setting:", StringComparison.OrdinalIgnoreCase) == true
        || det.ScriptText?.Contains("{setting:", StringComparison.OrdinalIgnoreCase) == true
        || det.Clauses?.Any(HasSettingToken) == true;
}
