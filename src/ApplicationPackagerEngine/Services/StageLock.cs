using System.Security.Cryptography;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>The stage lock stayed with another stage until the wait ended.</summary>
public sealed class StageLockTimeoutException(string message) : IOException(message);

/// <summary>
/// Runs the stages of one application and version one at a time, across threads and processes
/// (the app, <c>AppPackagerBuild.exe</c>, two command-line runs). The lock is a file in the
/// per-user temp folder, named from the stage folder path and opened with no sharing and
/// delete-on-close. The system closes the handle when the owner process ends, also after a
/// crash, so a stale file never blocks: the next stage opens it. Stages of other applications
/// or versions use other lock files and run in parallel.
/// </summary>
public static class StageLock
{
    /// <summary>How long a stage waits for another stage of the same application and version.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan NoticeEvery = TimeSpan.FromSeconds(30);

    /// <summary>The folder that holds the lock files.</summary>
    public static string LockFolder => Path.Combine(Path.GetTempPath(), "ApplicationPackager", "stage-locks");

    /// <summary>The lock file of the stage folder <paramref name="contentPath"/>.</summary>
    public static string LockPath(string contentPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentPath)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
        return Path.Combine(LockFolder, hash + ".lock");
    }

    /// <summary>
    /// Takes the lock of <paramref name="contentPath"/>, and waits while another stage holds it.
    /// Reports the wait through <paramref name="onProgress"/>. Throws <see cref="StageLockTimeoutException"/>
    /// when the wait passes <paramref name="timeout"/>. When the lock folder cannot be used, the stage
    /// continues without a lock and the report says so.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(
        string contentPath, string label, TimeSpan timeout, Action<string>? onProgress, CancellationToken ct)
    {
        string path;
        try
        {
            Directory.CreateDirectory(LockFolder);
            path = LockPath(contentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            onProgress?.Invoke($"The stage lock folder is not usable, so this stage runs without a lock: {ex.Message}");
            return NoLock.Instance;
        }

        var started = DateTime.UtcNow;
        var lastNotice = DateTime.MinValue;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (UnauthorizedAccessException)
            {
                // A lock file on its way out (delete pending) reports access denied; it opens on the next try.
            }
            catch (IOException ex) when (!Directory.Exists(LockFolder))
            {
                onProgress?.Invoke($"The stage lock folder is not usable, so this stage runs without a lock: {ex.Message}");
                return NoLock.Instance;
            }
            catch (IOException)
            {
                // The file is open in another stage.
            }

            var waited = DateTime.UtcNow - started;
            if (waited >= timeout)
                throw new StageLockTimeoutException(
                    $"Another stage of {label} has run for more than {(int)timeout.TotalMinutes} minutes. " +
                    "This stage did not start, so it cannot delete the files of the other stage. Try again when the other stage ends.");
            if (DateTime.UtcNow - lastNotice >= NoticeEvery)
            {
                lastNotice = DateTime.UtcNow;
                onProgress?.Invoke(waited < PollInterval
                    ? $"Another stage of {label} is running. This stage waits for it to end."
                    : $"Still waiting for the other stage of {label} ({(int)waited.TotalSeconds} s).");
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    private sealed class NoLock : IDisposable
    {
        public static readonly NoLock Instance = new();
        public void Dispose() { }
    }
}
