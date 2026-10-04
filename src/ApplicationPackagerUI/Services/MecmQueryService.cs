using System.Diagnostics;
using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Services;

public class MecmQueryService(SettingsService settingsService)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Runs from a file with the site code and name passed as parameters, so neither is
    // ever spliced into executable text.
    public const string QueryScript = """
        param($SiteCode, $CMName)
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
        try { Set-Location "${SiteCode}:" -ErrorAction Stop } catch {
            Write-Output 'ERROR:SITE_DRIVE_FAILED'; exit 4
        }
        function Test-MecmApplicationTitle {
            param([string]$CMName, [AllowEmptyString()][string]$Title)
            if ([string]::IsNullOrEmpty($Title) -or -not $Title.StartsWith($CMName, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
            $rest = $Title.Substring($CMName.Length)
            $continuation = if ($CMName -match '\d$') { '(\.\d[\w.+-]*)?' } else { '' }
            $detail = '\s+(\([^)]*\)|v?\d[\w.+-]*|x64|x86|arm64)'
            return ($rest -match ('^' + $continuation + '(' + $detail + ')*(\s+-\s+.*)?$'))
        }
        try {
            $apps = @(Get-CMApplication -Name $CMName -ErrorAction Stop)
            if (-not $apps -or $apps.Count -eq 0) {
                $apps = @(Get-CMApplication -Name "$CMName*" -ErrorAction Stop | Where-Object {
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

    public async Task<MecmQueryResult> QueryVersionAsync(string cmName, CancellationToken ct = default)
    {
        var siteCode = settingsService.Load().SiteCode;

        var scriptFile = Path.Combine(Path.GetTempPath(), $"packr-cmquery-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptFile, QueryScript, ct).ConfigureAwait(false);
            var startInfo = new ProcessStartInfo { FileName = "powershell.exe" };
            // Passed as argument values, never spliced into the script body.
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
