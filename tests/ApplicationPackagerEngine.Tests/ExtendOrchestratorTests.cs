using System.Diagnostics;
using System.Text;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Extend-mode orchestrator runs under Windows PowerShell 5.1 with stand-in
/// step scripts: exit codes propagate, 3010 folds through to the end, each step
/// runs in its own process, and a signed deployment passes no execution policy.
/// The stage folder path carries a space and an apostrophe, as a content path can.
/// </summary>
public sealed class ExtendOrchestratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apwb extend it's " + Guid.NewGuid().ToString("N"));

    public ExtendOrchestratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Stage(params (string Name, string Body)[] steps)
    {
        var stage = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        foreach (var (name, body) in steps)
            File.WriteAllText(Path.Combine(stage, name), body, new UTF8Encoding(false));
        return stage;
    }

    /// <summary>Runs install.ps1 the way install.bat does and returns its exit code.</summary>
    private static int Run(string stage, string orchestrator)
    {
        File.WriteAllText(Path.Combine(stage, "install.ps1"), orchestrator, Encoding.ASCII);
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        psi.Environment.Remove("PSModulePath");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(stage, "install.ps1") })
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(120_000), "Windows PowerShell did not return");
        return proc.ExitCode;
    }

    [Fact]
    public void AFailingGeneratedStepExitCodePropagates()
    {
        var stage = Stage(("install-generated.ps1", "exit 7"));
        Assert.Equal(7, Run(stage, ExtendOrchestrator.Build(null, "install-generated.ps1", null)));
    }

    [Fact]
    public void ANonZeroGeneratedStepStopsTheLaterSteps()
    {
        var marker = Path.Combine(_dir, "after-" + Guid.NewGuid().ToString("N") + ".txt");
        var stage = Stage(
            ("install-before.ps1", "exit 0"),
            ("install-generated.ps1", "exit 1603"),
            ("install-after.ps1", $"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value 'ran'; exit 0"));

        Assert.Equal(1603, Run(stage, ExtendOrchestrator.Build("install-before.ps1", "install-generated.ps1", "install-after.ps1")));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void AFailingBeforeHookStopsTheGeneratedStep()
    {
        var marker = Path.Combine(_dir, "generated-" + Guid.NewGuid().ToString("N") + ".txt");
        var stage = Stage(
            ("install-before.ps1", "exit 4"),
            ("install-generated.ps1", $"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value 'ran'; exit 0"));

        Assert.Equal(4, Run(stage, ExtendOrchestrator.Build("install-before.ps1", "install-generated.ps1", null)));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void A3010FromAHookBecomesTheFinalCodeWhenEverythingElseSucceeds()
    {
        var stage = Stage(("install-generated.ps1", "exit 0"), ("install-after.ps1", "exit 3010"));
        Assert.Equal(3010, Run(stage, ExtendOrchestrator.Build(null, "install-generated.ps1", "install-after.ps1")));
    }

    [Fact]
    public void A3010FromTheGeneratedStepFoldsThroughTheAfterStep()
    {
        var marker = Path.Combine(_dir, "after-" + Guid.NewGuid().ToString("N") + ".txt");
        var stage = Stage(
            ("install-before.ps1", "exit 0"),
            ("install-generated.ps1", "exit 3010"),
            ("install-after.ps1", $"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value 'ran'; exit 0"));

        Assert.Equal(3010, Run(stage, ExtendOrchestrator.Build("install-before.ps1", "install-generated.ps1", "install-after.ps1")));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void EveryStepRunsInItsOwnProcessSoAnEarlyExitCannotSkipTheRest()
    {
        var marker = Path.Combine(_dir, "after-" + Guid.NewGuid().ToString("N") + ".txt");
        var stage = Stage(
            ("install-before.ps1", "[Environment]::Exit(0)"),
            ("install-generated.ps1", "exit 0"),
            ("install-after.ps1", $"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value 'ran'; exit 0"));

        Assert.Equal(0, Run(stage, ExtendOrchestrator.Build("install-before.ps1", "install-generated.ps1", "install-after.ps1")));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void TheAfterStepRunsOnFailureWhenAskedAndTheFailingCodeStillWins()
    {
        var marker = Path.Combine(_dir, "cleanup-" + Guid.NewGuid().ToString("N") + ".txt");
        var stage = Stage(
            ("install-generated.ps1", "exit 5"),
            ("install-after.ps1", $"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value 'ran'; exit 0"));

        Assert.Equal(5, Run(stage, ExtendOrchestrator.Build(null, "install-generated.ps1", "install-after.ps1", afterRunsOnFailure: true)));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void SignedDeploymentOmitsTheExecutionPolicyArgument()
    {
        Assert.DoesNotContain("ExecutionPolicy", ExtendOrchestrator.Build(null, "install-generated.ps1", null, signedDeployment: true));
        Assert.Contains("'-ExecutionPolicy', 'Bypass'", ExtendOrchestrator.Build(null, "install-generated.ps1", null, signedDeployment: false));
    }

    [Fact]
    public void TheX86HostLaunchesTheSysWow64WindowsPowerShell()
    {
        Assert.Contains(@"'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'", ExtendOrchestrator.Build(null, "g.ps1", null, scriptHost: "x86"));
        Assert.Contains("$hostExe = 'powershell.exe'", ExtendOrchestrator.Build(null, "g.ps1", null));
    }
}
