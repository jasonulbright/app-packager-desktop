using System.Security.AccessControl;
using System.Security.Principal;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Refuses a staging folder that an account other than the current user or a machine
/// administrator can change. Staged installers and scripts later run on endpoints in
/// machine context, and the stage manifest hashes whatever bytes the folder holds.
/// </summary>
public static class StagingRootGuard
{
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    private const FileSystemRights ChangeRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string OwnerRightsSid = "S-1-3-4";

    /// <summary>
    /// Returns the full path of a staging root that passes <see cref="Verify"/>. A root
    /// that does not exist is created with access for the current user, SYSTEM and
    /// Administrators only, with no inherited entries. A blank root is returned as it is.
    /// </summary>
    public static string Prepare(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return root;
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            if (Path.GetDirectoryName(fullRoot) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { CurrentUser(), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(fullRoot).Create(security);
        }
        // A folder another account created first keeps that account's owner and entries.
        Verify(fullRoot);
        return fullRoot;
    }

    /// <summary>
    /// Throws when the folder's owner or any allow entry, inherited and inherit-only
    /// entries included, lets another account change content. A missing folder passes:
    /// it holds nothing to trust. A folder whose access list cannot be read is refused.
    /// </summary>
    public static void Verify(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        var others = OtherWriters(folder);
        if (others.Count > 0)
            throw new UnauthorizedAccessException(
                $"Staging folder '{folder}' can be changed by other accounts ({string.Join(", ", others)}). " +
                $"Use a folder that only your account and administrators can write, such as '{AppDataPaths.DefaultStagingRoot}'.");
    }

    /// <summary>The accounts, other than the current user and machine administrators, that own the folder or can change its content.</summary>
    public static IReadOnlyList<string> OtherWriters(string folder)
    {
        DirectorySecurity security;
        try
        {
            security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            throw new UnauthorizedAccessException($"The access list of staging folder '{folder}' could not be read: {ex.Message}", ex);
        }

        var trusted = TrustedSids();
        var others = new List<SecurityIdentifier>();
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !trusted.Contains(owner))
            others.Add(owner);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var mask = (int)rule.FileSystemRights;
            if ((rule.FileSystemRights & ChangeRights) == 0 && (mask & (GenericAll | GenericWrite)) == 0) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!trusted.Contains(sid) && !others.Contains(sid)) others.Add(sid);
        }
        return others.Select(Name).ToList();
    }

    private static SecurityIdentifier CurrentUser() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("The current Windows user SID could not be resolved for the staging folder check.");

    private static HashSet<SecurityIdentifier> TrustedSids()
    {
        var user = CurrentUser();
        var trusted = new HashSet<SecurityIdentifier>
        {
            user,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.CreatorOwnerSid, null),
            new(TrustedInstallerSid),
            new(OwnerRightsSid)
        };
        if (user.AccountDomainSid is { } domain)
            trusted.Add(new SecurityIdentifier(WellKnownSidType.AccountDomainAdminsSid, domain));
        return trusted;
    }

    private static string Name(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException) { return sid.Value; }
    }
}
