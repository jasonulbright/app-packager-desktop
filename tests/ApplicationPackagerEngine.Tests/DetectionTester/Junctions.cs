using System.Diagnostics;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

internal static class Junctions
{
    /// <summary>A real directory junction; mklink /J needs no elevation.</summary>
    public static void Create(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(30_000);
        Assert.True(Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "the junction was not created");
    }

    /// <summary>Removes the junction itself. A recursive delete of its parent fails with access denied once the target is gone.</summary>
    public static void Remove(string link)
    {
        if (Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(link, recursive: false);
    }
}
