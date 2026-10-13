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
    public void AModulePathInheritedFromPowerShell7IsReplacedForEveryWindowsPowerShellChild()
    {
        var expected = WindowsPowerShellHost.LogonModulePath(
            Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PSModulePath", EnvironmentVariableTarget.Machine));
        void AssertLogonValue(System.Diagnostics.ProcessStartInfo info)
        {
            if (expected.Length > 0) Assert.Equal(expected, info.Environment["PSModulePath"]);
            else Assert.False(info.Environment.ContainsKey("PSModulePath"));
        }

        var inherited = new System.Diagnostics.ProcessStartInfo("powershell.exe");
        inherited.Environment["PSModulePath"] = @"C:\Program Files\PowerShell\7\Modules";
        WindowsPowerShellHost.ApplyLogonModulePath(inherited);
        AssertLogonValue(inherited);

        AssertLogonValue(ApplicationPackagerUI.Services.PackagerExecutionService.ChildStartInfo(["-NoProfile"], null));
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

    [Fact]
    public async Task AChildThatWritesMoreStandardErrorThanThePipeHoldsCompletes()
    {
        var run = await WindowsPowerShellHost.RunAsync(
            "[Console]::Error.Write(('e' * 200000))\r\n[Console]::Error.Flush()\r\nWrite-Output 'done'\r\nexit 4",
            null, default, "ap-host-stderr").WaitAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(4, run.ExitCode);
        Assert.Equal("done", run.StdOut);
        Assert.Equal(200000, run.StdErr.Count(c => c == 'e'));
    }

    [Fact]
    public async Task ACancelledChildIsEndedBeforeTheCallReturnsAndItsScriptIsRemoved()
    {
        using var cts = new CancellationTokenSource();
        var pid = 0;
        var running = WindowsPowerShellHost.RunAsync(
            "Write-Output $PID\r\nStart-Sleep -Seconds 300\r\nexit 0",
            line => { if (int.TryParse(line, out var id)) { pid = id; cts.Cancel(); } },
            cts.Token, "ap-host-cancel");

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(90)));
            Assert.NotEqual(0, pid);
            Assert.False(IsRunning(pid), "the cancelled Windows PowerShell child is still running");
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "ap-host-cancel-*.ps1"));
        }
        finally
        {
            if (pid != 0 && IsRunning(pid))
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(true); } catch (Exception) { }
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    [Fact]
    public void StandardInputIsWrittenAsUtf8WithoutAPreamble()
    {
        var info = WindowsPowerShellHost.BuildStartInfo(@"C:\Temp\a.ps1", null, redirectStandardInput: true);
        Assert.Equal(65001, info.StandardInputEncoding!.CodePage);
        Assert.Empty(info.StandardInputEncoding.GetPreamble());
    }
}
