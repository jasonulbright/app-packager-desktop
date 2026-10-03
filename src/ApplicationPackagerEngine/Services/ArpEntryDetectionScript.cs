using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The ConfigMgr and Intune detection script for an Add/Remove Programs entry.
/// The script opens each registry view explicitly, so a 32-bit and a 64-bit
/// host read the same keys. It writes 'Installed' when an entry matches with a
/// DisplayVersion at or above Version, writes nothing otherwise, and exits 0.
/// A subkey that cannot be read is skipped; an Uninstall key that cannot be
/// listed stops the script with an error, which ConfigMgr and Intune report as
/// a failed detection, not as a missing product. Versions compare as four
/// numbers, a missing part as zero; a DisplayVersion that is not up to four
/// numbers never matches. Names and the publisher compare without case.
/// </summary>
public static partial class ArpEntryDetectionScript
{
    [GeneratedRegex(@"^\d+(\.\d+){0,3}$")]
    private static partial Regex FourPartVersion();

    public static string Build(ArpEntryDetection entry)
    {
        if (!string.IsNullOrEmpty(entry.Type) && !entry.Type.Equals(ArpEntryDetection.TypeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The entry type '{entry.Type}' is not ArpEntry.");
        var exactName = entry.DisplayName ?? "";
        var prefix = entry.DisplayNamePrefix ?? "";
        var suffix = entry.DisplayNameSuffix ?? "";
        var publisher = entry.Publisher ?? "";
        if (exactName.Length == 0 && prefix.Length == 0)
            throw new InvalidOperationException("The entry names no DisplayName or DisplayNamePrefix, so the script cannot find the Add/Remove Programs entry.");
        var version = (entry.Version ?? "").Trim();
        var parts = version.Split('.').ToList();
        if (!FourPartVersion().IsMatch(version) || parts.Any(p => !int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
            throw new InvalidOperationException($"The entry compares DisplayVersion with '{version}', which is not a version of up to four numbers.");
        while (parts.Count < 4) parts.Add("0");
        var views = (entry.View ?? "").ToLowerInvariant() switch
        {
            "32" => new[] { "Registry32" },
            "64" => ["Registry64"],
            "both" => ["Registry32", "Registry64"],
            _ => throw new InvalidOperationException($"The entry view '{entry.View}' is not 32, 64 or Both.")
        };

        var lines = new List<string>
        {
            $"$minimum = [version]'{string.Join(".", parts)}'",
            $"foreach ($view in @({string.Join(", ", views.Select(v => "[Microsoft.Win32.RegistryView]::" + v))})) {{",
            "    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)",
            "    $uninstall = $base.OpenSubKey('SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall')",
            "    if (-not $uninstall) { continue }",
            "    foreach ($name in $uninstall.GetSubKeyNames()) {",
            "        $key = $null",
            "        try {",
            "            $key = $uninstall.OpenSubKey($name)",
            "            if (-not $key) { continue }",
            "            $displayName = [string]$key.GetValue('DisplayName')",
            "            $publisher = [string]$key.GetValue('Publisher')",
            "            $displayVersion = [string]$key.GetValue('DisplayVersion')",
            "            $windowsInstaller = $key.GetValue('WindowsInstaller')",
            "        }",
            "        catch { continue }",
            "        finally { if ($key) { $key.Close() } }"
        };
        if (exactName.Length > 0)
        {
            lines.Add($"        if (-not [string]::Equals($displayName, {Quote(exactName)}, [StringComparison]::OrdinalIgnoreCase)) {{ continue }}");
        }
        else
        {
            lines.Add($"        if (-not $displayName.StartsWith({Quote(prefix)}, [StringComparison]::OrdinalIgnoreCase)) {{ continue }}");
            if (suffix.Length > 0) lines.Add($"        if (-not $displayName.EndsWith({Quote(suffix)}, [StringComparison]::OrdinalIgnoreCase)) {{ continue }}");
        }
        if (publisher.Length > 0) lines.Add($"        if (-not [string]::Equals($publisher, {Quote(publisher)}, [StringComparison]::OrdinalIgnoreCase)) {{ continue }}");
        if (entry.WindowsInstaller) lines.Add("        if ([string]$windowsInstaller -ne '1') { continue }");
        lines.AddRange(
        [
            "        $parts = @($displayVersion.Trim().Split('.'))",
            "        if ($parts.Count -gt 4) { continue }",
            "        $numbers = @(foreach ($part in $parts) { $n = 0; if ([int]::TryParse($part, [ref]$n) -and $n -ge 0) { $n } })",
            "        if ($numbers.Count -ne $parts.Count) { continue }",
            "        while ($numbers.Count -lt 4) { $numbers += 0 }",
            "        if ((New-Object System.Version $numbers[0], $numbers[1], $numbers[2], $numbers[3]) -ge $minimum) { Write-Output 'Installed'; exit 0 }",
            "    }",
            "    $uninstall.Close()",
            "}",
            "exit 0"
        ]);
        return string.Join("\r\n", lines);
    }

    private static string Quote(string text) => "'" + PowerShellLiteral.EscapeSingleQuoted(text) + "'";
}
