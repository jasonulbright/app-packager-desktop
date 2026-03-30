using System.Diagnostics;

namespace ApplicationPackagerUI.Services;

public sealed class MecmQueryResult
{
    public bool Found { get; init; }
    public string? DisplayName { get; init; }
    public string? SoftwareVersion { get; init; }
    public int MatchCount { get; init; }
}

public class MecmQueryService(SettingsService settingsService)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Inline PS script that replicates Get-MecmCurrentVersionByCMName from start-apppackager.ps1
    private const string QueryScript = """
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
            Write-Output 'ERROR:CM_CMDLETS_NOT_AVAILABLE'; exit 0
        }
        try { Set-Location "${SiteCode}:" -ErrorAction Stop } catch {
            Write-Output 'ERROR:SITE_DRIVE_FAILED'; exit 0
        }
        $apps = @(Get-CMApplication -Name $CMName -ErrorAction SilentlyContinue)
        if (-not $apps -or $apps.Count -eq 0) {
            $apps = @(Get-CMApplication -Name "$CMName*" -ErrorAction SilentlyContinue)
        }
        if (-not $apps -or $apps.Count -eq 0) {
            Write-Output 'NOT_FOUND'; exit 0
        }
        $exact = $apps | Where-Object { $_.LocalizedDisplayName -eq $CMName -or $_.Name -eq $CMName }
        if ($exact -and @($exact).Count -gt 0) { $chosen = @($exact)[0] }
        else {
            $parsable = @(); $nonParsable = @()
            foreach ($a in $apps) {
                try { $null = [version]$a.SoftwareVersion; $parsable += $a } catch { $nonParsable += $a }
            }
            if ($parsable.Count -gt 0) {
                $chosen = $parsable | Sort-Object { [version]$_.SoftwareVersion } -Descending | Select-Object -First 1
            } else {
                $chosen = $nonParsable | Sort-Object Name -Descending | Select-Object -First 1
            }
        }
        Write-Output "FOUND|$($chosen.LocalizedDisplayName)|$($chosen.SoftwareVersion)|$($apps.Count)"
        """;

    public async Task<MecmQueryResult> QueryVersionAsync(string cmName, CancellationToken ct = default)
    {
        var settings = settingsService.Load();
        var siteCode = settings.SiteCode;

        // Escape for -Command invocation
        var escapedScript = QueryScript.Replace("\"", "\\\"");
        var args = $"-NoProfile -ExecutionPolicy Bypass -Command \"& {{ {escapedScript} }} -SiteCode '{siteCode}' -CMName '{cmName}'\"";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(Timeout, ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return new MecmQueryResult { Found = false };
        }

        var stdout = (await stdoutTask).Trim();

        if (stdout.StartsWith("NOT_FOUND") || stdout.StartsWith("ERROR:"))
            return new MecmQueryResult { Found = false };

        if (stdout.StartsWith("FOUND|"))
        {
            var parts = stdout.Split('|');
            return new MecmQueryResult
            {
                Found = true,
                DisplayName = parts.Length > 1 ? parts[1] : null,
                SoftwareVersion = parts.Length > 2 ? parts[2] : null,
                MatchCount = parts.Length > 3 && int.TryParse(parts[3], out var mc) ? mc : 1
            };
        }

        return new MecmQueryResult { Found = false };
    }
}
