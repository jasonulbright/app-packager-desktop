using System.Security;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>Where the rules of a publish came from.</summary>
public enum WsusRuleSource
{
    /// <summary>The manifest's WsusDetection block.</summary>
    WsusDetection,
    /// <summary>The manifest detection.</summary>
    Detection,
    /// <summary>The Add/Remove Programs entry that the staged MSI registers.</summary>
    MsiEntry
}

/// <summary>
/// The applicability rule fragments of one update. The SDP API supplies the
/// bar: and lar: namespaces when it writes the package.
/// </summary>
public sealed record WsusApplicabilityRules(string IsInstalled, string IsInstallable, WsusRuleSource Source, ArpEntryDetection? Entry);

/// <summary>A detection WSUS cannot express; the message names the change that makes it publishable.</summary>
public sealed class WsusRuleException(string message) : Exception(message);

/// <summary>
/// Maps a stage manifest onto WSUS applicability rules for an update of an
/// installed product. IsInstalled is the detection widened from "this
/// version" to "this version or newer". IsInstallable holds only where the
/// detection finds the product with an older version: a computer without the
/// product never gets the update, and there is no fallback that installs
/// where the product is missing.
/// </summary>
public sealed partial class WsusRuleBuilder
{
    private readonly IMsiInspector? _msi;
    private readonly Func<string, string?> _environment;

    /// <param name="msi">Reads the staged MSI for the MsiEntry fallback; null turns the fallback off.</param>
    /// <param name="environment">Environment variable lookup for file paths; null reads this process's environment.</param>
    public WsusRuleBuilder(IMsiInspector? msi = null, Func<string, string?>? environment = null)
    {
        _msi = msi;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    // An equality detection becomes "this version or newer" so a client that
    // already runs a newer copy reports the update installed instead of being
    // offered a downgrade.
    private static readonly Dictionary<string, string> VersionComparison = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IsEquals"] = "GreaterThanOrEqualTo",
        ["GreaterEquals"] = "GreaterThanOrEqualTo",
        ["GreaterThan"] = "GreaterThan"
    };

    // The complement of each installed comparison: together they cover every
    // version a present copy can carry, and neither holds for a missing copy.
    private static readonly Dictionary<string, string> OlderComparison = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GreaterThanOrEqualTo"] = "LessThan",
        ["GreaterThan"] = "LessThanOrEqualTo"
    };

    private static readonly HashSet<string> TextComparison = new(StringComparer.OrdinalIgnoreCase)
    {
        "IsEquals", "BeginsWith", "EndsWith", "Contains"
    };

    private const string ExistenceOnly = "only checks that a file or registry key exists, which says nothing about the installed version. Add a file version or a registry version comparison to the detection, or a WsusDetection block that names the product's Add/Remove Programs entry. A new installation belongs to a Configuration Manager or Intune deployment.";

    [GeneratedRegex(@"\\\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductCodeSubkey();

    [GeneratedRegex(@"%(LOCALAPPDATA|APPDATA|USERPROFILE|HOMEPATH|HOMEDRIVE|USERNAME|TEMP|TMP|ONEDRIVE)%", RegexOptions.IgnoreCase)]
    private static partial Regex PerUserVariable();

    [GeneratedRegex(@"%[^%]+%")]
    private static partial Regex AnyVariable();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex AbsoluteLocalPath();

    [GeneratedRegex(@"^(x64|Arm64|Intel64|AMD64)$", RegexOptions.IgnoreCase)]
    private static partial Regex SixtyFourBitPlatform();

    /// <summary>
    /// A WsusDetection block (type ArpEntry) replaces the manifest detection for
    /// WSUS only. When the manifest detection cannot compare the installed
    /// version and <paramref name="contentFolder"/> holds the staged MSI, the
    /// rules read the Add/Remove Programs entry that MSI registers. Throws
    /// <see cref="WsusRuleException"/> for a detection WSUS cannot map.
    /// </summary>
    public WsusApplicabilityRules Build(StageManifest manifest, string? contentFolder = null)
    {
        if (manifest.WsusDetection is { } wsusDetection)
        {
            var (installed, installable) = ArpEntryRules(wsusDetection);
            return new(installed, installable, WsusRuleSource.WsusDetection, wsusDetection);
        }
        try
        {
            var (installed, installable) = DetectionRules(manifest.Detection);
            return new(installed, installable, WsusRuleSource.Detection, null);
        }
        catch (WsusRuleException)
        {
            var derived = string.IsNullOrEmpty(contentFolder) ? null : MsiArpDetection(manifest, contentFolder);
            if (derived is null) throw;
            var (installed, installable) = ArpEntryRules(derived);
            return new(installed, installable, WsusRuleSource.MsiEntry, derived);
        }
    }

    /// <summary>The manifest detection as rules; throws for a detection that cannot compare the installed version.</summary>
    public (string IsInstalled, string IsInstallable) DetectionRules(StageDetection? detection)
    {
        if (detection is null)
            throw new WsusRuleException("The manifest carries no detection, so WSUS could neither find an older version nor report the update installed.");

        var (connector, clauseDetections) = DetectionClauses(detection);
        var clauses = clauseDetections.Select(ClauseRules).ToList();
        var versionClauses = clauses.Where(c => c.IsVersion).ToList();
        var presenceClauses = clauses.Where(c => !c.IsVersion).ToList();

        var installed = Join(connector, clauses.Select(c => c.Installed).ToList());
        string installable;
        if (connector == "Or")
        {
            if (presenceClauses.Count > 0)
                throw new WsusRuleException($"The detection joins its clauses with OR, and at least one clause {ExistenceOnly}");
            installable = Join("Or", versionClauses.Select(c => c.Older!).ToList());
        }
        else
        {
            if (versionClauses.Count == 0) throw new WsusRuleException($"The detection {ExistenceOnly}");
            var parts = presenceClauses.Select(c => c.Installed).ToList();
            if (versionClauses.Count == 1)
            {
                parts.Add(versionClauses[0].Older!);
            }
            else
            {
                // Every version clause must find its value, and at least one must
                // find an older version. For any target above version 0, Older or
                // Installed holds only where the value exists.
                foreach (var clause in versionClauses) parts.Add(Join("Or", [clause.Older!, clause.Installed]));
                parts.Add(Join("Or", versionClauses.Select(c => c.Older!).ToList()));
            }
            installable = Join("And", parts);
        }
        return (installed, installable);
    }

    private static (string Connector, IReadOnlyList<StageDetection> Clauses) DetectionClauses(StageDetection detection)
    {
        if (!string.Equals(detection.Type, "Compound", StringComparison.OrdinalIgnoreCase)) return ("And", [detection]);
        if (detection.GroupSizes is { Length: > 0 })
            throw new WsusRuleException("Grouped compound detections have no single AND or OR connector the WSUS publisher can map.");
        var connector = string.Equals(detection.Connector, "Or", StringComparison.OrdinalIgnoreCase) ? "Or" : "And";
        var clauses = (detection.Clauses ?? []).Where(c => c is not null).ToList();
        if (clauses.Count == 0) throw new WsusRuleException("The compound detection carries no clauses.");
        return (connector, clauses);
    }

    private sealed record ClauseRule(bool IsVersion, string Installed, string? Older);

    /// <summary>
    /// One detection clause. A version clause: Installed is "this version or
    /// newer" and Older is "the file or value exists and carries an older
    /// version"; a missing file or value fails both. A presence clause:
    /// Installed is the existence check and Older is empty, because existence
    /// says nothing about the version.
    /// </summary>
    private ClauseRule ClauseRules(StageDetection clause)
    {
        var type = string.IsNullOrWhiteSpace(clause.Type) ? "RegistryKeyValue" : clause.Type;
        switch (type.ToLowerInvariant())
        {
            case "registrykeyvalue":
            {
                var location = RegistryLocation(clause);
                AssertStableRegistryLocation(location);
                var valueName = string.IsNullOrWhiteSpace(clause.ValueName) ? "DisplayVersion" : clause.ValueName;
                var expected = clause.ExpectedValue ?? "";
                var op = string.IsNullOrWhiteSpace(clause.Operator) ? "IsEquals" : clause.Operator;
                var propertyType = clause.PropertyType ?? "";
                if (propertyType.Length > 0
                    && !propertyType.Equals("String", StringComparison.OrdinalIgnoreCase)
                    && !propertyType.Equals("Version", StringComparison.OrdinalIgnoreCase))
                    throw new WsusRuleException($"Registry value '{valueName}' is compared as {propertyType}; the WSUS publisher maps version comparisons only.");
                if (!VersionComparison.TryGetValue(op, out var comparison))
                {
                    if (TextComparison.Contains(op))
                        throw new WsusRuleException($"The detection compares registry value '{valueName}' as text ({op} '{expected}'). WSUS can only tell an older installed version from a newer one with a version comparison. Change the detection to compare the value as a version.");
                    throw new WsusRuleException($"Detection operator '{op}' on registry value '{valueName}' has no WSUS applicability mapping.");
                }
                var version = WsusIdentity.VersionString(expected)
                    ?? throw new WsusRuleException($"The detection compares registry value '{valueName}' with '{expected}', which is not a version of up to four numbers. WSUS cannot compare it with the installed version. Change the detection to a numeric version.");
                var value = Xml(valueName);
                // RegSzToVersion compares a missing value as version 0, so an older
                // check without the existence guard holds on a computer that has
                // no copy of the product.
                var exists = $"<bar:RegValueExists {location.Attributes} Value=\"{value}\" Type=\"REG_SZ\" />";
                return new ClauseRule(true,
                    $"<bar:RegSzToVersion {location.Attributes} Value=\"{value}\" Comparison=\"{comparison}\" Data=\"{version}\" />",
                    Join("And", [exists, $"<bar:RegSzToVersion {location.Attributes} Value=\"{value}\" Comparison=\"{OlderComparison[comparison]}\" Data=\"{version}\" />"]));
            }
            case "registrykey":
            {
                var location = RegistryLocation(clause);
                AssertStableRegistryLocation(location);
                return new ClauseRule(false, $"<bar:RegKeyExists {location.Attributes} />", null);
            }
            case "file":
            {
                var location = FileLocation(clause);
                var propertyType = clause.PropertyType ?? "";
                if (propertyType.Equals("Version", StringComparison.OrdinalIgnoreCase))
                {
                    var op = string.IsNullOrWhiteSpace(clause.Operator) ? "GreaterEquals" : clause.Operator;
                    if (!VersionComparison.TryGetValue(op, out var comparison))
                        throw new WsusRuleException($"Detection operator '{op}' on a file version has no WSUS applicability mapping.");
                    var version = WsusIdentity.VersionString(clause.ExpectedValue)
                        ?? throw new WsusRuleException("The file detection compares a version that is not numeric.");
                    var exists = $"<bar:FileExists {location} />";
                    return new ClauseRule(true,
                        $"<bar:FileVersion {location} Comparison=\"{comparison}\" Version=\"{version}\" />",
                        Join("And", [exists, $"<bar:FileVersion {location} Comparison=\"{OlderComparison[comparison]}\" Version=\"{version}\" />"]));
                }
                if (propertyType.Length == 0 || propertyType.Equals("Existence", StringComparison.OrdinalIgnoreCase))
                    return new ClauseRule(false, $"<bar:FileExists {location} />", null);
                throw new WsusRuleException($"The file detection compares {propertyType}; the WSUS publisher maps file versions and existence only.");
            }
            case "script":
                throw new WsusRuleException("Script detection has no WSUS equivalent: applicability rules cannot run a script.");
            default:
                throw new WsusRuleException($"Detection type '{type}' has no WSUS applicability mapping.");
        }
    }

    private sealed record RegistryKeyLocation(string Attributes, string Subkey, bool VersionSpecific);

    private static RegistryKeyLocation RegistryLocation(StageDetection clause)
    {
        var hive = clause.Hive ?? "";
        if (hive.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase))
            throw new WsusRuleException("The detection reads HKEY_CURRENT_USER. WSUS evaluates applicability as SYSTEM, which has no signed-in user hive.");
        var subkey = (clause.RegistryKeyRelative ?? "").Trim().Trim('\\');
        if (string.IsNullOrWhiteSpace(subkey)) throw new WsusRuleException("The detection names no registry key.");
        // Same view as the ConfigMgr clause: a clause not marked 64-bit reads
        // the 32-bit view on a 64-bit client.
        var attributes = $"Key=\"HKEY_LOCAL_MACHINE\" Subkey=\"{Xml(subkey)}\"";
        if (!clause.Is64Bit) attributes += " RegType32=\"true\"";
        // A Windows Installer product key changes with every version, so its
        // presence proves this exact version, never an older one.
        return new RegistryKeyLocation(attributes, subkey, ProductCodeSubkey().IsMatch(subkey));
    }

    private static void AssertStableRegistryLocation(RegistryKeyLocation location)
    {
        if (location.VersionSpecific)
            throw new WsusRuleException($"The detection reads the Windows Installer product key '{location.Subkey}'. An MSI registers a new product key for each version, so WSUS cannot find an older installed version under that key. Change the detection to a file version, or to a version value under a registry key that every version uses, or add a WsusDetection block that names the product's Add/Remove Programs entry.");
    }

    /// <summary>
    /// The Path and Csidl attributes of a file clause. A clause not marked
    /// 64-bit expands %ProgramFiles% and %CommonProgramFiles% the way a 32-bit
    /// process does, the same as the ConfigMgr and Intune clauses. Known
    /// folders resolve on the client through their CSIDL, so a client whose
    /// Windows lives on another drive still matches.
    /// </summary>
    private string FileLocation(StageDetection clause)
    {
        var folder = clause.FilePath ?? "";
        var name = clause.FileName ?? "";
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(name)) throw new WsusRuleException("The file detection names no folder or file.");
        if (PerUserVariable().IsMatch(folder))
            throw new WsusRuleException($"The file detection reads the per-user path '{folder}'. WSUS evaluates applicability as SYSTEM, which resolves no signed-in user profile.");

        var programFiles = Env("ProgramW6432") ?? Env("ProgramFiles");
        var commonFiles = Env("CommonProgramW6432") ?? Env("CommonProgramFiles");
        var expandProgramFiles = programFiles;
        var expandCommonFiles = commonFiles;
        if (!clause.Is64Bit && Env("ProgramFiles(x86)") is { } x86) expandProgramFiles = x86;
        if (!clause.Is64Bit && Env("CommonProgramFiles(x86)") is { } commonX86) expandCommonFiles = commonX86;
        folder = Regex.Replace(folder, "%ProgramFiles%", (expandProgramFiles ?? "").Replace("$", "$$"), RegexOptions.IgnoreCase);
        folder = Regex.Replace(folder, "%CommonProgramFiles%", (expandCommonFiles ?? "").Replace("$", "$$"), RegexOptions.IgnoreCase);
        var expanded = Expand(folder).TrimEnd('\\');
        if (AnyVariable().IsMatch(expanded)) throw new WsusRuleException($"The file detection path '{folder}' carries an unresolved variable.");
        if (!AbsoluteLocalPath().IsMatch(expanded)) throw new WsusRuleException($"The file detection path '{folder}' is not an absolute local path.");

        var systemRoot = Env("SystemRoot") ?? "";
        var knownFolders = new (string? Path, int Csidl)[]
        {
            (Env("ProgramFiles(x86)"), 42),
            (programFiles, 38),
            (Env("ProgramData"), 35),
            (Path.Combine(systemRoot, "System32"), 37),
            (Env("SystemRoot"), 36)
        };
        foreach (var (knownPath, csidl) in knownFolders)
        {
            if (string.IsNullOrWhiteSpace(knownPath)) continue;
            var root = knownPath.TrimEnd('\\');
            if (expanded.Equals(root, StringComparison.OrdinalIgnoreCase) || expanded.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var relative = expanded[root.Length..].TrimStart('\\');
                var path = relative.Length > 0 ? Path.Combine(relative, name) : name;
                return $"Path=\"{Xml(path)}\" Csidl=\"{csidl}\"";
            }
        }
        return $"Path=\"{Xml(Path.Combine(expanded, name))}\"";
    }

    private string? Env(string name) => _environment(name) is { Length: > 0 } value ? value : null;

    /// <summary>%NAME% replaced by its value; an undefined variable stays as written.</summary>
    private string Expand(string text) =>
        AnyVariable().Replace(text, m => _environment(m.Value[1..^1]) is { } value ? value : m.Value);

    /// <summary>
    /// The rules of an Add/Remove Programs entry: per view a registry loop over
    /// the Uninstall subkeys finds the entry by its DisplayName, an optional
    /// Publisher and, with WindowsInstaller, the DWORD Windows Installer writes,
    /// then compares DisplayVersion. The Windows Update Agent reads the looped
    /// subkey through Key="HKEY_LOOP_TARGET" Subkey="\" ("." never matches), and
    /// each child rule repeats RegType32 or it reads the 64-bit view.
    /// </summary>
    public static (string IsInstalled, string IsInstallable) ArpEntryRules(ArpEntryDetection detection)
    {
        if (!string.Equals(detection.Type ?? "", ArpEntryDetection.TypeName, StringComparison.OrdinalIgnoreCase))
            throw new WsusRuleException($"WsusDetection type '{detection.Type}' is not supported; the WSUS publisher reads type ArpEntry.");
        var exactName = detection.DisplayName ?? "";
        var prefix = detection.DisplayNamePrefix ?? "";
        if (prefix.Length == 0 && exactName.Length == 0)
            throw new WsusRuleException("WsusDetection names no DisplayName or DisplayNamePrefix, so it cannot find the Add/Remove Programs entry.");
        var suffix = detection.DisplayNameSuffix ?? "";
        var publisher = detection.Publisher ?? "";
        var version = WsusIdentity.VersionString(detection.Version)
            ?? throw new WsusRuleException($"WsusDetection compares DisplayVersion with '{detection.Version}', which is not a version of up to four numbers.");
        var views = ArpViews(detection.View)
            ?? throw new WsusRuleException($"WsusDetection view '{detection.View}' is not 32, 64 or Both.");

        var installed = new List<string>();
        var older = new List<string>();
        foreach (var is32 in views)
        {
            var flag = is32 ? " RegType32=\"true\"" : "";
            var target = "Key=\"HKEY_LOOP_TARGET\" Subkey=\"\\\"" + flag;
            var match = new List<string>();
            if (exactName.Length > 0)
            {
                match.Add($"<bar:RegSz {target} Value=\"DisplayName\" Comparison=\"EqualTo\" Data=\"{Xml(exactName)}\" />");
            }
            else
            {
                match.Add($"<bar:RegSz {target} Value=\"DisplayName\" Comparison=\"BeginsWith\" Data=\"{Xml(prefix)}\" />");
                if (suffix.Length > 0) match.Add($"<bar:RegSz {target} Value=\"DisplayName\" Comparison=\"EndsWith\" Data=\"{Xml(suffix)}\" />");
            }
            if (publisher.Length > 0) match.Add($"<bar:RegSz {target} Value=\"Publisher\" Comparison=\"EqualTo\" Data=\"{Xml(publisher)}\" />");
            if (detection.WindowsInstaller) match.Add($"<bar:RegDword {target} Value=\"WindowsInstaller\" Comparison=\"EqualTo\" Data=\"1\" />");
            var loopOpen = "<bar:RegKeyLoop Key=\"HKEY_LOCAL_MACHINE\" Subkey=\"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\"" + flag + " TrueIf=\"Any\">";
            installed.Add(loopOpen + Join("And", [.. match,
                $"<bar:RegSzToVersion {target} Value=\"DisplayVersion\" Comparison=\"GreaterThanOrEqualTo\" Data=\"{version}\" />"]) + "</bar:RegKeyLoop>");
            // A missing DisplayVersion compares as version 0, so the older check
            // needs the existence guard.
            older.Add(loopOpen + Join("And", [.. match,
                $"<bar:RegValueExists {target} Value=\"DisplayVersion\" Type=\"REG_SZ\" />",
                $"<bar:RegSzToVersion {target} Value=\"DisplayVersion\" Comparison=\"LessThan\" Data=\"{version}\" />"]) + "</bar:RegKeyLoop>");
        }
        return (Join("Or", installed), Join("Or", older));
    }

    /// <summary>True per view for the 32-bit view; null for a view that is not 32, 64 or Both.</summary>
    public static bool[]? ArpViews(string? view) => (view ?? "").ToLowerInvariant() switch
    {
        "32" => [true],
        "64" => [false],
        "both" => [true, false],
        _ => null
    };

    /// <summary>
    /// The Add/Remove Programs entry the staged MSI registers: Windows Installer
    /// writes DisplayName = ProductName, Publisher = Manufacturer,
    /// DisplayVersion = ProductVersion and WindowsInstaller = 1, in the registry
    /// view of the package platform. A version inside ProductName becomes the
    /// gap between DisplayNamePrefix and DisplayNameSuffix, so every release
    /// matches; the WindowsInstaller flag keeps an EXE-installed copy with a
    /// similar name from matching. Null when the manifest does not install an
    /// MSI, the MSI cannot be read, or it lacks one of the three properties.
    /// </summary>
    public ArpEntryDetection? MsiArpDetection(StageManifest manifest, string contentFolder)
    {
        if (_msi is null || !string.Equals(manifest.InstallerType, "MSI", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(manifest.InstallerFile)) return null;
        string path;
        try { path = WsusPayload.ResolvePath(contentFolder, manifest.InstallerFile); }
        catch (WsusPayloadException) { return null; }
        if (!File.Exists(path)) return null;

        string name, publisher, productVersion, platform;
        try
        {
            var properties = _msi.ReadProperties(path);
            name = (properties.ProductName ?? "").Trim();
            publisher = (properties.Manufacturer ?? "").Trim();
            productVersion = (properties.ProductVersion ?? "").Trim();
            platform = _msi.ReadTemplateArchitecture(path) ?? "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        if (name.Length == 0 || publisher.Length == 0 || WsusIdentity.VersionString(productVersion) is null) return null;

        // The name may carry the version in a shorter form ("24.08" for
        // ProductVersion 24.08.00.0); the longest form found wins.
        var parts = productVersion.Split('.');
        var forms = new List<string>();
        for (var n = parts.Length; n >= 2; n--) forms.Add(string.Join(".", parts[..n]));
        var softwareVersion = (manifest.SoftwareVersion ?? "").Trim();
        if (softwareVersion.Length > 0) forms.Add(softwareVersion);
        Match? token = null;
        foreach (var form in forms.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length))
        {
            var m = Regex.Match(name, @"(?<![\w.])v?" + Regex.Escape(form) + @"(?![\w.])", RegexOptions.IgnoreCase);
            if (m.Success) { token = m; break; }
        }

        var entry = new ArpEntryDetection
        {
            View = SixtyFourBitPlatform().IsMatch(platform.Split(';')[0]) ? "64" : "32",
            Publisher = publisher,
            WindowsInstaller = true,
            Version = productVersion
        };
        if (token is null)
        {
            entry.DisplayName = name;
        }
        else
        {
            var prefix = name[..token.Index];
            if (prefix.Trim().Length == 0) return null;
            entry.DisplayNamePrefix = prefix;
            var suffix = name[(token.Index + token.Length)..];
            if (suffix.Length > 0) entry.DisplayNameSuffix = suffix;
        }
        return entry;
    }

    private static string Join(string connector, IReadOnlyList<string> rules) =>
        rules.Count == 1 ? rules[0] : $"<lar:{connector}>{string.Concat(rules)}</lar:{connector}>";

    private static string Xml(string? value) => value is null ? "" : SecurityElement.Escape(value) ?? "";
}
