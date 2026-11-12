using System.IO.Compression;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.InstallerScripts;
using ApplicationPackagerEngine.Services.Workbench;
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

    /// <summary>How long a stage waits for another stage of the same application and version.</summary>
    public TimeSpan StageLockTimeout { get; set; } = StageLock.DefaultTimeout;

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
    /// A vendor endpoint that answers HTTP 200 with an HTML interstitial
    /// would otherwise stage a web page as the installer (CPU-Z's endpoint
    /// does this). EXE payloads must start MZ; MSI payloads must start with
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
        if (type is not ("EXE" or "MSI" or "MSP" or "ZIP")) return;
        if (!File.Exists(path)) return;

        Span<byte> head = stackalloc byte[8];
        using (var fs = File.OpenRead(path))
        {
            var read = fs.Read(head);
            if (read < 8) throw new InvalidOperationException($"Downloaded installer is only {read} bytes: {Path.GetFileName(path)}");
        }

        var ok = type switch
        {
            "EXE" => head[0] == (byte)'M' && head[1] == (byte)'Z',
            "ZIP" => head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4,
            _ => head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0
        };
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
    /// A null or empty expected hash is not a verification: the file passes
    /// and the run says "NOT verified", so the log never reads as a checked file.
    /// The check that remains is the file type (see <see cref="ValidateInstallerMagic"/>).
    /// </summary>
    public static void VerifyPayloadHash(string path, string? expectedSha256, Action<string>? onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            onProgress?.Invoke("Payload SHA256 NOT verified: no hash is pinned, published in the version feed or listed in a checksum file for this download; only the file type was checked.");
            return;
        }

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

    /// <summary>Refuses a PE installer whose machine field does not match its declared payload architecture.</summary>
    public static void VerifyPayloadArchitecture(string path, string expectedArchitecture)
    {
        var expected = InstallerArchitectures.Resolve(expectedArchitecture);
        var actual = InstallerSignatureScanner.ReadPeArchitecture(path);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Payload architecture is '{actual}', expected '{expected}': {Path.GetFileName(path)}");
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

        return ExtractFeedHash(await FetchFeedAsync(config, ct), config, version);
    }

    private static async Task<string> FetchFeedAsync(VersionScraperConfig config, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, config.Url);
        var headers = (config.RequestHeaders ?? []).Select(Scrapers.CurlHeaders.Split).ToList();
        if (!config.NoUserAgent && !headers.Any(h => h.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
            request.Headers.TryAddWithoutValidation("User-Agent", HashFeedUserAgent);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);

        using var response = await HashFeedClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Resolves the build number the version feed publishes for the staged
    /// version. Returns null when the definition declares no BuildQuery.
    /// </summary>
    public static async Task<string?> ResolveFeedBuildAsync(
        VersionScraperConfig config, string version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.BuildQuery)) return null;
        if (string.IsNullOrWhiteSpace(config.Url)) return null;

        return ExtractFeedBuild(await FetchFeedAsync(config, ct), config, version);
    }

    /// <summary>
    /// Applies the build capture to already-fetched feed content: a regex whose
    /// named group "build", or group 1, holds the build. {version} in the
    /// query is the escaped version, so the build comes from the release that
    /// the version names. Null when nothing matches.
    /// </summary>
    public static string? ExtractFeedBuild(string content, VersionScraperConfig config, string version)
    {
        var query = config.BuildQuery;
        if (string.IsNullOrWhiteSpace(query)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(content,
            query.Replace("{version}", System.Text.RegularExpressions.Regex.Escape(version), StringComparison.OrdinalIgnoreCase));
        if (!match.Success) return null;

        var named = match.Groups["build"];
        if (named.Success) return named.Value;
        return match.Groups.Count > 1 ? match.Groups[1].Value : null;
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

    /// <summary>Gets the latest version for a JSON-defined app without downloading.</summary>
    public async Task<string?> GetLatestVersionAsync(AppDefinition def, CancellationToken ct = default)
    {
        if (StagesFromSavedInstaller(def))
            return string.IsNullOrWhiteSpace(def.LocalInstaller?.Version) ? null : def.LocalInstaller.Version.Trim();

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
        if (scraper is Scrapers.FileVersionScraper && Scrapers.FileVersionScraper.DiscoversUrl(config))
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
        string? installMode = null,
        StageInputs? inputs = null)
    {
        // A run already cancelled does no work and reports cancellation, rather than
        // starting a download whose result nobody wants.
        ct.ThrowIfCancellationRequested();

        if (FlowConflict(def) is { } conflict)
            return new StagingResult { Success = false, ErrorMessage = conflict };

        // The signing certificate resolves before anything is written, so an unusable
        // one refuses the build with no partial content.
        StageFinalizer finalizer;
        try
        {
            finalizer = StageFinalizer.Begin(inputs);
            downloadRoot = finalizer.ScopeDownloadRoot(StagingRootGuard.Prepare(downloadRoot));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StagingResult { Success = false, ErrorMessage = ex.Message };
        }

        if (def.InstallerType.Equals("ODT", StringComparison.OrdinalIgnoreCase))
            return await StageOdtAsync(def, downloadRoot, onProgress, ct, knownVersion, settingsValues, finalizer);

        if (def.BootstrapperLayout is not null)
            return await StageBootstrapperLayoutAsync(def, downloadRoot, onProgress, ct, knownVersion, finalizer);

        var partial = new PartialStage();
        IDisposable? stageLock = null;
        try
        {
            // Operator inputs the stage cannot do without refuse it before any download.
            var postExecutionBehavior = NormalizePostExecutionBehavior(def.PostExecutionBehavior);
            var sideFiles = ResolveSideFiles(def, inputs?.SideFiles);
            string? localInstaller = null;
            if (def.LocalSource is not null
                && (def.LocalSource.IsRequired || !string.IsNullOrWhiteSpace(inputs?.LocalSourceFolder)))
                localInstaller = LocalSourceResolver.Resolve(inputs?.LocalSourceFolder, def.LocalSource, def.Application);
            var savedInstaller = localInstaller is null ? SavedLocalInstaller(def, onProgress) : null;

            // A version from Check Latest is reused, except under a VersionTransform: knownVersion
            // is then the display version, and the download URL needs the raw version.
            string? rawVersion, version;
            if (savedInstaller is not null)
            {
                version = rawVersion = savedInstaller.Version.Trim();
            }
            else if (!string.IsNullOrEmpty(knownVersion) && string.IsNullOrEmpty(def.VersionScraper.VersionTransform))
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

            // version stays the transformed value the download URL substitutes; displayVersion
            // names the folder and the detection and is the version the operator sees.
            var displayVersion = version;
            if (savedInstaller is null && !string.IsNullOrEmpty(def.DisplayVersionOverride))
                displayVersion = def.DisplayVersionOverride
                    .Replace("{version}", rawVersion, StringComparison.OrdinalIgnoreCase);

            // A local installer stages under its own file version, which can
            // trail the version the vendor publishes.
            if (localInstaller is not null)
            {
                onProgress?.Invoke($"Local installer              : {localInstaller}");
                AuthenticodeVerifier.RequireValid(localInstaller, onProgress);
                var localVersion = LocalSourceResolver.ReadVersion(localInstaller);
                if (System.Version.TryParse(localVersion, out var local) && System.Version.TryParse(displayVersion, out var published)
                    && local < published)
                    onProgress?.Invoke($"Local installer {localVersion} is older than the published {displayVersion}; staging it as found.");
                version = rawVersion = displayVersion = localVersion;
            }

            onProgress?.Invoke($"Version: {displayVersion}");

            var appFolder = $"{def.Application.Replace(" ", "")}-{def.Architecture}";
            var baseRoot = Path.Combine(downloadRoot, appFolder);
            var contentPath = Workbench.WorkbenchPaths.CombineUnder(baseRoot, AssertVersionSegment(displayVersion));
            stageLock = await StageLock.AcquireAsync(contentPath, $"{def.Application} {displayVersion}", StageLockTimeout, onProgress, ct);
            partial.Track(downloadRoot, contentPath);
            Directory.CreateDirectory(contentPath);

            string downloadUrl;
            string installerFileName;
            string installerPath;
            if (localInstaller is not null)
            {
                downloadUrl = "";
                installerFileName = AssertPlainFileName(LocalSourceResolver.StagedName(def.LocalSource!, localInstaller, displayVersion,
                    def.InstallerFileName ?? Path.GetFileName(localInstaller)));
                installerPath = Workbench.WorkbenchPaths.CombineUnder(contentPath, installerFileName);
                StageLocalInstaller(localInstaller, installerPath, onProgress);
                AuthenticodeVerifier.RequireValid(installerPath, onProgress);
            }
            else if (savedInstaller is not null)
            {
                downloadUrl = "";
                installerFileName = AssertPlainFileName(string.IsNullOrWhiteSpace(def.InstallerFileName)
                    ? Path.GetFileName(savedInstaller.Path)
                    : def.InstallerFileName);
                // The wrapper .ps1 files are read with the Windows PowerShell default code page on the device.
                if (installerFileName.Any(c => c > 0x7F))
                    throw new InvalidOperationException($"The installer file name has characters other than ASCII. Rename the file, then add it again with Add from file: {installerFileName}");
                installerPath = Workbench.WorkbenchPaths.CombineUnder(contentPath, installerFileName);
                CopySavedInstaller(savedInstaller, installerPath, onProgress);
                ct.ThrowIfCancellationRequested();
                if (def.RequireValidSignature) AuthenticodeVerifier.RequireValid(installerPath, onProgress);
            }
            else
            {
                (downloadUrl, installerFileName, installerPath) =
                    await AcquireDownloadAsync(def, contentPath, version, rawVersion, onProgress, ct, downloadRoot, finalizer);
            }

            // A ZIP payload stays whole: the wrapper extracts it on the client.
            IReadOnlyDictionary<string, string> zipTokens = new Dictionary<string, string>();
            if (def.ZipPayload is not null)
            {
                if (!installerPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"ZipPayload needs a .zip payload, got {installerFileName}.");
                zipTokens = ZipPayload.Inspect(installerPath, def.ZipPayload);
                foreach (var (name, value) in zipTokens)
                    onProgress?.Invoke($"ZIP entry token {name}: {value}");
            }
            else if (installerPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(installerPath))
            {
                onProgress?.Invoke("Extracting ZIP archive...");
                var extractDir = Path.Combine(contentPath, "_zip_extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(installerPath, extractDir);

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
                    ValidateInstallerMagic(installerPath,
                        innerName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "MSI" : "EXE");
                }

                try { Directory.Delete(extractDir, true); } catch { }
                var originalZip = Path.Combine(contentPath, ResolveInstallerFileName(def, downloadUrl, displayVersion));
                try { if (File.Exists(originalZip) && originalZip != installerPath) File.Delete(originalZip); } catch { }
                if (!File.Exists(originalZip)) finalizer.ForgetVerifiedPayload(originalZip);
            }

            var isMsix = def.InstallerType.Equals("MSIX", StringComparison.OrdinalIgnoreCase);

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
                if (identity.IsBundle)
                    onProgress?.Invoke($"MSIX bundle architectures    : {string.Join(", ", identity.PackageArchitectures)}");

                // A neutral package runs on every architecture the definition can declare.
                if (!string.IsNullOrWhiteSpace(def.Architecture) && identity.PackageArchitectures.Count > 0
                    && !identity.PackageArchitectures.Any(a => a.Equals("neutral", StringComparison.OrdinalIgnoreCase)
                        || a.Equals(def.Architecture, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(identity.IsBundle
                        ? $"The MSIX bundle has application packages for {string.Join(", ", identity.PackageArchitectures)} only, but the definition declares '{def.Architecture}'."
                        : $"MSIX architecture is '{identity.ProcessorArchitecture}' but the definition declares '{def.Architecture}'.");

                // The AppX cmdlets refuse an unsigned or untrusted package, so
                // the signer is pinned into the install wrapper at stage time.
                var signature = _msixReader.ReadSignature(installerPath)
                    ?? throw new InvalidOperationException("MSIX package carries no signature; refusing to stage an unsigned package.");
                if (!signature.SignatureValid)
                    throw new InvalidOperationException("MSIX signature is not valid.");
                onProgress?.Invoke($"MSIX signer thumbprint       : {signature.Thumbprint}");
                if (signature.CertificateExpired)
                    onProgress?.Invoke("MSIX signing certificate expired and the signature has no valid timestamp from before that date; Windows refuses the package.");
                else if (!signature.ChainTrusted)
                    onProgress?.Invoke("MSIX signer does not chain to a trusted root on this machine; the target device must trust it.");
                else if (signature.Timestamp is { } stamped)
                    onProgress?.Invoke($"MSIX signing certificate expired after the timestamp of {stamped:yyyy-MM-dd}; the signature stays valid.");

                msixPackage = new StageMsixPackage
                {
                    Name = identity.Name,
                    Version = identity.Version,
                    Publisher = identity.Publisher,
                    ProcessorArchitecture = identity.ProcessorArchitecture,
                    ResourceId = identity.ResourceId.Length > 0 ? identity.ResourceId : null,
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

            InstallerMetadata? installerMetadata = null;
            if (productCode is null && _installerIdService is not null && !isMsix && def.ZipPayload is null &&
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

                    // An extracted ProductCode (WiX Burn inner MSI) is taken only where the
                    // definition resolves a {ProductCode} token; elsewhere it would put a
                    // value in the manifest that a machine without 7-Zip never reads.
                    if (!string.IsNullOrEmpty(installerMetadata.ProductCode))
                    {
                        if (ReferencesProductCode(def))
                        {
                            productCode = installerMetadata.ProductCode;
                            onProgress?.Invoke($"ProductCode (from {installerMetadata.MetadataSource}): {productCode}");
                        }
                        else
                        {
                            onProgress?.Invoke($"ProductCode {installerMetadata.ProductCode} ({installerMetadata.MetadataSource}) not recorded; the definition does not use it.");
                        }
                    }
                    if (!string.IsNullOrEmpty(installerMetadata.ProductVersion))
                        onProgress?.Invoke($"ProductVersion (from {installerMetadata.MetadataSource}): {installerMetadata.ProductVersion}");
                    // A manufacturer read off the wrong bundled EXE is as wrong as its name.
                    if (string.IsNullOrWhiteSpace(def.AppName) &&
                        !string.IsNullOrEmpty(installerMetadata.Manufacturer) &&
                        installerMetadata.Manufacturer.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                        IsProductNamePlausible(installerMetadata, def))
                        publisher = installerMetadata.Manufacturer;
                }
            }

            if (productCode is null && !isMsix && ReferencesProductCode(def))
                throw new InvalidOperationException(
                    $"{def.Application} resolves {{ProductCode}} from the installer, and no ProductCode could be read from {installerFileName}. " +
                    "Reading it from an EXE needs 7-Zip on this machine.");

            if (_iconService is not null && def.ZipPayload is null)
            {
                onProgress?.Invoke("Extracting application icon...");
                var iconResult = await _iconService.ExtractAsync(installerPath, def.Id, ct);
                if (iconResult.Success)
                    onProgress?.Invoke($"Icon cached: {iconResult.CachedPath}");
            }

            // InstallerSettings are the single source for the switches they own: the
            // definition's InstallArgs carries only the switches no parameter covers.
            var installArgs = ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, settingsValues);
            var uninstallArgs = ResolveUninstallArgs(
                ApplySettingTokens(def.UninstallArgs, def.InstallerSettings, settingsValues) ?? def.UninstallArgs,
                def.InstallerSettings, settingsValues);
            var uninstallCommand = ApplySettingTokens(def.UninstallCommand, def.InstallerSettings, settingsValues);

            var isMsiPayload = def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase)
                || installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            VerifyPayloadVersion(def, installerPath, isMsiPayload ? productVersion : null, isMsiPayload, displayVersion, onProgress);
            string? feedBuild = null;
            if (!string.IsNullOrWhiteSpace(def.VersionScraper.BuildQuery)
                && System.Text.Json.JsonSerializer.Serialize(def).Contains(StageTokens.FeedBuildToken, StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke("Reading the build number from the version feed...");
                feedBuild = await ResolveFeedBuildAsync(def.VersionScraper, rawVersion, ct);
                if (feedBuild is not null) onProgress?.Invoke($"Feed build: {feedBuild}");
            }
            var tokens = BuildStageTokens(def, installerPath, displayVersion, isMsiPayload ? productVersion : null, zipTokens, feedBuild);

            if (!string.IsNullOrWhiteSpace(def.CitrixSwitchStream))
                installArgs = BuildCitrixInstallArgs(def.CitrixSwitchStream, inputs?.CitrixSwitches, onProgress);
            installArgs = tokens.Apply(installArgs)!;
            uninstallCommand = tokens.Apply(uninstallCommand);

            // Built ahead of the wrappers: the EXE uninstall wrapper takes its
            // Add/Remove Programs lookup hints from the resolved detection rule.
            var detection = msixPackage is not null
                ? BuildMsixDetection(def.Detection, msixPackage)
                : BuildDetection(def, productCode, displayVersion, productVersion ?? rawVersion, installerMetadata, settingsValues, tokens);

            // A definition that declares both modes lets the
            // operator pick System or User; the staged installer's compiled
            // script supplies that branch, and switch, uninstaller, detection
            // and context move together with it.
            var installContext = def.InstallContext;
            string? modeUninstallCommandLine = null;
            if (!string.IsNullOrWhiteSpace(installMode))
            {
                var applied = ApplyInstallMode(def, installerPath, installMode, installArgs, detection, onProgress, uninstallCommand, displayVersion);
                installArgs = applied.InstallArgs;
                if (applied.UninstallExpression is not null) uninstallCommand = applied.UninstallExpression;
                modeUninstallCommandLine = applied.UninstallCommandLine;
                detection = applied.Detection;
                installContext = applied.InstallContext;
            }
            else if (msixPackage is null && def.ZipPayload is null)
            {
                // A mode-applied detection already comes from the installer's own branch.
                ArpDetectionKeyAssertion.CheckStage(detection, def.Detection, installerPath, installArgs, onProgress);
            }

            onProgress?.Invoke("Generating install/uninstall wrappers...");
            finalizer.PrepareStageRoot(contentPath);
            var wrappers = finalizer.Wrappers(_wrapperService);
            if (isMsix)
            {
                wrappers.GenerateMsixWrappers(contentPath, installerFileName,
                    msixPackage!.SignerThumbprint, msixPackage.Provisioned,
                    string.IsNullOrWhiteSpace(msixPackage.Publisher) ? null : MsixPackageReader.PublisherHash(msixPackage.Publisher),
                    uninstallCommand);
            }
            else if (def.ZipPayload is not null)
            {
                ZipPayload.GenerateWrappers(wrappers, contentPath, installerFileName, def.ZipPayload, displayVersion);
            }
            else if (def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase))
            {
                wrappers.GenerateMsiWrappers(contentPath, installerFileName, uninstallCommand, installArgs,
                    def.ExpandInstallArgs);
            }
            else if (def.InstallerType.Equals("MSP", StringComparison.OrdinalIgnoreCase))
            {
                wrappers.GenerateMspWrappers(contentPath, installerFileName, installArgs, uninstallCommand);
            }
            else if (def.ExtractSetup is not null)
            {
                wrappers.GenerateExtractSetupWrappers(contentPath, installerFileName,
                    def.ExtractSetup, installArgs, uninstallArgs);
            }
            else
            {
                var (arpKeyName, installFolder) = WrapperGeneratorService.ResolveUninstallHints(detection);
                wrappers.GenerateExeWrappers(contentPath, installerFileName,
                    installArgs, uninstallArgs, def.InstallerFramework, def.Application, uninstallCommand,
                    ResolvePostInstallScript(def, settingsValues, displayVersion), arpKeyName, installFolder,
                    def.PostInstallKillProcesses, def.RebootExitCodes, def.SuccessExitCodes);
            }

            if (def.TargetSecret is not null)
                TargetSecretScript.ApplyToFile(Path.Combine(contentPath, "install.ps1"), def.TargetSecret);

            var hasOptionalArchitecturePayloads = def.PayloadVariants is { Length: > 0 };
            var deploymentTypes = hasOptionalArchitecturePayloads
                ? await StageOptionalArchitecturePayloadsAsync(def, contentPath, version, rawVersion, displayVersion,
                    finalizer, onProgress, ct, inputs?.IncludeArchitectureVariants == true, downloadRoot)
                : null;

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

            var stageIdentity = ResolveStageIdentity(def, displayVersion, publisher, installerMetadata, onProgress);
            var cmAppName = stageIdentity.AppName;
            var cmDisplayName = stageIdentity.DisplayName;
            publisher = stageIdentity.Publisher;
            var cmVersion = stageIdentity.SoftwareVersion;
            if (deploymentTypes is { Length: > 1 })
            {
                cmAppName = string.IsNullOrWhiteSpace(def.CMName) ? def.Application : def.CMName;
                cmDisplayName = cmAppName;
            }

            var manifestPath = Path.Combine(contentPath, "stage-manifest.json");
            var manifest = new StageManifest
            {
                ApplicationId = CatalogApplicationId(def),
                AppName = cmAppName,
                DisplayName = cmDisplayName,
                Publisher = publisher,
                SoftwareVersion = cmVersion,
                InstallerFile = installerFileName,
                InstallerType = def.InstallerType,
                Architecture = def.Architecture,
                InstallContext = installContext,
                InstallMode = string.IsNullOrWhiteSpace(installMode) ? null : installMode,
                InstallArgs = installArgs.Replace(TargetSecretScript.Token, TargetSecretScript.ManifestText, StringComparison.Ordinal),
                UninstallArgs = uninstallArgs,
                UninstallCommand = modeUninstallCommandLine,
                ProductCode = productCode,
                RunningProcess = def.RunningProcess,
                RebootExitCodes = def.RebootExitCodes,
                SuccessExitCodes = def.SuccessExitCodes,
                Detection = detection,
                WsusDetection = msixPackage is null ? BuildWsusDetection(def, displayVersion, settingsValues, tokens) : null,
                MsixPackage = msixPackage,
                PostExecutionBehavior = postExecutionBehavior,
                DeploymentTypes = deploymentTypes,
                HasOptionalArchitecturePayloads = hasOptionalArchitecturePayloads ? true : null,
                OptionalArchitecturePayloadsStaged = hasOptionalArchitecturePayloads
                    ? inputs?.IncludeArchitectureVariants == true
                    : null
            };
            var sideFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sideFile, source) in sideFiles)
            {
                AssertSideFileName(sideFile.FileName, installerFileName, sideFileNames);
                // The file is copied as it is; its contents are never read. A copy from an
                // earlier stage of this version is replaced.
                Workbench.StageFileCopier.Copy(contentPath, source, sideFile.FileName, allowOverwrite: true);
                onProgress?.Invoke($"Placed {sideFile.Label} beside installer: {sideFile.FileName}");
            }
            IconExtractionService.StageIcon(def, contentPath, manifest, onProgress);
            partial.WritesManifest = true;
            finalizer.FinalizeAndWrite(_manifestService, manifestPath, manifest, onProgress);
            onProgress?.Invoke($"Manifest written: {manifestPath}");

            onProgress?.Invoke($"Stage complete: {contentPath}");

            partial.Completed = true;
            return new StagingResult
            {
                Success = true,
                Version = version,
                ContentPath = contentPath,
                ManifestPath = manifestPath,
                FromLocalSource = localInstaller is not null || savedInstaller is not null
            };
        }
        // Cancellation is not a staging failure. Returning it as one lets a caller read an
        // interrupted run as an ordinary result and carry on.
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StagingResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            try { partial.RemoveUnlessCompleted(onProgress); }
            finally { stageLock?.Dispose(); }
        }
    }

    /// <summary>
    /// The stage folder a run created, removed when the run ends without a successful result. A version folder
    /// that existed before the run holds an earlier stage and is kept. Stages of one application and version are
    /// serialized by <see cref="StageLock"/>, but a version folder that holds a stage manifest the run did not write is kept, because a
    /// concurrent run of the same version can have completed there. A manifest from a failed write of the run is
    /// removed with its folder. A parent folder the run created is removed only while it is empty.
    /// </summary>
    internal sealed class PartialStage
    {
        private string? _content;
        private string? _topCreated;

        public bool Completed { get; set; }

        /// <summary>True from the point the run writes its own stage manifest; a manifest after that point is the run's own.</summary>
        public bool WritesManifest { get; set; }

        /// <summary>Records <paramref name="contentPath"/> as created by this run when it does not exist yet.</summary>
        public void Track(string root, string contentPath)
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentPath));
            if (!current.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            if (Directory.Exists(current))
            {
                SnapshotExisting(current);
                return;
            }
            _content = current;
            while (current.Length > fullRoot.Length && !Directory.Exists(current))
            {
                _topCreated = current;
                current = Path.GetDirectoryName(current)!;
            }
        }

        // A version folder that held a stage before the run: the files at its top level that a run deletes or
        // rewrites (launchers, generated scripts, manifest) are copied aside, so a failed run puts them back.
        // A file over the size limit is an installer; a download lands in it by a move and is not copied.
        private const long SnapshotFileLimit = 8L * 1024 * 1024;
        private string? _existing;
        private string? _backup;
        private string? _unprotected;

        private void SnapshotExisting(string folder)
        {
            try
            {
                var backup = Path.Combine(Path.GetTempPath(), "apd-restage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);
                _backup = backup;
                _existing = folder;
                foreach (var file in Directory.EnumerateFiles(folder))
                    if (new FileInfo(file).Length <= SnapshotFileLimit)
                        File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No backup: the run proceeds as before and a failure leaves the folder as it is then.
                // A partial copy is dropped too: a restore of some names would mix earlier and failed-run files.
                DropBackup();
                _unprotected = folder;
            }
        }

        private void DropBackup()
        {
            var backup = _backup;
            _backup = null;
            _existing = null;
            if (backup is null) return;
            try { Directory.Delete(backup, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private void RestoreExisting(Action<string>? onProgress)
        {
            if (_backup is null || _existing is null) return;
            try
            {
                foreach (var saved in Directory.EnumerateFiles(_backup))
                    File.Copy(saved, Path.Combine(_existing, Path.GetFileName(saved)), overwrite: true);
                onProgress?.Invoke($"The run failed. The earlier stage files are back in {_existing}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onProgress?.Invoke($"The run failed and the earlier stage files could not be restored in {_existing}. Stage the version again. {ex.Message}");
            }
            finally { DropBackup(); }
        }

        public void RemoveUnlessCompleted(Action<string>? onProgress)
        {
            if (Completed) { DropBackup(); return; }
            if (_unprotected is not null)
                onProgress?.Invoke($"The run failed and the earlier stage files in {_unprotected} could not be kept. Stage the version again.");
            RestoreExisting(onProgress);
            if (_content is null || _topCreated is null) return;
            if (!WritesManifest && File.Exists(Path.Combine(_content, "stage-manifest.json")))
            {
                onProgress?.Invoke($"The stage folder holds a stage manifest and is kept: {_content}");
                return;
            }
            try
            {
                if (Directory.Exists(_content)) Directory.Delete(_content, recursive: true);
                for (var parent = Path.GetDirectoryName(_content);
                     parent is not null && parent.Length >= _topCreated.Length
                         && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any();
                     parent = Path.GetDirectoryName(parent))
                    Directory.Delete(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onProgress?.Invoke($"The incomplete stage folder could not be removed: {_content}. Remove it by hand. {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The workbench id of a catalog definition, catalog:&lt;Id&gt;, which every build of the
    /// definition carries with or without a run snapshot: the WSUS and Intune identities
    /// derive from it and stay the same across versions. Empty for a definition without
    /// a usable Id.
    /// </summary>
    public static string CatalogApplicationId(AppDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Id)) return "";
        try
        {
            var id = Models.Workbench.ApplicationIds.New(Models.Workbench.ApplicationOrigin.Catalog, name: def.Id);
            return Models.Workbench.ApplicationIds.IsValid(id) ? id : "";
        }
        catch (ArgumentException) { return ""; }
    }

    private async Task<StageDeploymentType[]?> StageOptionalArchitecturePayloadsAsync(
        AppDefinition definition, string contentPath, string version, string rawVersion, string displayVersion,
        Workbench.StageFinalizer finalizer, Action<string>? onProgress,
        CancellationToken ct, bool includeVariants, string? downloadRoot)
    {
        var variants = definition.PayloadVariants ?? [];
        if (variants.Length != 1)
            throw new InvalidOperationException("An optional architecture payload set must declare exactly one additional architecture variant.");
        if (definition.SupportsVariants?.Contains("Architecture", StringComparer.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException("PayloadVariants requires SupportsVariants to include Architecture.");
        if (!string.Equals(InstallerArchitectures.Resolve(definition.Architecture), InstallerArchitectures.X64, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Optional architecture payloads currently require an x64 base installer.");

        var variant = variants[0];
        if (!string.Equals(variant.Architecture, InstallerArchitectures.Arm64, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(variant.NameSuffix, InstallerArchitectures.Arm64, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The additional architecture payload must be the ARM64 deployment type.");
        var contentSubpath = variant.ContentSubpath ?? "";
        if (!Regex.IsMatch(contentSubpath, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException($"Payload variant content subpath '{variant.ContentSubpath}' must be one plain folder name.");
        if (!string.Equals(variant.InstallerType, "EXE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Optional ARM64 architecture payloads currently support EXE installers only.");
        if (string.IsNullOrWhiteSpace(variant.DownloadUrl)
            || (string.IsNullOrWhiteSpace(variant.PayloadSha256) && string.IsNullOrWhiteSpace(variant.PayloadHashQuery))
            || !variant.RequirePayloadHash)
            throw new InvalidOperationException("An optional architecture payload requires an HTTPS download URL and a mandatory SHA256 verification source.");

        var variantDefinition = BuildVariantDefinition(definition, variant);
        var resolvedUrl = variant.DownloadUrl
            .Replace("{display_version}", displayVersion, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", rawVersion, StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Optional architecture payload URLs must resolve to HTTPS.");

        var variantPath = Workbench.WorkbenchPaths.CombineUnder(contentPath, contentSubpath);
        var installerFileName = AssertPlainFileName(ResolveInstallerFileName(variantDefinition, resolvedUrl, displayVersion));
        var ownedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            installerFileName, "install.ps1", "install.bat", "uninstall.ps1", "uninstall.bat", "exitcode.txt"
        };

        if (!includeVariants)
        {
            onProgress?.Invoke("Optional ARM64 payload         : not selected");
            if (Directory.Exists(variantPath) && (File.GetAttributes(variantPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Optional architecture payload folder is a reparse point.");
            RemoveOptionalVariantFiles(variantPath, ownedNames);
            return null;
        }

        // A profile script replaces or wraps the generated script of the stage root only;
        // the variant folder would run its generated script without the profile's steps.
        if (finalizer.Snapshot is { } snapshot)
            foreach (var section in new[] { "Install", "Uninstall" })
            {
                var mode = Workbench.WorkbenchJson.Get(snapshot.Profile, section + ".Mode").Text;
                if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mode, "Extend", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"The profile's {section} script mode {mode} applies to the base installer only. Turn off the Architecture split for this profile, or use the generated {section.ToLowerInvariant()} script.");
            }

        AssertOptionalVariantFolder(contentPath, variantPath, variantDefinition, resolvedUrl, displayVersion);
        onProgress?.Invoke($"Downloading optional {variant.Architecture} payload...");
        Directory.CreateDirectory(variantPath);
        var (downloadUrl, fileName, installerPath) = await AcquireDownloadAsync(
            variantDefinition, variantPath, version, rawVersion, onProgress, ct, downloadRoot, finalizer);
        VerifyPayloadArchitecture(installerPath, variant.Architecture);

        var variantTokens = BuildStageTokens(variantDefinition, installerPath, displayVersion, null, new Dictionary<string, string>());
        var installArgs = variantTokens.Apply(variant.InstallArgs) ?? "";
        var uninstallArgs = variantTokens.Apply(variant.UninstallArgs) ?? "";
        var uninstallCommand = variantTokens.Apply(variant.UninstallCommand);
        var detection = BuildDetection(variantDefinition, null, displayVersion, rawVersion, tokens: variantTokens);

        RemoveOptionalVariantFiles(variantPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "install.ps1", "install.bat", "uninstall.ps1", "uninstall.bat", "exitcode.txt"
        });
        finalizer.PrepareStageRoot(variantPath);
        var wrappers = finalizer.Wrappers(_wrapperService);
        wrappers.GenerateExeWrappers(variantPath, fileName, installArgs, uninstallArgs,
            applicationName: definition.Application, uninstallCommand: uninstallCommand,
            postInstallKillProcesses: definition.PostInstallKillProcesses, rebootExitCodes: definition.RebootExitCodes,
            successExitCodes: variantDefinition.SuccessExitCodes);

        var result = new List<StageDeploymentType>
        {
            new()
            {
                NameSuffix = variant.NameSuffix,
                ContentSubpath = variant.ContentSubpath,
                InstallCommandLine = "install.bat",
                UninstallCommandLine = "uninstall.bat",
                Detection = detection,
                Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = variant.Architecture }]
            },
            // No commands or detection of its own: the base entry follows the manifest's,
            // so a profile's custom detection and command overrides reach it.
            new()
            {
                NameSuffix = definition.Architecture,
                Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = definition.Architecture }]
            }
        };
        onProgress?.Invoke($"Optional architecture payload  : {downloadUrl}");
        return [.. result];
    }

    private static AppDefinition BuildVariantDefinition(AppDefinition definition, AppDefinitionPayloadVariant variant) => new()
    {
        Id = definition.Id,
        Vendor = definition.Vendor,
        Application = definition.Application,
        CMName = definition.CMName,
        Architecture = variant.Architecture,
        InstallerType = variant.InstallerType,
        DownloadUrl = variant.DownloadUrl,
        InstallerFileName = variant.InstallerFileName,
        InstallArgs = variant.InstallArgs,
        UninstallArgs = variant.UninstallArgs,
        UninstallCommand = variant.UninstallCommand,
        Detection = variant.Detection,
        PayloadSha256 = variant.PayloadSha256,
        RequirePayloadHash = variant.RequirePayloadHash,
        RequireValidSignature = variant.RequireValidSignature,
        RunningProcess = definition.RunningProcess,
        SuccessExitCodes = definition.SuccessExitCodes,
        VersionScraper = new VersionScraperConfig
        {
            Type = definition.VersionScraper.Type,
            Url = definition.VersionScraper.Url,
            RequestHeaders = definition.VersionScraper.RequestHeaders,
            NoUserAgent = definition.VersionScraper.NoUserAgent,
            PayloadHashQuery = variant.PayloadHashQuery
        }
    };

    private static void AssertOptionalVariantFolder(string stageRoot, string variantPath,
        AppDefinition variantDefinition, string resolvedUrl, string displayVersion)
    {
        var root = Path.GetFullPath(stageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folder = Path.GetFullPath(variantPath);
        if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Optional architecture payload folder escapes the stage content root.");
        if (!Directory.Exists(folder)) return;
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Optional architecture payload folder is a reparse point.");

        var fileName = AssertPlainFileName(ResolveInstallerFileName(variantDefinition, resolvedUrl, displayVersion));
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            fileName, "install.ps1", "install.bat", "uninstall.ps1", "uninstall.bat", "exitcode.txt"
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (Directory.Exists(entry) || !allowed.Contains(Path.GetFileName(entry)))
                throw new InvalidOperationException("Optional architecture payload folder contains files outside its declared installer and generated wrappers.");
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Optional architecture payload folder contains a reparse-point file.");
        }
    }

    private static void RemoveOptionalVariantFiles(string variantPath, ISet<string> ownedNames)
    {
        if (!Directory.Exists(variantPath)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(variantPath))
        {
            if (Directory.Exists(entry) || !ownedNames.Contains(Path.GetFileName(entry))) continue;
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Optional architecture payload file is a reparse point and cannot be safely removed.");
            File.Delete(entry);
        }
    }

    /// <summary>
    /// Throws when a side file would replace the payload, a file the stage generates, the
    /// manifest, or another side file: the manifest would then seal the replacement.
    /// </summary>
    public static void AssertSideFileName(string sideFileName, string installerFileName, ISet<string> placed)
    {
        var generated = Wsus.WsusPayload.GeneratedContentNames.Contains(sideFileName, StringComparer.OrdinalIgnoreCase)
            || sideFileName.Equals(Wsus.WsusPayload.GeneratedContentFolder, StringComparison.OrdinalIgnoreCase)
            || sideFileName.Equals("exitcode.txt", StringComparison.OrdinalIgnoreCase);
        if (generated || sideFileName.Equals(installerFileName, StringComparison.OrdinalIgnoreCase) || !placed.Add(sideFileName))
            throw new InvalidOperationException(
                $"Side file '{sideFileName}' has the name of the installer, a generated file, the manifest or another side file.");
    }

    /// <summary>
    /// Copies the verified local installer to its staged path. A file already there is
    /// kept only when its bytes equal the source's; any other file is replaced, so the
    /// stage never carries an installer other than the one whose signature was checked.
    /// </summary>
    public static void StageLocalInstaller(string localInstaller, string installerPath, Action<string>? onProgress)
    {
        var name = Path.GetFileName(installerPath);
        if (!File.Exists(installerPath))
        {
            File.Copy(localInstaller, installerPath);
            onProgress?.Invoke($"Copied local installer: {name}");
            return;
        }
        if (string.Equals(ComputeSha256(installerPath), ComputeSha256(localInstaller), StringComparison.Ordinal))
        {
            onProgress?.Invoke($"Installer already staged: {name}");
            return;
        }
        File.Copy(localInstaller, installerPath, overwrite: true);
        onProgress?.Invoke($"Staged installer differed from the local source; replaced: {name}");
    }

    /// <summary>
    /// The saved installer of a definition with no download source, after a check that the file
    /// still exists and still has the saved SHA-256; null for a definition with a download source.
    /// Throws with the step the operator takes when the definition cannot stage.
    /// </summary>
    public static LocalInstallerConfig? SavedLocalInstaller(AppDefinition def, Action<string>? onProgress)
    {
        if (!StagesFromSavedInstaller(def)) return null;
        var saved = def.LocalInstaller;
        if (saved is null || string.IsNullOrWhiteSpace(saved.Path) || string.IsNullOrWhiteSpace(saved.Sha256) || string.IsNullOrWhiteSpace(saved.Version))
            throw new InvalidOperationException(
                $"{def.Application} has no download source and no saved installer. Add the installer again with Add from file, " +
                "or set a version source and a download URL in the Workbench.");
        if (!File.Exists(saved.Path))
            throw new InvalidOperationException(
                $"The installer that Add from file saved for {def.Application} is not found: {saved.Path}. " +
                "Put the file back in that folder, or add the installer again with Add from file.");
        RequireSavedHash(saved, saved.Path, "Nothing was staged.");
        onProgress?.Invoke($"Saved installer              : {saved.Path}");
        return saved;
    }

    /// <summary>True for a definition with no download source and no local installer folder: the saved installer is its only payload.</summary>
    public static bool StagesFromSavedInstaller(AppDefinition def) =>
        def.LocalSource is null && AddFromInstaller.AddFromInstallerService.IsLocalFileDefinition(def);

    /// <summary>
    /// Copies the saved installer to its staged path through a side file that replaces the staged
    /// path only when its SHA-256 equals the saved one, so the stage never seals another file.
    /// </summary>
    public static void CopySavedInstaller(LocalInstallerConfig saved, string installerPath, Action<string>? onProgress)
    {
        var copy = $"{installerPath}.{Guid.NewGuid():N}.partial";
        try
        {
            File.Copy(saved.Path, copy, overwrite: false);
            RequireSavedHash(saved, copy, "The copy was removed.");
            File.Move(copy, installerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(copy)) File.Delete(copy);
        }
        onProgress?.Invoke($"Copied saved installer: {Path.GetFileName(installerPath)}");
    }

    private static void RequireSavedHash(LocalInstallerConfig saved, string file, string outcome)
    {
        var actual = ComputeSha256(file);
        if (!string.Equals(actual, saved.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The installer {saved.Path} changed after Add from file saved it: its SHA-256 is {actual}, the saved SHA-256 is {saved.Sha256}. " +
                $"{outcome} Add the installer again with Add from file.");
    }

    /// <summary>
    /// Returns the version when it is one safe folder name: no separator, no "..", no
    /// drive or other character a file name cannot carry, and no trailing dot or space,
    /// which Windows strips and which would merge two versions into one folder.
    /// </summary>
    public static string AssertVersionSegment(string version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || version.Contains("..", StringComparison.Ordinal)
            || version.EndsWith('.') || version.EndsWith(' '))
            throw new InvalidOperationException($"Version '{version}' is not usable as a folder name.");
        return version;
    }

    /// <summary>Returns the name when it is a plain file name: no folder part, no "..", no character a file name cannot carry.</summary>
    public static string AssertPlainFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName is "." or ".."
            || fileName.EndsWith('.') || fileName.EndsWith(' '))
            throw new InvalidOperationException($"Installer file name '{fileName}' is not a plain file name.");
        return fileName;
    }

    /// <summary>
    /// Resolves the download URL, downloads the payload into the content
    /// folder unless it is already there, and verifies it: magic bytes, the
    /// SHA256 the definition, feed or vendor checksum file names, and the
    /// Authenticode signature when the definition requires one.
    /// </summary>
    private async Task<(string DownloadUrl, string InstallerFileName, string InstallerPath)> AcquireDownloadAsync(
        AppDefinition def, string contentPath, string version, string rawVersion,
        Action<string>? onProgress, CancellationToken ct, string? downloadRoot = null,
        Workbench.StageFinalizer? finalizer = null)
    {
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
        downloadUrl = ApplyDashedVersion(downloadUrl, version);
        var installerFileName = AssertPlainFileName(ResolveInstallerFileName(def, downloadUrl, version));
        var installerPath = Workbench.WorkbenchPaths.CombineUnder(contentPath, installerFileName);
        var headers = def.VersionScraper.RequestHeaders;
        var magicType = MagicTypeFor(installerPath, def.InstallerType);
        var sharedCacheRoot = SharedCacheRootFor(def, downloadRoot, onProgress);

        if (!File.Exists(installerPath))
        {
            // FileVersionScraper can download this file during Check Latest.
            // A hop-resolved scrape cached the file under the resolved URL's name.
            var cachedPath = def.VersionScraper.Type.Equals("FileVersion", StringComparison.OrdinalIgnoreCase)
                ? Scrapers.FileVersionScraper.GetCachedPath(
                    Scrapers.FileVersionScraper.DiscoversUrl(def.VersionScraper) ? downloadUrl : def.VersionScraper.Url)
                : null;

            if (cachedPath is not null)
            {
                File.Copy(cachedPath, installerPath);
                onProgress?.Invoke($"Copied from version cache: {installerFileName}");
                // The cache holds what the scrape downloaded; no hash covers it for most definitions.
                try { ValidateInstallerMagic(installerPath, magicType); }
                catch
                {
                    // The cache file is bad as well; a later stage then downloads a fresh copy.
                    foreach (var bad in new[] { installerPath, cachedPath })
                        try { File.Delete(bad); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    throw;
                }
            }
            else if (sharedCacheRoot is not null)
            {
                await SharedDownloadCache.FetchAsync(_downloadService, downloadUrl, installerPath, sharedCacheRoot, headers, onProgress, ct,
                    keepCachedOnFailedRefresh: def.VersionScraper.Type.Equals("FileVersion", StringComparison.OrdinalIgnoreCase));
                ValidateInstallerMagic(installerPath, magicType);
            }
            else
            {
                await _downloadService.DownloadAsync(downloadUrl, installerPath,
                    onProgress: onProgress, ct: ct, headers: headers);
                ValidateInstallerMagic(installerPath, magicType);
            }
        }
        else
        {
            onProgress?.Invoke($"Installer already cached: {installerFileName}");
        }

        // A pinned hash wins over the feed value:
        // it is the stronger statement, and a definition carrying both is
        // asserting the pin. A cached payload is verified too, so a bad
        // prior download does not stage on a later run.
        var expectedHash = def.PayloadSha256;
        if (string.IsNullOrWhiteSpace(expectedHash))
            expectedHash = await ResolveFeedHashAsync(def.VersionScraper, rawVersion, ct);
        if (string.IsNullOrWhiteSpace(expectedHash) && !string.IsNullOrWhiteSpace(def.PayloadSha256Url))
            expectedHash = await ResolveChecksumFileHashAsync(def, downloadUrl, rawVersion, installerFileName, onProgress, ct);
        if (def.RequirePayloadHash && string.IsNullOrWhiteSpace(expectedHash))
            throw new InvalidOperationException($"The version source carries no SHA256 for {installerFileName}; the payload cannot be verified.");
        try
        {
            VerifyPayloadHash(installerPath, expectedHash, onProgress);
        }
        catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(expectedHash))
        {
            // A torn download (a short body from a CDN) fails the
            // hash on every later run while the file sits in the cache.
            // One fresh download separates that from a vendor-side change.
            onProgress?.Invoke($"Payload SHA256 mismatch; discarding the cached file and downloading once more: {installerFileName}");
            File.Delete(installerPath);
            if (sharedCacheRoot is not null)
            {
                // The purge, the fresh download and both checks run under the entry lock; a replacement
                // that fails a check leaves no entry in the shared cache.
                await SharedDownloadCache.RefetchAsync(_downloadService, downloadUrl, installerPath, sharedCacheRoot,
                    path =>
                    {
                        ValidateInstallerMagic(path, magicType);
                        VerifyPayloadHash(path, expectedHash, onProgress);
                    }, headers, onProgress, ct);
            }
            else
            {
                await _downloadService.DownloadAsync(downloadUrl, installerPath,
                    onProgress: onProgress, ct: ct, headers: headers);
                ValidateInstallerMagic(installerPath, magicType);
                VerifyPayloadHash(installerPath, expectedHash, onProgress);
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedHash))
            finalizer?.RecordVerifiedPayload(installerPath, expectedHash);

        if (def.RequireValidSignature)
            AuthenticodeVerifier.RequireValid(installerPath, onProgress);

        return (downloadUrl, installerFileName, installerPath);
    }

    private static readonly System.Text.RegularExpressions.Regex VersionToken =
        new(@"\{[A-Za-z_]*version[A-Za-z_]*\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The message for a definition whose fields name two stage flows, or null. The stage takes the first
    /// matching branch, so the second flow would be ignored without a message. Bootstrapper layout, ZIP
    /// payload and extract-setup are three flows of their own, and extract-setup wraps an EXE installer.
    /// </summary>
    public static string? FlowConflict(AppDefinition def)
    {
        var flows = new List<string>();
        if (def.BootstrapperLayout is not null) flows.Add("BootstrapperLayout");
        if (def.ZipPayload is not null) flows.Add("ZipPayload");
        if (def.ExtractSetup is not null) flows.Add("ExtractSetup");
        if (flows.Count > 1)
            return $"The definition names more than one stage flow ({string.Join(", ", flows)}); keep one.";
        if (string.Equals(def.InstallerType, "ODT", StringComparison.OrdinalIgnoreCase) && flows.Count > 0)
            return $"The definition has InstallerType ODT and {flows[0]}; the ODT flow runs first and {flows[0]} would be ignored.";
        if (def.ExtractSetup is not null && !string.Equals(def.InstallerType, "EXE", StringComparison.OrdinalIgnoreCase))
            return $"The definition has ExtractSetup, which wraps an EXE installer, but InstallerType is {def.InstallerType ?? "empty"}; ExtractSetup would be ignored.";
        return null;
    }

    /// <summary>
    /// True for a definition whose download URL serves the current release under one
    /// fixed name: no version token in the URL and no URL read from the version source.
    /// </summary>
    public static bool IsFixedNameDownload(AppDefinition def) =>
        !string.IsNullOrWhiteSpace(def.DownloadUrl)
        && string.IsNullOrEmpty(def.VersionScraper.DownloadUrlQuery)
        && !VersionToken.IsMatch(def.DownloadUrl);

    /// <summary>The shared cache folder for a fixed-name download, or null when the stage has no download root or the folder cannot be created.</summary>
    private static string? SharedCacheRootFor(AppDefinition def, string? downloadRoot, Action<string>? onProgress)
    {
        if (string.IsNullOrWhiteSpace(downloadRoot) || !IsFixedNameDownload(def)) return null;
        try { return Workbench.WorkbenchPaths.GetSharedDownloadCacheRoot(downloadRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            onProgress?.Invoke($"Shared download cache unavailable; caching per download root: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The magic-byte check a download takes: a .zip file is checked as a ZIP
    /// whatever installer it carries, because the installer inside it is
    /// checked after extraction.
    /// </summary>
    public static string MagicTypeFor(string path, string installerType) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "ZIP" : installerType;

    private static readonly string[] PostExecutionBehaviors = ["BasedOnExitCode", "NoAction", "ProgramReboot", "ForceReboot"];

    /// <summary>The cmdlet spelling of a post-execution behavior; null when absent; throws on any other value.</summary>
    public static string? NormalizePostExecutionBehavior(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return PostExecutionBehaviors.FirstOrDefault(b => b.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"PostExecutionBehavior '{value}' is not one of {string.Join(", ", PostExecutionBehaviors)}.");
    }

    public const string SideFilesLocation = "Settings > Packager Files";

    /// <summary>
    /// Pairs each side file the definition declares with the chosen path.
    /// Throws when one is not chosen or the chosen file no longer exists.
    /// </summary>
    public static List<(SideFileConfig Config, string Source)> ResolveSideFiles(
        AppDefinition def, IReadOnlyDictionary<string, string>? chosen)
    {
        var resolved = new List<(SideFileConfig, string)>();
        foreach (var sideFile in def.SideFiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(sideFile.Key) || string.IsNullOrWhiteSpace(sideFile.FileName)
                || sideFile.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException($"{def.Application} declares a side file without a Key or a plain FileName.");
            if (chosen is null || !chosen.TryGetValue(sideFile.Key, out var path) || string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    $"{def.Application} requires a {sideFile.Label}. Choose {sideFile.FileName} in {SideFilesLocation}.");
            if (!File.Exists(path))
                throw new InvalidOperationException($"{def.Application} {sideFile.Label} not found: {path}");
            resolved.Add((sideFile, path));
        }
        return resolved;
    }

    /// <summary>
    /// Refuses a payload whose own version differs from ExpectedPayloadVersion:
    /// the MSI ProductVersion for an MSI, the FileVersion string otherwise.
    /// </summary>
    public static void VerifyPayloadVersion(AppDefinition def, string installerPath, string? msiProductVersion,
        bool isMsi, string displayVersion, Action<string>? onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(def.ExpectedPayloadVersion)) return;

        var expected = StageTokens.ApplyVersionParts(def.ExpectedPayloadVersion, displayVersion)!
            .Replace("{version}", displayVersion, StringComparison.OrdinalIgnoreCase);
        var actual = isMsi
            ? msiProductVersion?.Trim() ?? ""
            : (System.Diagnostics.FileVersionInfo.GetVersionInfo(installerPath).FileVersion ?? "").Trim();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Payload version '{actual}' does not match the expected version '{expected}': {Path.GetFileName(installerPath)}");
        onProgress?.Invoke($"Payload version verified     : {actual}");
    }

    /// <summary>
    /// The stage tokens for one payload. The EXE ProductVersion and the
    /// compiled-script DisplayVersion are read only when the definition
    /// names their token.
    /// </summary>
    public static StageTokens BuildStageTokens(AppDefinition def, string installerPath, string displayVersion,
        string? msiProductVersion, IReadOnlyDictionary<string, string> zipTokens, string? feedBuild = null)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(def);
        var productVersion = msiProductVersion;
        if (productVersion is null && text.Contains(StageTokens.ProductVersionToken, StringComparison.OrdinalIgnoreCase)
            && File.Exists(installerPath))
            productVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(installerPath).ProductVersion?.Trim();

        string? header = null;
        if (text.Contains(StageTokens.HeaderDisplayVersionToken, StringComparison.OrdinalIgnoreCase) && File.Exists(installerPath))
            header = CompiledScriptFacts.TryRead(InstallerSignatureScanner.Identify(installerPath), installerPath)?.DisplayVersion;

        string? bundleId = null;
        if (text.Contains(StageTokens.BundleIdToken, StringComparison.OrdinalIgnoreCase) && File.Exists(installerPath))
            bundleId = WixBurnBundleReader.ReadBundleId(installerPath);

        return new StageTokens
        {
            Version = displayVersion,
            ProductVersion = string.IsNullOrWhiteSpace(productVersion) ? null : productVersion,
            HeaderDisplayVersion = header,
            BundleId = bundleId,
            FeedBuild = feedBuild,
            Zip = zipTokens
        };
    }

    /// <summary>
    /// The Citrix Workspace install arguments for a stream, with each skipped
    /// setting reported. The EXE wrapper carries the arguments inside a
    /// single-quoted literal, so an apostrophe refuses the stage.
    /// </summary>
    public static string BuildCitrixInstallArgs(string stream, CitrixWorkspaceSwitches? switches, Action<string>? onProgress)
    {
        var built = CitrixInstallArguments.Build(switches, stream);
        foreach (var warning in built.Warnings)
            onProgress?.Invoke($"WARNING: {warning}");
        var offending = built.Arguments.FirstOrDefault(a => a.Contains('\''));
        if (offending is not null)
            throw new InvalidOperationException($"Citrix install argument contains an apostrophe, which the install wrapper cannot carry: {offending}");
        var args = string.Join(" ", built.Arguments);
        onProgress?.Invoke($"Install arguments            : {args}");
        return args;
    }

    /// <summary>{display_version_dashed} is the display version with every dot replaced by a hyphen.</summary>
    public static string ApplyDashedVersion(string text, string version)
    {
        // {version_base} is the version before its first hyphen, and {revision_dash} is the
        // hyphen and the text after it ("3.0.8-2" gives "3.0.8" and "-2"), or nothing. They
        // name an installer rebuilt under the same product version: gimp-3.0.8-setup-2.exe.
        var dash = version.IndexOf('-');
        var versionBase = dash < 0 ? version : version[..dash];
        var revisionDash = dash < 0 ? "" : version[dash..];
        return text
            .Replace("{display_version_dashed}", version.Replace('.', '-'), StringComparison.OrdinalIgnoreCase)
            .Replace("{version_base}", versionBase, StringComparison.OrdinalIgnoreCase)
            .Replace("{revision_dash}", revisionDash, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Text.RegularExpressions.Regex ChecksumLine =
        new(@"^\s*(?<hash>[0-9A-Fa-f]{64})(?:\s+\*?(?<name>\S.*?))?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The lower-case SHA256 a checksum file lists for <paramref name="fileName"/>,
    /// or null when no line names it. A line carrying a hash and no file name
    /// matches any file.
    /// </summary>
    public static string? ParseChecksumFile(string content, string fileName)
    {
        foreach (var line in content.Split('\n'))
        {
            var m = ChecksumLine.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            var name = m.Groups["name"].Value;
            if (name.Length == 0 || name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return m.Groups["hash"].Value.ToLowerInvariant();
        }
        return null;
    }

    private static async Task<string> ResolveChecksumFileHashAsync(
        AppDefinition def, string downloadUrl, string rawVersion, string installerFileName,
        Action<string>? onProgress, CancellationToken ct)
    {
        var url = def.PayloadSha256Url!
            .Replace("{download_url}", downloadUrl, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", rawVersion, StringComparison.OrdinalIgnoreCase);
        onProgress?.Invoke($"Checksum file                : {url}");

        using var process = Scrapers.CurlProcess.Start(
            ["-L", "--fail", "--silent", "--show-error", "--max-time", "60", .. Scrapers.CurlHeaders.Arguments(def.VersionScraper.RequestHeaders)], url);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(ct);
        var content = await stdout;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Failed to download checksum file: {url} ({(await stderr).Trim()})");

        return ParseChecksumFile(content, installerFileName)
            ?? throw new InvalidOperationException($"Checksum file names no hash for {installerFileName}: {url}");
    }

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
        Action<string>? onProgress, CancellationToken ct, string? knownVersion,
        StageFinalizer finalizer)
    {
        var partial = new PartialStage();
        IDisposable? stageLock = null;
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
            stageLock = await StageLock.AcquireAsync(contentPath, $"{def.Application} {version}", StageLockTimeout, onProgress, ct);
            partial.Track(downloadRoot, contentPath);
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

            // The bundle re-verifies every payload over an existing layout,
            // so a complete one is left alone.
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
            finalizer.PrepareStageRoot(contentPath);
            finalizer.Wrappers(_wrapperService).GenerateBootstrapperLayoutWrappers(contentPath, layout);

            var detection = BuildDetection(def, msiProps.ProductCode, msiProps.ProductVersion, msiProps.ProductVersion);

            var manifestPath = Path.Combine(contentPath, "stage-manifest.json");
            var manifest = new StageManifest
            {
                ApplicationId = CatalogApplicationId(def),
                AppName = $"{def.Application} - {version} ({def.Architecture})",
                DisplayName = $"{def.Application} ({def.Architecture})",
                Publisher = publisher,
                SoftwareVersion = version,
                InstallerFile = bootstrapperFileName,
                InstallerType = "EXE",
                Architecture = def.Architecture,
                InstallContext = def.InstallContext,
                InstallArgs = BuildLayoutInstallArgs(layout),
                UninstallArgs = $"{layout.QuietSwitch} {layout.UninstallSwitch}",
                UninstallCommand = $"{bootstrapperFileName} {layout.QuietSwitch} {layout.UninstallSwitch}",
                ProductCode = msiProps.ProductCode,
                RunningProcess = def.RunningProcess,
                RebootExitCodes = def.RebootExitCodes,
                SuccessExitCodes = def.SuccessExitCodes,
                Detection = detection
            };
            IconExtractionService.StageIcon(def, contentPath, manifest, onProgress);
            partial.WritesManifest = true;
            finalizer.FinalizeAndWrite(_manifestService, manifestPath, manifest, onProgress);
            onProgress?.Invoke($"Manifest written: {manifestPath}");
            onProgress?.Invoke($"Stage complete: {contentPath}");

            partial.Completed = true;
            return new StagingResult
            {
                Success = true,
                Version = version,
                ContentPath = contentPath,
                ManifestPath = manifestPath
            };
        }
        // Cancellation is not a staging failure. Returning it as one lets a caller read an
        // interrupted run as an ordinary result and carry on.
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StagingResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            try { partial.RemoveUnlessCompleted(onProgress); }
            finally { stageLock?.Dispose(); }
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
        Dictionary<string, string>? settingsValues, StageFinalizer finalizer)
    {
        var partial = new PartialStage();
        IDisposable? stageLock = null;
        try
        {
            var odt = def.Odt is null ? null : ResolveOdtConfig(def, settingsValues);
            if (odt is null || odt.ProductIds.Length == 0)
                return new StagingResult { Success = false, ErrorMessage = "OdtConfig with ProductIds is required for ODT apps" };
            var online = odt.DeploymentMode.Equals("Online", StringComparison.OrdinalIgnoreCase);
            onProgress?.Invoke($"ODT channel {odt.Channel}, {(online ? "Online (CDN install)" : "Offline (source downloaded)")}, excluded: {(odt.ExcludeApps.Length == 0 ? "none" : string.Join(", ", odt.ExcludeApps))}");

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

            var edition = def.Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "32" : "64";
            var appFolder = $"{def.Application.Replace(" ", "")}-{def.Architecture}";
            var baseRoot = Path.Combine(downloadRoot, appFolder);
            var contentPath = Path.Combine(baseRoot, version);
            stageLock = await StageLock.AcquireAsync(contentPath, $"{def.Application} {version}", StageLockTimeout, onProgress, ct);
            partial.Track(downloadRoot, contentPath);
            Directory.CreateDirectory(contentPath);

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

            // Offline: download.xml and setup.exe /download bring the source into
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

            var contentSetupExe = Path.Combine(contentPath, "setup.exe");
            if (!File.Exists(contentSetupExe))
            {
                File.Copy(setupExe, contentSetupExe);
                onProgress?.Invoke("Copied setup.exe to content folder.");
            }

            // install.xml carries no SourcePath, so an offline install reads the source
            // beside it and an online install reads the CDN. Offline pins the downloaded
            // build; online takes the channel's current build.
            var installXml = GenerateOdtXml(edition, fullVersion, odt, sourcePath: null, pinVersion: !online);
            await File.WriteAllTextAsync(Path.Combine(contentPath, "install.xml"), installXml, System.Text.Encoding.ASCII, ct);

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

            var installPs1 = string.Join("\r\n",
                "$setupPath = Join-Path $PSScriptRoot 'setup.exe'",
                "$configPath = Join-Path $PSScriptRoot 'install.xml'",
                "$proc = Start-Process -FilePath $setupPath -ArgumentList @('/configure', $configPath) -Wait -PassThru -NoNewWindow -WorkingDirectory $PSScriptRoot",
                "if (-not $proc) { exit 1 }",
                "exit $proc.ExitCode");
            var uninstallPs1 = string.Join("\r\n",
                "$setupPath = Join-Path $PSScriptRoot 'setup.exe'",
                "$configPath = Join-Path $PSScriptRoot 'uninstall.xml'",
                "$proc = Start-Process -FilePath $setupPath -ArgumentList @('/configure', $configPath) -Wait -PassThru -NoNewWindow -WorkingDirectory $PSScriptRoot",
                "if (-not $proc) { exit 1 }",
                "exit $proc.ExitCode");
            var installBat = finalizer.OdtLauncher("install.ps1");
            var uninstallBat = finalizer.OdtLauncher("uninstall.ps1");

            finalizer.PrepareStageRoot(contentPath);
            await File.WriteAllTextAsync(Path.Combine(contentPath, "install.ps1"), installPs1, System.Text.Encoding.ASCII, ct);
            await File.WriteAllTextAsync(Path.Combine(contentPath, "uninstall.ps1"), uninstallPs1, System.Text.Encoding.ASCII, ct);
            await File.WriteAllTextAsync(Path.Combine(contentPath, "install.bat"), installBat, System.Text.Encoding.ASCII, ct);
            await File.WriteAllTextAsync(Path.Combine(contentPath, "uninstall.bat"), uninstallBat, System.Text.Encoding.ASCII, ct);
            onProgress?.Invoke("Generated install/uninstall wrappers.");

            var detection = BuildDetection(def, null, version);
            var manifestPath = Path.Combine(contentPath, "stage-manifest.json");
            var publisher = def.Vendor;
            var manifest = new StageManifest
            {
                ApplicationId = CatalogApplicationId(def),
                AppName = $"{def.Application} - {version} ({def.Architecture})",
                DisplayName = $"{def.Application} ({def.Architecture})",
                Publisher = publisher,
                SoftwareVersion = version,
                InstallerFile = "setup.exe",
                InstallerType = "ODT",
                Architecture = def.Architecture,
                InstallArgs = "/configure install.xml",
                UninstallArgs = "/configure uninstall.xml",
                RunningProcess = def.RunningProcess,
                RebootExitCodes = def.RebootExitCodes,
                SuccessExitCodes = def.SuccessExitCodes,
                Detection = detection
            };
            IconExtractionService.StageIcon(def, contentPath, manifest, onProgress);
            partial.WritesManifest = true;
            finalizer.FinalizeAndWrite(_manifestService, manifestPath, manifest, onProgress);
            onProgress?.Invoke($"Manifest written: {manifestPath}");

            onProgress?.Invoke($"Stage complete: {contentPath}");
            partial.Completed = true;
            return new StagingResult
            {
                Success = true,
                ContentPath = contentPath,
                ManifestPath = manifestPath
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StagingResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            try { partial.RemoveUnlessCompleted(onProgress); }
            finally { stageLock?.Dispose(); }
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

    /// <summary>
    /// Determines the installer filename: explicit override → URL path → constructed fallback.
    /// </summary>
    private static string ResolveInstallerFileName(AppDefinition def, string downloadUrl, string version)
    {
        // fwlinks and query-string URLs need the definition's explicit file name.
        if (!string.IsNullOrEmpty(def.InstallerFileName))
            return def.InstallerFileName
                .Replace("{display_version}", version, StringComparison.OrdinalIgnoreCase)
                .Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

        var urlFileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
        if (!string.IsNullOrEmpty(urlFileName) && urlFileName.Contains('.'))
            return urlFileName;

        var safeName = def.Application.Replace(" ", "").Replace("(", "").Replace(")", "");
        var ext = def.InstallerType.ToUpperInvariant() switch
        {
            "MSI" => ".msi",
            "MSP" => ".msp",
            "MSIX" => ".msix",
            _ => ".exe"
        };
        return $"{safeName}-{version}{ext}";
    }

    private const string AllUserStoreApplications =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";

    /// <summary>
    /// The key template an MSIX definition records for its provisioning key rule. The stage resolves
    /// {PackageFullName} from the staged package.
    /// </summary>
    public const string MsixProvisioningKeyTemplate = AllUserStoreApplications + @"\{PackageFullName}";

    /// <summary>
    /// Detection for a staged MSIX. A definition whose Detection.Type is
    /// RegistryKey detects the provisioning store's key for the package full
    /// name; any other type keeps the provisioned-package script. A RegistryKey
    /// rule names no key or <see cref="MsixProvisioningKeyTemplate"/>; another
    /// key is refused, because the stage would replace it.
    /// </summary>
    public static StageDetection BuildMsixDetection(DetectionConfig? definitionDetection, StageMsixPackage package)
    {
        if (definitionDetection?.Type.Equals("RegistryKey", StringComparison.OrdinalIgnoreCase) != true)
            return BuildMsixScriptDetection(package);
        var template = definitionDetection.RegistryKeyTemplate?.Trim();
        if (!string.IsNullOrEmpty(template) && !template.Equals(MsixProvisioningKeyTemplate, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"An MSIX definition detects the package by its provisioning key. Set the registry key of the rule to {MsixProvisioningKeyTemplate}, or use a Script rule.");
        return BuildMsixProvisioningKeyDetection(package);
    }

    /// <summary>
    /// Existence detection on the all-user provisioning store key named by the
    /// package full name. The name carries the exact version and
    /// deprovisioning removes the key, so its presence is the installed state
    /// of this build. The key lives in the native registry view.
    /// </summary>
    public static StageDetection BuildMsixProvisioningKeyDetection(StageMsixPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Name) || string.IsNullOrWhiteSpace(package.Version)
            || string.IsNullOrWhiteSpace(package.ProcessorArchitecture) || string.IsNullOrWhiteSpace(package.Publisher))
            throw new InvalidOperationException(
                "MSIX identity is missing Name, Version, ProcessorArchitecture or Publisher; the package full name for detection cannot be built.");

        var fullName = MsixPackageReader.PackageFullName(package.Name, package.Version, package.ProcessorArchitecture, package.ResourceId, package.Publisher);
        return new StageDetection
        {
            Type = "RegistryKey",
            RegistryKeyRelative = $@"{AllUserStoreApplications}\{fullName}",
            Is64Bit = true,
            Hive = "LocalMachine"
        };
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
        var name = PowerShellLiteral.EscapeSingleQuoted(package.Name);
        var version = PowerShellLiteral.EscapeSingleQuoted(package.Version);

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

    private static readonly System.Text.RegularExpressions.Regex GuidKeyName =
        new(@"\{[0-9a-fA-F-]{36}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Applies the operator's install mode to a definition stage. The staged
    /// installer's compiled script must offer the mode; the definition must
    /// declare it. The mode switch replaces any mode switch already in the
    /// arguments, the uninstaller and detection follow the branch, a per-user
    /// branch detects through its HKCU uninstall key, and a system File
    /// detection moves between the two branch folders.
    /// </summary>
    public static InstallModeResult ApplyInstallMode(
        AppDefinition def, string installerPath, string installMode, string installArgs,
        StageDetection detection, Action<string>? onProgress = null, string? uninstallExpression = null,
        string? softwareVersion = null)
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
        else if (detection.Type == "File" && branch.InstallContext == "PerUser")
        {
            // A profile path cannot be resolved in the packaging or SYSTEM
            // context, so the per-user branch detects through the uninstall key
            // the branch itself names. Nothing is guessed from a folder name.
            var relative = branch.UninstallRegistryKeyRelative;
            var version = string.IsNullOrWhiteSpace(softwareVersion) ? detection.ExpectedValue : softwareVersion;
            // A rebuild suffix ("3.0.8-2") is no part of the version the installed product reports.
            var numeric = System.Text.RegularExpressions.Regex.Match(version ?? "", @"^\d+(\.\d+)*");
            if (numeric.Success) version = numeric.Value;
            // A key holding an installer variable, a wildcard or an environment
            // token was never resolved. A braced GUID is a resolved key name.
            var unresolved = GuidKeyName.Replace(relative, "");
            if (branch.RegistryHive != "HKCU" || string.IsNullOrWhiteSpace(relative)
                || unresolved.IndexOfAny(['$', '{', '}', '*', '?', '%']) >= 0
                || string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException(
                    $"Cannot create user detection for {Path.GetFileName(installerPath)}: the script did not supply a concrete HKCU uninstall key and application version.");
            newDetection = new StageDetection
            {
                Type = "RegistryKeyValue",
                Hive = "CurrentUser",
                RegistryKeyRelative = relative,
                ValueName = "DisplayVersion",
                PropertyType = "Version",
                Operator = "GreaterEquals",
                ExpectedValue = version,
                Is64Bit = false
            };
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

    /// <summary>
    /// Definitions write literal Program Files paths where the script reader
    /// writes environment form; the machine folders are expanded to their
    /// literal x64-host values so the two can be compared.
    /// </summary>
    private static string ExpandKnownFolders(string path) => path
        .Replace("%ProgramW6432%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles(x86)%", @"C:\Program Files (x86)", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%CommonProgramW6432%", @"C:\Program Files\Common Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%CommonProgramFiles(x86)%", @"C:\Program Files (x86)\Common Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%CommonProgramFiles%", @"C:\Program Files\Common Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramData%", @"C:\ProgramData", StringComparison.OrdinalIgnoreCase);

    /// <summary>The application name, display name, publisher and version one stage records.</summary>
    public sealed record StageIdentity(string AppName, string DisplayName, string Publisher, string SoftwareVersion);

    /// <summary>
    /// Names and versions a stage. The version is the scraped one on every machine: the
    /// inner MSI of a bundle carries its own build number (8.0.31.26421 for 8.0.31), and
    /// only a machine with 7-Zip reads it. A definition with AppName fixes the name and
    /// publisher; otherwise a metadata name that names this product is used (metadata read
    /// off the largest bundled EXE can name a bundled 7-Zip or GnuPG component), then the
    /// constructed name.
    /// </summary>
    public static StageIdentity ResolveStageIdentity(AppDefinition def, string displayVersion, string publisher,
        InstallerMetadata? installerMetadata, Action<string>? onProgress = null)
    {
        var fixedName = string.IsNullOrWhiteSpace(def.AppName)
            ? null
            : def.AppName.Replace("{version}", displayVersion, StringComparison.OrdinalIgnoreCase).Trim();
        var trustMetadataName = fixedName is null && installerMetadata is not null
            && IsProductNamePlausible(installerMetadata, def);
        var name = fixedName ?? (trustMetadataName ? installerMetadata!.ProductName : null);
        if (fixedName is not null) publisher = string.IsNullOrWhiteSpace(def.Publisher) ? def.Vendor : def.Publisher.Trim();
        if (installerMetadata?.ProductName is { Length: > 0 } && !trustMetadataName)
            onProgress?.Invoke($"Installer metadata names '{installerMetadata.ProductName}' ({installerMetadata.MetadataSource}); keeping the definition's name.");
        if (installerMetadata?.ProductVersion is { Length: > 0 } metadataVersion
            && !string.Equals(metadataVersion, displayVersion, StringComparison.OrdinalIgnoreCase))
            onProgress?.Invoke($"Installer metadata version {metadataVersion} ({installerMetadata.MetadataSource}); staging the scraped {displayVersion}.");
        return new StageIdentity(
            !string.IsNullOrEmpty(name) ? name : $"{def.Application} - {displayVersion} ({def.Architecture})",
            !string.IsNullOrEmpty(name) ? name : $"{def.Application} ({def.Architecture})",
            publisher,
            displayVersion);
    }

    /// <summary>True when the definition's detection, commands or arguments resolve a {ProductCode} token.</summary>
    public static bool ReferencesProductCode(AppDefinition def)
    {
        static bool Has(string? text) => text?.Contains("{ProductCode}", StringComparison.OrdinalIgnoreCase) == true;
        var detection = def.Detection;
        return Has(def.InstallArgs) || Has(def.UninstallArgs) || Has(def.UninstallCommand)
            || Has(detection.RegistryKeyTemplate) || Has(detection.ScriptText)
            || (detection.Clauses ?? []).Any(c => Has(c.RegistryKeyTemplate));
    }

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
        Dictionary<string, string>? settingsValues = null, StageTokens? tokens = null)
    {
        var det = ApplySettingTokens(def.Detection, def.InstallerSettings, settingsValues);
        if (StageTokens.HasTokens(det))
            det = tokens is null ? StageTokens.ApplyVersionParts(det, version) : tokens.Apply(det);

        if (det.RegistryKeyTemplate?.Contains("{ProductCode}", StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrEmpty(productCode)
            && metadata?.HasDetectionData == true)
        {
            return BuildDetectionFromMetadata(metadata, version, def.Detection.Is64Bit);
        }

        if (det.Type.Equals("Script", StringComparison.OrdinalIgnoreCase))
            return BuildScriptDetection(det, productCode, version);

        if (det.Type.Equals(ArpEntryDetection.TypeName, StringComparison.OrdinalIgnoreCase))
            return new StageDetection
            {
                Type = "Script",
                ScriptLanguage = "PowerShell",
                ScriptText = ArpEntryDetectionScript.Build(ResolveArpEntry(det, version))
            };

        if (det.Type.Equals("Compound", StringComparison.OrdinalIgnoreCase))
            return BuildCompoundDetection(det, productCode, version, rawVersion);

        return BuildSingleDetection(det, productCode, version, rawVersion);
    }

    /// <summary>
    /// The manifest's WsusDetection: the definition's Add/Remove Programs entry
    /// with the staged version, the same entry the detection script reads.
    /// Null for a definition whose detection is not of type ArpEntry.
    /// </summary>
    public static ArpEntryDetection? BuildWsusDetection(AppDefinition def, string version,
        Dictionary<string, string>? settingsValues = null, StageTokens? tokens = null)
    {
        if (!def.Detection.Type.Equals(ArpEntryDetection.TypeName, StringComparison.OrdinalIgnoreCase)) return null;
        var det = ApplySettingTokens(def.Detection, def.InstallerSettings, settingsValues);
        if (StageTokens.HasTokens(det))
            det = tokens is null ? StageTokens.ApplyVersionParts(det, version) : tokens.Apply(det);
        return ResolveArpEntry(det, version);
    }

    private static ArpEntryDetection ResolveArpEntry(DetectionConfig det, string version) =>
        (det.ArpEntry ?? throw new InvalidOperationException("Detection type 'ArpEntry' requires an ArpEntry block."))
            .With(text => text?.Replace("{version}", version, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// True for digits in two or more dot-separated parts. A scraped token without a dot
    /// ("473" for 4.73) compares as a different version than the one the product reports.
    /// </summary>
    private static bool IsDottedVersion([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d+(\.\d+)+$");

    /// <summary>
    /// Resolves one registry or file detection config into its staged form.
    /// </summary>
    private static StageDetection BuildSingleDetection(
        DetectionConfig det, string? productCode, string version, string? rawVersion)
    {
        if (det.Type.Equals("RegistryKeyValue", StringComparison.OrdinalIgnoreCase))
        {
            var regKey = det.RegistryKeyTemplate?
                .Replace("{ProductCode}", productCode ?? "", StringComparison.OrdinalIgnoreCase)
                .Replace("{version}", version, StringComparison.OrdinalIgnoreCase) ?? "";

            // For Version-type detection, sanitize version to valid System.Version format
            // (e.g., "11.0.30+7" → "11.0.30.7" — CM requires dotted version strings)
            // Use rawVersion (MSI ProductVersion) when available — ARP DisplayVersion comes from the MSI,
            // not from the scraped marketing version (e.g., XenCenter: scraped 2026.1.0, MSI 26.1.0).
            // A raw value that is not a dotted version is a scraped token ("1-8-3-0" for 1.8.3.0);
            // no installed product reports it.
            var detValue = IsDottedVersion(rawVersion) ? rawVersion : version;
            if (det.PropertyType?.Equals("Version", StringComparison.OrdinalIgnoreCase) == true)
                detValue = version.Replace("+", ".").Replace("-", ".");
            // A prefix comparison reads the value the package reports, so the
            // raw scrape is wrong here: it can be an untransformed token
            // ("723" for a release displayed as 7.23.0).
            else if (det.Operator?.Equals("BeginsWith", StringComparison.OrdinalIgnoreCase) == true)
                detValue = version;
            if (!string.IsNullOrEmpty(det.ExpectedValue))
                detValue = det.ExpectedValue.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

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
                && IsDottedVersion(rawVersion))
            {
                detVersion = rawVersion;
            }
            if (!string.IsNullOrEmpty(det.ExpectedValue))
                detVersion = det.ExpectedValue.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

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
            // A display marker of an analysed package. Publishing refuses a stage with this type: an MSIX is published as a script deployment type.
            return new StageDetection
            {
                Type = "MSIX",
                ExpectedValue = metadata.ProductName ?? "",
                Is64Bit = is64Bit
            };
        }

        return new StageDetection { Type = "Unknown" };
    }

    /// <summary>
    /// Stages an already-analysed local installer into the same versioned content
    /// layout the definition pipeline produces, so packaging consumes it unchanged.
    /// There is no version scrape or download — the file on disk is the source. With
    /// <paramref name="expected"/>, the copy lands under a side name and replaces the staged
    /// installer only when its SHA-256 matches; otherwise the stage fails before anything
    /// is finalized.
    /// </summary>
    public async Task<StagingResult> StageDroppedInstallerAsync(
        StageManifest manifest, string sourceInstallerPath, string downloadRoot,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        StageInputs? inputs = null,
        ExpectedInstaller? expected = null)
    {
        var partial = new PartialStage();
        IDisposable? stageLock = null;
        try
        {
            var finalizer = StageFinalizer.Begin(inputs);
            downloadRoot = finalizer.ScopeDownloadRoot(StagingRootGuard.Prepare(downloadRoot));
            if (!File.Exists(sourceInstallerPath))
                return new StagingResult { Success = false, ErrorMessage = $"Installer not found: {sourceInstallerPath}" };

            // Wrapper .ps1 files are written for Windows PowerShell defaults on the
            // target device; a non-ASCII installer filename is mangled inside the
            // wrapper and fails only at deployment time.
            if (manifest.InstallerFile.Any(c => c > 0x7F))
                return new StagingResult
                {
                    Success = false,
                    ErrorMessage = $"Installer filename contains non-ASCII characters; rename it before staging: {manifest.InstallerFile}"
                };

            var vendorFolder = MecmTarget.SanitizeFolderNamePublic(manifest.Publisher);
            var appFolder = MecmTarget.SanitizeFolderNamePublic(manifest.DisplayName ?? manifest.AppName);
            var versionFolder = MecmTarget.SanitizeFolderNamePublic(manifest.SoftwareVersion);

            if (string.IsNullOrWhiteSpace(appFolder))
                return new StagingResult { Success = false, ErrorMessage = "Application name contains no usable path characters." };
            if (string.IsNullOrWhiteSpace(versionFolder))
                return new StagingResult { Success = false, ErrorMessage = "Version contains no usable path characters." };
            if (string.IsNullOrWhiteSpace(vendorFolder))
                vendorFolder = "Unknown";

            var contentPath = Path.Combine(downloadRoot, vendorFolder, appFolder, versionFolder);
            stageLock = await StageLock.AcquireAsync(contentPath, $"{appFolder} {versionFolder}", StageLockTimeout, onProgress, ct);
            partial.Track(downloadRoot, contentPath);
            Directory.CreateDirectory(contentPath);
            ct.ThrowIfCancellationRequested();

            var stagedInstaller = Path.Combine(contentPath, manifest.InstallerFile);
            if (expected is null)
            {
                File.Copy(sourceInstallerPath, stagedInstaller, true);
            }
            else
            {
                var copy = $"{stagedInstaller}.{Guid.NewGuid():N}.partial";
                try
                {
                    File.Copy(sourceInstallerPath, copy, false);
                    var actual = ComputeSha256(copy);
                    if (!string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                        return new StagingResult
                        {
                            Success = false,
                            ErrorMessage = $"The installer copied from {sourceInstallerPath} does not match {expected.Source}: its SHA-256 is {actual}, " +
                                           $"the recorded one is {expected.Sha256}. Nothing was staged."
                        };
                    File.Move(copy, stagedInstaller, overwrite: true);
                }
                finally
                {
                    if (File.Exists(copy)) File.Delete(copy);
                }
            }
            onProgress?.Invoke($"Staged installer: {stagedInstaller}");

            onProgress?.Invoke("Generating install/uninstall wrappers...");
            finalizer.PrepareStageRoot(contentPath);
            var wrappers = finalizer.Wrappers(_wrapperService);
            if (manifest.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase))
                wrappers.GenerateMsiWrappers(contentPath, manifest.InstallerFile, null, manifest.InstallArgs);
            else
            {
                var (arpKeyName, installFolder) = WrapperGeneratorService.ResolveUninstallHints(manifest.Detection);
                var uninstallExpression = string.IsNullOrWhiteSpace(manifest.UninstallCommand)
                    ? null
                    : WrapperGeneratorService.UninstallExpressionFromCommandLine(manifest.UninstallCommand);
                if (uninstallExpression == "") uninstallExpression = null;
                wrappers.GenerateExeWrappers(contentPath, manifest.InstallerFile,
                    manifest.InstallArgs, manifest.UninstallArgs, manifest.DetectedEngine, manifest.AppName, uninstallExpression,
                    null, arpKeyName, installFolder);
            }

            var manifestPath = Path.Combine(contentPath, "stage-manifest.json");
            partial.WritesManifest = true;
            finalizer.FinalizeAndWrite(_manifestService, manifestPath, manifest, onProgress);
            onProgress?.Invoke($"Manifest written: {manifestPath}");

            partial.Completed = true;
            return new StagingResult
            {
                Success = true,
                Version = manifest.SoftwareVersion,
                ContentPath = contentPath,
                ManifestPath = manifestPath
            };
        }
        // Cancellation is not a staging failure; it propagates.
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StagingResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            try { partial.RemoveUnlessCompleted(onProgress); }
            finally { stageLock?.Dispose(); }
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
            else if (param.ControlType == LocaleListControl)
            {
                var list = ResolveLocaleList(param, value);
                if (list.Length > 0)
                    parts.Add(fragment.Replace("{value}", list, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (!string.IsNullOrEmpty(value))
                    parts.Add(fragment.Replace("{value}", MapValue(param, null, value), StringComparison.OrdinalIgnoreCase));
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>The InstallerParameter control type for a multi-select list of locale codes.</summary>
    public const string LocaleListControl = "LocaleList";

    private static readonly System.Text.RegularExpressions.Regex StoredLocalePattern =
        new("^([a-z]{2}_[A-Z]{2}|All)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The stored codes of a LocaleList, comma or space separated, split and kept only
    /// where they read as a locale code or All, as the stored preference is filtered.
    /// </summary>
    public static IReadOnlyList<string> ParseLocaleList(string? stored) =>
        (stored ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(code => StoredLocalePattern.IsMatch(code))
            .ToList();

    /// <summary>
    /// The value a LocaleList puts into its fragment: empty when nothing is selected, the
    /// mapped All keyword when All is selected, otherwise the selected codes plus
    /// AlwaysInclude, sorted and comma separated. A code the parameter does not list
    /// stops the stage.
    /// </summary>
    public static string ResolveLocaleList(InstallerParameter param, string? stored)
    {
        var codes = ParseLocaleList(stored);
        if (codes.Count == 0) return "";
        if (codes.Contains("All", StringComparer.Ordinal)) return MapValue(param, null, "All");

        var known = new HashSet<string>(param.Options ?? [], StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(param.AlwaysInclude)) known.Add(param.AlwaysInclude);
        var unknown = codes.Where(c => !known.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"Unknown locale code(s) for {param.Label}: {string.Join(", ", unknown)}. Known codes: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");

        var set = codes.ToList();
        if (!string.IsNullOrWhiteSpace(param.AlwaysInclude)) set.Add(param.AlwaysInclude);
        return string.Join(",", set.Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase));
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
    public static string? ResolvePostInstallScript(AppDefinition def, Dictionary<string, string>? values, string? version = null)
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

        var script = ApplySettingTokens(config.Script, def.InstallerSettings, values);
        return version is null || script is null ? script : script.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);
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
            ExpectedValue = ApplySettingTokens(det.ExpectedValue, parameters, values),
            ScriptLanguage = det.ScriptLanguage,
            ScriptText = ApplySettingTokens(det.ScriptText, parameters, values),
            Connector = det.Connector,
            Clauses = det.Clauses?.Select(c => ApplySettingTokens(c, parameters, values)).ToArray(),
            GroupSizes = det.GroupSizes,
            ArpEntry = det.ArpEntry?.With(text => ApplySettingTokens(text, parameters, values))
        };
    }

    private static bool HasSettingToken(DetectionConfig det) =>
        det.RegistryKeyTemplate?.Contains("{setting:", StringComparison.OrdinalIgnoreCase) == true
        || det.FilePath?.Contains("{setting:", StringComparison.OrdinalIgnoreCase) == true
        || det.FileName?.Contains("{setting:", StringComparison.OrdinalIgnoreCase) == true
        || det.ScriptText?.Contains("{setting:", StringComparison.OrdinalIgnoreCase) == true
        || det.ExpectedValue?.Contains("{setting:", StringComparison.OrdinalIgnoreCase) == true
        || det.Clauses?.Any(HasSettingToken) == true;
}
