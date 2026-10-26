namespace ApplicationPackagerEngine.Services;

/// <summary>Serializes the check-and-create of one object and one collection between sessions of this product.</summary>
public interface IDeploymentTargetLock
{
    /// <summary>The held lock, or null when another session holds it or it cannot be taken.</summary>
    IDisposable? TryAcquire(string objectKey, string collectionId);
}

/// <summary>
/// A lock file per object and collection in the folder of the audit log, opened without sharing
/// and deleted on close. Sessions that share one audit log folder, also on other computers through
/// a share, exclude each other; a process that ends releases its handle, so no lock file stays held.
/// </summary>
public sealed class DeploymentTargetLock(Func<string> auditPath) : IDeploymentTargetLock
{
    public IDisposable? TryAcquire(string objectKey, string collectionId)
    {
        if (!IsKey(objectKey) || !IsKey(collectionId)) return null;
        try
        {
            var audit = auditPath();
            if (Path.GetDirectoryName(audit) is not { Length: > 0 } folder || !Directory.Exists(folder)) return null;
            var path = LockPath(audit, objectKey, collectionId);
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return null;
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string LockPath(string auditPath, string objectKey, string collectionId) =>
        Path.Combine(Path.GetDirectoryName(auditPath)!, $"deployment-{objectKey.ToUpperInvariant()}-{collectionId.ToUpperInvariant()}.lock");

    private static bool IsKey(string value) => value.Length is > 0 and <= 16 && value.All(char.IsAsciiLetterOrDigit);
}
