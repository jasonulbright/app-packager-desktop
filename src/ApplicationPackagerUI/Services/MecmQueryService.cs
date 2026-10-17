using System.Diagnostics;
using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Services;

public class MecmQueryService(SettingsService settingsService)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Runs from a file with the site code, the name and the provider passed as parameters,
    // so none is ever spliced into executable text. With a provider, the site drive is the
    // one rooted at that provider: a drive the console left for another provider of the
    // same site code is replaced, so the answer comes from the provider the caller names.
    public const string QueryScript = """
        param($SiteCode, $CMName, $ProviderMachineName)
        if (-not (Get-Command -Name Get-CMApplication -ErrorAction SilentlyContinue)) {
            try {
                if ($env:SMS_ADMIN_UI_PATH) {
                    $cmModule = Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH) 'ConfigurationManager.psd1'
                    if (Test-Path -LiteralPath $cmModule) { Import-Module $cmModule -Force -ErrorAction Stop }
                }
            } catch {}
        }
        if (-not (Get-Command -Name Get-CMApplication -ErrorAction SilentlyContinue)) {
            Write-Output 'ERROR:CM_CMDLETS_NOT_AVAILABLE'; exit 3
        }
        function Test-SameProviderHost {
            param([string]$Left, [string]$Right)
            $a = $Left.Trim().TrimEnd('\').ToLowerInvariant()
            $b = $Right.Trim().TrimEnd('\').ToLowerInvariant()
            if ($a -eq $b) { return $true }
            # A short name and the fully qualified name of the same host.
            if ($a.Contains('.') -eq $b.Contains('.')) { return $false }
            return (($a -split '\.')[0] -eq ($b -split '\.')[0])
        }
        $provider = ([string]$ProviderMachineName).Trim()
        if ($provider) {
            try {
                $drive = Get-PSDrive -Name $SiteCode -ErrorAction SilentlyContinue
                if ($drive -and -not (Test-SameProviderHost -Left ([string]$drive.Root) -Right $provider)) {
                    Remove-PSDrive -Name $SiteCode -Force -ErrorAction Stop
                    $drive = $null
                }
                if (-not $drive) {
                    New-PSDrive -Name $SiteCode -PSProvider CMSite -Root $provider -ErrorAction Stop | Out-Null
                }
            } catch {
                Write-Output 'ERROR:SITE_DRIVE_FAILED'; exit 4
            }
        }
        try { Set-Location "${SiteCode}:" -ErrorAction Stop } catch {
            Write-Output 'ERROR:SITE_DRIVE_FAILED'; exit 4
        }
        """ + "\n" + MecmApplicationTitleRule.PowerShellFunctions + "\n" + """
        try {
            $apps = @(Get-CMApplication -Name $CMName -ErrorAction Stop)
            if (-not $apps -or $apps.Count -eq 0) {
                # The wildcard between the words also returns a title that carries the version mid-name.
                $apps = @(Get-CMApplication -Name (Get-MecmApplicationNamePattern -CMName $CMName) -ErrorAction Stop | Where-Object {
                        $title = if ($_.LocalizedDisplayName) { [string]$_.LocalizedDisplayName } else { [string]$_.Name }
                        Test-MecmApplicationTitle -CMName $CMName -Title $title
                    })
            }
        } catch {
            Write-Output 'ERROR:QUERY_FAILED'; exit 5
        }
        if (-not $apps -or $apps.Count -eq 0) {
            Write-Output 'NOT_FOUND'; exit 0
        }
        $exact = $apps | Where-Object { $_.LocalizedDisplayName -eq $CMName -or $_.Name -eq $CMName }
        if ($exact -and @($exact).Count -gt 0) { $chosen = @($exact)[0] }
        else {
            $parsable = @(); $nonParsable = @()
            foreach ($a in $apps) {
                try { $null = [version]([string]$a.SoftwareVersion -replace '^(\d+)$', '$1.0'); $parsable += $a } catch { $nonParsable += $a }
            }
            if ($parsable.Count -gt 0) {
                $chosen = $parsable | Sort-Object { [version]([string]$_.SoftwareVersion -replace '^(\d+)$', '$1.0') } -Descending | Select-Object -First 1
            } else {
                $chosen = $nonParsable | Sort-Object Name -Descending | Select-Object -First 1
            }
        }
        Write-Output "FOUND|$($chosen.LocalizedDisplayName)|$($chosen.SoftwareVersion)|$($apps.Count)"
        exit 0
        """;

    /// <summary>The query process: the site, the provider and the name are argument values, never script text.</summary>
    public static ProcessStartInfo BuildStartInfo(string scriptFile, string? siteCode, string? providerMachine, string? cmName)
    {
        var startInfo = new ProcessStartInfo { FileName = "powershell.exe" };
        WindowsPowerShellHost.ApplyLogonModulePath(startInfo);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptFile);
        startInfo.ArgumentList.Add("-SiteCode");
        startInfo.ArgumentList.Add(siteCode ?? "");
        startInfo.ArgumentList.Add("-CMName");
        startInfo.ArgumentList.Add(cmName ?? "");
        // An empty value would not survive the -File argument parsing as a bound parameter.
        if (!string.IsNullOrWhiteSpace(providerMachine))
        {
            startInfo.ArgumentList.Add("-ProviderMachineName");
            startInfo.ArgumentList.Add(providerMachine.Trim());
        }
        return startInfo;
    }

    public async Task<MecmQueryResult> QueryVersionAsync(string cmName, CancellationToken ct = default)
    {
        var settings = settingsService.Load();

        var scriptFile = Path.Combine(Path.GetTempPath(), $"packr-cmquery-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptFile, QueryScript, ct).ConfigureAwait(false);
            var startInfo = BuildStartInfo(scriptFile, settings.SiteCode, settings.ProviderMachine, cmName);

            var run = await BoundedProcessRunner.RunAsync(startInfo, Timeout, ct).ConfigureAwait(false);

            if (run.TimedOut)
                return MecmQueryResult.Failure($"query timed out after {Timeout.TotalSeconds:0}s");

            return MecmQueryClassifier.Classify(run.ExitCode, run.StdOut, run.StdErr);
        }
        finally
        {
            try { File.Delete(scriptFile); } catch (IOException) { }
        }
    }
}
