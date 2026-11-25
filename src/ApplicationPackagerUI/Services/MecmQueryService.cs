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
        param($SiteCode, $CMName, $ProviderMachineName, $ArpName)
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
        function Get-MecmTitleExtraWordCount {
            param([string]$CMName, [string]$Title)
            $lead = $CMName.Trim()
            if (-not $Title.StartsWith($lead, [System.StringComparison]::OrdinalIgnoreCase)) { return 0 }
            $inner = '^(x64|x86|arm64|amd64|32-bit|64-bit|exe|msi|v?\d+([.\-+_]\w+)*|[a-z]{2}-[a-z]{2}|latest)$'
            $extra = 0
            foreach ($m in [regex]::Matches($Title.Substring($lead.Length), '\([^)]*\)|\S+')) {
                $t = $m.Value
                if ($t -match '^\((.*)\)$') {
                    $words = @($Matches[1] -split '[\s,+/]+' | Where-Object { $_ })
                    if (@($words | Where-Object { $_ -notmatch $inner }).Count -gt 0) { $extra++ }
                    continue
                }
                if ($t -match '^-?(x64|x86|arm64|amd64|latest)?$' -or $t -match '^-?v?\d+([.\-+_]\w+)*$') { continue }
                $extra++
            }
            return $extra
        }
        $tier = 'rule'
        try {
            $apps = @(Get-CMApplication -Name $CMName -ErrorAction Stop)
            if (-not $apps -or $apps.Count -eq 0) {
                # The wildcard between the words also returns a title that carries the version mid-name.
                $apps = @(Get-CMApplication -Name (Get-MecmApplicationNamePattern -CMName $CMName) -ErrorAction Stop | Where-Object {
                        $title = if ($_.LocalizedDisplayName) { [string]$_.LocalizedDisplayName } else { [string]$_.Name }
                        Test-MecmApplicationTitle -CMName $CMName -Title $title
                    })
            }
            if (-not $apps -or $apps.Count -eq 0) {
                # The PowerShell app creates the name of an installer ("PuTTY release 0.85 (64-bit)", "Git 2.47.1 (x64)"),
                # and the rule above allows release details only. Last tier: the words of CMName at the start of the title,
                # any words after them; the next character must end the word, so "Git" does not find "GitHub Desktop".
                $tier = 'prefix'
                $apps = @(Get-CMApplication -Name (([string]$CMName).Trim() + '*') -ErrorAction Stop | Where-Object {
                        $title = if ($_.LocalizedDisplayName) { [string]$_.LocalizedDisplayName } else { [string]$_.Name }
                        $lead = ([string]$CMName).Trim()
                        $title.StartsWith($lead, [System.StringComparison]::OrdinalIgnoreCase) -and ($title.Length -eq $lead.Length -or -not [char]::IsLetterOrDigit($title[$lead.Length]))
                    })
            }
            if (-not $apps -or $apps.Count -eq 0) {
                # The installer names the application by its Add/Remove Programs name (ArpName of the definition, patterns joined by " | ",
                # for example "Microsoft Azure PowerShell - *" or "IntuneDebugTools*"). The text before the first wildcard asks the site; -like decides.
                $tier = 'arp'
                $found = @()
                foreach ($pattern in (([string]$ArpName) -split '\s*\|\s*' | Where-Object { $_ -and $_ -ne 'none' })) {
                    $head = ($pattern -split '[*?\[]')[0].TrimEnd()
                    if ($head.Length -lt 3) { continue }
                    $found += @(Get-CMApplication -Name ($head + '*') -ErrorAction Stop | Where-Object {
                            $title = if ($_.LocalizedDisplayName) { [string]$_.LocalizedDisplayName } else { [string]$_.Name }
                            $title -like $pattern
                        })
                }
                $apps = @($found | Sort-Object LocalizedDisplayName -Unique)
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
            # Best fit: the title with the fewest words after CMName that are not release details ("PowerShell 7-x64" before
            # "PowerShell 7 (Devolutions)"); then the newest version. The PowerShell app picks the same way.
            $scored = @($apps | ForEach-Object {
                    $title = if ($_.LocalizedDisplayName) { [string]$_.LocalizedDisplayName } else { [string]$_.Name }
                    $ver = try { [version]([string]$_.SoftwareVersion -replace '^(\d+)$', '$1.0') } catch { [version]'0.0' }
                    [pscustomobject]@{ App = $_; Title = $title; Extra = (Get-MecmTitleExtraWordCount -CMName $CMName -Title $title); Ver = $ver }
                })
            $chosen = ($scored | Sort-Object @{ Expression = 'Extra'; Descending = $false }, @{ Expression = 'Ver'; Descending = $true }, @{ Expression = 'Title'; Descending = $true } | Select-Object -First 1).App
        }
        $note = ''
        if ($tier -in 'prefix','arp') {
            $others = @($apps | Where-Object { [string]$_.LocalizedDisplayName -ne [string]$chosen.LocalizedDisplayName } | ForEach-Object { ([string]$_.LocalizedDisplayName) -replace '[|;]', ' ' })
            $note = "tier=$tier; chose '$((([string]$chosen.LocalizedDisplayName) -replace '[|;]', ' '))'" + $(if ($others.Count) { "; other matches: " + ($others -join '; ') } else { '' })
        }
        $suffix = if ($note) { "|$note" } else { '' }
        Write-Output "FOUND|$($chosen.LocalizedDisplayName)|$($chosen.SoftwareVersion)|$($apps.Count)$suffix"
        exit 0
        """;

    /// <summary>The query process: the site, the provider and the name are argument values, never script text.</summary>
    public static ProcessStartInfo BuildStartInfo(string scriptFile, string? siteCode, string? providerMachine, string? cmName, string? arpName = null)
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
        if (!string.IsNullOrWhiteSpace(arpName))
        {
            startInfo.ArgumentList.Add("-ArpName");
            startInfo.ArgumentList.Add(arpName.Trim());
        }
        // An empty value would not survive the -File argument parsing as a bound parameter.
        if (!string.IsNullOrWhiteSpace(providerMachine))
        {
            startInfo.ArgumentList.Add("-ProviderMachineName");
            startInfo.ArgumentList.Add(providerMachine.Trim());
        }
        return startInfo;
    }

    public async Task<MecmQueryResult> QueryVersionAsync(string cmName, CancellationToken ct = default, string? arpName = null)
    {
        var settings = settingsService.Load();

        var scriptFile = Path.Combine(Path.GetTempPath(), $"packr-cmquery-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptFile, QueryScript, ct).ConfigureAwait(false);
            var startInfo = BuildStartInfo(scriptFile, settings.SiteCode, settings.ProviderMachine, cmName, arpName);

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
