using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>What the import does with one key of the PowerShell preferences file.</summary>
public enum PowerShellImportOutcome
{
    /// <summary>The key has an APD setting and a valid value that differs from the APD value. The import writes it.</summary>
    Import,

    /// <summary>The key has an APD setting and the same value. Nothing changes.</summary>
    Same,

    /// <summary>The key has an APD setting or an APD equivalent, and the import leaves it: a credential, machine state, a per-application value or a value APD refuses.</summary>
    Skipped,

    /// <summary>The key has no APD setting, or the version of the PowerShell app that wrote it is not known to APD.</summary>
    Unknown
}

/// <summary>One line of the preview: a key of the PowerShell file, what it holds, what APD holds, and what the import does.</summary>
public sealed record PowerShellImportRow(string Key, PowerShellImportOutcome Outcome, string PowerShellValue, string ApdValue, string Note)
{
    /// <summary>The preview writes only the rows that differ.</summary>
    public bool Differs => Outcome == PowerShellImportOutcome.Import;
}

/// <summary>
/// The preview of an import from the PowerShell app preferences file (AppPackager.preferences.json): every key with its outcome.
/// Nothing is written until <see cref="PowerShellOptionsImport.Apply"/> runs. A bad file gives an <see cref="Error"/> and no rows.
/// </summary>
public sealed class PowerShellImportPreview
{
    internal PowerShellImportPreview(string? sourcePath, string? error, IReadOnlyList<PowerShellImportRow> rows, IReadOnlyDictionary<string, Action<AppSettings>> writers)
    {
        SourcePath = sourcePath;
        Error = error;
        Rows = rows;
        Writers = writers;
    }

    public string? SourcePath { get; }

    /// <summary>The reason the file could not be read, or null.</summary>
    public string? Error { get; }

    public IReadOnlyList<PowerShellImportRow> Rows { get; }

    internal IReadOnlyDictionary<string, Action<AppSettings>> Writers { get; }

    /// <summary>True when the import can write the row of this key.</summary>
    public bool CanWrite(string key) => Writers.ContainsKey(key);

    /// <summary>Rows with an APD setting and a valid value, whether or not they differ.</summary>
    public int Imported => Rows.Count(r => r.Outcome is PowerShellImportOutcome.Import or PowerShellImportOutcome.Same);

    /// <summary>Imported rows whose value differs from the APD value; the import writes these.</summary>
    public int Differs => Rows.Count(r => r.Outcome == PowerShellImportOutcome.Import);

    public int Skipped => Rows.Count(r => r.Outcome == PowerShellImportOutcome.Skipped);

    public int Unknown => Rows.Count(r => r.Outcome == PowerShellImportOutcome.Unknown);

    public string Summary => Error is not null
        ? Error
        : $"{Imported} can be imported ({Differs} differ from the current value), {Skipped} skipped, {Unknown} unknown.";
}

/// <summary>
/// Reads the preferences file of the PowerShell app App Packager and maps each key that has an APD setting.
/// The file is only read: it is never changed, and a credential (a protected token, secret or key) is never read into a row or written.
/// </summary>
public static class PowerShellOptionsImport
{
    public const string FileName = "AppPackager.preferences.json";

    private const string CredentialNote = "A credential. It is never imported; enter it under Settings.";
    private const string ProtectedSecretNote = "Protected with DPAPI for the Windows user of the PowerShell app. APD cannot read it and never imports it; enter the secret under Settings.";
    private const string DetectedToolNote = "A tool found on the other computer. APD detects its own tools.";
    private const string IntuneWinToolNote = "The path of IntuneWinAppUtil.exe on the other computer. APD downloads its own checked copy of the tool.";
    private const string PerApplicationNote = "Saved for each application, and the application ids of the two apps differ. Set it in APD.";

    /// <summary>
    /// The preferences file of the PowerShell app next to the script: the parent of the Packagers folder, then the folder of the Packagers folder itself.
    /// Null when none exists.
    /// </summary>
    public static string? FindBesideScript(IEnumerable<string?> packagersFolders)
    {
        foreach (var folder in packagersFolders.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            var trimmed = folder!.TrimEnd('\\', '/');
            foreach (var candidate in new[] { Path.GetDirectoryName(trimmed), trimmed })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                var path = Path.Combine(candidate, FileName);
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }

    /// <summary>Reads <paramref name="path"/> without changing it and builds the preview against the current settings.</summary>
    public static PowerShellImportPreview PreviewFile(string path, AppSettings current)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PowerShellImportPreview(path, "The file could not be read: " + ex.Message, [], new Dictionary<string, Action<AppSettings>>());
        }

        var preview = Preview(text, current);
        return new PowerShellImportPreview(path, preview.Error, preview.Rows, preview.Writers);
    }

    /// <summary>Builds the preview of the JSON text against the current settings.</summary>
    public static PowerShellImportPreview Preview(string json, AppSettings current)
    {
        var none = new Dictionary<string, Action<AppSettings>>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            return new PowerShellImportPreview(null, "The file is not valid JSON: " + ex.Message, [], none);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new PowerShellImportPreview(null, "The file is not a preferences file: the top level is not an object.", [], none);

            var rows = new List<PowerShellImportRow>();
            var writers = new Dictionary<string, Action<AppSettings>>(StringComparer.OrdinalIgnoreCase);
            Walk(document.RootElement, "", current, rows, writers);
            return new PowerShellImportPreview(null, null, rows, writers);
        }
    }

    /// <summary>
    /// Writes the chosen rows that differ into <paramref name="target"/>. <paramref name="chosen"/> null chooses every row that differs.
    /// Returns the number of keys written.
    /// </summary>
    public static int Apply(PowerShellImportPreview preview, AppSettings target, IReadOnlySet<string>? chosen = null)
    {
        var written = 0;
        foreach (var row in preview.Rows.Where(r => r.Differs))
        {
            if (chosen is not null && !chosen.Contains(row.Key)) continue;
            if (!preview.Writers.TryGetValue(row.Key, out var write)) continue;
            write(target);
            written++;
        }

        return written;
    }

    // ---------------------------------------------------------------- walking the file

    private static void Walk(JsonElement node, string prefix, AppSettings current, List<PowerShellImportRow> rows, Dictionary<string, Action<AppSettings>> writers)
    {
        foreach (var property in node.EnumerateObject())
        {
            var key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            var value = property.Value;

            if (IsCredentialName(property.Name))
            {
                var protectedNote = property.Name.EndsWith("Protected", StringComparison.OrdinalIgnoreCase) ? ProtectedSecretNote : CredentialNote;
                rows.Add(new PowerShellImportRow(key, PowerShellImportOutcome.Skipped, "", "", protectedNote));
                continue;
            }

            if (Containers.TryGetValue(key, out var container))
            {
                rows.Add(new PowerShellImportRow(key, PowerShellImportOutcome.Skipped, "", "", container));
                continue;
            }

            if (key.Equals("Intune.PublishToIntune", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(PublishToIntuneRow(key, value, node, current, writers));
                continue;
            }

            if (key.StartsWith("DetectedTools.", StringComparison.OrdinalIgnoreCase) && key.IndexOf('.', 14) < 0)
            {
                var tool = key.Equals("DetectedTools.IntuneWinAppUtil", StringComparison.OrdinalIgnoreCase) ? IntuneWinToolNote : DetectedToolNote;
                rows.Add(new PowerShellImportRow(key, PowerShellImportOutcome.Skipped, "", "", tool));
                continue;
            }

            if (Rules.TryGetValue(key, out var rule))
            {
                rows.Add(rule.Evaluate(key, value, current, writers));
                continue;
            }

            // A block that APD has no rule for is read key by key, so a credential inside it is still named and left alone.
            if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any())
            {
                Walk(value, key, current, rows, writers);
                continue;
            }

            rows.Add(new PowerShellImportRow(key, PowerShellImportOutcome.Unknown, Display(value), "", "APD has no setting for this key."));
        }
    }

    /// <summary>
    /// APD has no separate publish switch: it publishes to Intune when the deployment target includes Intune. The row is derived from the
    /// deployment target; a file from before the target existed (PublishToIntune true and no valid target) writes the target MECMAndIntune, as the PowerShell app does.
    /// </summary>
    private static PowerShellImportRow PublishToIntuneRow(string key, JsonElement value, JsonElement node, AppSettings current, Dictionary<string, Action<AppSettings>> writers)
    {
        var apdTarget = current.Intune.DeploymentTarget;
        var apd = DeploymentTargets.PublishesToIntune(apdTarget) ? "True" : "False";
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new PowerShellImportRow(key, PowerShellImportOutcome.Skipped, Display(value), apd, "A true or false value is expected.");

        var publish = value.ValueKind == JsonValueKind.True;
        var text = publish ? "True" : "False";
        var hasTarget = node.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "DeploymentTarget", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } t
            && new[] { DeploymentTargets.Mecm, DeploymentTargets.MecmAndIntune, DeploymentTargets.IntuneOnly, DeploymentTargets.MecmAndWsus, DeploymentTargets.WsusOnly }
                .Any(n => string.Equals(n, t.Value.GetString(), StringComparison.OrdinalIgnoreCase));

        if (!hasTarget && publish)
        {
            if (apdTarget is DeploymentTargets.MecmAndIntune or DeploymentTargets.IntuneOnly)
                return new PowerShellImportRow(key, PowerShellImportOutcome.Same, text, apd, "Same. APD publishes to Intune when the deployment target includes Intune.");
            writers[key] = s => s.Intune.DeploymentTarget = DeploymentTargets.MecmAndIntune;
            return new PowerShellImportRow(key, PowerShellImportOutcome.Import, text, apd, "Will be written as the deployment target MECMAndIntune (an older file has no target).");
        }

        return new PowerShellImportRow(key, PowerShellImportOutcome.Skipped, text, apd,
            "APD has no separate switch. It publishes to Intune when the deployment target (Intune.DeploymentTarget) includes Intune.");
    }

    /// <summary>A name that holds a credential. The value is never read.</summary>
    internal static bool IsCredentialName(string name) =>
        name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Protected", StringComparison.OrdinalIgnoreCase);

    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => "(" + value.GetArrayLength() + " items)",
        JsonValueKind.Object => "(object)",
        JsonValueKind.Null => "",
        _ => value.GetRawText()
    };

    // ---------------------------------------------------------------- rules

    /// <summary>A key whose whole block is left alone, with the reason.</summary>
    private static readonly Dictionary<string, string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FirstRunCompleted"] = "APD keeps its own first-run flag.",
        ["AppFlow.Tracked"] = PerApplicationNote,
        ["AppFlow.CadenceOverrides"] = PerApplicationNote,
        ["AppFlow.Destinations"] = PerApplicationNote,
        ["LocalSourceFolders"] = PerApplicationNote,
        ["HiddenApplications"] = PerApplicationNote,
        ["DiscoveryDismissed"] = PerApplicationNote,
        ["DiscoveryEnabled"] = PerApplicationNote,
        ["DeploymentConditions"] = PerApplicationNote,
        ["CommandOverrides"] = PerApplicationNote,
        ["M365Options"] = "Install parameters. APD saves them for each application under Install Parameters.",
        ["M365CustomConfig"] = "Install parameters. APD saves them for each application under Install Parameters.",
        ["M365ExcludeApps"] = "Install parameters. APD saves them for each application under Install Parameters.",
        ["M365DeployMode"] = "Install parameters. APD saves them for each application under Install Parameters.",
        ["SSMSInstallOptions"] = "Install parameters. APD saves them for each application under Install Parameters.",
        ["DBeaverInstallOptions"] = "Install parameters. APD saves them for each application under Install Parameters.",
        ["AdobeReaderInstallOptions"] = "Install parameters. APD saves them for each application under Install Parameters.",
        ["BeyondCompareOptions"] = "Install parameters. APD saves them for each application under Install Parameters.",
        ["Supersedence.TitleOverrides"] = "Saved for each application, and the application identifiers of the two apps differ. Set it in APD.",
        ["Supersedence.AcceptedExactBlocks"] = "Accepted for one application and version, and the application identifiers of the two apps differ. Accept it again in APD.",
        ["Supersedence.InstalledValues"] = "Values read from an installed product on the other computer. APD records its own.",
        ["Intune.AcceptedRequirementDrops"] = "Accepted for one application, and the application identifiers of the two apps differ. Accept it again in APD.",
        ["WorkbenchDataRoot"] = "The folder of the PowerShell Workbench. APD keeps its own data folder.",
    };

    private static readonly Dictionary<string, Rule> Rules = BuildRules();

    private static Dictionary<string, Rule> BuildRules()
    {
        var rules = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        void Add(Rule rule) => rules[rule.Path] = rule;

        Add(Rule.Text("SiteCode", s => s.SiteCode, (s, v) => { s.SiteCode = v; s.SiteCodeSaved = true; }, v => v.Length is >= 1 and <= 5 && v.All(char.IsLetterOrDigit) ? null : "A site code has 1 to 5 letters or digits."));
        Add(Rule.Text("ProviderMachineName", s => s.ProviderMachine, (s, v) => s.ProviderMachine = v.Trim()));
        Add(Rule.Text("MECM.ServerFQDN", s => s.ProviderMachine, (s, v) => s.ProviderMachine = v.Trim()));
        Add(Rule.Text("FileShareRoot", s => s.FileShareRoot, (s, v) => s.FileShareRoot = v));
        Add(Rule.Choice("ContentLayout", [ContentLayouts.Nested, ContentLayouts.Flat], s => s.ContentLayout, (s, v) => s.ContentLayout = v));
        Add(Rule.Text("DownloadRoot", s => s.DownloadRoot, (s, v) => s.DownloadRoot = v,
            v => ApplicationPackagerEngine.Services.AppDataPaths.IsLegacyStagingRoot(v) ? "The PowerShell default folder. APD keeps its staging folder for each user." : null));
        Add(Rule.Number("EstimatedRuntimeMins", 1, 120, s => s.EstimatedRuntimeMins, (s, v) => s.EstimatedRuntimeMins = v));
        Add(Rule.Number("MaximumRuntimeMins", 15, 720, s => s.MaximumRuntimeMins, (s, v) => s.MaximumRuntimeMins = v));
        Add(Rule.Choice("ContentFallback", [DeploymentTypeContentOptions.Allow, DeploymentTypeContentOptions.Deny], s => s.ContentFallback, (s, v) => s.ContentFallback = v));
        Add(Rule.Choice("SlowNetworkDeploymentMode", [DeploymentTypeContentOptions.Download, DeploymentTypeContentOptions.DoNothing], s => s.SlowNetworkDeploymentMode, (s, v) => s.SlowNetworkDeploymentMode = v));
        Add(Rule.Text("CompanyName", s => s.CompanyName, (s, v) => s.CompanyName = v));
        Add(Rule.Choice("M365Channel", ["MonthlyEnterprise", "Current"], s => s.M365Channel, (s, v) => s.M365Channel = v,
            legacy: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["SemiAnnual"] = "MonthlyEnterprise", ["SemiAnnualPreview"] = "MonthlyEnterprise" }));
        Add(Rule.Text("BeyondCompareKeyFile", s => s.SideFiles.TryGetValue("BeyondCompareKeyFile", out var p) ? p : "", (s, v) => s.SideFiles["BeyondCompareKeyFile"] = v));
        Add(Rule.Bool("IncludeVersionInTitle", s => s.IncludeVersionInTitle, (s, v) => s.IncludeVersionInTitle = v));
        Add(Rule.Bool("RemoveVersionFromTitle", s => s.RemoveVersionFromTitle, (s, v) => s.RemoveVersionFromTitle = v));
        Add(Rule.Bool("AppendArchitectureToTitle", s => s.AppendArchitectureToTitle, (s, v) => s.AppendArchitectureToTitle = v));
        Add(Rule.Bool("CheckForUpdatesAtStartup", s => s.CheckForUpdatesAtStartup, (s, v) => s.CheckForUpdatesAtStartup = v));

        // APD keeps one SQL Server name, as server\instance,port. The PowerShell app keeps the three parts in separate keys.
        Add(Rule.Text("DiscoverySqlServer", s => SqlName.Parse(s.DiscoverySqlServer).Server, (s, v) => s.DiscoverySqlServer = SqlName.WithServer(s.DiscoverySqlServer, v)));
        Add(Rule.Text("DiscoverySqlInstance", s => SqlName.Parse(s.DiscoverySqlServer).Instance, (s, v) => s.DiscoverySqlServer = SqlName.WithInstance(s.DiscoverySqlServer, v),
            v => v.Contains('\\') || v.Contains(',') ? "An instance name has no backslash or comma." : null));
        Add(Rule.Number("DiscoverySqlPort", 0, 65535, s => SqlName.Parse(s.DiscoverySqlServer).Port, (s, v) => s.DiscoverySqlServer = SqlName.WithPort(s.DiscoverySqlServer, v)));
        Add(Rule.Text("DiscoverySqlDatabase", s => s.DiscoverySqlDatabase, (s, v) => s.DiscoverySqlDatabase = v.Trim()));

        Add(Rule.Bool("Systems.ConfigMgr", s => s.Systems?.ConfigMgr ?? true, (s, v) => (s.Systems ??= new SystemsInUse()).ConfigMgr = v));
        Add(Rule.Bool("Systems.Intune", s => s.Systems?.Intune ?? false, (s, v) => (s.Systems ??= new SystemsInUse()).Intune = v));
        Add(Rule.Bool("Systems.Wsus", s => s.Systems?.Wsus ?? false, (s, v) => (s.Systems ??= new SystemsInUse()).Wsus = v));

        Add(Rule.Choice("AppFlow.Action", [OneClickActions.Report, OneClickActions.Stage, OneClickActions.StageAndPackage], s => s.OneClick.Action, (s, v) => s.OneClick.Action = v));
        Add(Rule.Number("AppFlow.DefaultCadenceDays", 1, 365, s => s.OneClick.DefaultCadenceDays, (s, v) => s.OneClick.DefaultCadenceDays = v));
        Add(Rule.Bool("AppFlow.ForceOnLaunch", s => s.OneClick.ForceOnLaunch, (s, v) => s.OneClick.ForceOnLaunch = v));
        Add(Rule.Choice("AppFlow.OnExisting", ["Skip", "Overwrite"], s => s.OneClick.OnExisting, (s, v) => s.OneClick.OnExisting = v));
        Add(Rule.Destinations("AppFlow.DefaultDestinations"));

        Add(Rule.Bool("ContentDistribution.AutoDistribute", s => s.ContentDistribution.AutoDistribute, (s, v) => s.ContentDistribution.AutoDistribute = v));
        Add(Rule.Text("ContentDistribution.DPGroupName", s => s.ContentDistribution.DpGroupName, (s, v) => s.ContentDistribution.DpGroupName = v.Trim()));
        Add(Rule.Bool("ContentDistribution.DeployToTestCollection", s => s.ContentDistribution.DeployToTestCollection, (s, v) => s.ContentDistribution.DeployToTestCollection = v));
        Add(Rule.Text("ContentDistribution.TestCollectionName", s => s.ContentDistribution.TestCollectionName, (s, v) => s.ContentDistribution.TestCollectionName = v.Trim()));
        Add(Rule.Bool("ContentDistribution.TestCollectionIsUser", s => s.ContentDistribution.TestCollectionIsUser, (s, v) => s.ContentDistribution.TestCollectionIsUser = v));
        Add(Rule.Bool("ContentDistribution.CreateTestCollectionIfMissing", s => s.ContentDistribution.CreateTestCollectionIfMissing, (s, v) => s.ContentDistribution.CreateTestCollectionIfMissing = v));

        Add(Rule.Choice("Intune.DeploymentTarget", [DeploymentTargets.Mecm, DeploymentTargets.MecmAndIntune, DeploymentTargets.IntuneOnly, DeploymentTargets.MecmAndWsus, DeploymentTargets.WsusOnly],
            s => s.Intune.DeploymentTarget, (s, v) => s.Intune.DeploymentTarget = v));
        Add(Rule.Text("Intune.MinimumWindowsRelease", s => ApplicationPackagerEngine.Services.IntuneWindowsReleases.Resolve(s.Intune.MinimumWindowsRelease),
            (s, v) => s.Intune.MinimumWindowsRelease = ApplicationPackagerEngine.Services.IntuneWindowsReleases.Resolve(v),
            v => ApplicationPackagerEngine.Services.IntuneWindowsReleases.IsWellFormed(v.Trim()) ? null : "A release looks like Windows10_21H2 or Windows11_23H2."));
        Add(Rule.Text("Intune.TenantId", s => s.Intune.TenantId, (s, v) => s.Intune.TenantId = v.Trim()));
        Add(Rule.Text("Intune.ClientId", s => s.Intune.ClientId, (s, v) => s.Intune.ClientId = v.Trim()));

        Add(Rule.Text("Wsus.ServerName", s => s.Wsus.ServerName, (s, v) => s.Wsus.ServerName = v.Trim()));
        Add(Rule.Number("Wsus.PortNumber", 1, 65535, s => s.Wsus.PortNumber, (s, v) => s.Wsus.PortNumber = v));
        Add(Rule.Bool("Wsus.UseSsl", s => s.Wsus.UseSsl, (s, v) => s.Wsus.UseSsl = v));
        Add(Rule.Choice("Wsus.Classification", WsusClassifications.Names, s => s.Wsus.Classification, (s, v) => s.Wsus.Classification = v));
        Add(Rule.Text("Wsus.ApprovalGroup", s => s.Wsus.ApprovalGroup, (s, v) => s.Wsus.ApprovalGroup = v.Trim()));
        Add(Rule.Bool("Wsus.DeclineSuperseded", s => s.Wsus.DeclineSuperseded, (s, v) => s.Wsus.DeclineSuperseded = v));

        Add(Rule.Number("Vulnerability.CacheTtlMinutes", 0, 100000, s => s.Vulnerability.CacheTtlMinutes, (s, v) => s.Vulnerability.CacheTtlMinutes = v));
        Add(Rule.Bool("Vulnerability.LookupAfterCheckCm", s => s.Vulnerability.LookupAfterCheckCm, (s, v) => s.Vulnerability.LookupAfterCheckCm = v));

        Add(Rule.Choice("Supersedence.Mode", ["Off", "Replace", "Replace and uninstall"], s => ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.TryParseMode(s.Supersedence.Mode, out var m) ? ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.Text(m) : "Off",
            (s, v) => s.Supersedence.Mode = v,
            legacy: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ReplaceAndUninstall"] = "Replace and uninstall", ["replace-uninstall"] = "Replace and uninstall", ["replace-and-uninstall"] = "Replace and uninstall" }));
        Add(Rule.Choice("Supersedence.ConfigMgrVersionRule", ["Minimum", "Exact"], s => ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.TryParseRule(s.Supersedence.ConfigMgrVersionRule, out var r) ? ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.Text(r) : "Minimum", (s, v) => s.Supersedence.ConfigMgrVersionRule = v));
        Add(Rule.Choice("Supersedence.IntuneVersionRule", ["Minimum", "Exact"], s => ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.TryParseRule(s.Supersedence.IntuneVersionRule, out var r) ? ApplicationPackagerEngine.Services.Supersedence.SupersedenceNames.Text(r) : "Minimum", (s, v) => s.Supersedence.IntuneVersionRule = v));
        Add(Rule.Bool("SkipConfigReview", s => s.SkipConfigReview, (s, v) => s.SkipConfigReview = v));

        Add(Rule.Bool("ScriptSigning.SignDetection", s => s.ScriptSigning.SignDetection, (s, v) => s.ScriptSigning.SignDetection = v));
        Add(Rule.Bool("ScriptSigning.SignRequirements", s => s.ScriptSigning.SignRequirements, (s, v) => s.ScriptSigning.SignRequirements = v));
        Add(Rule.Bool("ScriptSigning.SignDeployment", s => s.ScriptSigning.SignDeployment, (s, v) => s.ScriptSigning.SignDeployment = v));
        Add(Rule.Bool("ScriptSigning.RequireDetection", s => s.ScriptSigning.RequireDetection, (s, v) => s.ScriptSigning.RequireDetection = v));
        Add(Rule.Bool("ScriptSigning.RequireRequirements", s => s.ScriptSigning.RequireRequirements, (s, v) => s.ScriptSigning.RequireRequirements = v));
        Add(Rule.Bool("ScriptSigning.RequireDeployment", s => s.ScriptSigning.RequireDeployment, (s, v) => s.ScriptSigning.RequireDeployment = v));
        Add(Rule.Text("ScriptSigning.CertificateThumbprint", s => s.ScriptSigning.CertificateThumbprint, (s, v) => s.ScriptSigning.CertificateThumbprint = v.Trim(),
            v => v.Length == 0 || (v.Length == 40 && v.All(Uri.IsHexDigit)) ? null : "A thumbprint is 40 hexadecimal characters."));
        Add(Rule.Choice("ScriptSigning.StoreLocation", [SigningPolicyStores.CurrentUser, SigningPolicyStores.LocalMachine], s => s.ScriptSigning.StoreLocation, (s, v) => s.ScriptSigning.StoreLocation = v));
        Add(Rule.Text("ScriptSigning.TimestampServer", s => s.ScriptSigning.TimestampServer, (s, v) => s.ScriptSigning.TimestampServer = v.Trim()));
        Add(Rule.Bool("ScriptSigning.TimestampRequired", s => s.ScriptSigning.TimestampRequired, (s, v) => s.ScriptSigning.TimestampRequired = v));
        Add(Rule.Choice("ScriptSigning.HashAlgorithm", ["SHA256", "SHA384", "SHA512", "SHA1"], s => s.ScriptSigning.HashAlgorithm, (s, v) => s.ScriptSigning.HashAlgorithm = v));
        return rules;
    }

    /// <summary>The SQL Server name of APD (server\instance,port) and its three parts.</summary>
    internal static class SqlName
    {
        public static (string Server, string Instance, int Port) Parse(string? name)
        {
            var text = (name ?? "").Trim();
            var port = 0;
            var comma = text.LastIndexOf(',');
            if (comma >= 0 && int.TryParse(text[(comma + 1)..].Trim(), out var parsed)) { port = parsed; text = text[..comma].Trim(); }
            var slash = text.IndexOf('\\');
            return slash >= 0 ? (text[..slash].Trim(), text[(slash + 1)..].Trim(), port) : (text, "", port);
        }

        public static string Join(string server, string instance, int port) =>
            server.Length == 0 ? "" : server + (instance.Length > 0 ? "\\" + instance : "") + (port > 0 ? "," + port : "");

        /// <summary>A server value of an older PowerShell file may hold the whole name (server\instance,port); it then sets all three parts.</summary>
        public static string WithServer(string? current, string value)
        {
            var given = Parse(value);
            var (_, instance, port) = Parse(current);
            return value.Contains('\\') || value.Contains(',') ? Join(given.Server, given.Instance, given.Port) : Join(given.Server, instance, port);
        }

        public static string WithInstance(string? current, string value)
        {
            var (server, _, port) = Parse(current);
            return Join(server, value.Trim(), port);
        }

        public static string WithPort(string? current, int value)
        {
            var (server, instance, _) = Parse(current);
            return Join(server, instance, value);
        }
    }

    private static class SigningPolicyStores
    {
        public const string CurrentUser = ApplicationPackagerEngine.Models.Signing.SigningPolicy.CurrentUser;
        public const string LocalMachine = ApplicationPackagerEngine.Models.Signing.SigningPolicy.LocalMachine;
    }

    /// <summary>One key of the PowerShell file that has an APD setting: how to read the value, check it and write it.</summary>
    private sealed class Rule
    {
        public required string Path { get; init; }
        public required Func<JsonElement, (bool Ok, string Text, string? Why)> Parse { get; init; }
        public required Func<AppSettings, string> Current { get; init; }
        public required Func<string, Action<AppSettings>> Writer { get; init; }

        public PowerShellImportRow Evaluate(string key, JsonElement value, AppSettings current, Dictionary<string, Action<AppSettings>> writers)
        {
            var (ok, text, why) = Parse(value);
            var apd = Current(current);
            if (!ok) return new PowerShellImportRow(key, PowerShellImportOutcome.Skipped, Display(value), apd, why ?? "The value is not valid for APD.");
            if (string.Equals(text, apd, StringComparison.Ordinal)) return new PowerShellImportRow(key, PowerShellImportOutcome.Same, text, apd, "Same.");
            writers[key] = Writer(text);
            return new PowerShellImportRow(key, PowerShellImportOutcome.Import, text, apd, "Will be written.");
        }

        public static Rule Text(string path, Func<AppSettings, string> get, Action<AppSettings, string> set, Func<string, string?>? check = null) => new()
        {
            Path = path,
            Parse = v =>
            {
                if (v.ValueKind == JsonValueKind.Null) return (true, "", null);
                if (v.ValueKind != JsonValueKind.String) return (false, "", "A text value is expected.");
                var text = v.GetString() ?? "";
                var why = check?.Invoke(text);
                return why is null ? (true, text, null) : (false, text, why);
            },
            Current = s => get(s) ?? "",
            Writer = text => s => set(s, text)
        };

        public static Rule Bool(string path, Func<AppSettings, bool> get, Action<AppSettings, bool> set) => new()
        {
            Path = path,
            Parse = v => v.ValueKind switch
            {
                JsonValueKind.True => (true, "True", null),
                JsonValueKind.False => (true, "False", null),
                _ => (false, "", "A true or false value is expected.")
            },
            Current = s => get(s) ? "True" : "False",
            Writer = text => s => set(s, text == "True")
        };

        public static Rule Number(string path, int min, int max, Func<AppSettings, int> get, Action<AppSettings, int> set) => new()
        {
            Path = path,
            Parse = v =>
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number)) return Range(number);
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return Range(parsed);
                return (false, "", "A whole number is expected.");

                (bool, string, string?) Range(int n) => n < min || n > max
                    ? (false, n.ToString(), $"APD accepts {min} to {max}.")
                    : (true, n.ToString(), null);
            },
            Current = s => get(s).ToString(),
            Writer = text => s => set(s, int.Parse(text))
        };

        public static Rule Choice(string path, IEnumerable<string> allowed, Func<AppSettings, string> get, Action<AppSettings, string> set, IReadOnlyDictionary<string, string>? legacy = null)
        {
            var names = allowed.ToArray();
            return new Rule
            {
                Path = path,
                Parse = v =>
                {
                    if (v.ValueKind != JsonValueKind.String) return (false, "", "A text value is expected.");
                    var text = v.GetString() ?? "";
                    if (legacy is not null && legacy.TryGetValue(text, out var replaced)) text = replaced;
                    var match = names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                    return match is null ? (false, text, "APD offers: " + string.Join(", ", names) + ".") : (true, match, null);
                },
                Current = s => get(s) ?? "",
                Writer = text => s => set(s, text)
            };
        }

        public static Rule Destinations(string path) => new()
        {
            Path = path,
            Parse = v =>
            {
                if (v.ValueKind != JsonValueKind.Object) return (false, "", "An object is expected.");
                var set = FromElement(v);
                return (true, Describe(set), null);
            },
            Current = s => s.OneClick.DefaultDestinations is { } d ? Describe(d) : "(from the deployment target)",
            Writer = text => s => s.OneClick.DefaultDestinations = FromDescription(text)
        };

        private static DestinationSet FromElement(JsonElement v)
        {
            bool Flag(string name) => v.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.True };
            return new DestinationSet(Flag("ConfigMgr"), Flag("WSUS"), Flag("Intune"));
        }

        private static string Describe(DestinationSet d)
        {
            var names = new List<string>();
            if (d.ConfigMgr) names.Add("ConfigMgr");
            if (d.Wsus) names.Add("WSUS");
            if (d.Intune) names.Add("Intune");
            return names.Count == 0 ? "(none)" : string.Join(", ", names);
        }

        private static DestinationSet FromDescription(string text) =>
            new(text.Contains("ConfigMgr"), text.Contains("WSUS"), text.Contains("Intune"));
    }
}
