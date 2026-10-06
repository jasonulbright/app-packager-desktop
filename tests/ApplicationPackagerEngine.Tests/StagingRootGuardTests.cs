using System.Security.AccessControl;
using System.Security.Principal;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A staging folder is used only when the current user and machine administrators are
/// the only accounts that own it or can change its content.
/// </summary>
public sealed class StagingRootGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-stage-guard-" + Guid.NewGuid().ToString("N"));

    public StagingRootGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void Grant(string folder, WellKnownSidType account, FileSystemRights rights, PropagationFlags propagation = PropagationFlags.None)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(account, null), rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagation, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [Fact]
    public void PrepareCreatesAMissingRootWithNoInheritedEntryAndNoOtherWriter()
    {
        var parent = Path.Combine(_root, "shared");
        Directory.CreateDirectory(parent);
        Grant(parent, WellKnownSidType.AuthenticatedUserSid, FileSystemRights.Modify);
        var root = Path.Combine(parent, "stage");

        Assert.Equal(root, StagingRootGuard.Prepare(root));

        Assert.True(new DirectoryInfo(root).GetAccessControl().AreAccessRulesProtected);
        Assert.Empty(StagingRootGuard.OtherWriters(root));
        var child = Directory.CreateDirectory(Path.Combine(root, "app", "1.0")).FullName;
        StagingRootGuard.Verify(child);
    }

    [Fact]
    public void AnExistingRootAnotherAccountCanWriteIsRefusedAndNamesTheAccount()
    {
        Grant(_root, WellKnownSidType.AuthenticatedUserSid, FileSystemRights.Modify);

        var refusal = Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Prepare(_root));

        Assert.Contains("other accounts", refusal.Message);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Translate(typeof(NTAccount)).Value, refusal.Message);
        Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Verify(_root));
    }

    [Fact]
    public void AnInheritOnlyWriteEntryIsRefusedOnTheRootAndOnContentBeneathIt()
    {
        Grant(_root, WellKnownSidType.BuiltinUsersSid, FileSystemRights.Modify, PropagationFlags.InheritOnly);
        var content = Directory.CreateDirectory(Path.Combine(_root, "app", "1.0")).FullName;

        Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Verify(_root));
        Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Verify(content));
    }

    [Fact]
    public void AReadOnlyEntryForAnotherAccountPasses()
    {
        Grant(_root, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);

        Assert.Equal(_root, StagingRootGuard.Prepare(_root));
    }

    [Fact]
    public void AMissingFolderAndABlankRootPass()
    {
        StagingRootGuard.Verify(Path.Combine(_root, "absent"));
        Assert.Equal("", StagingRootGuard.Prepare(""));
    }
}
