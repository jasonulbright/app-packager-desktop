using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.InstallerScripts;

namespace ApplicationPackagerEngine.Services;

/// <summary>The uninstall key and registry view a staged installer's own header names.</summary>
public sealed record InstallerArpKey(string Engine, string Key, string RegistryView);

/// <summary>
/// Compares the literal Add/Remove Programs key a detection names against the key the
/// staged installer's own header names. The literal was confirmed on a client; a vendor
/// that renames the key or moves it between registry views in a later build would
/// otherwise stage a rule that never evaluates true and an uninstall that finds nothing.
/// </summary>
public static partial class ArpDetectionKeyAssertion
{
    public const string NoHeaderKeyWarning = "Installer header names no ARP key; detection keeps the confirmed literal.";

    private const string UninstallPrefix = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    [GeneratedRegex(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}")]
    private static partial Regex BracedGuid();

    [GeneratedRegex(@"^HK(LM|CU):\\", RegexOptions.IgnoreCase)]
    private static partial Regex HivePrefix();

    [GeneratedRegex(@"^SOFTWARE\\WOW6432Node\\", RegexOptions.IgnoreCase)]
    private static partial Regex Wow6432Node();

    /// <summary>
    /// Throws when the decoded key or view differs from the expected one; warns and
    /// returns when the header names no key. The key comparison ignores case.
    /// </summary>
    public static void Assert(string? decodedKey, string? decodedView, string expectedKey, bool is64BitView, Action<string>? onWarning = null)
    {
        if (string.IsNullOrWhiteSpace(decodedKey))
        {
            onWarning?.Invoke(NoHeaderKeyWarning);
            return;
        }

        var relative = HivePrefix().Replace(decodedKey, "");
        relative = Wow6432Node().Replace(relative, @"SOFTWARE\");
        if (!string.Equals(relative, expectedKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Installer registers '{relative}' but detection expects '{expectedKey}'.");

        if (!string.IsNullOrEmpty(decodedView))
        {
            var decoded64 = decodedView != "32";
            if (decoded64 != is64BitView)
                throw new InvalidOperationException($"Installer writes its ARP key in the {decodedView}-bit view but detection expects the other view.");
        }
    }

    /// <summary>
    /// True for a definition key template that names one uninstall key outright: under
    /// the Uninstall key, and no token other than a braced GUID.
    /// </summary>
    public static bool IsLiteralUninstallKey(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return false;
        if (!template.StartsWith(UninstallPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return BracedGuid().Replace(template, "").IndexOfAny(['{', '}', '$', '%', '*', '?']) < 0;
    }

    /// <summary>
    /// The key the staged installer's header names. NSIS and Inno Setup take the branch
    /// the install arguments select (/allusers or /currentuser) and the default branch
    /// otherwise; a WiX Burn bundle names its bundle id. Null for any other engine;
    /// an empty key when the header could not be read or computes the key at run time.
    /// </summary>
    public static InstallerArpKey? ReadInstallerKey(string installerPath, string? installArgs)
    {
        var engine = InstallerSignatureScanner.Identify(installerPath);
        switch (engine)
        {
            case "NSIS":
            case "InnoSetup":
            {
                var facts = CompiledScriptFacts.TryRead(engine, installerPath);
                if (facts is null) return new InstallerArpKey(engine, "", "");
                var tokens = WrapperGeneratorService.Tokenize(installArgs ?? "");
                InstallBranch? branch = null;
                if (tokens.Contains("/allusers", StringComparer.OrdinalIgnoreCase)) facts.Branches.TryGetValue("AllUsers", out branch);
                else if (tokens.Contains("/currentuser", StringComparer.OrdinalIgnoreCase)) facts.Branches.TryGetValue("CurrentUser", out branch);
                branch ??= facts.DefaultBranch;
                return new InstallerArpKey(engine, branch?.UninstallRegistryKey ?? "", branch?.RegistryView ?? "");
            }
            case "WixBurn":
            {
                var bundleId = WixBurnBundleReader.ReadBundleId(installerPath);
                return new InstallerArpKey(engine, bundleId is null ? "" : $@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{bundleId}", "");
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Checks a staged detection against its installer. Only registry clauses whose
    /// definition template is a literal uninstall key take part. Several such clauses
    /// pass when any one of them matches; otherwise the first mismatch is thrown.
    /// </summary>
    public static void CheckStage(StageDetection detection, DetectionConfig? definitionDetection,
        string installerPath, string? installArgs, Action<string>? onProgress = null)
    {
        var literals = LiteralClauses(detection, definitionDetection).ToList();
        if (literals.Count == 0) return;

        var header = ReadInstallerKey(installerPath, installArgs);
        if (header is null) return;
        if (string.IsNullOrWhiteSpace(header.Key))
        {
            onProgress?.Invoke(NoHeaderKeyWarning);
            return;
        }

        InvalidOperationException? first = null;
        foreach (var clause in literals)
        {
            try
            {
                Assert(header.Key, header.RegistryView, clause.RegistryKeyRelative!, clause.Is64Bit);
                onProgress?.Invoke($"ARP key confirmed            : {clause.RegistryKeyRelative} ({header.Engine} header)");
                return;
            }
            catch (InvalidOperationException ex)
            {
                first ??= ex;
            }
        }
        throw first!;
    }

    private static IEnumerable<StageDetection> LiteralClauses(StageDetection detection, DetectionConfig? definition)
    {
        static bool IsRegistry(StageDetection d) => d.Type is "RegistryKeyValue" or "RegistryKey";

        if (string.Equals(detection.Type, "Compound", StringComparison.OrdinalIgnoreCase))
        {
            var clauses = detection.Clauses ?? [];
            var templates = definition?.Clauses ?? [];
            if (clauses.Length != templates.Length) yield break;
            for (var i = 0; i < clauses.Length; i++)
                if (IsRegistry(clauses[i]) && IsLiteralUninstallKey(templates[i].RegistryKeyTemplate)
                    && !string.IsNullOrWhiteSpace(clauses[i].RegistryKeyRelative))
                    yield return clauses[i];
            yield break;
        }

        if (IsRegistry(detection) && IsLiteralUninstallKey(definition?.RegistryKeyTemplate)
            && !string.IsNullOrWhiteSpace(detection.RegistryKeyRelative))
            yield return detection;
    }
}
