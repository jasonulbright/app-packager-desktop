using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads and writes condition-templates.json beside the app config
/// (%LOCALAPPDATA%\AppPackager). A missing or malformed file falls back to
/// the built-in defaults so the Package phase never depends on the file
/// existing.
/// </summary>
public sealed class ConditionTemplateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public ConditionTemplateService(string? path = null)
    {
        _path = path ?? Path.Combine(AppDataPaths.Root, "condition-templates.json");
    }

    public string TemplatesPath => _path;

    /// <summary>The built-in condition template document.</summary>
    public static ConditionTemplateDocument Defaults() => new()
    {
        SchemaVersion = 1,
        Conditions =
        [
            new ConditionTemplate
            {
                Id = "cpu-arch",
                GlobalConditionName = "APKG - CPU Architecture",
                Kind = "Wql",
                RuleType = "CommonValue",
                DataType = "Integer",
                Namespace = @"root\cimv2",
                Class = "Win32_Processor",
                Property = "Architecture",
                // Win32_OperatingSystem.OSArchitecture returns a localized
                // string; the numeric processor architecture compares the
                // same on every OS language.
                Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["x64"] = "9",
                    ["ARM64"] = "12"
                },
                Description = "Win32_Processor.Architecture (9 = x64, 12 = ARM64)."
            },
            new ConditionTemplate
            {
                Id = "os-language",
                GlobalConditionName = "Operating System Language",
                Kind = "BuiltIn",
                RuleType = "OperatingSystemLanguage",
                // The site ships two conditions under this name; PlatformType
                // 1 is Windows, 2 is Mobile.
                PlatformType = 1
            },
            new ConditionTemplate
            {
                Id = "vpn-connected",
                GlobalConditionName = "APKG - VPN Connected",
                Kind = "Script",
                RuleType = "Boolean",
                DataType = "Boolean",
                AdapterPatterns = ["Zscaler", "Juniper", "PANGP", "Cisco AnyConnect", "Fortinet"],
                AliasPattern = "vpn",
                Description = "True when an IP-enabled network adapter description matches a configured VPN client pattern or an interface alias contains the alias pattern."
            }
        ]
    };

    /// <summary>
    /// The file document when present and parseable, otherwise the built-in
    /// defaults. <paramref name="onWarning"/> reports a fallback.
    /// </summary>
    public ConditionTemplateDocument Load(Action<string>? onWarning = null)
    {
        if (File.Exists(_path))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<ConditionTemplateDocument>(
                    File.ReadAllText(_path), JsonOptions);
                if (doc?.Conditions is { Length: > 0 }) return doc;
                onWarning?.Invoke($"Condition templates file has no Conditions; using built-in defaults: {_path}");
            }
            catch (Exception ex)
            {
                onWarning?.Invoke($"Condition templates file unreadable; using built-in defaults: {ex.Message}");
            }
        }
        return Defaults();
    }

    public string Save(ConditionTemplateDocument templates)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(templates, JsonOptions), Encoding.UTF8);
        return _path;
    }

    /// <summary>
    /// The script body a Script-kind template installs on the site: the
    /// template's own ScriptText when set, otherwise the generated VPN
    /// detection script.
    /// </summary>
    public static string ResolveScriptText(ConditionTemplate template)
    {
        if (template.ScriptText is { Length: > 0 })
            return string.Join("\r\n", template.ScriptText);
        return BuildVpnScriptText(template.AdapterPatterns ?? [], template.AliasPattern ?? "");
    }

    /// <summary>
    /// Builds the client-side detection script for the VPN-connected global
    /// condition. The script runs inside the CM agent's requirement
    /// evaluation, so it must emit exactly one boolean and nothing else on
    /// the output stream.
    /// </summary>
    public static string BuildVpnScriptText(IEnumerable<string> adapterPatterns, string aliasPattern)
    {
        var quoted = adapterPatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(PowerShellLiteral.SingleQuoted);

        var lines = new List<string>
        {
            $"$patterns = @({string.Join(", ", quoted)})",
            "$found = $false",
            "foreach ($adapter in @(Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter \"IPEnabled='True'\" -ErrorAction SilentlyContinue)) {",
            "    foreach ($p in $patterns) {",
            "        if ($adapter.Description -like ('*' + $p + '*')) { $found = $true }",
            "    }",
            "}"
        };

        if (!string.IsNullOrWhiteSpace(aliasPattern))
        {
            var aliasLike = "*" + PowerShellLiteral.EscapeSingleQuoted(aliasPattern) + "*";
            lines.AddRange(
            [
                "if (-not $found) {",
                "    foreach ($ip in @(Get-CimInstance -ClassName MSFT_NetIPAddress -Namespace 'root/StandardCimv2' -ErrorAction SilentlyContinue)) {",
                $"        if ($ip.InterfaceAlias -like '{aliasLike}') {{ $found = $true }}",
                "    }",
                "}"
            ]);
        }

        lines.Add("$found");
        return string.Join("\r\n", lines);
    }
}
