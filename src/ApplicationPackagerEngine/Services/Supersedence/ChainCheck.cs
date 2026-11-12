using System.Text;
using System.Xml.Linq;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>A version clause that the detection of a deployment type of the old application holds, read from the site.</summary>
public sealed record OldVersionClause(string DeploymentType, string Operator);

/// <summary>The old application with the version clauses of its detection.</summary>
public sealed record OldApplicationDetection(string Title, IReadOnlyList<OldVersionClause> Clauses);

/// <summary>
/// The chain check of the specification: before the publish, the detection of the old application is read and compared with the new
/// detection. It warns and never blocks.
/// </summary>
public static class ChainCheck
{
    public const string DetectionLine = "APDXML";

    private static bool IsMinimum(string op) =>
        op.Equals("GreaterEquals", StringComparison.OrdinalIgnoreCase) || op.Equals("GreaterThanOrEquals", StringComparison.OrdinalIgnoreCase)
        || op.Equals("GreaterThanOrEqual", StringComparison.OrdinalIgnoreCase);

    private static bool IsExact(string op) =>
        op.Equals("Equals", StringComparison.OrdinalIgnoreCase) || op.Equals("IsEquals", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The operators of the version clauses of one deployment type, read from its stored XML: every expression whose operand is a
    /// setting reference of the data type Version. A document that cannot be read gives no clause.
    /// </summary>
    public static IReadOnlyList<string> VersionOperators(string? deploymentTypeXml)
    {
        var operators = new List<string>();
        Collect(deploymentTypeXml, operators, 0);
        return operators;
    }

    private static void Collect(string? xml, List<string> operators, int depth)
    {
        if (string.IsNullOrWhiteSpace(xml) || depth > 2) return;
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return; }
        // The detection method is stored as XML text inside an argument of the installer, so it is read as a document of its own.
        foreach (var holder in document.Descendants().Where(e => !e.HasElements && e.Value.Contains("<EnhancedDetectionMethod", StringComparison.Ordinal)))
            Collect(holder.Value, operators, depth + 1);
        foreach (var expression in document.Descendants().Where(e => e.Name.LocalName == "Expression"))
        {
            var op = expression.Elements().FirstOrDefault(e => e.Name.LocalName == "Operator")?.Value;
            if (string.IsNullOrWhiteSpace(op)) continue;
            var operands = expression.Elements().FirstOrDefault(e => e.Name.LocalName == "Operands");
            var isVersion = operands?.Descendants().Any(e => e.Name.LocalName is "SettingReference" or "ConstantValue"
                && string.Equals((string?)e.Attribute("DataType"), "Version", StringComparison.OrdinalIgnoreCase)) == true;
            if (isVersion) operators.Add(op.Trim());
        }
    }

    /// <summary>The version clauses of the new detection that still say "this version or newer" after the rule was written.</summary>
    public static IReadOnlyList<string> MinimumClauses(StageManifest manifest)
    {
        var found = new List<string>();
        void Walk(StageDetection d)
        {
            if (d.Type.Equals("Compound", StringComparison.OrdinalIgnoreCase)) { foreach (var c in d.Clauses ?? []) Walk(c); return; }
            var versionClause = d.Type.Equals("File", StringComparison.OrdinalIgnoreCase)
                ? string.IsNullOrWhiteSpace(d.PropertyType) || d.PropertyType.Equals("Version", StringComparison.OrdinalIgnoreCase)
                : d.Type.Equals("RegistryKeyValue", StringComparison.OrdinalIgnoreCase)
                  && (d.PropertyType?.Equals("Version", StringComparison.OrdinalIgnoreCase) == true
                      || (d.ValueName ?? "").Contains("Version", StringComparison.OrdinalIgnoreCase));
            if (!versionClause) return;
            var op = string.IsNullOrWhiteSpace(d.Operator)
                ? (d.Type.Equals("File", StringComparison.OrdinalIgnoreCase) ? VersionRuleApplier.MinimumOperator : "IsEquals")
                : d.Operator;
            if (op.Equals(VersionRuleApplier.MinimumOperator, StringComparison.OrdinalIgnoreCase)) found.Add(VersionRuleApplier.Describe(d));
        }
        Walk(manifest.Detection);
        foreach (var type in manifest.DeploymentTypes ?? []) if (type.Detection is { } own) Walk(own);
        return found;
    }

    /// <summary>
    /// The warnings: (1) Replace and uninstall with Exact, and the old application still has a GreaterEquals version clause, so its
    /// uninstall is reported as failed; (2) the old application and the new one disagree (an Exact clause against Minimum, or the
    /// reverse); (3) the new application keeps a GreaterEquals version clause while the mode is Replace and uninstall.
    /// </summary>
    public static IReadOnlyList<string> Evaluate(
        string newTitle, VersionRule newRule, SupersedenceMode mode, StageManifest newManifest, OldApplicationDetection old)
    {
        var warnings = new List<string>();
        var oldMinimum = old.Clauses.Where(c => IsMinimum(c.Operator)).ToList();
        var oldExact = old.Clauses.Where(c => IsExact(c.Operator)).ToList();

        if (mode == SupersedenceMode.ReplaceAndUninstall && newRule == VersionRule.Exact)
            foreach (var clause in oldMinimum)
                warnings.Add($"'{old.Title}' ({clause.DeploymentType}) has a version clause with GreaterEquals, so its uninstall will still be reported as failed after the uninstall. Rewrite its clause to Exact first.");

        if (newRule == VersionRule.Minimum && oldExact.Count > 0)
            warnings.Add($"'{old.Title}' has an Exact version clause and '{newTitle}' is published with Minimum, so the chain is inconsistent.");
        if (newRule == VersionRule.Exact && oldMinimum.Count > 0 && mode != SupersedenceMode.ReplaceAndUninstall)
            warnings.Add($"'{old.Title}' has a GreaterEquals version clause and '{newTitle}' is published with Exact, so the chain is inconsistent.");

        if (mode == SupersedenceMode.ReplaceAndUninstall)
            foreach (var clause in MinimumClauses(newManifest))
                warnings.Add($"'{newTitle}' keeps a GreaterEquals version clause ({clause}), so the next uninstall through supersedence will be reported as failed.");
        return warnings;
    }

    /// <summary>Reads the lines <c>APDXML|b64 deployment type name|b64 XML</c> of the detection script.</summary>
    public static OldApplicationDetection ParseDetection(string title, string? output)
    {
        var clauses = new List<OldVersionClause>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length != 3 || parts[0] != DetectionLine) continue;
            string Decode(string value)
            {
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
                catch (FormatException) { return ""; }
            }
            var name = Decode(parts[1]);
            foreach (var op in VersionOperators(Decode(parts[2]))) clauses.Add(new OldVersionClause(name, op));
        }
        return new OldApplicationDetection(title, clauses);
    }

    /// <summary>The deployment types of an application with their stored XML, for <see cref="ParseDetection"/>.</summary>
    public static string BuildDetectionScript(string siteCode, string? providerMachine, string applicationName)
    {
        var sb = new StringBuilder();
        MecmTarget.AppendCmPreamble(sb, siteCode, providerMachine);
        sb.AppendLine($"foreach ($chainDt in @(Get-CMDeploymentType -ApplicationName '{PowerShellLiteral.EscapeSingleQuoted(applicationName)}' -DisableWildcardHandling)) {{");
        sb.AppendLine("    $chainName = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$chainDt.LocalizedDisplayName))");
        sb.AppendLine("    $chainXml = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$chainDt.SDMPackageXML))");
        sb.AppendLine($"    Write-Host ('{DetectionLine}|{{0}}|{{1}}' -f $chainName, $chainXml)");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
