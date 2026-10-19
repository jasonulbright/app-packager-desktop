using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

/// <summary>Real Windows PowerShell child processes.</summary>
public sealed class DetectionScriptHostTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-dtrun-" + Guid.NewGuid().ToString("N"));

    private string RunFolder => Path.Combine(_root, "run");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private WindowsPowerShellDetectionScriptHost Host(int outputLimit = WindowsPowerShellDetectionScriptHost.DefaultOutputLimit) =>
        new(outputLimit, RunFolder);

    private sealed class RecordingJob : IDisposable
    {
        public int Disposed;
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    [Fact]
    public async Task StdoutStderrAndTheExitCodeAreCaptured()
    {
        var run = await Host().RunAsync(
            "Write-Output 'Installed'\n[Console]::Error.WriteLine('warned')\nexit 3", Generous, CancellationToken.None);

        Assert.Equal("Installed", run.StdOut);
        Assert.Contains("warned", run.StdErr);
        Assert.Equal(3, run.ExitCode);
        Assert.False(run.TimedOut);
        Assert.False(run.OutputTruncated);
    }

    [Fact]
    public async Task AScriptThatPrintsNothingExitsZeroWithEmptyOutput()
    {
        var run = await Host().RunAsync("$x = 1", Generous, CancellationToken.None);

        Assert.Equal("", run.StdOut);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task WriteHostReachesStdout()
    {
        var run = await Host().RunAsync("Write-Host 'found'", Generous, CancellationToken.None);

        Assert.Equal("found", run.StdOut);
    }

    [Fact]
    public async Task AScriptThatReadsTheHostGetsEndOfInputInsteadOfWaiting()
    {
        var run = await Host().RunAsync(
            "$a = Read-Host 'Name'\n$b = [Console]::ReadLine()\nif ($a -or $b) { 'got input' } else { 'no input' }",
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(run.TimedOut);
        Assert.Equal("no input", run.StdOut);
    }

    [Fact]
    public async Task ATerminatingErrorIsANonZeroExitWithTheErrorOnStderr()
    {
        var run = await Host().RunAsync("throw 'broken rule'", Generous, CancellationToken.None);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("broken rule", run.StdErr);
    }

    [Fact]
    public async Task OutputPastTheLimitIsCutAndTheRunStillEnds()
    {
        var run = await Host(outputLimit: 1000).RunAsync(
            "Write-Output ('x' * 200000)", Generous, CancellationToken.None);

        Assert.True(run.OutputTruncated);
        Assert.True(run.StdOut.Length <= 1000);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task TheTimeoutEndsTheProcessTreeAndReturnsATimedOutRun()
    {
        const string script = """
            $child = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -WindowStyle Hidden -PassThru
            [Console]::Out.WriteLine($child.Id)
            [Console]::Out.Flush()
            Start-Sleep 120
            """;
        var watch = Stopwatch.StartNew();

        var run = await Host().RunAsync(script, TimeSpan.FromSeconds(8), CancellationToken.None);

        Assert.True(run.TimedOut);
        Assert.Null(run.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
        Assert.True(int.TryParse(run.StdOut.Trim(), out var childId), $"stdout: '{run.StdOut}'");
        Assert.True(HasExited(childId));
    }

    [Fact]
    public async Task CancellationEndsTheRunAndThrows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Host().RunAsync("Start-Sleep 120", Generous, cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task TheScriptFileIsWrittenInTheRunFolderAndRemovedAfterTheRun()
    {
        string? written = null;
        var existedDuringRun = false;
        var host = new WindowsPowerShellDetectionScriptHost(runFolder: RunFolder)
        {
            ScriptFileCreated = path => { written = path; existedDuringRun = File.Exists(path); }
        };

        var run = await host.RunAsync("'x'", Generous, CancellationToken.None);

        Assert.Equal("x", run.StdOut);
        Assert.NotNull(written);
        Assert.True(existedDuringRun);
        Assert.Equal(RunFolder, Path.GetDirectoryName(written), ignoreCase: true);
        Assert.Matches(@"^ap-detection-[0-9a-f]{32}\.ps1$", Path.GetFileName(written));
        Assert.False(File.Exists(written));
    }

    [Fact]
    public void TheDefaultRunFolderIsUnderTheDataRoot()
    {
        Assert.Equal(Path.Combine(AppDataPaths.Root, "DetectionTester", "run"),
            WindowsPowerShellDetectionScriptHost.DefaultRunFolder(), ignoreCase: true);
    }

    [Fact]
    public async Task TheFirstRunRemovesScriptFilesOlderThanADayAndKeepsTheRest()
    {
        Directory.CreateDirectory(RunFolder);
        var stale = Path.Combine(RunFolder, "ap-detection-" + Guid.NewGuid().ToString("N") + ".ps1");
        var recent = Path.Combine(RunFolder, "ap-detection-" + Guid.NewGuid().ToString("N") + ".ps1");
        var other = Path.Combine(RunFolder, "notes.txt");
        foreach (var file in new[] { stale, recent, other }) File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-2));
        var host = Host();

        await host.RunAsync("'x'", Generous, CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));

        var staleAgain = Path.Combine(RunFolder, "ap-detection-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(staleAgain, "x");
        File.SetLastWriteTimeUtc(staleAgain, DateTime.UtcNow.AddDays(-2));
        await host.RunAsync("'x'", Generous, CancellationToken.None);
        Assert.True(File.Exists(staleAgain));
    }

    [Fact]
    public async Task ARunFolderThatIsAJunctionIsRefusedAndNothingRuns()
    {
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(_root);
        Junctions.Create(RunFolder, target);
        var started = false;
        var host = new WindowsPowerShellDetectionScriptHost(runFolder: RunFolder) { AssignJob = p => { started = true; return new RecordingJob(); } };

        var ex = await Assert.ThrowsAsync<DetectionScriptRefusedException>(() => host.RunAsync("'x'", Generous, CancellationToken.None));

        Assert.Equal(DetectionScriptRefusedException.RunFolderIsLink, ex.Message);
        Assert.False(started);
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public async Task ARunIsRefusedAndTheProcessEndedWhenItCannotBeContained()
    {
        int? processId = null;
        var host = new WindowsPowerShellDetectionScriptHost(runFolder: RunFolder)
        {
            AssignJob = process => { processId = process.Id; return null; }
        };

        var ex = await Assert.ThrowsAsync<DetectionScriptRefusedException>(() =>
            host.RunAsync("Start-Sleep 120", Generous, CancellationToken.None));

        Assert.Equal(DetectionScriptRefusedException.NoContainment, ex.Message);
        Assert.NotNull(processId);
        Assert.True(HasExited(processId.Value));
        Assert.Empty(Directory.GetFiles(RunFolder, "ap-detection-*.ps1"));
    }

    [Fact]
    public async Task ACancelDuringTheOutputDrainEndsTheRunAndTheJob()
    {
        var childIdFile = Path.Combine(_root, "child.txt");
        Directory.CreateDirectory(_root);
        var script = $$"""
            $child = Start-Process -FilePath cmd.exe -ArgumentList '/c ping -n 20 127.0.0.1 >nul' -NoNewWindow -PassThru
            Set-Content -LiteralPath '{{childIdFile}}' -Value $child.Id
            """;
        var job = new RecordingJob();
        using var cts = new CancellationTokenSource();
        Stopwatch? sinceDrain = null;
        var host = new WindowsPowerShellDetectionScriptHost(runFolder: RunFolder)
        {
            AssignJob = _ => job,
            DrainStarted = () => { sinceDrain = Stopwatch.StartNew(); cts.Cancel(); }
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(script, Generous, cts.Token));

            Assert.NotNull(sinceDrain);
            Assert.True(sinceDrain.Elapsed < WindowsPowerShellDetectionScriptHost.OutputDrainTimeout, $"elapsed {sinceDrain.Elapsed}");
            Assert.True(job.Disposed >= 1);
        }
        finally
        {
            if (File.Exists(childIdFile) && int.TryParse(File.ReadAllText(childIdFile).Trim(), out var childId))
            {
                try { using var child = Process.GetProcessById(childId); child.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    [Fact]
    public async Task AChildThatKeepsTheOutputPipeOpenDoesNotHoldTheRun()
    {
        const string script = """
            $child = Start-Process -FilePath cmd.exe -ArgumentList '/c ping -n 30 127.0.0.1 >nul' -NoNewWindow -PassThru
            [Console]::Out.WriteLine($child.Id)
            """;
        var watch = Stopwatch.StartNew();

        var run = await Host().RunAsync(script, Generous, CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"elapsed {watch.Elapsed}");
        Assert.False(run.TimedOut);
        Assert.Equal(0, run.ExitCode);
        Assert.True(int.TryParse(run.StdOut.Trim(), out var childId), $"stdout: '{run.StdOut}'");
        Assert.True(HasExited(childId));
    }

    [Fact]
    public async Task AChildLeftRunningByAScriptThatExitedIsEnded()
    {
        const string script = """
            $child = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -WindowStyle Hidden -PassThru
            [Console]::Out.WriteLine($child.Id)
            """;

        var run = await Host().RunAsync(script, Generous, CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.True(int.TryParse(run.StdOut.Trim(), out var childId), $"stdout: '{run.StdOut}'");
        Assert.True(HasExited(childId));
    }

    [Fact]
    public async Task NonAsciiTextComesBackUnchanged()
    {
        const string name = "Contoso Café Ünïcödé 日本語 Ωmega";

        var run = await Host().RunAsync(
            $"Write-Output '{name}'\nWrite-Host '{name}'\n[Console]::Error.WriteLine('{name}')", Generous, CancellationToken.None);

        Assert.Equal($"{name}{Environment.NewLine}{name}", run.StdOut);
        Assert.Contains(name, run.StdErr);
    }

    [Fact]
    public async Task AScriptThatStartsWithAUsingStatementStillRuns()
    {
        var run = await Host().RunAsync(
            "using namespace System.Text\n[StringBuilder]::new('ok').ToString()", Generous, CancellationToken.None);

        Assert.Equal("ok", run.StdOut);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task TheCapDoesNotSplitASurrogatePair()
    {
        var run = await Host(outputLimit: 3).RunAsync(
            "Write-Output ('ab' + ([char]::ConvertFromUtf32(0x1F600) * 4))", Generous, CancellationToken.None);

        Assert.True(run.OutputTruncated);
        Assert.Equal("ab", run.StdOut);
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(10_000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
