<#
.SYNOPSIS
    The lab check of Q-124: the Architecture column of Settings, Application Settings, and the One Click chain. The installed app starts in an isolated data folder with no Check Latest run,
    and the checks are driven with real mouse input. Each check says PASS or FAIL. The run ends with "CHAIN-FAILS=<n>".

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from a folder that holds the scripts of tools\guide-capture:
        Run-VisibleProbe.ps1 -Worktree <folder with these scripts> -ProbeArgs "& .\Probe-OneClickChain.ps1 -Run C:\guide\runs\apd-q124"
    The run reads and changes nothing on the site (the action of One Click is Report only). Per theme:
      1. Settings, Application Settings shows the column Architecture and the column Install scope, and no column Package source;
      2. with no Check Latest run, One Click runs Check Latest, Check ConfigMgr and Check CVEs before its plan opens (the session log shows the three finished lines in that order);
      3. the plan window opens after the chain and shows the version of the product (a screenshot is saved under <Run>\checks\<theme>).
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string]$FileShareRoot = '\cm01.contoso.com\ContentShare$'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$sevenZipId = 'fa505d1415214f14'
$script:Fails = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') { if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" } }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Check-Shot([string]$Theme, [string]$Id) { Move-MouseAway; Save-Shot (Join-Path $Run (Join-Path 'checks' (Join-Path $Theme.ToLower() ($Id + '.png')))) }
function Session-Log([string]$Data) {
    $f = Get-ChildItem $Data -Recurse -Filter packr.log -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($f) { Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue } else { '' }
}
function Run-Check([string]$Name, [scriptblock]$Body) { try { & $Body } catch { $script:Fails++; "FAIL ${Name}: $($_.Exception.Message)" } }

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $staging = New-RunStaging $data
    $settings = @{
        DownloadRoot = $staging; FileShareRoot = $FileShareRoot; SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; SiteCodeSaved = $true
        Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true }; CheckForUpdatesAtStartup = $false
        OneClick = @{ Action = 'Report'; TrackedApplications = @($sevenZipId) }
    }
    $proc = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Start-Sleep 3
        Run-Check "$theme-01" {
            if (-not (Click-Nav 'Settings')) { throw 'The sidebar has no Settings entry.' }
            Start-Sleep 3
            Real-Click (Find-El $w 'nav_supersedence' -Timeout 10); Start-Sleep 2
            $grid = Find-El $w 'dgSupersedenceApps' -Timeout 10
            Check "$theme-01a the grid of Application Settings shows" ($null -ne $grid) 'no grid'
            Check "$theme-01b the column Architecture shows" ($null -ne (Find-El $grid -Name 'Architecture' -Timeout 5)) 'no Architecture header'
            Check "$theme-01c the column Install scope shows" ($null -ne (Find-El $grid -Name 'Install scope' -Timeout 5)) 'no Install scope header'
            Check "$theme-01d the column Package source is gone" ($null -eq (Find-El $grid -Name 'Package source' -Timeout 2)) 'Package source still shows'
            Check-Shot $theme 'architecture-column'
            $null = Click-Nav 'App Packager'; Start-Sleep 3
        }
        Run-Check "$theme-02" {
            $w = Main-Window
            $before = Session-Log $data
            Check "$theme-02a no Check Latest ran before One Click" ($before -notmatch 'Latest version:') 'the log has a Latest version line'
            Real-Click (Find-El $w 'btnOneClick')
            $deadline = (Get-Date).AddSeconds(420)
            while ((Get-Date) -lt $deadline -and (Session-Log $data) -notmatch 'the plan opens for') { Start-Sleep 3 }
            Start-Sleep 12
            $log = Session-Log $data
            $i1 = $log.IndexOf('Latest version:'); $i2 = $log.IndexOf('ConfigMgr: 7-Zip'); $i3 = $log.IndexOf('CVEs: 7-Zip')
            Check "$theme-02b the chain ran Check Latest, Check ConfigMgr and Check CVEs in that order" ($i1 -ge 0 -and $i2 -gt $i1 -and $i3 -gt $i2) "positions: $i1, $i2, $i3"
            Check "$theme-02c the log shows the latest version of 7-Zip" ($log -match 'Latest version: \S+') 'no Latest version line'
            Check "$theme-02d the plan opened after the checks" ($log -match 'the checks are done, the plan opens for 1 application') 'no plan line'
            Check-Shot $theme 'one-click-plan'
            if (-not (Close-ApdWindow 'One Click')) { throw 'The One Click window did not close.' }
            Start-Sleep 1
        }
    }
    finally { Stop-Apd }
}
"CHAIN-FAILS=$script:Fails"
