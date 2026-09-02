using System.Reflection;
using System.Runtime.InteropServices;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads an icon stream out of an MSI Icon table through the Windows Installer
/// COM API. Prefers the stream named by ARPPRODUCTICON and falls back to the
/// table's first row.
/// </summary>
internal static class MsiIconReader
{
    private const BindingFlags Call = BindingFlags.InvokeMethod;
    private const BindingFlags Get = BindingFlags.GetProperty;

    /// <summary>
    /// The icon stream's bytes, or null when the database carries no Icon
    /// table, no rows, or cannot be opened.
    /// </summary>
    internal static byte[]? Read(string msiPath, Action<string>? onLog = null)
    {
        var installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer");
        if (installerType is null) return null;

        object? installer = null;
        object? database = null;
        try
        {
            installer = Activator.CreateInstance(installerType);
            database = installerType.InvokeMember("OpenDatabase", Call, null, installer,
                new object[] { msiPath, 0 });
            if (database is null) return null;

            var wanted = FetchString(database,
                "SELECT `Value` FROM `Property` WHERE `Property` = 'ARPPRODUCTICON'");

            // A ProductIcon name that does not resolve (authoring drift) still
            // leaves the table's own rows usable.
            var bytes = string.IsNullOrWhiteSpace(wanted)
                ? null
                : ReadStreamRow(database, $"SELECT `Name`, `Data` FROM `Icon` WHERE `Name` = '{wanted.Replace("'", "''")}'");
            return bytes ?? ReadStreamRow(database, "SELECT `Name`, `Data` FROM `Icon`");
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"MSI icon read failed         : {ex.Message}");
            return null;
        }
        finally
        {
            // A live Database handle keeps a write lock on the file for the rest
            // of the process lifetime, so both objects are released explicitly.
            foreach (var com in new[] { database, installer })
                if (com is not null) { try { Marshal.ReleaseComObject(com); } catch { } }
        }
    }

    private static string FetchString(object database, string query)
    {
        try
        {
            var view = database.GetType().InvokeMember("OpenView", Call, null, database, new object[] { query });
            if (view is null) return "";
            view.GetType().InvokeMember("Execute", Call, null, view, new object?[] { null });
            var record = view.GetType().InvokeMember("Fetch", Call, null, view, null);
            var value = record is null
                ? ""
                : record.GetType().InvokeMember("StringData", Get, null, record, new object[] { 1 }) as string ?? "";
            view.GetType().InvokeMember("Close", Call, null, view, null);
            return value;
        }
        catch
        {
            // An MSI without a Property table row for the ARP icon is normal.
            return "";
        }
    }

    private static byte[]? ReadStreamRow(object database, string query)
    {
        try
        {
            var view = database.GetType().InvokeMember("OpenView", Call, null, database, new object[] { query });
            if (view is null) return null;
            view.GetType().InvokeMember("Execute", Call, null, view, new object?[] { null });
            var record = view.GetType().InvokeMember("Fetch", Call, null, view, null);
            if (record is null) return null;

            var size = Convert.ToInt32(record.GetType().InvokeMember("DataSize", Get, null, record, new object[] { 2 }));
            if (size <= 0) return null;

            // MsiReadStreamInteger (1) hands back a binary string; each char is
            // one byte of the stream.
            var raw = record.GetType().InvokeMember("ReadStream", Call, null, record, new object[] { 2, size, 1 });
            view.GetType().InvokeMember("Close", Call, null, view, null);

            if (raw is string text)
            {
                var buffer = new byte[text.Length];
                for (var i = 0; i < text.Length; i++) buffer[i] = (byte)text[i];
                return buffer;
            }
            return raw as byte[];
        }
        catch
        {
            return null;
        }
    }
}
