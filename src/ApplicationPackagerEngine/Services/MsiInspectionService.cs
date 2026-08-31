using System.Runtime.InteropServices;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Extracts properties from MSI files using the Windows Installer COM API.
/// Replaces the PS1 Get-MsiPropertyMap function.
/// </summary>
public sealed class MsiInspectionService : IMsiInspector
{
    /// <summary>
    /// Reads standard MSI properties: ProductName, ProductVersion, Manufacturer, ProductCode,
    /// plus the UpgradeCode and the ALLUSERS/MSIINSTALLPERUSER pair that decides HKLM vs HKCU.
    /// </summary>
    public MsiProperties ReadProperties(string msiPath)
    {
        if (!File.Exists(msiPath))
            throw new FileNotFoundException("MSI file not found", msiPath);

        var installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer")
            ?? throw new InvalidOperationException("WindowsInstaller.Installer COM class not available");

        object? installer = null;
        object? database = null;
        object? view = null;
        object? record = null;

        try
        {
            installer = Activator.CreateInstance(installerType);
            database = installerType.InvokeMember("OpenDatabase",
                System.Reflection.BindingFlags.InvokeMethod, null, installer,
                new object[] { msiPath, 0 }); // 0 = msiOpenDatabaseModeReadOnly

            var properties = new Dictionary<string, string>();
            var propertyNames = new[]
            {
                "ProductName", "ProductVersion", "Manufacturer", "ProductCode",
                "UpgradeCode", "ALLUSERS", "MSIINSTALLPERUSER"
            };

            foreach (var name in propertyNames)
            {
                var query = $"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'";
                view = database!.GetType().InvokeMember("OpenView",
                    System.Reflection.BindingFlags.InvokeMethod, null, database,
                    new object[] { query });

                view!.GetType().InvokeMember("Execute",
                    System.Reflection.BindingFlags.InvokeMethod, null, view,
                    new object?[] { null });

                record = view.GetType().InvokeMember("Fetch",
                    System.Reflection.BindingFlags.InvokeMethod, null, view, null);

                if (record is not null)
                {
                    var value = record.GetType().InvokeMember("StringData",
                        System.Reflection.BindingFlags.GetProperty, null, record,
                        new object[] { 1 }) as string ?? "";
                    properties[name] = value;
                    Marshal.FinalReleaseComObject(record);
                    record = null;
                }

                view.GetType().InvokeMember("Close",
                    System.Reflection.BindingFlags.InvokeMethod, null, view, null);
                Marshal.FinalReleaseComObject(view);
                view = null;
            }

            return new MsiProperties
            {
                ProductName = properties.GetValueOrDefault("ProductName", ""),
                ProductVersion = properties.GetValueOrDefault("ProductVersion", ""),
                Manufacturer = properties.GetValueOrDefault("Manufacturer", ""),
                ProductCode = properties.GetValueOrDefault("ProductCode", ""),
                UpgradeCode = properties.GetValueOrDefault("UpgradeCode", ""),
                AllUsers = properties.TryGetValue("ALLUSERS", out var allUsers) ? allUsers : null,
                MsiInstallPerUser = properties.GetValueOrDefault("MSIINSTALLPERUSER", "")
            };
        }
        finally
        {
            if (record is not null) Marshal.FinalReleaseComObject(record);
            if (view is not null) Marshal.FinalReleaseComObject(view);
            if (database is not null) Marshal.FinalReleaseComObject(database);
            if (installer is not null) Marshal.FinalReleaseComObject(installer);
        }
    }

    /// <summary>
    /// Reads the architecture half of the SummaryInformation Template field
    /// (property 7, formatted "&lt;platform&gt;;&lt;language ids&gt;").
    /// </summary>
    public string ReadTemplateArchitecture(string msiPath)
    {
        if (!File.Exists(msiPath))
            throw new FileNotFoundException("MSI file not found", msiPath);

        var installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer")
            ?? throw new InvalidOperationException("WindowsInstaller.Installer COM class not available");

        object? installer = null;
        object? summary = null;

        try
        {
            installer = Activator.CreateInstance(installerType);
            summary = installerType.InvokeMember("SummaryInformation",
                System.Reflection.BindingFlags.GetProperty, null, installer,
                new object[] { msiPath, 0 });

            var template = summary!.GetType().InvokeMember("Property",
                System.Reflection.BindingFlags.GetProperty, null, summary,
                new object[] { 7 }) as string ?? "";

            var platform = template.Split(';')[0].Trim();
            return platform;
        }
        catch
        {
            return "";
        }
        finally
        {
            if (summary is not null) Marshal.FinalReleaseComObject(summary);
            if (installer is not null) Marshal.FinalReleaseComObject(installer);
        }
    }
}

public sealed class MsiProperties
{
    public string ProductName { get; init; } = "";
    public string ProductVersion { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string ProductCode { get; init; } = "";
    public string UpgradeCode { get; init; } = "";

    /// <summary>
    /// ALLUSERS value, or null when the Property table has no ALLUSERS row.
    /// The absent/empty distinction changes the install scope: an empty value
    /// means per-user, an absent row means per-machine.
    /// </summary>
    public string? AllUsers { get; init; }

    public string MsiInstallPerUser { get; init; } = "";

    /// <summary>
    /// True when the package registers under HKCU rather than HKLM:
    /// ALLUSERS empty, or ALLUSERS=2 paired with MSIINSTALLPERUSER=1,
    /// or MSIINSTALLPERUSER=1 with no ALLUSERS row.
    /// </summary>
    public bool IsPerUser =>
        AllUsers is null
            ? MsiInstallPerUser == "1"
            : AllUsers.Length == 0 || (AllUsers == "2" && MsiInstallPerUser == "1");
}
