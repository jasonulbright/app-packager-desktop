using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>A detection of a deployment type that has one version clause, as the site stores it.</summary>
public sealed record StoredClause(
    string Kind, string LogicalName, string Operator, string ExpectedValue,
    string Path, string FileName, bool Is64Bit, string Hive, string Key, string ValueName);

/// <summary>What the site holds for one deployment type: its detection, when that is one clause, and why it is not otherwise.</summary>
public sealed record StoredDetection(StoredClause? Clause, string? NotRewritableReason);

/// <summary>
/// The optional rewrite of the version clause of an old application to Exact (see "Rewrite step" in the specification). The reader reads
/// the one-clause detections that the site stores (checked on ConfigMgr 5.00.9146: the detection method is XML text, and its settings
/// and its rule give the clause). The rewrite removes the clause by its logical name and adds the same clause with the other operator.
/// </summary>
public static class OldVersionRewrite
{
    public const string RewrittenLine = "Rewritten";
    public const string DeploymentLine = "APDDEP";
    public const string DeploymentEndLine = "APDDEPEND";

    /// <summary>True when the deployment script ran to its end marker: an answer without it is not an answer.</summary>
    public static bool DeploymentsComplete(string? output) =>
        (output ?? "").Split('\n').Any(l => l.Trim() == DeploymentEndLine);

    /// <summary>Reads the single version clause of a deployment type from its stored XML.</summary>
    public static StoredDetection ReadDetection(string? deploymentTypeXml)
    {
        if (string.IsNullOrWhiteSpace(deploymentTypeXml)) return new(null, "the detection could not be read");
        XDocument outer;
        try { outer = XDocument.Parse(deploymentTypeXml); }
        catch (System.Xml.XmlException) { return new(null, "the detection could not be read"); }
        var holder = outer.Descendants().FirstOrDefault(e => !e.HasElements && e.Value.Contains("<EnhancedDetectionMethod", StringComparison.Ordinal));
        if (holder is null) return new(null, "the deployment type has no enhanced detection method");
        XDocument method;
        try { method = XDocument.Parse(holder.Value); }
        catch (System.Xml.XmlException) { return new(null, "the detection method could not be read"); }

        var settings = method.Descendants().Where(e => e.Name.LocalName is "File" or "SimpleSetting").ToList();
        var expressions = method.Descendants().Where(e => e.Name.LocalName == "Expression").ToList();
        if (expressions.Count != 1 || settings.Count != 1)
            return new(null, "the detection has more than one clause or a connector, which is not rewritten");

        var expression = expressions[0];
        var op = expression.Elements().FirstOrDefault(e => e.Name.LocalName == "Operator")?.Value.Trim() ?? "";
        var reference = expression.Descendants().FirstOrDefault(e => e.Name.LocalName == "SettingReference");
        var constant = expression.Descendants().FirstOrDefault(e => e.Name.LocalName == "ConstantValue");
        if (reference is null || constant is null || !string.Equals((string?)reference.Attribute("DataType"), "Version", StringComparison.OrdinalIgnoreCase))
            return new(null, "the clause is not a version clause");
        var setting = settings[0];
        var logicalName = (string?)setting.Attribute("LogicalName") ?? "";
        if (logicalName.Length == 0 || logicalName != (string?)reference.Attribute("SettingLogicalName"))
            return new(null, "the clause does not match its setting");
        var expected = (string?)constant.Attribute("Value") ?? "";

        if (setting.Name.LocalName == "File")
        {
            var path = setting.Elements().FirstOrDefault(e => e.Name.LocalName == "Path")?.Value ?? "";
            var filter = setting.Elements().FirstOrDefault(e => e.Name.LocalName == "Filter")?.Value ?? "";
            return new(new StoredClause("File", logicalName, op, expected, path, filter,
                string.Equals((string?)setting.Attribute("Is64Bit"), "true", StringComparison.OrdinalIgnoreCase), "", "", ""), null);
        }
        var source = setting.Descendants().FirstOrDefault(e => e.Name.LocalName == "RegistryDiscoverySource");
        if (source is null) return new(null, "the clause is not a file or a registry value");
        var hive = (string?)source.Attribute("Hive") ?? "";
        var key = source.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value ?? "";
        var valueName = source.Elements().FirstOrDefault(e => e.Name.LocalName == "ValueName")?.Value ?? "";
        return new(new StoredClause("Registry", logicalName, op, expected, "", "",
            string.Equals((string?)source.Attribute("Is64Bit"), "true", StringComparison.OrdinalIgnoreCase), hive, key, valueName), null);
    }

    public static string CmdletOperator(string storedOperator) =>
        storedOperator.Equals("Equals", StringComparison.OrdinalIgnoreCase) ? "IsEquals" : storedOperator;

    /// <summary>True for the hives that the rewrite writes (HKLM and HKCU); another hive is left at plan time with a reason.</summary>
    public static bool HiveSupported(string hive) =>
        hive.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase);

    private static string CmdletHive(string hive) => hive.ToUpperInvariant() switch
    {
        "HKEY_LOCAL_MACHINE" => "LocalMachine",
        "HKEY_CURRENT_USER" => "CurrentUser",
        _ => throw new InvalidOperationException($"The registry hive '{hive}' is not one that the rewrite writes.")
    };

    /// <summary>
    /// The script that replaces the clause of one deployment type: the same clause with <paramref name="newOperator"/> (a cmdlet word:
    /// IsEquals or GreaterEquals). It prints <c>Rewritten|application|deployment type|old operator|new operator|logical name</c> for the log.
    /// </summary>
    public static string BuildRewriteScript(
        string siteCode, string? providerMachine, string applicationName, string deploymentTypeName, StoredClause clause, string newOperator)
    {
        static string Q(string text) => PowerShellLiteral.EscapeSingleQuoted(text);
        if (newOperator is not ("IsEquals" or "GreaterEquals")) throw new ArgumentException("The operator is IsEquals or GreaterEquals.", nameof(newOperator));
        var sb = new StringBuilder();
        MecmTarget.AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine("Set-Location C:");
        sb.AppendLine(clause.Kind == "File"
            ? $"$rewriteClause = New-CMDetectionClauseFile -Path '{Q(clause.Path)}' -FileName '{Q(clause.FileName)}' -PropertyType Version -Value -ExpressionOperator {newOperator} -ExpectedValue '{Q(clause.ExpectedValue)}'{(clause.Is64Bit ? " -Is64Bit" : "")}"
            : $"$rewriteClause = New-CMDetectionClauseRegistryKeyValue -Hive {CmdletHive(clause.Hive)} -KeyName '{Q(clause.Key)}' -ValueName '{Q(clause.ValueName)}' -PropertyType Version -Value -ExpressionOperator {newOperator} -ExpectedValue '{Q(clause.ExpectedValue)}'{(clause.Is64Bit ? " -Is64Bit" : "")}");
        sb.AppendLine($"Set-Location '{Q(siteCode)}:'");
        sb.AppendLine($"Set-CMScriptDeploymentType -ApplicationName '{Q(applicationName)}' -DeploymentTypeName '{Q(deploymentTypeName)}' -RemoveDetectionClause '{Q(clause.LogicalName)}' -AddDetectionClause $rewriteClause -ErrorAction Stop | Out-Null");
        // The names go out as base64, as in the other lines of the scripts: a separator in a title must not turn a rewrite into a failure.
        sb.AppendLine("function ConvertTo-RewriteField([string]$text) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)) }");
        sb.AppendLine($"Write-Host ('{RewrittenLine}|{{0}}|{{1}}|{{2}}|{{3}}|{{4}}' -f (ConvertTo-RewriteField '{Q(applicationName)}'), (ConvertTo-RewriteField '{Q(deploymentTypeName)}'), '{Q(CmdletOperator(clause.Operator))}', '{newOperator}', (ConvertTo-RewriteField '{Q(clause.LogicalName)}'))");
        return sb.ToString();
    }

    /// <summary>The deployments of one application with their purpose: <c>APDDEP|b64 name|offer type</c> (0 is Required).</summary>
    public static string BuildDeploymentScript(string siteCode, string? providerMachine, string applicationName)
    {
        var sb = new StringBuilder();
        MecmTarget.AppendCmPreamble(sb, siteCode, providerMachine);
        // Names are literal here (the cmdlet treats a name as a wildcard otherwise: a title with [x64] would match no deployment and the
        // application would read as having none). The end marker tells a complete answer from a script that printed nothing.
        sb.AppendLine($"$rewriteApplication = '{PowerShellLiteral.EscapeSingleQuoted(applicationName)}'");
        sb.AppendLine("foreach ($rewriteDeployment in @(Get-CMApplicationDeployment -Name $rewriteApplication -DisableWildcardHandling -ErrorAction Stop)) {");
        sb.AppendLine("    if ([string]$rewriteDeployment.ApplicationName -ne $rewriteApplication) { continue }");
        sb.AppendLine("    $collectionB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$rewriteDeployment.CollectionName))");
        sb.AppendLine($"    Write-Host ('{DeploymentLine}|{{0}}|{{1}}' -f $collectionB64, $rewriteDeployment.OfferTypeID)");
        sb.AppendLine("}");
        sb.AppendLine($"Write-Host '{DeploymentEndLine}'");
        return sb.ToString();
    }

    /// <summary>The collections that hold a required deployment, read from the output of <see cref="BuildDeploymentScript"/>.</summary>
    public static IReadOnlyList<string> RequiredCollections(string? output)
    {
        var found = new List<string>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length != 3 || parts[0] != DeploymentLine || parts[2] != "0") continue;
            try { found.Add(Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]))); }
            catch (FormatException) { found.Add("(unreadable)"); }
        }
        return found;
    }
}

/// <summary>One item of the plan: a deployment type of an old application, and what the rewrite does with it.</summary>
public sealed record RewriteItem(
    string Application, string Version, string DeploymentType, StoredClause? Clause, string? Skip)
{
    public bool Rewrites => Clause is not null && Skip is null;
}

/// <summary>The plan of the rewrite: what would change, and why each other clause is left.</summary>
public sealed record RewritePlan(IReadOnlyList<RewriteItem> Items, IReadOnlyList<string> Blocked)
{
    public int ClauseCount => Items.Count(i => i.Rewrites);
    public int ApplicationCount => Items.Where(i => i.Rewrites).Select(i => i.Application).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public static class RewritePlanner
{
    public const string ConfirmationText =
        "ConfigMgr evaluates the detection again on the devices. A device that has a newer version than the one in the clause, because " +
        "it updated itself or got it in another way, then reads \"not installed\".";

    /// <param name="old">The old applications with their deployment types (the lookup output, without the newest version).</param>
    /// <param name="detections">The stored detection of each deployment type, by <see cref="Key"/> (application and deployment type).</param>
    /// <param name="requiredCollections">The collections with a required deployment, by application title.</param>
    /// <param name="guard">The Exact guard: it reads the installed value of the title and of the old version.</param>
    /// <summary>The key of a deployment type in the plan: the application and the deployment type, so that two applications that hold a type of the same name never share a detection.</summary>
    public static string Key(string application, string deploymentType) => application + "\u0001" + deploymentType;

    public static RewritePlan Plan(
        IReadOnlyList<LineApplication> old, IReadOnlyDictionary<string, StoredDetection> detections,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredCollections,
        Func<LineApplication, StoredClause, ExactGuardVerdict> guard)
    {
        var items = new List<RewriteItem>();
        var blocked = new List<string>();
        foreach (var app in old)
        {
            if (requiredCollections.TryGetValue(app.Title, out var required) && required.Count > 0)
            {
                blocked.Add($"'{app.Title}' has a required deployment ({string.Join(", ", required)}). Remove the required deployment, or leave this application out.");
                continue;
            }
            foreach (var dt in app.DeploymentTypes)
            {
                if (!detections.TryGetValue(Key(app.Title, dt.Name), out var detection) || detection.Clause is null)
                {
                    items.Add(new RewriteItem(app.Title, app.SoftwareVersion, dt.Name, null, detection?.NotRewritableReason ?? "the detection was not read"));
                    continue;
                }
                var clause = detection.Clause;
                if (clause.Kind == "Registry" && !OldVersionRewrite.HiveSupported(clause.Hive))
                    items.Add(new RewriteItem(app.Title, app.SoftwareVersion, dt.Name, clause, $"the registry hive {clause.Hive} is not one that the rewrite writes"));
                else if (!clause.Operator.Equals("GreaterEquals", StringComparison.OrdinalIgnoreCase))
                    items.Add(new RewriteItem(app.Title, app.SoftwareVersion, dt.Name, clause, $"the operator is {clause.Operator}, not GreaterEquals"));
                else if (!string.Equals(clause.ExpectedValue.Trim(), app.SoftwareVersion.Trim(), StringComparison.OrdinalIgnoreCase))
                    items.Add(new RewriteItem(app.Title, app.SoftwareVersion, dt.Name, clause, $"the expected value '{clause.ExpectedValue}' is not the version of the application"));
                else
                {
                    var verdict = guard(app, clause);
                    items.Add(verdict.Result == ClauseResult.ExactApplied
                        ? new RewriteItem(app.Title, app.SoftwareVersion, dt.Name, clause, null)
                        : new RewriteItem(app.Title, app.SoftwareVersion, dt.Name, clause, "not rewritten: " + verdict.Detail));
                }
            }
        }
        return new RewritePlan(items, blocked);
    }
}

/// <summary>One line of the rewrite log. The log is the only way back, because the publish script purges the revision history.</summary>
public sealed record RewriteLogEntry(
    DateTimeOffset At, string Application, string DeploymentType, string OldOperator, string NewOperator, string LogicalName, bool Restored = false);

/// <summary>The log of the rewrite, one JSON object per line.</summary>
public static class RewriteLog
{
    public static string Serialize(RewriteLogEntry entry) => JsonSerializer.Serialize(entry);

    /// <summary>The entries of a log text. A line that cannot be read is skipped.</summary>
    public static IReadOnlyList<RewriteLogEntry> Read(string? text)
    {
        var entries = new List<RewriteLogEntry>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (JsonSerializer.Deserialize<RewriteLogEntry>(line) is { } entry) entries.Add(entry); }
            catch (JsonException) { }
        }
        return entries;
    }

    private static string Decode(string text)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
        catch (FormatException) { return text; }
    }

    /// <summary>Reads the <c>Rewritten|...</c> lines of a rewrite script into log entries (names are base64).</summary>
    public static IReadOnlyList<RewriteLogEntry> FromScriptOutput(string? output, DateTimeOffset at)
    {
        var entries = new List<RewriteLogEntry>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length == 6 && parts[0] == OldVersionRewrite.RewrittenLine)
                entries.Add(new RewriteLogEntry(at, Decode(parts[1]), Decode(parts[2]), parts[3], parts[4], Decode(parts[5])));
        }
        return entries;
    }

    /// <summary>
    /// What Restore writes back: for each deployment type of the given applications whose last entry is a rewrite (not a restore), the
    /// first entry, which holds the operator that the deployment type had before the first rewrite. Other applications are left.
    /// </summary>
    public static IReadOnlyList<RewriteLogEntry> ToRestore(IReadOnlyList<RewriteLogEntry> log, IReadOnlyCollection<string> applications)
    {
        var wanted = new HashSet<string>(applications, StringComparer.OrdinalIgnoreCase);
        return log.Where(e => wanted.Contains(e.Application))
            .GroupBy(e => (e.Application.ToUpperInvariant(), e.DeploymentType.ToUpperInvariant()))
            .Select(g => g.OrderBy(e => e.At).ToList())
            .Where(entries => !entries[^1].Restored && entries.Any(e => !e.Restored))
            .Select(entries => entries.First(e => !e.Restored))
            .ToList();
    }
}
