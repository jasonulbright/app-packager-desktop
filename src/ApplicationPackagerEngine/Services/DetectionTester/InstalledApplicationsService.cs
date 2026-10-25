using System.Globalization;
using Microsoft.Win32;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The Add or Remove Programs entries of this computer: HKLM in the 64-bit view, HKLM in
/// the 32-bit view (WOW6432Node) and HKCU. An entry or hive that cannot be read is skipped.
/// </summary>
public sealed class InstalledApplicationsService(IRegistryProvider? registry = null) : IInstalledApplicationsService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Wow64UninstallPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly IRegistryProvider _registry = registry ?? WindowsRegistryProvider.Instance;

    private static readonly (RegistryHive Hive, RegistryView View, string Source, string DisplayPath)[] Hives =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM 64-bit", $@"HKLM\{UninstallPath}"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM 32-bit", $@"HKLM\{Wow64UninstallPath}"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU", $@"HKCU\{UninstallPath}")
    ];

    public IReadOnlyList<InstalledApplication> Enumerate(bool includeSystemComponents = false)
    {
        var apps = new List<InstalledApplication>();
        foreach (var (hive, view, source, displayPath) in Hives)
        {
            try
            {
                using var parent = _registry.OpenKey(hive, view, UninstallPath);
                if (parent is null) continue;
                foreach (var id in parent.GetSubKeyNames())
                {
                    var app = TryRead(parent, id, source, $@"{displayPath}\{id}", includeSystemComponents);
                    if (app is not null) apps.Add(app);
                }
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
            }
        }
        return apps
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => Array.FindIndex(Hives, h => h.Source == a.Source))
            .ToList();
    }

    private static InstalledApplication? TryRead(IRegistryKeyReader parent, string id, string source, string keyPath, bool includeSystemComponents)
    {
        try
        {
            using var key = parent.OpenSubKey(id);
            if (key is null) return null;
            var name = Text(key, "DisplayName");
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (!includeSystemComponents && IsSystemComponent(key.GetValue("SystemComponent"))) return null;
            return new InstalledApplication(
                name.Trim(),
                Text(key, "Publisher"),
                Text(key, "DisplayVersion"),
                Text(key, "InstallDate"),
                source,
                keyPath,
                Text(key, "UninstallString"),
                Text(key, "QuietUninstallString"),
                Text(key, "InstallLocation"));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

    private static bool IsSystemComponent(object? value) => value switch
    {
        int i => i == 1,
        long l => l == 1,
        string s => s.Trim() == "1",
        _ => false
    };

    private static string Text(IRegistryKeyReader key, string name) => key.GetValue(name) switch
    {
        null => "",
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        string[] list => string.Join(" ", list),
        _ => ""
    };

    private static bool IsReadFailure(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException or IOException;
}
