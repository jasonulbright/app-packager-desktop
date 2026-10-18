using Microsoft.Win32;

namespace ApplicationPackagerEngine.Services;

/// <summary>Read access to the registry of this computer, replaceable in tests.</summary>
public interface IRegistryProvider
{
    /// <summary>
    /// Opens a key for reading. Returns null when the key does not exist. Throws
    /// <see cref="UnauthorizedAccessException"/>, <see cref="System.Security.SecurityException"/>
    /// or <see cref="IOException"/> when the key exists but cannot be read.
    /// </summary>
    IRegistryKeyReader? OpenKey(RegistryHive hive, RegistryView view, string path);
}

public interface IRegistryKeyReader : IDisposable
{
    IReadOnlyList<string> GetSubKeyNames();

    /// <summary>Null when the subkey does not exist; throws as <see cref="IRegistryProvider.OpenKey"/> does.</summary>
    IRegistryKeyReader? OpenSubKey(string name);

    /// <summary>The value with environment variables expanded, or null when the value does not exist.</summary>
    object? GetValue(string name);

    /// <summary><see cref="RegistryValueKind.Unknown"/> when the value does not exist.</summary>
    RegistryValueKind GetValueKind(string name);
}

public sealed class WindowsRegistryProvider : IRegistryProvider
{
    public static readonly WindowsRegistryProvider Instance = new();

    public IRegistryKeyReader? OpenKey(RegistryHive hive, RegistryView view, string path)
    {
        var root = RegistryKey.OpenBaseKey(hive, view);
        try
        {
            if (string.IsNullOrEmpty(path)) return new Reader(root);
            var key = root.OpenSubKey(path, writable: false);
            root.Dispose();
            return key is null ? null : new Reader(key);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private sealed class Reader(RegistryKey key) : IRegistryKeyReader
    {
        public IReadOnlyList<string> GetSubKeyNames() => key.GetSubKeyNames();

        public IRegistryKeyReader? OpenSubKey(string name)
        {
            var sub = key.OpenSubKey(name, writable: false);
            return sub is null ? null : new Reader(sub);
        }

        public object? GetValue(string name) => key.GetValue(name);

        public RegistryValueKind GetValueKind(string name)
        {
            try { return key.GetValueKind(name); }
            catch (IOException) { return RegistryValueKind.Unknown; }
        }

        public void Dispose() => key.Dispose();
    }
}
