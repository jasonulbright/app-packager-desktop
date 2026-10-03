using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// The Extend-mode install.ps1 (or uninstall.ps1): it runs the before hook, the
/// packager's generated script and the after hook, each as its own powershell.exe
/// child, so an exit inside one step cannot skip the steps after it.
/// </summary>
public static class ExtendOrchestrator
{
    /// <summary>
    /// A non-zero step exit code ends the run with that code; 3010 from any step is
    /// held and becomes the exit code when every step otherwise succeeds. With
    /// <paramref name="afterRunsOnFailure"/> the after step also runs when the
    /// generated step fails, and the generated step's code still wins. A signed
    /// deployment launches the steps with no execution-policy argument.
    /// </summary>
    public static string Build(string? before, string generated, string? after,
        bool afterRunsOnFailure = false, bool signedDeployment = false, string scriptHost = DeploymentLauncher.HostX64)
    {
        var hostExpression = scriptHost == DeploymentLauncher.HostX86
            ? "Join-Path ([Environment]::GetFolderPath('Windows')) 'SysWOW64\\WindowsPowerShell\\v1.0\\powershell.exe'"
            : "'powershell.exe'";
        var policyArgument = signedDeployment ? "" : ", '-ExecutionPolicy', 'Bypass'";

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "$hostExe = " + hostExpression,
            "$rebootPending = $false",
            "function Invoke-Step {",
            "    param([string]$Name)",
            "    $script = Join-Path $PSScriptRoot $Name",
            "    if (-not (Test-Path -LiteralPath $script)) { throw \"Step script not found: $script\" }",
            $"    $stepArgs = @('-NoProfile', '-NonInteractive'{policyArgument}, '-File', $script)",
            "    $proc = Start-Process -FilePath $hostExe -ArgumentList $stepArgs -Wait -PassThru -NoNewWindow",
            "    return [int]$proc.ExitCode",
            "}",
            ""
        };

        if (!string.IsNullOrWhiteSpace(before))
        {
            lines.Add($"$code = Invoke-Step -Name '{before}'");
            lines.Add("if ($code -eq 3010) { $rebootPending = $true; $code = 0 }");
            lines.Add("if ($code -ne 0) { exit $code }");
        }

        lines.Add($"$code = Invoke-Step -Name '{generated}'");
        lines.Add("if ($code -eq 3010) { $rebootPending = $true; $code = 0 }");
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (afterRunsOnFailure)
            {
                lines.Add("$generatedCode = $code");
                lines.Add($"$afterCode = Invoke-Step -Name '{after}'");
                lines.Add("if ($afterCode -eq 3010) { $rebootPending = $true; $afterCode = 0 }");
                lines.Add("if ($generatedCode -ne 0) { exit $generatedCode }");
                lines.Add("if ($afterCode -ne 0) { exit $afterCode }");
            }
            else
            {
                lines.Add("if ($code -ne 0) { exit $code }");
                lines.Add($"$code = Invoke-Step -Name '{after}'");
                lines.Add("if ($code -eq 3010) { $rebootPending = $true; $code = 0 }");
                lines.Add("if ($code -ne 0) { exit $code }");
            }
        }
        else
        {
            lines.Add("if ($code -ne 0) { exit $code }");
        }
        lines.Add("if ($rebootPending) { exit 3010 }");
        lines.Add("exit 0");

        return string.Join("\r\n", lines) + "\r\n";
    }
}
