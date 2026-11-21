<#
.SYNOPSIS
    The lab capture run of the App Packager page states (decision D-21, queue rows Q-20a and Q-65c): drives the real app on the lab client with UI Automation, one theme
    after the other, and saves the guide images of the toolbar with its current buttons (grid after the checks, Select Update Available, disabled publish button and its
    tooltip, Configure dialog, Application Already Exists dialog, Operation history window).

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-PackagesPage.ps1 -Run C:\guide\runs\apd-<timestamp>"
    The images are <Run>\images\<theme>\<id>.png (the work area, 1920 x 1032). Every file lives inside the run folder; the settings of the lab user stay as they are.
    A step that fails says FAIL for its image and the run goes on. Nothing is published: the dialogs are closed with Cancel, and the Stage of the run goes to the
    staging folder of the run.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string[]]$Checked = @('7-Zip', 'Audacity', 'CMTrace Open', '.NET 8 Desktop Runtime', 'Adobe Acrobat', 'AIMP'),
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$cli = Join-Path $AppFolder 'AppPackagerBuild.exe'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Shot([string]$Theme, [string]$Id) {
    Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $Theme.ToLower() ($Id + '.png')))) -MaskFooter
    "OK $Theme $Id"
}
function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) { Start-Sleep 2 }
}
function Step([string]$Theme, [string]$Id, [scriptblock]$Body) {
    try { & $Body } catch { "FAIL $Theme ${Id}: $($_.Exception.Message)" }
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    $staging = Join-Path $data (Join-Path 'Users' (Join-Path $sid 'Staging'))
    New-Item -ItemType Directory -Force $staging | Out-Null
    & icacls $staging /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    # One stage of 7-Zip with the command-line build gives the Operation history window an operation to show.
    $ErrorActionPreference = 'Continue'
    $out = & $cli --application '7zip' --stage --data-root $data --download-root $staging 2>&1 | Out-String
    "stage 7-Zip ($theme): exit $LASTEXITCODE"
    $ErrorActionPreference = 'Stop'

    $settings = @{ FileShareRoot = $FileShareRoot; DownloadRoot = $staging; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true } }
    $null = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Step $theme 'checks' {
            foreach ($name in $Checked) {
                Set-ElText (Find-El $w 'txtAppPackagerSearch') $name
                Start-Sleep 2
                $grid = Find-El $w 'PackagerGrid'
                $box = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) |
                    Where-Object { $_.Current.AutomationId -ne 'SelectAllCheckBox' } | Select-Object -First 1
                if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
            }
            Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
            Start-Sleep 1
            Real-Click (Find-El $w -Name 'Check Latest' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 240
            Real-Click (Find-El $w -Name 'Check ConfigMgr' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 240
            "status: $(Status-Text $w)"
        }
        Step $theme 'core-packages-list' {
            Move-MouseAway
            Shot $theme 'core-packages-list'
            Shot $theme 'packages'
        }
        Step $theme 'core-packages-select-update' {
            Real-Click (Find-El $w -Name 'Select Update Available' -ControlType 'Button' -Timeout 5)
            # The row with the update is below the visible rows: scroll the grid with the wheel until the row is on screen.
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 500
            for ($i = 0; $i -lt 40; $i++) {
                $row = Find-El $w -Name 'Audacity (x64)' -Timeout 1
                if ($row -and -not $row.Current.IsOffscreen -and $row.Current.BoundingRectangle.Y -gt 260 -and $row.Current.BoundingRectangle.Y -lt 420) { break }
                [ApdW]::mouse_event(0x0800, 0, 0, [uint32]4294967176, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
            }
            Move-MouseAway
            Shot $theme 'core-packages-select-update'
        }
        Step $theme 'core-packages-export-check-results' {
            # The Check ConfigMgr run of the 'checks' step wrote the result file in the data folder of the run; the button below the grid is enabled.
            $resultFile = Join-Path $data (Join-Path 'Users' (Join-Path $sid (Join-Path 'SRLPackr' 'CheckConfigMgrResult.csv')))
            if (-not (Test-Path -LiteralPath $resultFile)) { throw "no result file at $resultFile" }
            "result file: $((Get-Content -LiteralPath $resultFile | Measure-Object).Count) lines"
            Get-Content -LiteralPath $resultFile -TotalCount 4
            $b = Find-El $w 'btnExportCheckResults'
            if (-not $b -or -not $b.Current.IsEnabled) { throw 'Export Check Results is missing or disabled' }
            Move-MouseAway
            Shot $theme 'core-packages-export-check-results'
        }
        Step $theme 'core-packages-buttons-disabled' {
            $b = Find-El $w 'btnPublishIntune'
            [System.Windows.Forms.Cursor]::Position = Get-ElCenter $b
            Start-Sleep -Milliseconds 400
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ((Get-ElCenter $b).X + 2), ((Get-ElCenter $b).Y + 2)
            Start-Sleep -Seconds 2
            Shot $theme 'core-packages-buttons-disabled'
            Move-MouseAway
        }
        Step $theme 'core-packages-cve-columns' {
            Real-Click (Find-El $w 'btnCheckCves')
            Wait-Idle $w 180
            "status after Check CVEs: $(Status-Text $w)"
            Move-MouseAway
            Shot $theme 'core-packages-cve-columns'
        }
        Step $theme 'core-packages-operation-history' {
            # The window is not in the UI Automation tree of the app, so the shot is cut at its place: the window opens centered over the page of the work area.
            Real-Click (Find-El $w 'btnOperationHistory')
            Start-Sleep 4
            Move-MouseAway
            Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $theme.ToLower() 'core-packages-operation-history.png'))) -Rect @(370, 136, 1181, 760)
            "OK $theme core-packages-operation-history"
            [System.Windows.Forms.SendKeys]::SendWait('%{F4}')
            Start-Sleep 2
        }
        Step $theme 'core-packages-review-dialog' {
            # MariaDB Server is a definition that sets ReviewBeforeStage: Stage shows the Configure dialog. Cancel skips the application.
            foreach ($b in (Find-El $w 'PackagerGrid').FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox)))) {
                if ($b.Current.AutomationId -ne 'SelectAllCheckBox' -and $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') { Real-Click $b }
            }
            Set-ElText (Find-El $w 'txtAppPackagerSearch') 'MariaDB Server'
            Start-Sleep 2
            $box = (Find-El $w 'PackagerGrid').FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) | Where-Object { $_.Current.AutomationId -ne 'SelectAllCheckBox' } | Select-Object -First 1
            Real-Click $box
            Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
            Start-Sleep 1
            Real-Click (Find-El $w -Name 'Stage' -ControlType 'Button' -Timeout 5)
            Start-Sleep 8
            Move-MouseAway
            Shot $theme 'core-packages-review-dialog'
            [System.Windows.Forms.SendKeys]::SendWait('%{F4}')
            Start-Sleep 3
            if (Find-El $w 'btnCancelOperation' -Timeout 2) { Real-Click (Find-El $w 'btnCancelOperation'); Start-Sleep 3 }
            Start-Sleep 3
        }
    }
    finally { Stop-Apd }
}
