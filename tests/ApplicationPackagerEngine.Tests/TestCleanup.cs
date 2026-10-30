namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Best-effort removal of a test temp folder. A folder that a held handle or a denied entry keeps on disk is
/// written to standard error with its path, so a leftover folder can be traced to the test that made it.
/// </summary>
internal static class TestCleanup
{
    public static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Test cleanup left the folder {path}: {ex.Message}");
        }
    }
}
