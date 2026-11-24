<#
.SYNOPSIS
    Lab probe of Discover unmanaged software in the real app (Q-129): each scenario starts the app on a fresh data root with legacy single-site settings
    (the app migrates them into the per-site layout on start), clicks Discover all and Discover not selected, and prints what the user sees.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from a folder that holds the published app and these scripts:
        Run-VisibleProbe.ps1 -Worktree <folder> -ProbeArgs "& .\Probe-Discover.ps1 -Run C:\guide\discover-probe"
    Reads only: Discover reads the site database. Nothing is written to the site. The output has one RESULT line per scenario and button.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string]$AppFolder = $PSScriptRoot,
    [string[]]$Only = @(),
    [int]$WaitSeconds = 150
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$version = (Get-Item $exe).VersionInfo.ProductVersion
"APP $version"

# Name, settings the old single-site layout holds. SiteCode MCM and the provider cm01 are the lab site.
$scenarios = [ordered]@{
    'provider-lookup'  = @{ SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com' }
    'sql-override'     = @{ SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; DiscoverySqlServer = 'cm01.contoso.com'; DiscoverySqlDatabase = 'CM_MCM' }
    'sql-only-server'  = @{ SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; DiscoverySqlServer = 'cm01.contoso.com' }
    'sql-explicit-port'= @{ SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; DiscoverySqlServer = 'cm01.contoso.com,1433'; DiscoverySqlDatabase = 'CM_MCM' }
    'sql-wrong-db'     = @{ SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; DiscoverySqlServer = 'cm01.contoso.com'; DiscoverySqlDatabase = 'CM_XXX' }
    'sql-wrong-port'   = @{ SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; DiscoverySqlServer = 'cm01.contoso.com,1444'; DiscoverySqlDatabase = 'CM_MCM' }
    'sql-named-nohost' = @{ SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; DiscoverySqlServer = 'cm01.contoso.com\NOSUCH'; DiscoverySqlDatabase = 'CM_MCM' }
    'sql-unknown-host' = @{ SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; DiscoverySqlServer = 'NOHOST01'; DiscoverySqlDatabase = 'CM_MCM' }
    'provider-wrong'   = @{ SiteCode = 'MCM'; ProviderMachine = 'NOHOST01' }
    'site-wrong-code'  = @{ SiteCode = 'ZZZ'; ProviderMachine = 'cm01.contoso.com' }
}
if ($Only.Count) { foreach ($k in @($scenarios.Keys)) { if ($Only -notcontains $k) { $scenarios.Remove($k) } } }

function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Press([string]$id) { $e = Find-El $null $id -Timeout 5; if (-not $e) { return $false }; Click-El $e; Start-Sleep -Milliseconds 800; $true }

foreach ($name in $scenarios.Keys) {
    $data = Join-Path $Run "data-$name"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $staging = New-RunStaging $data
    $settings = @{ DownloadRoot = $staging; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    foreach ($k in $scenarios[$name].Keys) { $settings[$k] = $scenarios[$name][$k] }
    $script:GuideTheme = 'Light'
    $null = Start-Apd $exe $data 'Light' $settings
    try {
        (Main-Window).SetFocus()
        $w = Main-Window
        $null = Click-Nav 'Settings'; Start-Sleep 3
        $null = Press 'nav_configmgr'; Start-Sleep 2
        foreach ($button in 'btnDiscoverNotSelected', 'btnDiscoverAll') {
            $present = Find-El $null $button -Timeout 3
            if (-not $present) {
                # An older release has one Discover button on the App Packager page.
                $null = Click-Nav 'App Packager'; Start-Sleep 3
                $button = 'btnDiscover'
            }
            $enabled = (Find-El $null $button -Timeout 5)
            if (-not $enabled) { "RESULT $version $name $button : NO BUTTON"; continue }
            $isEnabled = $enabled.Current.IsEnabled
            $started = Get-Date
            Click-El $enabled
            $outcome = $null
            $onSettings = $true
            $deadline = (Get-Date).AddSeconds($WaitSeconds)
            $seen = ''
            while ((Get-Date) -lt $deadline -and -not $outcome) {
                Start-Sleep 3
                $prompt = Find-El $null 'txtDiscoverPrompt' -Timeout 1
                if ($prompt) { $outcome = 'PROMPT: ' + $prompt.Current.Name; $null = Press 'btnNotNow'; break }
                $here = Status-Text (Main-Window)
                if ($here) { $seen = $here }
                if ($here -and -not (Find-El $null 'btnCancelOperation' -Timeout 1)) { $outcome = 'STATUS: ' + $here; break }
                if ((Get-Date) - $started -gt [TimeSpan]::FromSeconds(20) -and $onSettings) {
                    # Look at the App Packager page, where the status line is.
                    $null = Click-Nav 'App Packager'; Start-Sleep 2; $onSettings = $false
                }
            }
            if (-not $outcome) { $outcome = 'TIMEOUT, status: ' + $seen }
            $visibleOnSettings = $false
            "RESULT $version $name $button enabled=$isEnabled $([int]((Get-Date) - $started).TotalSeconds)s : $outcome"
            if ($button -eq 'btnDiscover') { break }
            $null = Click-Nav 'Settings'; Start-Sleep 2; $null = Press 'nav_configmgr'; Start-Sleep 1
        }
        # The migration: the single-site settings must now be in the site folder, and still hold the SQL values.
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $site = Join-Path $data "Users\$sid\sites\$($scenarios[$name].SiteCode)\settings.json"
        if (Test-Path $site) {
            $j = Get-Content $site -Raw | ConvertFrom-Json
            "MIGRATED $name site settings: SiteCode=$($j.SiteCode) Provider=$($j.ProviderMachine) Sql=$($j.DiscoverySqlServer) Db=$($j.DiscoverySqlDatabase)"
        } else { "MIGRATED $name : no site settings file ($site)" }
    }
    catch { "ERROR $name : $($_.Exception.Message)" }
    finally { Close-OtherWindows; Stop-Apd }
}
