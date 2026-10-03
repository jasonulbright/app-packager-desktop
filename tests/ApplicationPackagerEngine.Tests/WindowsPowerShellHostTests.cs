using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class WindowsPowerShellHostTests
{
    [Fact]
    public void RunsTheScriptFileUnderWindowsPowerShellWithoutAProfile()
    {
        var info = WindowsPowerShellHost.BuildStartInfo(@"C:\Temp\ap-mecm-1.ps1");
        Assert.Equal("powershell.exe", info.FileName);
        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -File \"C:\\Temp\\ap-mecm-1.ps1\"", info.Arguments);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.False(info.RedirectStandardInput);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
    }

    [Fact]
    public void QuotesEachScriptArgumentAfterTheScriptPath()
    {
        var info = WindowsPowerShellHost.BuildStartInfo(@"C:\Temp\a.ps1", ["-InputPath", @"C:\Temp\req dir\request.json", "-Name", "say \"hi\"", @"C:\trail\"], redirectStandardInput: true);
        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -File \"C:\\Temp\\a.ps1\" -InputPath \"C:\\Temp\\req dir\\request.json\" -Name \"say \\\"hi\\\"\" C:\\trail\\", info.Arguments);
        Assert.True(info.RedirectStandardInput);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void QuotesAnArgumentTheWayTheCommandLineParserReadsItBack(string argument, string expected)
    {
        Assert.Equal(expected, WindowsPowerShellHost.QuoteArgument(argument));
    }

    [Fact]
    public void TheModulePathIsTheLogonValueNotTheInheritedOne()
    {
        Assert.Equal(@"C:\U;C:\M", WindowsPowerShellHost.LogonModulePath(@"C:\U", @"C:\M"));
        Assert.Equal(@"C:\M", WindowsPowerShellHost.LogonModulePath(null, @"C:\M"));
        Assert.Equal("", WindowsPowerShellHost.LogonModulePath(" ", null));

        var info = WindowsPowerShellHost.BuildStartInfo(@"C:\Temp\a.ps1");
        var expected = WindowsPowerShellHost.LogonModulePath(
            Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.Machine));
        if (expected.Length > 0) Assert.Equal(expected, info.Environment["PSModulePath"]);
        else Assert.False(info.Environment.ContainsKey("PSModulePath"));
    }

    [Fact]
    public async Task PassesArgumentsAndStandardInputToTheScriptAndRemovesItsFile()
    {
        var lines = new List<string>();
        var run = await WindowsPowerShellHost.RunAsync(
            "param([string]$Name)\r\nWrite-Output ('name ' + $Name)\r\nWrite-Output ('input ' + [Console]::In.ReadLine())\r\nWrite-Output ('edition ' + $PSVersionTable.PSEdition)\r\n[Console]::Error.WriteLine('problem')\r\nexit 3",
            lines.Add, default, "ap-host-test", ["-Name", "a b"], "secret line");
        Assert.Equal(3, run.ExitCode);
        Assert.Equal(["name a b", "input secret line", "edition Desktop"], lines);
        Assert.Contains("problem", run.StdErr);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "ap-host-test-*.ps1"));
    }
}
