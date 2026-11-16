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

    /// <summary>What the check of a staging folder found.</summary>
    public enum FolderState { NotSet, Missing, Safe, Blocked, Allowed, Unreadable }

    /// <summary>The note that the stage result and the log carry when a folder that other accounts can write to is used with the acknowledgement.</summary>
    public const string AllowedNote = "staging allowed by user acknowledgement";

    /// <summary>The first words of <see cref="BlockedText"/>. A message that starts with them is the refusal of this guard.</summary>
    public const string BlockedPrefix = "Staging is blocked for security";

    /// <summary>The folder that only the current user and administrators can write.</summary>
    public static string SafeFolder => AppDataPaths.DefaultStagingRoot;

    /// <summary>
    /// The one text of the refusal: Settings, the stage result, the run status and the log show it. It names the folder, says that other accounts
    /// can write there, says staging is blocked for security, and names the fix.
    /// </summary>
    public static string BlockedText(string folder, IReadOnlyList<string> others) =>
        $"{BlockedPrefix}: staging folder '{folder}' can be changed by other accounts ({string.Join(", ", others)}), so a local user could swap a staged installer or script before it is published. " +
        $"Use a folder that only your account and administrators can write, such as '{SafeFolder}'.";

    /// <summary>The reason that "Allow this folder anyway" shows, in plain words, before the user acknowledges it.</summary>
    public const string AllowanceReason =
        "Other accounts can write to this folder, so a local user could swap a staged installer or script before it is published. The check blocks that. " +
        "If you allow the folder, staging runs there anyway, only for this folder and only for your Windows account. " +
        "The allowance ends when the folder or its access list changes.";

    /// <summary>True when the text is the refusal of this guard.</summary>
    public static bool IsBlockedText(string? text) => text is not null && text.TrimStart().StartsWith(BlockedPrefix, StringComparison.Ordinal);

    /// <summary>The result of <see cref="Check"/>: the state, the accounts that can write, the text to show and the allowance in force.</summary>
    public sealed record FolderStatus(string Folder, FolderState State, IReadOnlyList<string> OtherWriters, string Text, StagingAllowance? Allowance)
    {
        /// <summary>True when a stage would stop at this folder.</summary>
        public bool StopsStaging => State is FolderState.Blocked or FolderState.Unreadable;
    }

    /// <summary>
    /// Checks a folder as Settings does when it is set: never throws. A folder that other accounts can write to is Blocked, or Allowed when
    /// the current user acknowledged it and its access list has not changed since.
    /// </summary>
    public static FolderStatus Check(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new FolderStatus("", FolderState.NotSet, [], "", null);
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new FolderStatus(folder, FolderState.Unreadable, [], $"The folder '{folder}' is not a valid path: {ex.Message}", null);
        }

        if (!Directory.Exists(full)) return new FolderStatus(full, FolderState.Missing, [], "", null);
        IReadOnlyList<string> others;
        try { others = OtherWriters(full); }
        catch (UnauthorizedAccessException ex) { return new FolderStatus(full, FolderState.Unreadable, [], ex.Message, null); }
        if (others.Count == 0) return new FolderStatus(full, FolderState.Safe, [], "", null);
        var text = BlockedText(full, others);
        var allowance = StagingAllowanceStore.Find(full);
        return allowance is null
            ? new FolderStatus(full, FolderState.Blocked, others, text, null)
            : new FolderStatus(full, FolderState.Allowed, others, text, allowance);
    }

    /// <summary>The allowance note when the folder is used only because the current user acknowledged it; null otherwise.</summary>
    public static string? AllowanceNote(string? folder)
    {
        var status = Check(folder);
        return status.State == FolderState.Allowed ? $"{AllowedNote} ({status.Allowance!.User}, {status.Allowance.AcknowledgedAt:yyyy-MM-dd})" : null;
    }

    /// <summary>
    /// Throws when the folder's owner or any allow entry, inherited and inherit-only
    /// entries included, lets another account change content, unless the current user
    /// acknowledged that folder and its access list has not changed. A missing folder passes:
    /// it holds nothing to trust. A folder whose access list cannot be read is refused.
    /// </summary>
    public static void Verify(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        var others = OtherWriters(folder);
        if (others.Count == 0) return;
        if (StagingAllowanceStore.Find(folder) is not null) return;
        throw new UnauthorizedAccessException(BlockedText(folder, others));
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
