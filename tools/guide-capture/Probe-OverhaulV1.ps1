<#
.SYNOPSIS
    The lab check of the V1 overhaul phases 0 and 1 (queue row Q-126): the real app on CLIENT01 against CM01, light and dark.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with this script> -ProbeArgs "& .\Probe-OverhaulV1.ps1 -Run C:\guide\runs\v1-<timestamp>"
    Phase 1: each theme opens every sidebar page at 1920 x 1032 and saves a screenshot (no color change is expected: no page adopts a surface).
    Phase 0 (first theme only): Publish All with ConfigMgr and WSUS in use stages and publishes one application. The combined target must run the ConfigMgr part and the
    WSUS part, each once, and the log carries both results. A second Publish All with ConfigMgr only meets the application that exists and Skip answers the dialog
    (the conflict path). The caller removes the application from the site afterward.
    A step that fails says FAIL and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$Application = '7-Zip',
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    [string]$WsusServer = 'cm01.contoso.com'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Shot([string]$Theme, [string]$Id) { Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $Theme.ToLower() ($Id + '.png')))) -MaskFooter; "OK $Theme $Id" }
function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    $beat = Get-Date
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) {
        # The run ends a probe that prints nothing for ten minutes, so a wait says so every 30 seconds.
        if (((Get-Date) - $beat).TotalSeconds -ge 30) { "waiting: $(Status-Text $w)"; $beat = Get-Date }
        Start-Sleep 2
    }
}
function Step([string]$Theme, [string]$Id, [scriptblock]$Body) { try { & $Body } catch { "FAIL $Theme ${Id}: $($_.Exception.Message)" } }
function Other-Windows { @(Get-ApdWindows | Where-Object { $_.Current.Name -notmatch '^App Packager' }) }
function Click-ByName($Scope, [string[]]$Names) {
    foreach ($n in $Names) { $b = Find-El $Scope -Name $n -ControlType 'Button' -Timeout 2; if ($b) { Real-Click $b; return $n } }
    throw ('No button named ' + ($Names -join ', ') + ': ' + ((Get-ElList $Scope 40) -join ' / '))
}
function Check-Row($w, [string]$Name) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Name
    Start-Sleep 2
    $grid = Find-El $w 'PackagerGrid'
    $box = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) |
        Where-Object { $_.Current.AutomationId -ne 'SelectAllCheckBox' } | Select-Object -First 1
    if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
    Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
    Start-Sleep 1
}
function Log-Lines([string]$Data, [string]$Pattern) {
    $files = Get-ChildItem $Data -Recurse -Filter 'packr*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime
    foreach ($f in $files) { Get-Content -LiteralPath $f.FullName | Where-Object { $_ -match $Pattern } | ForEach-Object { "  log: $_" } }
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    $staging = Join-Path $data (Join-Path 'Users' (Join-Path $sid 'Staging'))
    New-Item -ItemType Directory -Force $staging | Out-Null
    & icacls $staging /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    $settings = @{ FileShareRoot = $FileShareRoot; DownloadRoot = $staging; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true }
        Wsus = @{ ServerName = $WsusServer; PortNumber = 8530; UseSsl = $false } }
    $null = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        # Phase 1: every page opens in this theme.
        Step $theme 'pages' {
            foreach ($page in 'App Packager', 'Add from web', 'Add from file', 'Installer Analysis', 'Detection Tester', 'Deployment Helper', 'Pivots and Scripts', 'Collection and Compliance', 'Maintenance Windows', 'DP Content Manager', 'Settings', 'Workbench') {
                if (-not (Click-Nav $page)) { "FAIL $theme ${page}: the sidebar has no entry with this name"; continue }
                Start-Sleep -Seconds 3
                Move-MouseAway
                Shot $theme ('page-' + (($page -replace '[^A-Za-z0-9]+', '-').ToLower()))
                if ($page -eq 'Workbench') { [System.Windows.Forms.SendKeys]::SendWait('%{F4}'); Start-Sleep -Seconds 2 }
            }
            $null = Click-Nav 'App Packager'
            Start-Sleep 2
        }
        if ($theme -ne $Themes[0]) { continue }

        # Phase 0: the combined target.
        Step $theme 'publish-all-combined' {
            $w = Main-Window
            $tip = (Find-El $w 'btnPublishAll').Current.HelpText
            "Publish All enabled: $((Find-El $w 'btnPublishAll').Current.IsEnabled); tooltip: $tip"
            Check-Row $w $Application
            Real-Click (Find-El $w -Name 'Check Latest' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 240
            Real-Click (Find-El $w -Name 'Stage' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 420
            foreach ($i in 1..3) { $dlg = Other-Windows | Select-Object -First 1; if ($dlg) { try { $null = Click-ByName $dlg @('Continue', 'OK', 'Yes'); Wait-Idle $w 300 } catch { break } } }
            "status after Stage: $(Status-Text $w)"
            Set-ElText (Find-El $w 'txtAppPackagerComment') 'Overhaul V1 lab check'
            Check-Row $w $Application
            Real-Click (Find-El $w 'btnPublishAll')
            Wait-Idle $w 600
            $dlg = Other-Windows | Select-Object -First 1
            if ($dlg) { $null = Click-ByName $dlg @('Continue', 'OK', 'Yes'); Wait-Idle $w 600 }
            "status after Publish All (ConfigMgr and WSUS): $(Status-Text $w)"
            Move-MouseAway
            Shot $theme 'publish-all-combined'
            Log-Lines $data 'Package|Packaged|WSUS|Publish'
        }
        Step $theme 'publish-all-existing' {
            # The application exists on the site now: a second Publish All, ConfigMgr only, asks what to do; Skip answers it.
            Stop-Apd
            $settings.Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false }
            $null = Start-Apd $exe $data $theme $settings
            $w = Main-Window
            $w.SetFocus()
            Check-Row $w $Application
            Real-Click (Find-El $w 'btnPublishAll')
            Start-Sleep 25
            Move-MouseAway
            Shot $theme 'publish-all-existing'
            $skip = Find-El $w -Name 'Skip' -ControlType 'Button' -Timeout 5
            if ($skip) { Real-Click $skip; 'Skip answered the Application Already Exists dialog.' } else { 'No Skip button found.' }
            Wait-Idle $w 120
            "status after the second Publish All: $(Status-Text $w)"
            Log-Lines $data 'kept|exists|Existing'
        }
    }
    finally { Stop-Apd }
}
