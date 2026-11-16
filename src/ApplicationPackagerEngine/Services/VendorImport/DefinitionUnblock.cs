using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>What a piece of code in a definition is: a script, a command, or a program or file path that is never unblocked.</summary>
public enum CodeKind { Script, Command, Permanent }

/// <summary>One piece of code of a definition: where it is, its kind, and its text (empty for a permanent item, which has no text to show).</summary>
public sealed record CodeItem(string Name, CodeKind Kind, string Text);

/// <summary>
/// The unblock of a script or a command (step 3 of the import, "Code in a definition", rule 2 and 3). A definition that carries only scripts
/// and commands can be unblocked, one definition at a time, after the administrator saw the full text of every piece and its SHA-256. A
/// definition with a path to a local file or a bootstrapper that runs on the administrator's computer stays blocked. The digest covers the
/// text of every piece and every setting that a piece uses, so an update that changes one of them blocks the definition again.
/// </summary>
public static class DefinitionUnblock
{
    /// <summary>The code of the definition with its kind and text; the names are those of <see cref="DefinitionCode.Find"/>.</summary>
    public static IReadOnlyList<CodeItem> Analyze(AppDefinition d)
    {
        var items = new List<CodeItem>();
        if (d.PostInstallScript is not null) items.Add(new("PostInstallScript", CodeKind.Script, d.PostInstallScript.Script));
        if (!string.IsNullOrWhiteSpace(d.UninstallCommand)) items.Add(new("UninstallCommand", CodeKind.Command, d.UninstallCommand));
        if (d.ExpandInstallArgs && !string.IsNullOrWhiteSpace(d.InstallArgs)) items.Add(new("InstallArgs with ExpandInstallArgs", CodeKind.Command, d.InstallArgs));
        // The wrapper deletes the folder that ExtractDirName names and runs the program that InnerSetupPath names, as the system account: not a command to unblock.
        if (d.ExtractSetup is not null) items.Add(new("ExtractSetup (ExtractArgs)", CodeKind.Permanent, ""));
        if (d.BootstrapperLayout is not null) items.Add(new("BootstrapperLayout", CodeKind.Permanent, ""));
        foreach (var setting in d.InstallerSettings ?? [])
        {
            if (!string.IsNullOrWhiteSpace(setting.CommandFragment)) items.Add(new($"InstallerSettings[{setting.Key}].CommandFragment", CodeKind.Command, setting.CommandFragment));
            if (!string.IsNullOrWhiteSpace(setting.UninstallFragment)) items.Add(new($"InstallerSettings[{setting.Key}].UninstallFragment", CodeKind.Command, setting.UninstallFragment));
        }
        foreach (var variant in d.PayloadVariants ?? [])
            if (!string.IsNullOrWhiteSpace(variant.UninstallCommand)) items.Add(new($"PayloadVariants[{variant.NameSuffix}].UninstallCommand", CodeKind.Command, variant.UninstallCommand));
        if (d.LocalSource is not null) items.Add(new("LocalSource", CodeKind.Permanent, ""));
        if (d.LocalInstaller is not null) items.Add(new("LocalInstaller", CodeKind.Permanent, ""));
        if (d.SideFiles is { Length: > 0 }) items.Add(new("SideFiles", CodeKind.Permanent, ""));
        if (d.TargetSecret is not null) items.Add(new("TargetSecret", CodeKind.Permanent, ""));
        if (d.Odt is not null) items.Add(new("Odt", CodeKind.Permanent, ""));
        if (d.ZipPayload is not null) items.Add(new("ZipPayload", CodeKind.Permanent, ""));
        AddDetection(d.Detection, "Detection", items);
        foreach (var variant in d.PayloadVariants ?? []) AddDetection(variant.Detection, $"PayloadVariants[{variant.NameSuffix}].Detection", items);
        return items;
    }

    private static void AddDetection(DetectionConfig? detection, string where, List<CodeItem> items)
    {
        if (detection is null) return;
        if (!string.IsNullOrWhiteSpace(detection.ScriptText) || detection.Type.Equals("Script", StringComparison.OrdinalIgnoreCase))
            items.Add(new($"{where}.ScriptText", CodeKind.Script, $"[language {detection.ScriptLanguage}, 64-bit {detection.Is64Bit}]{Environment.NewLine}{detection.ScriptText ?? ""}"));
        foreach (var (clause, index) in (detection.Clauses ?? []).Select((c, i) => (c, i)))
            AddDetection(clause, $"{where}.Clauses[{index}]", items);
    }

    /// <summary>True when the definition carries code and every piece of it is a script or a command.</summary>
    public static bool CanUnblock(AppDefinition d)
    {
        var items = Analyze(d);
        return items.Count > 0 && items.All(i => i.Kind != CodeKind.Permanent);
    }

    /// <summary>
    /// True when a blocked definition is blocked only by scripts and commands and the record lists the digest of exactly this code
    /// (<c>Id:Digest</c>), so the store may write it.
    /// </summary>
    public static bool IsUnblocked(ValidatedDefinition definition, IEnumerable<string> unblockedDigests) =>
        definition.Findings.Where(f => f.Severity == FindingSeverity.Blocking).All(f => f.Code == "CarriesCode")
        && CanUnblock(definition.Definition)
        && unblockedDigests.Contains(definition.Id + ":" + Digest(definition.Definition), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The SHA-256 (hex) of every script and command of the definition (name and text) and of every setting (the whole InstallerSettings list
    /// with its defaults, options, value maps and conditions, and the condition of the post-install script). A change of any of them changes
    /// the digest.
    /// </summary>
    public static string Digest(AppDefinition d)
    {
        var text = new StringBuilder();
        foreach (var item in Analyze(d).Where(i => i.Kind != CodeKind.Permanent))
            text.Append(item.Name).Append('\0').Append(item.Text).Append('\0');
        text.Append("ExpandInstallArgs=").Append(d.ExpandInstallArgs).Append('\0');
        text.Append("PostInstallScript.When=").Append(d.PostInstallScript?.WhenSetting).Append('=').Append(d.PostInstallScript?.WhenValue).Append('\0');
        text.Append(JsonSerializer.Serialize(d.InstallerSettings ?? []));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The name and the SHA-256 of every script and command, and the digest of the definition; no text.</summary>
    public static string Summary(AppDefinition d)
    {
        var text = new StringBuilder();
        foreach (var item in Analyze(d).Where(i => i.Kind != CodeKind.Permanent))
            text.AppendLine($"{item.Kind}: {item.Name}, {item.Text.Length} characters, SHA-256 {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Text)))}");
        text.AppendLine("Digest of the definition: " + Digest(d));
        return text.ToString();
    }

    /// <summary>The full text that the unblock window shows: each script and command with its name, its SHA-256 and the digest of the definition.</summary>
    public static string Describe(AppDefinition d)
    {
        var text = new StringBuilder();
        foreach (var item in Analyze(d).Where(i => i.Kind != CodeKind.Permanent))
        {
            text.AppendLine($"{item.Kind}: {item.Name}");
            text.AppendLine("SHA-256 " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Text))));
            text.AppendLine(item.Text);
            text.AppendLine();
        }
        text.AppendLine("Digest of the definition (the texts and every setting that they use): " + Digest(d));
        return text.ToString();
    }
}
