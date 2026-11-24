<#
.SYNOPSIS
    The lab check of a release candidate (the end-to-end run before the release pull request): the installed app is started in an isolated data folder, one theme after the other, and driven
    with real mouse input. Each check says PASS or FAIL. The run ends with "CANDIDATE-FAILS=<n>".

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from a folder that holds the scripts of tools\guide-capture:
        Run-VisibleProbe.ps1 -Worktree <folder with these scripts> -ProbeArgs "& .\Probe-CandidateCheck.ps1 -Run C:\guide\runs\apd-cand -ExpectedVersion 2026.1123.59"
    The app comes from -AppFolder (the per-user install of the candidate installer). The run reads and changes nothing on the site: it checks a product with Check Latest and Check ConfigMgr and scans
    Site Hygiene (read only). Per theme:
      1. the file version of the app is the expected version;
      2. the toolbar is one row in the order One Click, Check Latest, Check ConfigMgr, Check CVEs, Stage, Publish All, Publish to ConfigMgr, Publish to Intune, Publish to WSUS, CVE Report, Ops History;
         Cancel shows only while a run is active;
      3. the sidebar has the entry WSUS Updates, and Settings, Application Settings opens its grid;
      4. Check Latest and Check ConfigMgr for one product end with their complete line and the buttons are enabled again;
      5. Check Latest for every product, then Cancel with a real mouse click: the status line reads "Cancelled: N of M products done.", Cancel is hidden and the buttons are enabled again;
      6. a Site Hygiene scan ends and shows its results;
      7. the app idles for 60 seconds and does not crash (the process runs, a crash file does not appear).
    A screenshot of the work area of each check is saved under <Run>\checks\<theme>.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string]$Product = '7-Zip',
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    [int]$IdleSeconds = 60
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$script:Fails = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') { if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" } }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Check-Shot([string]$Theme, [string]$Id) { Move-MouseAway; Save-Shot (Join-Path $Run (Join-Path 'checks' (Join-Path $Theme.ToLower() ($Id + '.png')))) }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) { Start-Sleep 2 }
}
function Run-Check([string]$Name, [scriptblock]$Body) { try { & $Body } catch { $script:Fails++; "FAIL ${Name}: $($_.Exception.Message)" } }
function Check-Row($w, [string]$Name) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Name
    Start-Sleep 2
    $grid = Find-El $w 'PackagerGrid'
    $box = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) |
        Where-Object { $_.Current.AutomationId -ne 'SelectAllCheckBox' } | Select-Object -First 1
    if (-not $box) { throw "The grid has no row for '$Name'." }
    if ($box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
    Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
    Start-Sleep 1
}
function Uncheck-All($w) {
    $all = Find-El $w 'SelectAllCheckBox' -Timeout 5
    if (-not $all) { return }
    $t = $all.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($t.Current.ToggleState -eq 'On') { Real-Click $all }
}

$crashDir = Join-Path $env:LOCALAPPDATA 'AppPackagerData'
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
        Run-Check "$theme-01" {
            $v = (Get-Item $exe).VersionInfo.ProductVersion
            Check "$theme-01 the app version is $ExpectedVersion" ($v -like "$ExpectedVersion*") "version: $v"
        }
        Run-Check "$theme-02" {
            $order = 'One Click', 'Check Latest', 'Check ConfigMgr', 'Check CVEs', 'Stage', 'Publish All', 'Publish to ConfigMgr', 'Publish to Intune', 'Publish to WSUS', 'CVE Report', 'Ops History'
            $rects = foreach ($n in $order) { $b = Find-El $w -Name $n -ControlType 'Button' -Timeout 5; if ($b) { $b.Current.BoundingRectangle } else { $null } }
            $missing = @($order | Where-Object { $true }) | Where-Object { $i = [array]::IndexOf($order, $_); -not $rects[$i] }
            Check "$theme-02a every toolbar button shows" (-not $missing) ("missing: " + ($missing -join ', '))
            $present = @($rects | Where-Object { $_ })
            $oneRow = ($present | ForEach-Object { [int]$_.Y } | Sort-Object -Unique).Count -le 1 -or (($present | Measure-Object -Property Y -Maximum).Maximum - ($present | Measure-Object -Property Y -Minimum).Minimum) -lt 8
            Check "$theme-02b the toolbar is one row" $oneRow 'the buttons are on more than one line'
            $xs = @($present | ForEach-Object { $_.X })
            Check "$theme-02c the toolbar is in the owner order" ((($xs | Sort-Object) -join ',') -eq ($xs -join ',')) ($xs -join ',')
            Check "$theme-02d Cancel is hidden at idle" ($null -eq (Find-El $w 'btnCancelOperation' -Timeout 2)) 'Cancel shows with no run'
            Check-Shot $theme 'toolbar'
        }
        Run-Check "$theme-03" {
            $wsus = Find-El $w -Name 'WSUS Updates' -Timeout 5
            Check "$theme-03a the sidebar has WSUS Updates" ($null -ne $wsus) 'no entry'
            if (-not (Click-Nav 'Settings')) { throw 'The sidebar has no Settings entry.' }
            Start-Sleep 3
            Real-Click (Find-El $w 'nav_supersedence' -Timeout 10); Start-Sleep 2
            $grid = Find-El $w 'dgSupersedenceApps' -Timeout 10
            Check "$theme-03b Settings, Application Settings shows its grid" ($null -ne $grid) 'no grid'
            Check-Shot $theme 'application-settings'
            $null = Click-Nav 'App Packager'; Start-Sleep 3
        }
        Run-Check "$theme-04" {
            $w = Main-Window
            Check-Row $w $Product
            Real-Click (Find-El $w -Name 'Check Latest' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 180
            $s1 = Status-Text $w
            Check "$theme-04a Check Latest ends with its complete line" ($s1 -match 'complete') "status: $s1"
            Real-Click (Find-El $w -Name 'Check ConfigMgr' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 240
            $s2 = Status-Text $w
            Check "$theme-04b Check ConfigMgr ends with its complete line" ($s2 -match 'complete|Ready') "status: $s2"
            $cl = Find-El $w -Name 'Check Latest' -ControlType 'Button' -Timeout 5
            Check "$theme-04c the buttons are enabled again" ($cl.Current.IsEnabled) 'Check Latest is disabled'
            Check-Shot $theme 'checks'
            Uncheck-All $w
        }
        Run-Check "$theme-05" {
            $w = Main-Window
            $all = Find-El $w 'SelectAllCheckBox' -Timeout 5
            Real-Click $all
            Real-Click (Find-El $w -Name 'Check Latest' -ControlType 'Button' -Timeout 5)
            Start-Sleep 6
            $cancel = Find-El $w 'btnCancelOperation' -Timeout 10
            Check "$theme-05a Cancel shows while a run is active" ($null -ne $cancel) 'no Cancel button'
            Check-Shot $theme 'cancel-running'
            if ($cancel) { Real-Click $cancel }
            $deadline = (Get-Date).AddSeconds(90)
            while ((Get-Date) -lt $deadline -and -not ((Status-Text $w) -match '^Cancelled')) { Start-Sleep 2 }
            $s = Status-Text $w
            Check "$theme-05b the status line reads Cancelled: N of M products done." ($s -match '^Cancelled: \d+ of \d+ products? done\.$') "status: $s"
            Start-Sleep 3
            Check "$theme-05c Cancel is hidden after the run" ($null -eq (Find-El $w 'btnCancelOperation' -Timeout 3)) 'Cancel still shows'
            $cl = Find-El $w -Name 'Check Latest' -ControlType 'Button' -Timeout 5
            Check "$theme-05d the buttons are enabled again" ($cl.Current.IsEnabled) 'Check Latest is disabled'
            Check-Shot $theme 'cancelled'
            Uncheck-All $w
        }
        Run-Check "$theme-06" {
            $null = Click-Nav 'Site Hygiene'; Start-Sleep 4
            Real-Click (Need-El 'btnHygieneScan')
            $deadline = (Get-Date).AddSeconds(300)
            Start-Sleep 5
            while ((Get-Date) -lt $deadline -and (Find-El $null 'btnHygieneCancel' -Timeout 1)) { Start-Sleep 3 }
            Start-Sleep 3
            $results = Find-El $w 'dgHygieneResults' -Timeout 5
            Check "$theme-06 the Site Hygiene scan ended and shows its results" ($null -ne $results -or $null -ne (Find-El $w 'pnlHygieneResults' -Timeout 5)) 'no results'
            Check-Shot $theme 'site-hygiene'
            $null = Click-Nav 'App Packager'; Start-Sleep 3
        }
        Run-Check "$theme-07" {
            $before = @(Get-ChildItem $crashDir -Recurse -Filter '*crash*' -ErrorAction SilentlyContinue).Count
            Start-Sleep $IdleSeconds
            $after = @(Get-ChildItem $crashDir -Recurse -Filter '*crash*' -ErrorAction SilentlyContinue).Count
            Check "$theme-07 the app idles $IdleSeconds seconds without a crash" (-not $proc.HasExited -and $after -eq $before) "exited: $($proc.HasExited); crash files $before -> $after"
            Check-Shot $theme 'idle'
        }
    }
    finally { Stop-Apd }
}
"CANDIDATE-FAILS=$script:Fails"
