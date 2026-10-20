using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The clipboard summary, the ConfigMgr JSON digest, and new CSV and HTML files under
/// &lt;data root&gt;\InstallerAnalysis\Reports. CSV cells are quoted and a cell a spreadsheet would
/// read as a formula starts with an apostrophe; HTML values are encoded and the page forbids
/// scripts. Facts that name .intunewin key material are dropped from every output.
/// </summary>
public sealed class InstallerReportWriter(string? folder = null, Func<DateTimeOffset>? clock = null) : IInstallerReportWriter
{
    private static readonly HashSet<string> KeyMaterial = new(StringComparer.OrdinalIgnoreCase)
    {
        "EncryptionKey", "MacKey", "InitializationVector", "Mac", "IV"
    };

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public string Folder => folder ?? InstallerAnalysisFolders.Reports();

    private static IEnumerable<AnalysisFact> Visible(IEnumerable<AnalysisFact> facts) => facts.Where(f => !KeyMaterial.Contains(f.Name));

    private static string Deployment(AnalysisReport r, string name) => r.Deployment.FirstOrDefault(f => f.Name == name)?.Value ?? "";

    public string Summary(AnalysisReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var s = new StringBuilder();
        void Line(string text) => s.Append(text).Append("\r\n");
        void Field(string name, string value, int width = 23) => Line("  " + (name + ":").PadRight(width) + " " + value);

        Line("Installer Analysis: " + r.FileName);
        Line(new string('-', 50));
        Line("Type:         " + (r.TypeName.Length > 0 ? r.TypeName : r.Type));
        Line("Architecture: " + r.Architecture);
        Line("Size:         " + InstallerAnalysisService.FormatBytes(r.Size));
        Line("SHA-256:      " + r.Sha256);
        if (r.Signature is { } sig)
            Line("Signed:       " + sig.Status + (sig.Subject.Length > 0 ? " (" + sig.Subject + ")" : "") + (sig.Timestamped ? ", timestamped" : ""));
        if (r.RequestedExecutionLevel.Length > 0) Line("Elevation:    " + r.RequestedExecutionLevel + " (manifest requestedExecutionLevel)");

        Line("");
        Line("Deployment fields:");
        foreach (var f in r.Deployment.Where(f => f.Value.Length > 0 && f.Name != "Notes"))
            Field(f.Name, f.Value + (f.Confidence == "Predicted" ? "  (predicted)" : ""));
        if (r.DetectionHint.Length > 0) Field("Detection", r.DetectionHint);

        if (r.Modes.Count > 0)
        {
            Line("");
            Line("Install modes:");
            foreach (var m in r.Modes)
            {
                Line("  " + m.Name + (m.IsDefault ? " (default)" : "") + ":");
                if (m.InstallCommand.Length > 0) Field("Install", m.InstallCommand, 21);
                if (m.InstallFolder.Length > 0) Field("Folder", m.InstallFolder, 21);
                if (m.UninstallCommand.Length > 0) Field("Uninstall", m.UninstallCommand, 21);
                if (m.ArpKey.Length > 0) Field("Key", m.ArpKey + (m.View.Length > 0 ? " (" + m.View + "-bit view)" : ""), 21);
            }
        }

        if (r.PostPatch is { } p)
        {
            Line("");
            Line("Effective post-patch detection target:");
            Field("Patched product", p.DisplayName);
            Field("ProductCode", p.ProductCode);
            Field("DisplayVersion", p.DisplayVersion);
            if (p.BaseVersion.Length > 0) Field("BaseDisplayVersion", p.BaseVersion);
            Field("UninstallRegistryKey", p.ArpKey);
            Field("Explanation", p.Explanation);
        }

        if (r.MsiProperties.Count > 0)
        {
            Line("");
            Line("MSI properties:");
            foreach (var name in new[] { "ProductCode", "UpgradeCode", "ProductVersion", "Manufacturer", "ALLUSERS", "MSIINSTALLPERUSER" })
                if (r.MsiProperties.FirstOrDefault(f => f.Name == name) is { } f) Field(name, f.Value);
            Field("Rows", r.MsiProperties.Count.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var block in r.Metadata)
        {
            Line("");
            Line(block.Title + ":");
            foreach (var f in Visible(block.Facts)) Field(f.Name, f.Value);
        }

        if (r.Notes.Count > 0)
        {
            Line("");
            Line("Notes:");
            foreach (var n in r.Notes) Line("  " + n);
        }
        return s.ToString();
    }

    public string Json(AnalysisReport r, bool includeKeyMaterial)
    {
        ArgumentNullException.ThrowIfNull(r);
        var msi = new JsonObject();
        foreach (var f in r.MsiProperties) msi[f.Name] = f.Value;
        var package = new JsonObject();
        foreach (var block in r.Metadata)
        {
            var o = new JsonObject();
            foreach (var f in Visible(block.Facts)) o[f.Name] = f.Value;
            package[block.Title] = o;
        }
        var modes = new JsonArray();
        foreach (var m in r.Modes)
            modes.Add(new JsonObject
            {
                ["Name"] = m.Name, ["Default"] = m.IsDefault, ["Switch"] = m.Switch, ["InstallCommand"] = m.InstallCommand,
                ["InstallFolder"] = m.InstallFolder, ["UninstallCommand"] = m.UninstallCommand, ["UninstallRegistryKey"] = m.ArpKey,
                ["Hive"] = m.Hive, ["View"] = m.View, ["Context"] = m.Context
            });
        var root = new JsonObject
        {
            ["SchemaVersion"] = "1.0",
            ["Source"] = new JsonObject { ["FileName"] = r.FileName, ["FileSize"] = r.Size, ["SHA256"] = r.Sha256 },
            ["Application"] = new JsonObject
            {
                ["DisplayName"] = Deployment(r, "DisplayName"),
                ["DisplayVersion"] = Deployment(r, "DisplayVersion"),
                ["Publisher"] = Deployment(r, "Publisher"),
                ["Architecture"] = r.Architecture,
                ["InstallerType"] = r.Type,
                ["ProductCode"] = Deployment(r, "ProductCode"),
                ["UpgradeCode"] = Deployment(r, "UpgradeCode")
            },
            ["Deployment"] = new JsonObject
            {
                ["InstallCommand"] = Deployment(r, "InstallCommand"),
                ["UninstallCommand"] = Deployment(r, "UninstallCommand"),
                ["InstallContext"] = Deployment(r, "InstallContext"),
                ["Notes"] = Deployment(r, "Notes")
            },
            ["Detection"] = new JsonObject
            {
                ["Hint"] = r.DetectionHint,
                ["UninstallRegistryKey"] = Deployment(r, "UninstallRegistryKey"),
                ["UninstallRegistryKeyNote"] = string.Join(" ", r.Notes)
            },
            ["Signature"] = r.Signature is null ? null : new JsonObject
            {
                ["Status"] = r.Signature.Status, ["Subject"] = r.Signature.Subject, ["Issuer"] = r.Signature.Issuer,
                ["Thumbprint"] = r.Signature.Thumbprint, ["Timestamped"] = r.Signature.Timestamped
            },
            ["InstallModes"] = modes,
            ["PostPatch"] = r.PostPatch is null ? null : new JsonObject
            {
                ["ProductCode"] = r.PostPatch.ProductCode, ["DisplayName"] = r.PostPatch.DisplayName, ["DisplayVersion"] = r.PostPatch.DisplayVersion,
                ["BaseDisplayVersion"] = r.PostPatch.BaseVersion, ["UninstallRegistryKey"] = r.PostPatch.ArpKey, ["Explanation"] = r.PostPatch.Explanation
            },
            ["Raw"] = new JsonObject { ["MsiProperties"] = msi, ["PackageMetadata"] = package }
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<string[]> Rows(AnalysisReport r, IReadOnlyList<RegistryWrite> reg)
    {
        yield return ["Section", "Name", "Value", "Confidence", "Source"];
        yield return ["Source", "FileName", r.FileName, "", ""];
        yield return ["Source", "Size", r.Size.ToString(CultureInfo.InvariantCulture), "", ""];
        yield return ["Source", "SHA256", r.Sha256, "", ""];
        yield return ["Source", "Type", r.Type, "", ""];
        yield return ["Source", "Architecture", r.Architecture, "", ""];
        if (r.Signature is { } sig)
        {
            yield return ["Signature", "Status", sig.Status, "", ""];
            yield return ["Signature", "Subject", sig.Subject, "", ""];
            yield return ["Signature", "Thumbprint", sig.Thumbprint, "", ""];
            yield return ["Signature", "Timestamped", sig.Timestamped ? "yes" : "no", "", ""];
        }
        foreach (var f in r.Deployment) yield return ["Deployment", f.Name, f.Value, f.Confidence, f.Source];
        if (r.DetectionHint.Length > 0) yield return ["Deployment", "Detection", r.DetectionHint, "", ""];
        foreach (var m in r.Modes)
        {
            var section = "Install mode " + m.Name + (m.IsDefault ? " (default)" : "");
            yield return [section, "InstallCommand", m.InstallCommand, "", ""];
            yield return [section, "InstallFolder", m.InstallFolder, "", ""];
            yield return [section, "UninstallCommand", m.UninstallCommand, "", ""];
            yield return [section, "UninstallRegistryKey", m.ArpKey, "", m.View];
        }
        if (r.PostPatch is { } p)
        {
            yield return ["Post-patch", "ProductCode", p.ProductCode, "", ""];
            yield return ["Post-patch", "DisplayVersion", p.DisplayVersion, "", ""];
            yield return ["Post-patch", "UninstallRegistryKey", p.ArpKey, "", ""];
            yield return ["Post-patch", "Explanation", p.Explanation, "", ""];
        }
        foreach (var block in r.Metadata)
            foreach (var f in Visible(block.Facts)) yield return [block.Title, f.Name, f.Value, f.Confidence, f.Source];
        foreach (var f in r.MsiProperties) yield return ["MSI property", f.Name, f.Value, f.Confidence, f.Source];
        foreach (var n in r.Notes) yield return ["Note", "", n, "", ""];
    }

    private static readonly string[] RegistryColumns = ["Origin", "Hive", "Key", "Name", "Value", "Type", "Action", "View", "Section", "Condition"];

    private static string[] RegistryRow(RegistryWrite w) => [w.Origin, w.Hive, w.Key, w.Name, w.Value, w.ValueType, w.Action, w.View, w.Section, w.Condition];

    public string WriteCsv(AnalysisReport r, IReadOnlyList<RegistryWrite> reg)
    {
        ArgumentNullException.ThrowIfNull(r);
        reg ??= [];
        var text = new StringBuilder();
        foreach (var row in Rows(r, reg)) text.Append(string.Join(",", row.Select(ReportFile.Cell))).Append("\r\n");
        if (reg.Count > 0)
        {
            text.Append("\r\n").Append(string.Join(",", RegistryColumns.Select(ReportFile.Cell))).Append("\r\n");
            foreach (var w in reg) text.Append(string.Join(",", RegistryRow(w).Select(ReportFile.Cell))).Append("\r\n");
        }
        return Write("csv", text.ToString());
    }

    public string WriteHtml(AnalysisReport r, IReadOnlyList<PayloadEntry> payload, IReadOnlyList<RegistryWrite> reg)
    {
        ArgumentNullException.ThrowIfNull(r);
        payload ??= [];
        reg ??= [];
        static string E(string v) => WebUtility.HtmlEncode(v);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>Installer analysis</title><style>")
            .Append("body{font-family:\"Segoe UI\",Arial,sans-serif;margin:20px}table{border-collapse:collapse;width:100%;margin-bottom:24px}")
            .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #ccc;vertical-align:top;word-break:break-word}th{border-bottom:2px solid #333}")
            .Append("</style></head><body>\r\n<h1>Installer analysis: ").Append(E(r.FileName)).Append("</h1>\r\n<p>Generated ")
            .Append(E(_clock().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))).Append(".</p>\r\n");

        void Table(string title, IEnumerable<string[]> rows)
        {
            html.Append("<h2>").Append(E(title)).Append("</h2>\r\n<table>");
            var first = true;
            foreach (var row in rows)
            {
                html.Append("<tr>");
                foreach (var cell in row) html.Append(first ? "<th>" : "<td>").Append(E(cell)).Append(first ? "</th>" : "</td>");
                html.Append("</tr>\r\n");
                first = false;
            }
            html.Append("</table>\r\n");
        }

        Table("Analysis", Rows(r, reg));
        if (payload.Count > 0)
            Table("Payload", new[] { new[] { "Path", "Size", "Kind", "Type" } }
                .Concat(payload.Select(p => new[] { p.Path, p.Size.ToString(CultureInfo.InvariantCulture), p.Kind, p.InnerType })));
        if (reg.Count > 0)
            Table("Registry settings", new[] { RegistryColumns }.Concat(reg.Select(RegistryRow)));
        html.Append("</body></html>\r\n");
        return Write("html", html.ToString());
    }

    private string Write(string extension, string content)
    {
        var target = Folder;
        var container = DataFolderLinks.ContainerOf(target, folder is null);
        if (DataFolderLinks.HasLink(target, container))
            throw new InstallerAnalysisException("The reports folder is a junction or symbolic link; nothing was written.");
        return ReportFile.WriteNew(target, "installer-analysis", extension, content, _clock());
    }
}
