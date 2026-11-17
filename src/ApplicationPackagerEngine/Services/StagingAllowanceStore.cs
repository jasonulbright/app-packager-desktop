using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The acknowledgement of one staging folder that other accounts can write to: the folder, when and by which Windows user it was accepted, and a
/// fingerprint of the owner and access list at that time. The allowance ends when the folder or the fingerprint changes.
/// </summary>
public sealed record StagingAllowance(string Folder, DateTimeOffset AcknowledgedAt, string User, string AccessFingerprint, bool Acknowledged = true);

/// <summary>
/// Stores the allowances per user in the per-user data folder (staging-allowances.json). A record counts only while it is acknowledged, was made by the
/// current Windows user, and still matches the folder's access list. There is no other way to allow a folder: no setting and no command line option.
/// </summary>
public static class StagingAllowanceStore
{
    public const string FileName = "staging-allowances.json";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string PathOfFile() => Path.Combine(AppDataPaths.Root, FileName);

    private static string Key(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    internal static string CurrentUserName() => WindowsIdentity.GetCurrent().Name;

    /// <summary>A hash of the owner and the access list of the folder, inherited entries included.</summary>
    public static string Fingerprint(string folder)
    {
        var security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var sddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sddl)));
    }

    public static IReadOnlyList<StagingAllowance> All()
    {
        lock (Gate) return Read();
    }

    private static List<StagingAllowance> Read()
    {
        try
        {
            var path = PathOfFile();
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<StagingAllowance>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // An unreadable file allows nothing.
            return [];
        }
    }

    private static void Write(List<StagingAllowance> records)
    {
        var path = PathOfFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The allowance in force for the folder or the nearest folder above it: acknowledged, made by the current user, and matching the access list now.
    /// A record that no longer matches is removed.
    /// </summary>
    public static StagingAllowance? Find(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var full = Key(folder);
        lock (Gate)
        {
            var records = Read();
            StagingAllowance? found = null;
            var changed = false;
            foreach (var record in records.ToList())
            {
                var key = Key(record.Folder);
                var covers = string.Equals(key, full, StringComparison.OrdinalIgnoreCase)
                             || full.StartsWith(key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (!covers) continue;
                if (Valid(record, key)) { found ??= record; continue; }
                if (!Directory.Exists(key) || !record.Acknowledged || !string.Equals(record.User, CurrentUserName(), StringComparison.OrdinalIgnoreCase)
                    || !FingerprintMatches(record, key))
                {
                    records.Remove(record);
                    changed = true;
                }
            }

            if (changed) TryWrite(records);
            return found;
        }
    }

    private static bool FingerprintMatches(StagingAllowance record, string folder)
    {
        try { return string.Equals(record.AccessFingerprint, Fingerprint(folder), StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException) { return false; }
    }

    private static bool Valid(StagingAllowance record, string folder) =>
        record.Acknowledged
        && Directory.Exists(folder)
        && string.Equals(record.User, CurrentUserName(), StringComparison.OrdinalIgnoreCase)
        && FingerprintMatches(record, folder);

    private static void TryWrite(List<StagingAllowance> records)
    {
        try { Write(records); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The next read removes the stale record again. */ }
    }

    /// <summary>
    /// Records the acknowledgement of the folder by the current Windows user. Refused when <paramref name="acknowledged"/> is false, when the folder
    /// does not exist, or when its access list cannot be read. An older record of the same folder is replaced.
    /// </summary>
    public static StagingAllowance Allow(string folder, bool acknowledged, DateTimeOffset? at = null)
    {
        if (!acknowledged) throw new InvalidOperationException("The folder is allowed only with the acknowledgement.");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException($"The folder '{folder}' does not exist.");
        var full = Key(folder);
        var record = new StagingAllowance(full, at ?? DateTimeOffset.UtcNow, CurrentUserName(), Fingerprint(full));
        lock (Gate)
        {
            var records = Read().Where(r => !string.Equals(Key(r.Folder), full, StringComparison.OrdinalIgnoreCase)).ToList();
            records.Add(record);
            Write(records);
        }

        return record;
    }

    /// <summary>Removes the allowance of one folder.</summary>
    public static void Revoke(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        var full = Key(folder);
        lock (Gate)
        {
            var records = Read();
            if (records.RemoveAll(r => string.Equals(Key(r.Folder), full, StringComparison.OrdinalIgnoreCase)) > 0) TryWrite(records);
        }
    }

    /// <summary>Removes every allowance except the one of <paramref name="keep"/>: the staging folder changed, so the others end.</summary>
    public static void RemoveAllExcept(string? keep)
    {
        var keepKey = string.IsNullOrWhiteSpace(keep) ? null : Key(keep);
        lock (Gate)
        {
            var records = Read();
            if (records.RemoveAll(r => keepKey is null || !string.Equals(Key(r.Folder), keepKey, StringComparison.OrdinalIgnoreCase)) > 0) TryWrite(records);
        }
    }
}
