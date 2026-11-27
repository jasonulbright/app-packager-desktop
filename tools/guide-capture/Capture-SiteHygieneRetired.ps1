<#
.SYNOPSIS
    The lab capture run of the Show blockers window of Site Hygiene (Q-89): drives the real app on the lab client against the ConfigMgr site, in one theme.
    It scans, opens the Show blockers window for the retired application that a task sequence installs, and saves the image. It runs no fix.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-InSession.ps1 takes it), from the folder of the published app with these scripts:
        & .\Capture-SiteHygieneRetired.ps1 -Theme Light -Run <work folder>
    The image is <Run>\images\<theme>\tools-site-hygiene-blockers.png. The run changes nothing on the site.
    The site must hold the retired application -BlockedApp, which a task sequence installs and another application depends on
    (tools/guide-capture/lab-seed, part Blockers).
#>
param(
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light',
    # The retired application of the Show blockers image (tools/guide-capture/lab-seed, part Blockers).
    [string]$BlockedApp = 'ZZAPD Blocker Retired',
    [string]$Run = (Join-Path $PSScriptRoot 'out'),
    [string]$AppFolder = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')

$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$data = Join-Path $Run "data-$Theme"
$out = Join-Path $Run (Join-Path 'images' $Theme.ToLower())
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
# The output of a run that stops with an error is not returned by the runner, so the run keeps its own transcript.
Start-Transcript -Path (Join-Path $out ('run-' + $Theme + '.log')) -Force | Out-Null

function Shot([string]$Id) {
    Move-MouseAway
    Save-Shot (Join-Path $out ($Id + '.png')) -MaskFooter
    "OK $Theme $Id"
}

# The cell of the findings grid whose name matches the pattern; null when none does.
function Find-Cell([string]$Like, [double]$Timeout = 20) {
    $grid = Find-El $null 'dgFindings' -Timeout 10
    if (-not $grid) { return $null }
    $deadline = (Get-Date).AddSeconds($Timeout)
    do {
        foreach ($e in $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($e.Current.Name -like $Like -and -not $e.Current.IsOffscreen) { return $e }
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    $null
}

function Show-Filter([string]$Text) {
    $box = Find-El $null 'txtFindingFilter' -Timeout 10
    Set-ElText $box $Text
    Start-Sleep -Seconds 3
}

function Select-Finding([string]$Like) {
    $cell = Find-Cell $Like
    if (-not $cell) { throw "The findings grid has no row like '$Like'." }
    Click-El $cell
    Start-Sleep -Seconds 1
}

$null = Start-Apd $exe $data $Theme
try {
    $w = Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1
    $w.SetFocus()
    # The sidebar entry of Site Hygiene.
    if (-not (Click-Nav 'Site Hygiene' 20)) { throw 'The sidebar has no Site Hygiene entry.' }
    Start-Sleep -Seconds 4
    $scan = Find-El $null 'btnHygieneScan' -Timeout 20
    'scan button found: ' + [bool]$scan
    if (-not $scan) { Get-ElList (Get-ApdWindows | Select-Object -First 1) 60 }
    $result = Invoke-ApdHygieneScan { Invoke-El $scan }
    "scan: $($result.Status) $($result.Summary)"
    if (-not $result.Ended) { throw 'The scan did not end.' }

    # Show blockers for the retired application that a task sequence installs and that another application depends on.
    Show-Filter 'TSQ-05'
    Select-Finding "*$BlockedApp"
    Click-El (Find-El $null 'btnHygieneShowBlockers' -Timeout 10)
    $summary = Find-El $null 'txtBlockersSummary' -Timeout 20
    if (-not $summary) { throw 'The Show blockers window did not open.' }
    Start-Sleep -Seconds 2
    "blockers: " + $summary.Current.Name
    Shot 'tools-site-hygiene-blockers'
    Invoke-El (Find-El $null 'btnBlockersClose' -Timeout 10)
    Start-Sleep -Seconds 2
}
finally { Stop-Apd; Stop-Transcript | Out-Null }
