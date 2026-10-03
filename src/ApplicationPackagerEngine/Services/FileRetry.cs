namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Bounded retries for a file another writer or reader holds for a moment: a store file
/// mid-replace, or a file a reader opened without delete sharing.
/// </summary>
public static class FileRetry
{
    /// <summary>The longest a read or a replace waits for a file another process holds.</summary>
    public static readonly TimeSpan SharingWait = TimeSpan.FromSeconds(2);

    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    /// <summary>True for a sharing or lock violation: another handle holds the file, and the call may succeed once it closes.</summary>
    public static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is SharingViolation or LockViolation;

    /// <summary>Reads the whole file, retrying a sharing or lock violation up to <see cref="SharingWait"/>.</summary>
    public static string ReadAllText(string path)
    {
        var deadline = DateTime.UtcNow + SharingWait;
        while (true)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException ex) when (IsSharingViolation(ex) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="source"/> over <paramref name="destination"/> in one rename, so
    /// the destination name exists throughout. A reader holding the destination without
    /// delete sharing refuses the rename for a moment (a sharing violation or access
    /// denied), which is retried up to <see cref="SharingWait"/>.
    /// </summary>
    public static void MoveReplacing(string source, string destination)
    {
        var deadline = DateTime.UtcNow + SharingWait;
        while (true)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline && File.Exists(source))
            {
                Thread.Sleep(10);
            }
        }
    }
}
