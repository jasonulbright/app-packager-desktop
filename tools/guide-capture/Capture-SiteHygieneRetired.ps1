<#
.SYNOPSIS
    The lab run of the Site Hygiene fixes for retired and superseded applications (Q-89): drives the real app on the lab client against the ConfigMgr site, in one
    theme. It scans, opens the Show blockers window for the retired application of a task sequence, runs the APP-07 fix and then the APP-08 fix through the plan window, and
    saves the images of the Show blockers window and of the plans.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app with ApdUiDriver.ps1:
        Run-VisibleProbe.ps1 -Worktree <published folder with both scripts> -ProbeArgs "& .\Capture-SiteHygieneRetired.ps1 -Theme Light"
    The images are <Run>\images\<theme>\<id>.png. The status bar, which names the site server, is painted over in each image.

    What the site must hold first (throw-away applications of the lab, made and removed by the person who runs the check; the run removes nothing else):
      ZZQ89 Retired2 (retired) supersedes ZZQ89 Active2, ZZQ89 Active supersedes ZZQ89 Old, and a task sequence installs the retired ZZQ89 Retired, which ZZQ89 Active depends on.
    Pass -SkipFixes to open the Show blockers window only; the run then changes nothing on the site.
#>
param(
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light',
    [switch]$SkipFixes,
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

# Runs the plan window: the check boxes that the plan asks (the option stays on), then Confirm, then waits for the result and closes the window.
function Run-Plan([string]$Id) {
    Click-El (Find-El $null 'btnHygieneRunFix' -Timeout 10)
    $confirm = Find-El $null 'btnChangePlanConfirm' -Timeout 180
    if (-not $confirm) { throw 'The plan window did not open.' }
    Start-Sleep -Seconds 2
    Shot "$Id-plan"
    foreach ($box in 'chkChangePlanExtra', 'chkChangePlanDestructive', 'chkChangePlanSecond') {
        $el = Find-El $null $box -Timeout 2
        if ($el -and -not $el.Current.IsOffscreen) {
            $toggle = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle(); Start-Sleep -Milliseconds 500 }
        }
    }
    $option = Find-El $null 'chkChangePlanOption' -Timeout 2
    "option check box: " + $(if ($option) { $option.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } else { 'none' })
    Invoke-El $confirm
    $close = $null
    $deadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $deadline -and -not $close) {
        Start-Sleep -Seconds 3
        $c = Find-El $null 'btnChangePlanClose' -Timeout 1
        if ($c -and -not $c.Current.IsOffscreen -and $c.Current.IsEnabled) { $close = $c }
    }
    if (-not $close) { throw 'The plan did not finish.' }
    $result = Find-El $null 'txtChangePlanResult' -Timeout 2
    "result of $($Id): " + $(if ($result) { $result.Current.Name } else { '(no text)' })
    $summary = Find-El $null 'txtChangePlanSummary' -Timeout 2
    if ($summary) { "summary: " + $summary.Current.Name }
    Shot "$Id-result"
    Invoke-El $close
    Start-Sleep -Seconds 2
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
    Invoke-El $scan
    $deadline = (Get-Date).AddMinutes(8)
    do {
        Start-Sleep -Seconds 5
        $summary = Find-El $null 'txtHygieneSummary' -Timeout 1
        $text = if ($summary) { $summary.Current.Name } else { '' }
    } while ((Get-Date) -lt $deadline -and $text -notmatch 'finding')
    "scan: $text"

    # Show blockers for the retired application that a task sequence installs and that another application depends on.
    Show-Filter 'TSQ-05'
    Select-Finding '*ZZQ89 Retired'
    Click-El (Find-El $null 'btnHygieneShowBlockers' -Timeout 10)
    $summary = Find-El $null 'txtBlockersSummary' -Timeout 20
    if (-not $summary) { throw 'The Show blockers window did not open.' }
    Start-Sleep -Seconds 2
    "blockers: " + $summary.Current.Name
    Shot 'tools-site-hygiene-blockers'
    Invoke-El (Find-El $null 'btnBlockersClose' -Timeout 10)
    Start-Sleep -Seconds 2

    if (-not $SkipFixes) {
        Show-Filter 'APP-07'
        Select-Finding 'ZZQ89 Retired2'
        Run-Plan 'tools-site-hygiene-app07'

        Show-Filter 'APP-08'
        Select-Finding 'ZZQ89 Old'
        Run-Plan 'tools-site-hygiene-app08'
    }
}
finally { Stop-Apd; Stop-Transcript | Out-Null }
