using System.Diagnostics;
using System.IO.Compression;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A ZIP payload extracts into the parent of its install folder, so every entry
/// has to resolve inside the install folder. Inspection refuses the stage, and
/// the generated install script refuses on the client before it removes or
/// extracts anything.
/// </summary>
public sealed class ZipPayloadContainmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-zipcontain-" + Guid.NewGuid().ToString("N"));

    public ZipPayloadContainmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string NewZip(string name, params string[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            if (entry.EndsWith('/')) { zip.CreateEntry(entry); continue; }
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write("x"u8);
        }
        return path;
    }

    private static ZipPayloadConfig Config(string installDir) => new() { InstallDir = installDir };

    [Theory]
    [InlineData("AnypointStudio/../Users/Public/evil.cmd")]
    [InlineData("AnypointStudio/sub/../../evil.cmd")]
    [InlineData("AnypointStudio/readme.txt:hidden")]
    [InlineData("/AnypointStudio/evil.cmd")]
    [InlineData("C:/AnypointStudio/evil.cmd")]
    [InlineData("AnypointStudioX/evil.cmd")]
    public void AnEntryThatResolvesOutsideTheInstallFolderRefusesTheStage(string hostile)
    {
        var zip = NewZip("hostile.zip", "AnypointStudio/AnypointStudio.exe", hostile);

        var ex = Assert.Throws<InvalidOperationException>(() => ZipPayload.Inspect(zip, Config(@"C:\AnypointStudio")));

        Assert.Contains(hostile, ex.Message);
    }

    [Fact]
    public void EntriesInsideTheInstallFolderPass()
    {
        var zip = NewZip("ok.zip", "AnypointStudio/", "AnypointStudio/AnypointStudio.exe", "AnypointStudio/plugins/a/b.jar");
        Assert.Empty(ZipPayload.Inspect(zip, Config(@"C:\AnypointStudio")));
    }

    private (int ExitCode, string StdErr) RunInstall(string installDir, string zipPath)
    {
        var content = Path.Combine(_dir, "content-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(content);
        File.Copy(zipPath, Path.Combine(content, "payload.zip"));
        ZipPayload.GenerateWrappers(new WrapperGeneratorService(), content, "payload.zip", Config(installDir));

        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(content, "install.ps1") })
            info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000));
        return (process.ExitCode, stderr.Result);
    }

    [Fact]
    public void TheInstallScriptRefusesAnEscapingEntryBeforeItRemovesOrExtractsAnything()
    {
        var parent = Path.Combine(_dir, "Program Files");
        var installDir = Path.Combine(parent, "AnypointStudio");
        Directory.CreateDirectory(installDir);
        var earlier = Path.Combine(installDir, "earlier.txt");
        File.WriteAllText(earlier, "previous version");
        var zip = NewZip("hostile.zip", "AnypointStudio/AnypointStudio.exe", "AnypointStudio/../escaped.cmd");

        var (exitCode, stderr) = RunInstall(installDir, zip);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("escaped.cmd", stderr);
        Assert.False(File.Exists(Path.Combine(parent, "escaped.cmd")));
        Assert.True(File.Exists(earlier));
        Assert.False(File.Exists(Path.Combine(installDir, "AnypointStudio.exe")));
    }

    [Fact]
    public void TheInstallScriptReplacesTheFolderForAContainedZip()
    {
        var parent = Path.Combine(_dir, "it's here");
        var installDir = Path.Combine(parent, "AnypointStudio");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "earlier.txt"), "previous version");
        var zip = NewZip("ok.zip", "AnypointStudio/", "AnypointStudio/AnypointStudio.exe", "AnypointStudio/plugins/p.jar");

        var (exitCode, stderr) = RunInstall(installDir, zip);

        Assert.True(exitCode == 0, stderr);
        Assert.True(File.Exists(Path.Combine(installDir, "AnypointStudio.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "plugins", "p.jar")));
        Assert.False(File.Exists(Path.Combine(installDir, "earlier.txt")));
    }

    [Fact]
    public void AnInstallFolderWithATypographicApostropheIsEscaped()
    {
        var output = Path.Combine(_dir, "wrappers");
        Directory.CreateDirectory(output);
        ZipPayload.GenerateWrappers(new WrapperGeneratorService(), output, "a\u2019b.zip", Config("C:\\Vendor\u2019s\\App"));

        var install = File.ReadAllText(Path.Combine(output, "install.ps1"));
        Assert.Contains("$installDir = 'C:\\Vendor\u2019\u2019s\\App'", install);
        Assert.Contains("'a\u2019\u2019b.zip'", install);
    }
}
