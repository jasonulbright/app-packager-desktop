using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ApplicationPackagerEngine.Services.InstallerScripts;

namespace ApplicationPackagerEngine.Services;

public sealed partial class InstallerAnalysisService
{
    private const int ManifestLimitBytes = 4 * 1024 * 1024;

    private static readonly Dictionary<int, string> SummaryNames = new()
    {
        [7] = "Template", [9] = "Revision number", [2] = "Title", [3] = "Subject", [4] = "Author", [5] = "Keywords",
        [6] = "Comments", [18] = "Creating application", [12] = "Created", [13] = "Last saved", [14] = "Schema",
        [15] = "Word count", [19] = "Security"
    };

    private static void ReadMsi(string path, AnalysisLimits limits, List<AnalysisFact> properties, List<MetadataBlock> metadata, List<string> notes, CancellationToken ct)
    {
        try
        {
            using var db = MsiDatabase.Open(path);
            var (rows, truncated) = MsiTables.Properties(db, limits.MaxEntries, ct);
            foreach (var (name, value) in rows) properties.Add(new AnalysisFact(name, value, "Confirmed", "msi-property-table"));
            if (truncated) notes.Add($"Only the first {limits.MaxEntries:N0} MSI properties are listed.");
            var cabinets = MsiTables.ExternalCabinets(db, ct);
            if (cabinets.Count > 0)
                notes.Add("The MSI installs files from external cabinets that are not inside this file: " + string.Join(", ", cabinets.Take(5)) + (cabinets.Count > 5 ? ", ..." : "") + ".");
        }
        catch (OperationCanceledException) { throw; }
        catch (InstallerAnalysisException) { throw; }
        catch (Exception)
        {
            notes.Add("The MSI database could not be read; it may be damaged.");
        }
        var summary = Safe(() => MsiDatabase.ReadSummary(path), new Dictionary<int, string>());
        var facts = SummaryNames.Where(n => summary.ContainsKey(n.Key)).Select(n => new AnalysisFact(n.Value, summary[n.Key], "Confirmed", "msi-summary-information")).ToList();
        if (facts.Count > 0) metadata.Add(new MetadataBlock("MSI summary information", facts));
    }

    /// <summary>
    /// Reads a patch's summary information and MsiPatchMetadata, and the effective detection
    /// target: the first target ProductCode, or the base MSI's when one is given, with the
    /// post-patch version from the "Name (version)" convention of the patch DisplayName.
    /// </summary>
    private EffectiveDetection? ReadMsp(string path, string? baseMsi, List<MetadataBlock> metadata, List<string> notes, CancellationToken ct)
    {
        Dictionary<string, string> meta;
        try
        {
            using var db = MsiDatabase.Open(path, patch: true);
            meta = MsiTables.PatchMetadata(db, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            notes.Add("The patch could not be read; it may be damaged.");
            return null;
        }
        var summary = Safe(() => MsiDatabase.ReadSummary(path), new Dictionary<int, string>());
        var template = summary.GetValueOrDefault(7, "");
        var targets = template.Split(';').Select(t => t.Trim()).Where(t => Regex.IsMatch(t, @"^\{[0-9A-Fa-f-]{36}\}$")).ToList();
        var patchCodes = Regex.Matches(summary.GetValueOrDefault(9, ""), @"\{[0-9A-Fa-f-]{36}\}").Select(m => m.Value).ToList();
        var displayName = meta.GetValueOrDefault("DisplayName", "");
        string? version = null;
        foreach (var source in new[] { displayName, meta.GetValueOrDefault("Description", ""), summary.GetValueOrDefault(3, ""), summary.GetValueOrDefault(2, "") })
        {
            var m = Regex.Match(source, @"\(([0-9]+(?:\.[0-9]+){1,3})\)");
            if (m.Success) { version = m.Groups[1].Value; break; }
        }

        var facts = new List<AnalysisFact>
        {
            Fact("DisplayName", displayName, "msi-patch-metadata"),
            Fact("TargetProductName", meta.GetValueOrDefault("TargetProductName", ""), "msi-patch-metadata"),
            Fact("Post-patch version", version, "msi-patch-metadata", "Predicted"),
            Fact("Classification", meta.GetValueOrDefault("Classification", ""), "msi-patch-metadata"),
            Fact("ManufacturerName", meta.GetValueOrDefault("ManufacturerName", ""), "msi-patch-metadata"),
            Fact("MoreInfoURL", meta.GetValueOrDefault("MoreInfoURL", ""), "msi-patch-metadata"),
            Fact("CreationTimeUTC", meta.GetValueOrDefault("CreationTimeUTC", ""), "msi-patch-metadata"),
            Fact("AllowRemoval", meta.GetValueOrDefault("AllowRemoval", ""), "msi-patch-metadata"),
            Fact("Primary PatchCode", patchCodes.FirstOrDefault(), "msi-summary-information"),
            Fact("Patch codes", patchCodes.Count.ToString(CultureInfo.InvariantCulture), "msi-summary-information"),
            Fact("Target ProductCodes", string.Join(", ", targets), "msi-summary-information")
        };
        metadata.Add(new MetadataBlock("MSI patch (" + Path.GetFileName(path) + ")", facts.Where(f => f.Value.Length > 0).ToList()));

        string productCode = targets.FirstOrDefault() ?? "";
        string baseVersion = "";
        string architecture = "";
        var explanation = "The ProductCode is the patch's first target; the version is read from the patch DisplayName.";
        if (baseMsi is not null)
        {
            try
            {
                var props = _msi.ReadProperties(baseMsi);
                if (!string.IsNullOrEmpty(props.ProductCode))
                {
                    productCode = props.ProductCode;
                    baseVersion = props.ProductVersion;
                    architecture = _msi.ReadTemplateArchitecture(baseMsi);
                    explanation = "The ProductCode is the base MSI's (" + Path.GetFileName(baseMsi) + "); the version is read from the patch DisplayName.";
                    if (targets.Count > 0 && !targets.Contains(productCode, StringComparer.OrdinalIgnoreCase))
                        explanation += " The base ProductCode is not among the patch targets.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add("The base MSI next to the patch could not be read.");
            }
        }
        if (string.IsNullOrEmpty(productCode)) return null;

        string key;
        if (architecture.Length == 0)
        {
            key = ArpHklm32 + "\\" + productCode;
            explanation += " The base package architecture is unknown: a 32-bit product registers under WOW6432Node, a 64-bit one under " + ArpHklm64 + ".";
        }
        else
        {
            key = (architecture.StartsWith("Intel", StringComparison.OrdinalIgnoreCase) && !architecture.Equals("Intel64", StringComparison.OrdinalIgnoreCase)
                ? ArpHklm32 : ArpHklm64) + "\\" + productCode;
        }
        if (version is null) explanation += " The patch DisplayName carries no version in parentheses.";
        return new EffectiveDetection(productCode, meta.GetValueOrDefault("TargetProductName", ""), version ?? "", key, explanation)
        {
            BaseVersion = baseVersion,
            PatchFile = Path.GetFileName(path)
        };
    }

    private async Task<EffectiveDetection?> PostPatchFromPayloadAsync(string path, string engine, AnalysisLimits limits, IProgress<string> progress,
        List<MetadataBlock> metadata, List<string> notes, CancellationToken ct)
    {
        IReadOnlyList<PayloadEntry> payload;
        try
        {
            payload = await ListPayloadCoreAsync(path, engine, limits, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null;
        }
        var msp = payload.Where(e => !e.IsDirectory && e.InnerType == "MSP").OrderByDescending(e => e.Size).FirstOrDefault();
        if (msp is null) return null;
        if (msp.Size > limits.MaxExtractBytes)
        {
            notes.Add("The inner patch is larger than the extraction limit; the post-patch target was not read.");
            return null;
        }
        var msi = payload.Where(e => !e.IsDirectory && e.InnerType == "MSI").OrderBy(e => e.Size).FirstOrDefault();
        var session = _sandbox.NewSession();
        try
        {
            progress.Report("Extracting " + Path.GetFileName(msp.Path) + " to read the post-patch target");
            var mspPath = await ExtractEntryCoreAsync(path, engine, msp, session, limits, ct).ConfigureAwait(false);
            string? msiPath = null;
            if (msi is not null && msi.Size <= limits.MaxExtractBytes)
            {
                progress.Report("Extracting " + Path.GetFileName(msi.Path) + " to read the base ProductCode");
                msiPath = await ExtractEntryCoreAsync(path, engine, msi, session, limits, ct).ConfigureAwait(false);
            }
            return ReadMsp(mspPath, msiPath, metadata, notes, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            notes.Add("The inner patch could not be extracted; the post-patch target was not read.");
            return null;
        }
        finally
        {
            try { _sandbox.Release(session); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallerAnalysisException) { }
        }
    }

    private static MetadataBlock NsisBlock(string path, List<string> notes)
    {
        var m = NsisScriptReader.Read(path);
        var facts = new List<AnalysisFact> { Fact("Compression", m.Compression + (m.Solid ? " solid" : ""), "nsis-compiled-script") };
        if (!m.HeaderAvailable)
        {
            notes.Add(string.IsNullOrEmpty(m.Note) ? "The NSIS script header was not decoded." : m.Note);
            facts.Add(Fact("Header", "not decoded", "nsis-compiled-script"));
            return new MetadataBlock("NSIS compiled script", facts);
        }
        facts.Add(Fact("Strings", m.Unicode ? "Unicode" : "ANSI", "nsis-compiled-script"));
        facts.Add(Fact("Name", m.Name, "nsis-compiled-script"));
        facts.Add(Fact("InstallDir", m.InstallDir == m.InstallDirWindows || m.InstallDirWindows.Length == 0 ? m.InstallDir : m.InstallDir + "  ->  " + m.InstallDirWindows, "nsis-compiled-script"));
        if (m.InstallDirCandidates.Count > 1) facts.Add(Fact("StrCpy $INSTDIR", string.Join(" | ", m.InstallDirCandidates), "nsis-compiled-script"));
        facts.Add(Fact("InstallDirRegKey", m.InstallDirRegKey, "nsis-compiled-script"));
        facts.Add(Fact("Uninstaller", m.UninstallerPath == m.UninstallerPathWindows || m.UninstallerPathWindows.Length == 0 ? m.UninstallerPath : m.UninstallerPath + "  ->  " + m.UninstallerPathWindows, "nsis-compiled-script"));
        facts.Add(Fact("ARP hive", m.RegistryHive + (m.RegistryView.Length > 0 ? " (" + m.RegistryView + "-bit view)" : ""), "nsis-compiled-script"));
        facts.Add(Fact("ShellVarContext", m.ShellVarContext, "nsis-compiled-script"));
        facts.Add(Fact("Install context", m.InstallContext, "nsis-compiled-script"));
        if (m.InstallModes.Count > 1)
            facts.Add(Fact("Install modes", string.Join(", ", m.InstallModes.Select(x => x == m.InstallMode ? x + " (default)" : x + " via " + (x == "AllUsers" ? m.AllUsersSwitch : m.CurrentUserSwitch))), "nsis-compiled-script"));
        facts.Add(Fact("Requested execution level", m.RequestedExecutionLevel, "pe-manifest"));
        if (m.ArpValues.Count > 0) facts.Add(Fact("ARP values", string.Join(", ", m.ArpValues.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)), "nsis-compiled-script"));
        facts.Add(Fact("Note", m.Note, "nsis-compiled-script"));
        return new MetadataBlock("NSIS compiled script", facts.Where(f => f.Value.Length > 0).ToList());
    }

    private static MetadataBlock InnoBlock(string path, List<string> notes)
    {
        var m = InnoSetupReader.Read(path);
        var facts = new List<AnalysisFact>();
        if (!m.HeaderAvailable)
        {
            notes.Add(string.IsNullOrEmpty(m.Note) ? "The Inno Setup header was not decoded." : m.Note);
            facts.Add(Fact("Setup data", m.DataVersion, "inno-setup-header"));
            facts.Add(Fact("Header", "not decoded", "inno-setup-header"));
            return new MetadataBlock("Inno Setup [Setup] header", facts.Where(f => f.Value.Length > 0).ToList());
        }
        facts.Add(Fact("Setup data", m.DataVersion + (m.Compression.Length > 0 ? ", " + m.Compression : ""), "inno-setup-header"));
        facts.Add(Fact("AppId", m.AppId, "inno-setup-header"));
        facts.Add(Fact("AppName", m.AppName, "inno-setup-header"));
        facts.Add(Fact("AppVersion", m.AppVersion, "inno-setup-header"));
        if (m.ArpDisplayName != m.AppName) facts.Add(Fact("ARP name", m.ArpDisplayName, "inno-setup-header"));
        facts.Add(Fact("DefaultDirName", m.DefaultDirName == m.InstallDirWindows || m.InstallDirWindows.Length == 0 ? m.DefaultDirName : m.DefaultDirName + "  ->  " + m.InstallDirWindows, "inno-setup-header"));
        facts.Add(Fact("Uninstaller", m.UninstallerPath == m.UninstallerPathWindows || m.UninstallerPathWindows.Length == 0 ? m.UninstallerPath : m.UninstallerPath + "  ->  " + m.UninstallerPathWindows, "inno-setup-header"));
        if (m.PrivilegesRequired.Length > 0)
            facts.Add(Fact("PrivilegesRequired", m.PrivilegesRequired + (m.PrivilegesRequiredOverridesAllowed.Count > 0 ? " (overrides allowed: " + string.Join(", ", m.PrivilegesRequiredOverridesAllowed) + ")" : ""), "inno-setup-header"));
        facts.Add(Fact("ArchitecturesAllowed", m.ArchitecturesAllowed, "inno-setup-header"));
        facts.Add(Fact("64-bit install mode", (m.ArchitecturesInstallIn64BitMode.Length > 0 ? m.ArchitecturesInstallIn64BitMode : "no") + (m.Is64BitInstallMode ? " (x64 host installs 64-bit)" : ""), "inno-setup-header"));
        facts.Add(Fact("MinVersion", m.MinWindowsVersion, "inno-setup-header"));
        facts.Add(Fact("ARP hive", m.RegistryHive + (m.RegistryView.Length > 0 ? " (" + m.RegistryView + "-bit view)" : ""), "inno-setup-header"));
        facts.Add(Fact("Install context", m.InstallContext, "inno-setup-header"));
        if (m.InstallModes.Count > 1)
            facts.Add(Fact("Install modes", string.Join(", ", m.InstallModes.Select(x => x == m.InstallMode ? x + " (default)" : x + " via " + (x == "AllUsers" ? m.AllUsersSwitch : m.CurrentUserSwitch))), "inno-setup-header"));
        if (!Regex.IsMatch(m.CreateUninstallRegKey, "^(?i)(yes)?$")) facts.Add(Fact("CreateUninstallRegKey", m.CreateUninstallRegKey, "inno-setup-header"));
        if (!Regex.IsMatch(m.Uninstallable, "^(?i)(yes)?$")) facts.Add(Fact("Uninstallable", m.Uninstallable, "inno-setup-header"));
        facts.Add(Fact("Requested execution level", m.RequestedExecutionLevel + (m.RequestedExecutionLevel.Length > 0 && m.PrivilegesRequired.Length > 0 ? "; setup elevates itself for PrivilegesRequired=" + m.PrivilegesRequired : ""), "pe-manifest"));
        facts.Add(Fact("Note", m.Note, "inno-setup-header"));
        return new MetadataBlock("Inno Setup [Setup] header", facts.Where(f => f.Value.Length > 0).ToList());
    }

    private static MetadataBlock VelopackBlock(string path, List<string> notes)
    {
        var m = VelopackMetadataReader.Read(path);
        if (!m.HeaderAvailable)
        {
            notes.Add("The Velopack package inside the setup was not read.");
            return new MetadataBlock("Velopack package", [Fact("Package", "not read", "velopack-nuspec")]);
        }
        return new MetadataBlock("Velopack package", new List<AnalysisFact>
        {
            Fact("Package ID", m.PackageId, "velopack-nuspec"),
            Fact("Package version", m.PackageVersion, "velopack-nuspec"),
            Fact("App architecture", m.Architecture, "velopack-nuspec"),
            Fact("Main executable", m.MainExe, "velopack-nuspec"),
            Fact("Channel", m.Channel, "velopack-nuspec"),
            Fact("Default folder", m.InstallDir, "velopack-nuspec"),
            Fact("Install context", "PerUser", "velopack-nuspec")
        }.Where(f => f.Value.Length > 0).ToList());
    }

    /// <summary>
    /// The bundle id from the .wixburn section. A per-machine bundle registers under that id;
    /// the 32-bit Burn engine writes to the 32-bit registry view.
    /// </summary>
    private static MetadataBlock BurnBlock(string path, string architecture, DeploymentOverrides overrides)
    {
        var bundleId = WixBurnBundleReader.ReadBundleId(path);
        if (bundleId is null) return new MetadataBlock("WiX Burn bundle", [Fact("BundleId", "not found", "wixburn-section")]);
        var root = architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ? ArpHklm32 : ArpHklm64;
        overrides.ArpKey = root + "\\" + bundleId;
        overrides.ArpKeySource = "wixburn-section";
        overrides.ProductCode = bundleId;
        overrides.Source = "wixburn-section";
        return new MetadataBlock("WiX Burn bundle", new List<AnalysisFact>
        {
            Fact("BundleId", bundleId, "wixburn-section"),
            Fact("Registration", "Per-machine bundles register under the BundleId; a per-user bundle registers under HKCU.", "wixburn-section", "Predicted")
        });
    }

    // ---- zip-based packages ------------------------------------------------------------

    private static XDocument? ParseXml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ManifestLimitBytes };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader);
    }

    private static string Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() ?? "";

    private static XElement? ChildElement(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static void ReadZipPackage(string path, string engine, AnalysisLimits limits, List<MetadataBlock> metadata, List<string> notes,
        DeploymentOverrides o, CancellationToken ct)
    {
        try
        {
            using var zip = BoundedZip.Open(path, limits.MaxEntries, "read");
            var entries = zip.Entries(limits.MaxEntries);
            ct.ThrowIfCancellationRequested();
            switch (engine)
            {
                case "MSIX":
                case "MsixBundle":
                    ReadMsix(zip, engine, metadata, o);
                    break;
                case "NuGet":
                case "Chocolatey":
                    ReadNuspec(zip, entries, engine, metadata, o);
                    break;
                case "Intunewin":
                    ReadIntunewin(zip, metadata, o);
                    break;
                case "PsadtV3":
                case "PsadtV4":
                    ReadPsadt(zip, entries, engine, metadata, o);
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (InstallerAnalysisException ex)
        {
            notes.Add(ex.Message);
        }
        catch (Exception)
        {
            notes.Add("The package manifest could not be read; the archive may be damaged.");
        }
    }

    private static void ReadMsix(BoundedZip zip, string engine, List<MetadataBlock> metadata, DeploymentOverrides o)
    {
        var bundle = engine == "MsixBundle";
        var doc = ParseXml(zip.ReadText(bundle ? "AppxMetadata/AppxBundleManifest.xml" : "AppxManifest.xml", ManifestLimitBytes));
        var root = doc?.Root ?? throw new InvalidDataException("The package manifest is missing.");
        var identity = ChildElement(root, "Identity");
        var name = (string?)identity?.Attribute("Name") ?? "";
        var publisher = (string?)identity?.Attribute("Publisher") ?? "";
        var version = (string?)identity?.Attribute("Version") ?? "";
        var arch = (string?)identity?.Attribute("ProcessorArchitecture") ?? "";
        var properties = ChildElement(root, "Properties");
        var displayName = Child(properties, "DisplayName");
        var publisherDisplay = Child(properties, "PublisherDisplayName");
        var family = name.Length > 0 && publisher.Length > 0 ? name + "_" + MsixPackageReader.PublisherHash(publisher) : "";
        var facts = new List<AnalysisFact>
        {
            Fact("Identity Name", name, "appx-manifest"),
            Fact("Identity Publisher", publisher, "appx-manifest"),
            Fact("Identity Version", version, "appx-manifest"),
            Fact("ProcessorArchitecture", arch, "appx-manifest"),
            Fact("ResourceId", (string?)identity?.Attribute("ResourceId"), "appx-manifest"),
            Fact("PackageFamilyName", family, "appx-manifest"),
            Fact("DisplayName", displayName, "appx-manifest"),
            Fact("PublisherDisplayName", publisherDisplay, "appx-manifest"),
            Fact("Description", Child(properties, "Description"), "appx-manifest")
        };
        var device = ChildElement(ChildElement(root, "Dependencies"), "TargetDeviceFamily");
        if (device is not null) facts.Add(Fact("TargetDeviceFamily", ((string?)device.Attribute("Name") ?? "") + " " + ((string?)device.Attribute("MinVersion") ?? ""), "appx-manifest"));
        var applications = ChildElement(root, "Applications")?.Elements().Where(e => e.Name.LocalName == "Application")
            .Select(e => (string?)e.Attribute("Executable") ?? "").Where(e => e.Length > 0).Take(10).ToList() ?? [];
        if (applications.Count > 0) facts.Add(Fact("Executables", string.Join(", ", applications), "appx-manifest"));
        if (bundle)
        {
            var packages = ChildElement(root, "Packages")?.Elements().Where(e => e.Name.LocalName == "Package").Take(200)
                .Select(p => string.Join(" ", new[] { (string?)p.Attribute("Type"), (string?)p.Attribute("Architecture"), (string?)p.Attribute("Version"), (string?)p.Attribute("ResourceId"), (string?)p.Attribute("FileName") }.Where(s => !string.IsNullOrEmpty(s))))
                .ToList() ?? [];
            facts.Add(Fact("Bundled packages", packages.Count.ToString(CultureInfo.InvariantCulture), "appx-bundle-manifest"));
            for (var i = 0; i < packages.Count && i < 50; i++) facts.Add(Fact("Package " + (i + 1), packages[i], "appx-bundle-manifest"));
        }
        metadata.Add(new MetadataBlock(bundle ? "MSIX bundle manifest" : "MSIX manifest", facts.Where(f => f.Value.Trim().Length > 0).ToList()));

        o.Source = bundle ? "appx-bundle-manifest" : "appx-manifest";
        o.DisplayName = displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || displayName.Length == 0 ? name : displayName;
        o.DisplayVersion = version;
        o.Publisher = publisherDisplay.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || publisherDisplay.Length == 0 ? publisher : publisherDisplay;
        o.ProductCode = name;
        o.Architecture = arch is "x64" or "x86" or "arm64" or "neutral" ? (arch == "arm64" ? "ARM64" : arch) : arch;
        if (name.Length > 0)
        {
            o.DetectionHint = $"MSIX family-name detection: Get-AppxPackage -Name {name}" + (family.Length > 0 ? $" (family {family})." : ".");
            o.UninstallCommand = $"Get-AppxPackage -Name '{name}' | Remove-AppxPackage";
        }
    }

    private static void ReadNuspec(BoundedZip zip, IReadOnlyList<System.IO.Compression.ZipArchiveEntry> entries, string engine, List<MetadataBlock> metadata, DeploymentOverrides o)
    {
        var nuspec = entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The package has no nuspec.");
        var doc = ParseXml(zip.ReadText(nuspec.FullName, ManifestLimitBytes));
        var meta = ChildElement(doc?.Root, "metadata") ?? throw new InvalidDataException("The nuspec has no metadata.");
        var id = Child(meta, "id");
        var version = Child(meta, "version");
        var description = Child(meta, "description");
        if (description.Length > 300) description = description[..297] + "...";
        metadata.Add(new MetadataBlock("NuGet package (nuspec)", new List<AnalysisFact>
        {
            Fact("Id", id, "nuspec"),
            Fact("Version", version, "nuspec"),
            Fact("Title", Child(meta, "title"), "nuspec"),
            Fact("Authors", Child(meta, "authors"), "nuspec"),
            Fact("Owners", Child(meta, "owners"), "nuspec"),
            Fact("ProjectUrl", Child(meta, "projectUrl"), "nuspec"),
            Fact("Tags", Child(meta, "tags"), "nuspec"),
            Fact("Description", description, "nuspec"),
            Fact("Chocolatey scripts", engine == "Chocolatey" ? string.Join(", ", entries.Where(e => Path.GetFileName(e.FullName).StartsWith("chocolatey", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)).Select(e => e.FullName).Take(10)) : "", "nuspec")
        }.Where(f => f.Value.Length > 0).ToList()));
        o.Source = "nuspec";
        o.DisplayName = Child(meta, "title") is { Length: > 0 } title ? title : id;
        o.DisplayVersion = version;
        o.Publisher = Child(meta, "authors");
        o.ProductCode = id;
        if (engine == "Chocolatey")
        {
            o.InstallCommand = $"choco install {id} --version={version} -y --source=\"<SourceDirOrFeed>\"";
            o.UninstallCommand = $"choco uninstall {id} -y";
            o.DetectionHint = $"Chocolatey package detection: choco list --local-only {id}";
        }
        else
        {
            o.InstallCommand = $"nuget install {id} -Version {version} -Source \"<SourceDirOrFeed>\"";
            o.DetectionHint = "NuGet packages are a package source, not an installer; detection follows the consumer.";
        }
    }

    /// <summary>The Detection.xml facts. The EncryptionInfo values are never read into the report.</summary>
    private static void ReadIntunewin(BoundedZip zip, List<MetadataBlock> metadata, DeploymentOverrides o)
    {
        var doc = ParseXml(zip.ReadText(IntuneWinPackageReader.MetadataEntry, ManifestLimitBytes));
        var info = doc?.Root ?? throw new InvalidDataException("Detection.xml is missing.");
        var facts = new List<AnalysisFact>
        {
            Fact("Name", Child(info, "Name"), "intunewin-detection-xml"),
            Fact("SetupFile", Child(info, "SetupFile"), "intunewin-detection-xml"),
            Fact("FileName", Child(info, "FileName"), "intunewin-detection-xml"),
            Fact("UnencryptedContentSize", Child(info, "UnencryptedContentSize"), "intunewin-detection-xml"),
            Fact("ToolVersion", (string?)info.Attribute("ToolVersion") ?? Child(info, "ToolVersion"), "intunewin-detection-xml"),
            Fact("Encrypted", ChildElement(info, "EncryptionInfo") is null ? "no" : "yes (key material is not shown)", "intunewin-detection-xml")
        };
        var msi = ChildElement(info, "MsiInfo");
        foreach (var field in new[] { "MsiProductCode", "MsiProductVersion", "MsiUpgradeCode", "MsiPackageCode", "MsiExecutionContext", "MsiRequiresLogon", "MsiRequiresReboot", "MsiIsMachineInstall", "MsiIsUserInstall", "MsiIncludesServices", "MsiIncludesODBCDataSource", "MsiContainsSystemRegistryKeys", "MsiContainsSystemFolders", "MsiPublisher" })
            facts.Add(Fact(field, Child(msi, field), "intunewin-detection-xml"));
        metadata.Add(new MetadataBlock("Intune package (Detection.xml)", facts.Where(f => f.Value.Length > 0).ToList()));
        o.Source = "intunewin-detection-xml";
        o.DisplayName = Child(info, "Name");
        o.ProductCode = Child(msi, "MsiProductCode");
        o.DisplayVersion = Child(msi, "MsiProductVersion");
        o.Publisher = Child(msi, "MsiPublisher");
        o.DetectionHint = "Intune detection is defined in the Intune portal; Detection.xml names the setup file" + (o.ProductCode.Length > 0 ? " and the MSI ProductCode " + o.ProductCode : "") + ".";
    }

    private static void ReadPsadt(BoundedZip zip, IReadOnlyList<System.IO.Compression.ZipArchiveEntry> entries, string engine, List<MetadataBlock> metadata, DeploymentOverrides o)
    {
        var scriptName = engine == "PsadtV4" ? "Invoke-AppDeployToolkit.ps1" : "Deploy-Application.ps1";
        var script = entries.Where(e => string.Equals(Path.GetFileName(e.FullName), scriptName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault();
        var facts = new List<AnalysisFact> { Fact("Deployment script", script?.FullName, "psadt") };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (script is not null)
        {
            var text = zip.ReadText(script.FullName, ManifestLimitBytes) ?? "";
            foreach (var field in new[] { "AppVendor", "AppName", "AppVersion", "AppArch", "AppLang", "AppRevision", "AppScriptVersion", "AppScriptDate", "AppScriptAuthor" })
            {
                var pattern = engine == "PsadtV4"
                    ? @"(?im)^\s*" + field + @"\s*=\s*'([^'\r\n]*)'"
                    : @"(?im)^\s*\[(?:string|version)\]\$" + field + @"\s*=\s*\[?(?:version\])?\s*'([^'\r\n]*)'";
                var m = Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                if (m.Success) values[field] = m.Groups[1].Value;
            }
        }
        foreach (var (key, value) in values) facts.Add(Fact(key, value, "psadt"));
        var toolkit = engine == "PsadtV4"
            ? Regex.Match(ReadFirst(zip, entries, "PSAppDeployToolkit.psd1"), @"ModuleVersion\s*=\s*'([^']+)'", RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value
            : Regex.Match(ReadFirst(zip, entries, "AppDeployToolkitMain.ps1"), @"\$appDeployMainScriptVersion\s*=\s*\[version\]\s*'([^']+)'", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Groups[1].Value;
        facts.Add(Fact("Toolkit version", toolkit, "psadt"));
        metadata.Add(new MetadataBlock("PSAppDeployToolkit", facts.Where(f => f.Value.Length > 0).ToList()));
        o.Source = "psadt";
        o.DisplayName = values.GetValueOrDefault("AppName", "");
        o.DisplayVersion = values.GetValueOrDefault("AppVersion", "");
        o.Publisher = values.GetValueOrDefault("AppVendor", "");
        if (o.DisplayName.Length > 0)
        {
            o.ArpKey = ArpHklm64 + "\\" + o.DisplayName;
            o.ArpKeySource = "engine-convention";
        }
    }

    private static string ReadFirst(BoundedZip zip, IReadOnlyList<System.IO.Compression.ZipArchiveEntry> entries, string leaf)
    {
        var entry = entries.Where(e => string.Equals(Path.GetFileName(e.FullName), leaf, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Length).FirstOrDefault();
        return entry is null ? "" : zip.ReadText(entry.FullName, ManifestLimitBytes) ?? "";
    }
}
