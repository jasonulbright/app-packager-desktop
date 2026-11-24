<#
.SYNOPSIS
    The lab check of Q-127: every action that works through a counted set shows "N of M" in the status line while it runs, and in the line of a cancelled or finished run.
    The run ends with "PROGRESS-FAILS=<n>".

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from a folder that holds the scripts of tools\guide-capture:
        Run-VisibleProbe.ps1 -Worktree <folder with these scripts> -ProbeArgs "& .\Probe-ProgressText.ps1 -Run C:\guide\runs\apd-q127"
    The run reads and changes nothing on the site. Per theme, with every application selected:
      1. Check Latest shows "Check Latest: N of M — name" with M greater than 1; Cancel ends it with "Check Latest cancelled: N of M products checked.";
      2. Check ConfigMgr shows "Check ConfigMgr: N of M — name" (and the start line "Check ConfigMgr: 0 of M" or the first item); Cancel ends it with "Check ConfigMgr cancelled: N of M products checked.";
      3. Check CVEs shows "CVEs: N of M — name"; Cancel ends it with "CVE check cancelled" or "Check CVEs cancelled: N of M products checked.";
      4. with one application selected, Check ConfigMgr ends with "ConfigMgr query complete. 1 of 1 checked.".
    A screenshot of the status line of each check is saved under <Run>\progress\<theme>.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$script:Fails = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') { if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" } }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Run-Check([string]$Name, [scriptblock]$Body) { try { & $Body } catch { $script:Fails++; "FAIL ${Name}: $($_.Exception.Message)" } }
function Shot([string]$Theme, [string]$Id) { Move-MouseAway; Save-Shot (Join-Path $Run (Join-Path 'progress' (Join-Path $Theme.ToLower() ($Id + '.png')))) }

# The names of the text elements of the window that read like a status line of a run.
function Status-Lines($w) {
    $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    @($all | ForEach-Object { try { $_.Current.Name } catch { '' } } | Where-Object { $_ -match '^(Check Latest|Check ConfigMgr|CVEs|Check CVEs|CVE check|ConfigMgr query|Cancelled)' })
}

# Waits until a status line matches the pattern; returns the line or $null.
function Wait-Line($w, [string]$Pattern, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $hit = Status-Lines $w | Where-Object { $_ -match $Pattern } | Select-Object -First 1
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)
    $null
}

function Select-All($w, [bool]$On) {
    $all = Find-El $w 'SelectAllCheckBox' -Timeout 10
    $t = $all.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if (($t.Current.ToggleState -eq 'On') -ne $On) { Real-Click $all }
    if ($On -and $t.Current.ToggleState -ne 'On') { Real-Click $all }
}

# Starts the action, waits for its running line, then cancels it and waits for its cancelled line.
function Prove-Counted($w, [string]$Theme, [string]$Id, [string]$Button, [string]$RunningPattern, [string]$CancelledPattern) {
    Real-Click (Find-El $w -Name $Button -ControlType 'Button' -Timeout 5)
    $running = Wait-Line $w $RunningPattern 90
    Check "$Theme $Id running line shows N of M" ($null -ne $running) "no line like $RunningPattern; lines: $((Status-Lines $w) -join ' | ')"
    "INFO running line: '$running'"
    Shot $Theme "$Id-running"
    $cancel = Find-El $w 'btnCancelOperation' -Timeout 10
    if ($cancel) { Real-Click $cancel }
    $cancelled = Wait-Line $w $CancelledPattern 90
    Check "$Theme $Id cancelled line shows N of M" ($null -ne $cancelled) "no line like $CancelledPattern; lines: $((Status-Lines $w) -join ' | ')"
    "INFO cancelled line: '$cancelled'"
    Shot $Theme "$Id-cancelled"
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) { Start-Sleep 2 }
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $staging = New-RunStaging $data
    $settings = @{
        DownloadRoot = $staging; FileShareRoot = $FileShareRoot; SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; SiteCodeSaved = $true
        Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true }; CheckForUpdatesAtStartup = $false
    }
    $proc = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Start-Sleep 3
        Run-Check "$theme-latest" {
            Select-All $w $true
            Prove-Counted $w $theme 'check-latest' 'Check Latest' '^Check Latest: \d+ of (\d{2,}|[2-9]) — ' '^Check Latest cancelled: \d+ of \d+ products checked\.$'
        }
        Run-Check "$theme-configmgr" {
            Prove-Counted $w $theme 'check-configmgr' 'Check ConfigMgr' '^Check ConfigMgr: \d+ of (\d{2,}|[2-9])( — |$)' '^Check ConfigMgr cancelled: \d+ of \d+ products checked\.$'
        }
        Run-Check "$theme-cves" {
            Prove-Counted $w $theme 'check-cves' 'Check CVEs' '^CVEs: \d+ of (\d{2,}|[2-9]) — ' '(cancelled: \d+ of \d+|^Cancelled: \d+ of \d+)'
        }
        Run-Check "$theme-finished" {
            Select-All $w $false
            Set-ElText (Find-El $w 'txtAppPackagerSearch') '7-Zip'
            Start-Sleep 2
            $boxes = @((Find-El $w 'PackagerGrid').FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) |
                Where-Object { $_.Current.AutomationId -ne 'SelectAllCheckBox' })
            if ($boxes.Count -lt 1) { throw 'The grid has no row for 7-Zip.' }
            if ($boxes[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $boxes[0] }
            Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
            Real-Click (Find-El $w -Name 'Check ConfigMgr' -ControlType 'Button' -Timeout 5)
            $end = Wait-Line $w '^ConfigMgr query complete\. 1 of 1 checked\.$' 240
            Check "$theme configmgr finished line shows N of M" ($null -ne $end) "lines: $((Status-Lines $w) -join ' | ')"
            Shot $theme 'check-configmgr-finished'
        }
    }
    finally { Stop-Apd }
}
"PROGRESS-FAILS=$script:Fails"
