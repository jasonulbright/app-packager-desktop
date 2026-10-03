using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A PS1 packager child receives every argument as one intact value, reads a closed
/// standard input, and writes logs whose names carry the build.
/// </summary>
public sealed class PackagerChildProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-child-" + Guid.NewGuid().ToString("N"));

    public PackagerChildProcessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private PackagerInfo Row(string script) => new() { Script = Path.GetFileName(script), FullPath = script };

    private static async Task<(int ExitCode, string StdOut)> RunAsync(ProcessStartInfo startInfo)
    {
        var run = await BoundedProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(60));
        Assert.False(run.TimedOut, "the child waited on its standard input");
        return (run.ExitCode, run.StdOut);
    }

    [Theory]
    [InlineData("Release \"Pilot\" approved")]
    [InlineData("ends with a backslash \\")]
    [InlineData("two  spaces and a \"quote")]
    public async Task ACommentWithQuotesReachesThePackagerAsOneValue(string comment)
    {
        var script = Path.Combine(_root, "package-echo.ps1");
        File.WriteAllText(script, "param([switch]$PackageOnly, [string]$SiteCode, [string]$Comment, [string]$LogPath, [string]$DownloadRoot)\r\n[Console]::Out.Write('[' + $Comment + ']')");
        var settings = new AppSettings { SiteCode = "LAB", DownloadRoot = Path.Combine(_root, "dl") };

        var (arguments, _) = PackagerExecutionService.PackageArguments(Row(script), settings, comment, null, "20261003-120000-abcdef01");
        var startInfo = PackagerExecutionService.ChildStartInfo(arguments, null);

        Assert.Equal("", startInfo.Arguments);
        Assert.Contains(comment, startInfo.ArgumentList);
        var (exitCode, stdout) = await RunAsync(startInfo);
        Assert.Equal(0, exitCode);
        Assert.Equal("[" + comment + "]", stdout);
    }

    [Fact]
    public async Task APackagerThatReadsStandardInputGetsEndOfInputInsteadOfWaiting()
    {
        var script = Path.Combine(_root, "package-prompt.ps1");
        File.WriteAllText(script, "$line = [Console]::In.ReadLine()\r\nif ($null -eq $line) { exit 3 }\r\nexit 4");

        var startInfo = PackagerExecutionService.ChildStartInfo(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script], null);

        Assert.True(startInfo.RedirectStandardInput);
        var (exitCode, _) = await RunAsync(startInfo);
        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void ChildLogNamesCarryTheBuildId()
    {
        var settings = new AppSettings { SiteCode = "LAB", DownloadRoot = Path.Combine(_root, "dl") };
        var row = Row(Path.Combine(_root, "package-tool.ps1"));

        var (_, stage) = PackagerExecutionService.StageArguments(row, settings, null, "20261003-120000-aaaaaaaa");
        var (_, package) = PackagerExecutionService.PackageArguments(row, settings, "", null, "20261003-120000-bbbbbbbb");
        var (_, unprofiled) = PackagerExecutionService.StageArguments(row, settings, null, null);
        var (_, unprofiledAgain) = PackagerExecutionService.StageArguments(row, settings, null, null);

        Assert.Contains("20261003-120000-aaaaaaaa", Path.GetFileName(stage.StructuredLog));
        Assert.Contains("20261003-120000-aaaaaaaa", Path.GetFileName(stage.OutLog));
        Assert.Contains("20261003-120000-bbbbbbbb", Path.GetFileName(package.ErrLog));
        Assert.NotEqual(unprofiled.OutLog, unprofiledAgain.OutLog);
    }
}
