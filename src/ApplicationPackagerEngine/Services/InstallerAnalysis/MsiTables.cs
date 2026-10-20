using System.Globalization;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>The tables of a Windows Installer database that the analysis reads.</summary>
internal static class MsiTables
{
    private static readonly Dictionary<int, string> RootNames = new()
    {
        [-1] = "HKMU", [0] = "HKCR", [1] = "HKCU", [2] = "HKLM", [3] = "HKU"
    };

    /// <summary>Every Property table row in table order.</summary>
    public static (List<KeyValuePair<string, string>> Rows, bool Truncated) Properties(MsiDatabase db, int maxRows, CancellationToken ct)
    {
        var rows = new List<KeyValuePair<string, string>>();
        if (!db.HasTable("Property")) return (rows, false);
        var (_, truncated) = db.Query("SELECT `Property`, `Value` FROM `Property`", r =>
        {
            rows.Add(new(r.String(1) ?? "", r.String(2) ?? ""));
            return true;
        }, maxRows, ct);
        return (rows, truncated);
    }

    /// <summary>The Registry and RemoveRegistry rows with the owning component, its condition and its view.</summary>
    public static (List<RegistryWrite> Rows, bool Truncated) Registry(MsiDatabase db, int maxRows, CancellationToken ct)
    {
        var rows = new List<RegistryWrite>();
        var components = new Dictionary<string, (int Attributes, string Condition)>(StringComparer.Ordinal);
        var truncated = false;
        if (db.HasTable("Component"))
        {
            var (_, cut) = db.Query("SELECT `Component`, `Attributes`, `Condition` FROM `Component`", r =>
            {
                components[r.String(1) ?? ""] = (r.Integer(2) ?? 0, r.String(3) ?? "");
                return true;
            }, Math.Max(maxRows, 1) * 4, ct);
            truncated |= cut;
        }

        string ViewOf(string component) =>
            components.TryGetValue(component, out var c) ? ((c.Attributes & 256) != 0 ? "64" : "32") : "";
        string ConditionOf(string component) =>
            components.TryGetValue(component, out var c) ? c.Condition : "";
        string RootOf(int? root) =>
            root is { } r && RootNames.TryGetValue(r, out var name) ? name : "root " + root?.ToString(CultureInfo.InvariantCulture);

        if (db.HasTable("Registry"))
        {
            var (_, cut) = db.Query("SELECT `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`", r =>
            {
                var name = r.String(3);
                var (type, text, action) = DecodeValue(r.String(4), name);
                var component = r.String(5) ?? "";
                if (action != "write" && name is "+" or "-" or "*") name = "";
                rows.Add(new RegistryWrite("MsiRegistryTable", RootOf(r.Integer(1)), r.String(2) ?? "", name ?? "", text, type, ConditionOf(component))
                {
                    Action = action,
                    View = ViewOf(component),
                    Section = component
                });
                return true;
            }, maxRows, ct);
            truncated |= cut;
        }
        if (db.HasTable("RemoveRegistry") && rows.Count < maxRows)
        {
            var (_, cut) = db.Query("SELECT `Root`, `Key`, `Name`, `Component_` FROM `RemoveRegistry`", r =>
            {
                var name = r.String(3) ?? "";
                var component = r.String(4) ?? "";
                rows.Add(new RegistryWrite("MsiRegistryTable", RootOf(r.Integer(1)), r.String(2) ?? "", name == "-" ? "" : name, "", "", ConditionOf(component))
                {
                    Action = name == "-" ? "delete key" : "delete value",
                    View = ViewOf(component),
                    Section = component
                });
                return true;
            }, maxRows - rows.Count, ct);
            truncated |= cut;
        }
        return (rows, truncated);
    }

    /// <summary>
    /// Splits a Registry table Value into its type, its text and the action. #x is binary,
    /// #% expandable, ## a literal leading #, # followed by digits a DWORD, [~] a multi-string.
    /// A null Value with Name +, - or * only creates or removes the key.
    /// </summary>
    public static (string Type, string Text, string Action) DecodeValue(string? value, string? name)
    {
        if (value is null)
        {
            return name switch
            {
                "+" => ("", "", "create key"),
                "-" => ("", "", "delete key on uninstall"),
                "*" => ("", "", "create key; delete on uninstall"),
                _ => ("REG_SZ", "", "write")
            };
        }
        if (value.StartsWith("#x", StringComparison.Ordinal)) return ("REG_BINARY", value[2..], "write");
        if (value.StartsWith("#%", StringComparison.Ordinal)) return ("REG_EXPAND_SZ", value[2..], "write");
        if (value.StartsWith("##", StringComparison.Ordinal)) return ("REG_SZ", value[1..], "write");
        var dword = Regex.Match(value, @"^#(-?\d+)$");
        if (dword.Success) return ("REG_DWORD", dword.Groups[1].Value, "write");
        if (value.Contains("[~]", StringComparison.Ordinal)) return ("REG_MULTI_SZ", value, "write");
        return ("REG_SZ", value, "write");
    }

    /// <summary>The binary streams of the database (embedded cabinets, Binary and Icon entries), with their sizes.</summary>
    public static (List<(string Name, long Size)> Rows, bool Truncated) Streams(MsiDatabase db, int maxRows, CancellationToken ct)
    {
        var rows = new List<(string, long)>();
        var (_, truncated) = db.Query("SELECT `Name`, `Data` FROM `_Streams`", r =>
        {
            rows.Add((r.String(1) ?? "", r.DataSize(2)));
            return true;
        }, maxRows, ct);
        return (rows, truncated);
    }

    /// <summary>Copies one named stream to <paramref name="target"/>.</summary>
    public static bool CopyStream(MsiDatabase db, string name, Stream target, long maxBytes, CancellationToken ct)
    {
        var found = false;
        db.Query("SELECT `Name`, `Data` FROM `_Streams`", r =>
        {
            if (!string.Equals(r.String(1), name, StringComparison.Ordinal)) return true;
            r.CopyStream(2, target, maxBytes, ct);
            found = true;
            return false;
        }, int.MaxValue, ct);
        return found;
    }

    /// <summary>The Media table cabinets that live outside the database (no leading #).</summary>
    public static List<string> ExternalCabinets(MsiDatabase db, CancellationToken ct)
    {
        var result = new List<string>();
        if (!db.HasTable("Media")) return result;
        db.Query("SELECT `Cabinet` FROM `Media`", r =>
        {
            var cabinet = r.String(1);
            if (!string.IsNullOrEmpty(cabinet) && !cabinet.StartsWith('#')) result.Add(cabinet);
            return true;
        }, 10_000, ct);
        return result;
    }

    /// <summary>The MsiPatchMetadata rows of a patch opened as a patch file.</summary>
    public static Dictionary<string, string> PatchMetadata(MsiDatabase db, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!db.HasTable("MsiPatchMetadata")) return result;
        db.Query("SELECT `Company`, `Property`, `Value` FROM `MsiPatchMetadata`", r =>
        {
            var property = r.String(2);
            if (!string.IsNullOrEmpty(property) && !result.ContainsKey(property)) result[property] = r.String(3) ?? "";
            return true;
        }, 10_000, ct);
        return result;
    }
}

/// <summary>An <see cref="IMsiInspector"/> over msi.dll, so the analysis holds no COM objects.</summary>
internal sealed class NativeMsiInspector : IMsiInspector
{
    public MsiProperties ReadProperties(string msiPath)
    {
        using var db = MsiDatabase.Open(msiPath);
        var (rows, _) = MsiTables.Properties(db, 100_000, CancellationToken.None);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in rows) map.TryAdd(name, value);
        return new MsiProperties
        {
            ProductName = map.GetValueOrDefault("ProductName", ""),
            ProductVersion = map.GetValueOrDefault("ProductVersion", ""),
            Manufacturer = map.GetValueOrDefault("Manufacturer", ""),
            ProductCode = map.GetValueOrDefault("ProductCode", ""),
            UpgradeCode = map.GetValueOrDefault("UpgradeCode", ""),
            AllUsers = map.TryGetValue("ALLUSERS", out var allUsers) ? allUsers : null,
            MsiInstallPerUser = map.GetValueOrDefault("MSIINSTALLPERUSER", "")
        };
    }

    public string ReadTemplateArchitecture(string msiPath) =>
        MsiDatabase.ReadSummary(msiPath).TryGetValue(7, out var template) ? template.Split(';')[0].Trim() : "";
}
