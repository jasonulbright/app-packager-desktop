<#
.SYNOPSIS
    Lab run of the Site Hygiene fixes for a broken supersedence (SUP-01) and for the objects of a retired application (Q-136): drives the real
    app on the lab client against the site, runs each fix through the plan window, prints the result text of each run and the lines that the
    application log file holds about each failure.

.DESCRIPTION
    Make the throw-away objects first: tools\lab-tests\site-hygiene-retired\New-Fixture.ps1 on CM01 (prefix SHT2). Run under the capture lock from
    the folder of the published app with ApdUiDriver.ps1:
        Run-VisibleProbe.ps1 -Worktree <published folder with both scripts> -ProbeArgs "& .\Probe-SiteHygieneRetired.ps1"
    The run changes only the objects whose name starts with the prefix. Remove them afterwards with Remove-Fixture.ps1.
    The last line is "PROBE-FAILS=<n>": the number of expectations that did not hold.
#>
param(
    [string]$Prefix = 'SHT2',
    [string]$Theme = 'Light',
    [string]$Run = 'C:\guide\runs\q136-window'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')

$exe = Join-Path $PSScriptRoot 'ApplicationPackagerUI.exe'
$data = Join-Path $Run 'data'
Remove-Item $Run -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Run | Out-Null
Start-Transcript -Path (Join-Path $Run 'run.log') -Force | Out-Null
$script:Fails = 0
function Expect([bool]$Condition, [string]$What) {
    if ($Condition) { "PASS  $What" } else { $script:Fails++; "FAIL  $What" }
}

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
    Set-ElText (Find-El $null 'txtFindingFilter' -Timeout 10) $Text
    Start-Sleep -Seconds 3
}
function Scan {
    Invoke-El (Find-El $null 'btnHygieneScan' -Timeout 20)
    $deadline = (Get-Date).AddMinutes(8)
    do {
        Start-Sleep -Seconds 5
        $summary = Find-El $null 'txtHygieneSummary' -Timeout 1
        $text = if ($summary) { $summary.Current.Name } else { '' }
    } while ((Get-Date) -lt $deadline -and $text -notmatch 'finding')
    "scan: $text"
}

# Runs the plan window of the selected finding and returns the result text; $null when the finding is not in the grid.
function Run-Plan([string]$Like, [string]$Id) {
    $cell = Find-Cell $Like 15
    $script:Last = ''
    if (-not $cell) { "NO ROW  $Id ($Like)"; return }
    Click-El $cell
    Start-Sleep -Seconds 1
    Click-El (Find-El $null 'btnHygieneRunFix' -Timeout 10)
    $confirm = Find-El $null 'btnChangePlanConfirm' -Timeout 150
    if (-not $confirm) {
        # A slow lab client can swallow the first click: select the row again and press the button once more.
        "RETRY   $Id : the plan window did not open yet"
        $again = Find-Cell $Like 15
        if ($again) { Click-El $again; Start-Sleep -Seconds 2 }
        Invoke-El (Find-El $null 'btnHygieneRunFix' -Timeout 10)
        $confirm = Find-El $null 'btnChangePlanConfirm' -Timeout 240
    }
    if (-not $confirm) { throw "The plan window of $Id did not open." }
    Start-Sleep -Seconds 2
    foreach ($box in 'chkChangePlanExtra', 'chkChangePlanDestructive', 'chkChangePlanSecond') {
        $el = Find-El $null $box -Timeout 2
        if ($el -and -not $el.Current.IsOffscreen) {
            $toggle = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle(); Start-Sleep -Milliseconds 500 }
        }
    }
    Invoke-El $confirm
    $close = $null
    $deadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $deadline -and -not $close) {
        Start-Sleep -Seconds 3
        $c = Find-El $null 'btnChangePlanClose' -Timeout 1
        if ($c -and -not $c.Current.IsOffscreen -and $c.Current.IsEnabled) { $close = $c }
    }
    if (-not $close) { throw "The plan of $Id did not finish." }
    $result = Find-El $null 'txtChangePlanResult' -Timeout 2
    $summary = Find-El $null 'txtChangePlanSummary' -Timeout 2
    $text = (@($(if ($result) { $result.Current.Name }), $(if ($summary) { $summary.Current.Name })) | Where-Object { $_ }) -join ' | '
    "RESULT $Id : $text"
    $script:Last = $text
    Invoke-El $close
    Start-Sleep -Seconds 2
}

$null = Start-Apd $exe $data $Theme
try {
    $w = Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1
    $w.SetFocus()
    Click-El (Find-El $null -Name 'Site Hygiene' -Timeout 20)
    Start-Sleep -Seconds 4
    Scan

    # 1. SUP-01: a supersedence rule to a deleted application, on an active, a deployed, a retired and a retired deployed application.
    Show-Filter 'SUP-01'
    Run-Plan "*$Prefix-BS-Plain*" 'sup01-plain'
    Expect ($script:Last -match 'Done|removed|no longer has a supersedence') 'SUP-01 on an active application is removed'
    Run-Plan "*$Prefix-BS-Deployed*" 'sup01-deployed'
    Expect ($script:Last -match 'Done|removed|no longer has a supersedence') 'SUP-01 on an active application with a deployment is removed'
    Run-Plan "*$Prefix-BS-Retired*" 'sup01-retired'
    Expect ($script:Last -match 'Done|removed|no longer has a supersedence') 'SUP-01 on a retired application is removed'
    Run-Plan "*$Prefix-BS-RetDeployed*" 'sup01-retdeployed'
    # The result text of the plan window counts the objects; the exact reason of a skipped object is in the object result and in the log file (checked at the end).
    Expect ($script:Last -match 'skipped') 'SUP-01 on a retired application with a deployment is skipped, not changed'

    # 2. The objects of a retired application: its deployment, the rules that point at it, then the application itself.
    Show-Filter 'APP-02'
    Run-Plan "*$Prefix-Ret*" 'app02-deployment'
    Expect ($script:Last -match 'Done|Deleted') 'APP-02 deployment of the retired application is removed'
    Scan
    Show-Filter 'SUP-04'
    Run-Plan "*$Prefix-Sup*" 'sup04'
    Expect ($script:Last -match 'Done|no longer has') 'SUP-04 supersedence on the retired application is removed'
    Show-Filter 'DEP-04'
    Run-Plan "*$Prefix-Dep*" 'dep04'
    Expect ($script:Last -match 'Done|no longer has') 'DEP-04 dependency on the retired application is removed'
    Scan
    # APP-01 offers an application only when it is older than 30 days, so a new throw-away application has no row. The removal of the
    # retired application after these steps is checked with the engine (see the vault session report of Q-138).
    Show-Filter 'APP-01'
    Run-Plan "*$Prefix-Ret*" 'app01'
    Scan
    Show-Filter $Prefix
    $left = Find-Cell "*$Prefix-*" 10
    "rows with the prefix after the last scan: " + $(if ($left) { $left.Current.Name } else { 'none' })
}
finally {
    Stop-Apd
    # The application log file of the run: what the log holds about each failure.
    foreach ($file in Get-ChildItem -Path $data -Recurse -Filter 'app-*.log' -ErrorAction SilentlyContinue) {
        "LOG FILE $($file.FullName) ($([int]($file.Length / 1KB)) KB)"
        Get-Content -LiteralPath $file.FullName | Where-Object { $_ -match "failed|FAILED|was not run|stopped|Blocked by" -and $_ -match 'WARNING|ERROR' } | Select-Object -First 40
    }
    $logText = (@(Get-ChildItem -Path $data -Recurse -Filter 'app-*.log' -ErrorAction SilentlyContinue) | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    Expect ($logText -match "ClearBrokenSupersedence was not run\. Object: Supersedence '$Prefix-BS-RetDeployed'.*retired and has a deployment") 'the log file names the object and the reason of the skipped fix'
    "PROBE-FAILS=$script:Fails"
    Stop-Transcript | Out-Null
}
