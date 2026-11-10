using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// The WSUS compatibility check on the default build of a definition, without a
/// download: the wrappers, the detection and the manifest are built the way the stage
/// builds them for a stand-in installer, with no install mode, no operator settings and
/// no optional architecture payload, and the publisher's own check reads the result.
/// When the detection cannot compare the installed version, the publisher reads the
/// Add/Remove Programs entry out of the staged MSI. Whether an MSI yields a usable
/// entry depends on its own Property table, which a stand-in does not have, so such a
/// definition has two possible results.
/// </summary>
internal static class DefaultBuildWsusCheck
{
    private const string Version = "1.2.3.4";
    private const string ProductCode = "{11111111-2222-3333-4444-555555555555}";

    private sealed class StandInMsi(AppDefinition def, bool carriesEntry) : IMsiInspector
    {
        public MsiProperties ReadProperties(string msiPath) => new()
        {
            ProductName = !carriesEntry ? "" : string.IsNullOrWhiteSpace(def.Application) ? "Product" : def.Application,
            Manufacturer = string.IsNullOrWhiteSpace(def.Vendor) ? "Vendor" : def.Vendor,
            ProductVersion = Version,
            ProductCode = ProductCode
        };

        public string ReadTemplateArchitecture(string msiPath) =>
            def.Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "Intel" : "x64";
    }

    public static IReadOnlyList<WsusFinding> Findings(AppDefinition def, bool msiCarriesEntry = true) =>
        Stage(def, (folder, manifest) => WsusCompatibility.Evaluate(manifest, folder, new WsusRuleBuilder(new StandInMsi(def, msiCarriesEntry))));

    /// <summary>The generated install and uninstall scripts and launchers of the default build, by file name.</summary>
    public static IReadOnlyDictionary<string, string> Scripts(AppDefinition def) =>
        Stage(def, (folder, _) => Directory.GetFiles(folder)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".ps1" or ".bat")
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllText, StringComparer.OrdinalIgnoreCase));

    private static T Stage<T>(AppDefinition def, Func<string, StageManifest, T> use)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-wsus-tag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var manifest = def.InstallerType.Equals("ODT", StringComparison.OrdinalIgnoreCase) ? Odt(def, folder)
                : def.BootstrapperLayout is not null ? Layout(def, folder)
                : Installer(def, folder);

            manifest.FileHashes = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => new StageFileHash { RelativePath = Path.GetRelativePath(folder, path), Sha256 = new string('0', 64) })
                .ToArray();
            return use(folder, manifest);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// <summary>The result for an MSI that yields a usable Add/Remove Programs entry.</summary>
    public static WsusSupportTag Compute(AppDefinition def, bool msiCarriesEntry = true) =>
        WsusSupportTag.FromBlockingCodes(WsusCompatibility.BlockingCodes(Findings(def, msiCarriesEntry)));

    /// <summary>Every result the check can give for the default build: one, or two when it depends on the staged MSI.</summary>
    public static IReadOnlyList<string> Possible(AppDefinition def) =>
        new[] { Compute(def).Text, Compute(def, msiCarriesEntry: false).Text }.Distinct().ToList();

    private static StageManifest Odt(AppDefinition def, string folder)
    {
        File.WriteAllText(Path.Combine(folder, "setup.exe"), "MZ");
        File.WriteAllText(Path.Combine(folder, "install.ps1"), string.Join("\r\n",
            "$setupPath = Join-Path $PSScriptRoot 'setup.exe'",
            "$configPath = Join-Path $PSScriptRoot 'install.xml'",
            "$proc = Start-Process -FilePath $setupPath -ArgumentList @('/configure', $configPath) -Wait -PassThru -NoNewWindow -WorkingDirectory $PSScriptRoot",
            "if (-not $proc) { exit 1 }",
            "exit $proc.ExitCode"));
        File.WriteAllText(Path.Combine(folder, "install.xml"), "<Configuration />");
        return new StageManifest
        {
            InstallerFile = "setup.exe",
            InstallerType = "ODT",
            SoftwareVersion = Version,
            Detection = StagingService.BuildDetection(def, null, Version)
        };
    }

    private static StageManifest Layout(AppDefinition def, string folder)
    {
        var layout = def.BootstrapperLayout!;
        var bootstrapper = WrapperGeneratorService.ResolveBootstrapperFileName(layout);
        File.WriteAllText(Path.Combine(folder, bootstrapper), "MZ");
        Directory.CreateDirectory(Path.Combine(folder, "Installers"));
        File.WriteAllText(Path.Combine(folder, "Installers", "component.msi"), "msi");
        new WrapperGeneratorService().GenerateBootstrapperLayoutWrappers(folder, layout);
        return new StageManifest
        {
            InstallerFile = bootstrapper,
            InstallerType = "EXE",
            InstallContext = def.InstallContext,
            SoftwareVersion = Version,
            ProductCode = ProductCode,
            Detection = StagingService.BuildDetection(def, ProductCode, Version, Version)
        };
    }

    private static StageManifest Installer(AppDefinition def, string folder)
    {
        var isMsix = def.InstallerType.Equals("MSIX", StringComparison.OrdinalIgnoreCase);
        var isMsi = def.InstallerType.Equals("MSI", StringComparison.OrdinalIgnoreCase);
        var installerFileName = isMsix ? "installer.msix" : def.ZipPayload is not null ? "payload.zip" : isMsi ? "installer.msi" : "installer.exe";
        var installerPath = Path.Combine(folder, installerFileName);
        File.WriteAllText(installerPath, "stand-in");

        IReadOnlyDictionary<string, string> zipTokens = def.ZipPayload?.EntryTokens is { } entryTokens
            ? entryTokens.Keys.ToDictionary(name => name, _ => "1.2.3")
            : new Dictionary<string, string>();

        var installArgs = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, null);
        var uninstallArgs = StagingService.ResolveUninstallArgs(
            StagingService.ApplySettingTokens(def.UninstallArgs, def.InstallerSettings, null) ?? def.UninstallArgs, def.InstallerSettings, null);
        var uninstallCommand = StagingService.ApplySettingTokens(def.UninstallCommand, def.InstallerSettings, null);
        var tokens = new StageTokens { Version = Version, ProductVersion = Version, HeaderDisplayVersion = Version, BundleId = ProductCode, FeedBuild = "1.2.3.4", Zip = zipTokens };
        if (!string.IsNullOrWhiteSpace(def.CitrixSwitchStream))
            installArgs = StagingService.BuildCitrixInstallArgs(def.CitrixSwitchStream, null, null);
        installArgs = tokens.Apply(installArgs)!;
        uninstallCommand = tokens.Apply(uninstallCommand);

        var productCode = isMsi || StagingService.ReferencesProductCode(def) ? ProductCode : null;
        StageMsixPackage? msix = isMsix
            ? new StageMsixPackage { Name = "Vendor.Product", Version = Version, Publisher = "CN=Vendor", ProcessorArchitecture = def.Architecture, SignerThumbprint = new string('A', 40) }
            : null;
        var detection = msix is not null
            ? StagingService.BuildMsixDetection(def.Detection, msix)
            : StagingService.BuildDetection(def, productCode, Version, Version, null, null, tokens);

        var wrappers = new WrapperGeneratorService();
        if (isMsix)
            wrappers.GenerateMsixWrappers(folder, installerFileName, msix!.SignerThumbprint, msix.Provisioned);
        else if (def.ZipPayload is not null)
            ZipPayload.GenerateWrappers(wrappers, folder, installerFileName, def.ZipPayload, Version);
        else if (isMsi)
            wrappers.GenerateMsiWrappers(folder, installerFileName, uninstallCommand, installArgs, def.ExpandInstallArgs);
        else if (def.ExtractSetup is not null)
            wrappers.GenerateExtractSetupWrappers(folder, installerFileName, def.ExtractSetup, installArgs, uninstallArgs);
        else
        {
            var (arpKeyName, installFolder) = WrapperGeneratorService.ResolveUninstallHints(detection);
            wrappers.GenerateExeWrappers(folder, installerFileName, installArgs, uninstallArgs, def.InstallerFramework, def.Application,
                uninstallCommand, StagingService.ResolvePostInstallScript(def, null, Version), arpKeyName, installFolder,
                def.PostInstallKillProcesses, def.RebootExitCodes, def.SuccessExitCodes);
        }

        if (def.TargetSecret is not null)
            TargetSecretScript.ApplyToFile(Path.Combine(folder, "install.ps1"), def.TargetSecret);

        return new StageManifest
        {
            InstallerFile = installerFileName,
            InstallerType = def.InstallerType,
            InstallContext = def.InstallContext,
            SoftwareVersion = Version,
            ProductCode = productCode,
            Detection = detection,
            WsusDetection = msix is null ? StagingService.BuildWsusDetection(def, Version, null, tokens) : null,
            MsixPackage = msix
        };
    }
}
