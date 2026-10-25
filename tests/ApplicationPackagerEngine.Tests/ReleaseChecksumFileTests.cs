using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// checksums.txt is read by sha256sum -c, which takes a carriage return as part of the
/// file name and then reports the file as missing.
/// </summary>
public class ReleaseChecksumFileTests
{
    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    [Fact]
    public async Task TheChecksumFileHasOneLfTerminatedLinePerArtifact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apkg-checksums-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var zip = Path.Combine(dir, "AppPackagerDesktop-1.2.3.zip");
            var setup = Path.Combine(dir, "AppPackagerDesktop-Setup-1.2.3.exe");
            File.WriteAllBytes(zip, [1, 2, 3]);
            File.WriteAllBytes(setup, Encoding.ASCII.GetBytes("setup"));
            var output = Path.Combine(dir, "checksums.txt");

            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            var script = Path.Combine(AppContext.BaseDirectory, "Tools", "write-checksums.ps1");
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                         $"& {Quote(script)} -Path @({Quote(zip)}, {Quote(setup)}) -Destination {Quote(output)}" })
                start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdoutTask;
            Assert.True(process.ExitCode == 0, stderr);

            var bytes = File.ReadAllBytes(output);
            Assert.DoesNotContain((byte)0x0D, bytes);
            Assert.NotEqual(0xEF, bytes[0]);
            Assert.Equal((byte)0x0A, bytes[^1]);
            var expected = string.Concat(new[] { zip, setup }.Select(p =>
                $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()}  {Path.GetFileName(p)}\n"));
            Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
