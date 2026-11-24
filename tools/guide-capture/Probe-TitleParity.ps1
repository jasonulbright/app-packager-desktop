<#
.SYNOPSIS
    Lab check of the title parity (Q-128): the Packages grid shows the titles of the PowerShell app, the search finds a row by its title and its ConfigMgr name, Check ConfigMgr finds the applications that
    the site holds by CMName, and Convert from ConfigMgr previews three of these applications. Each check says PASS or FAIL. The run ends with "TITLE-PARITY-FAILS=<n>".

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from a folder that holds the repository files (the golden file is read from tests\):
        Run-VisibleProbe.ps1 -Worktree <repo folder> -ProbeArgs "& .\tools\guide-capture\Probe-TitleParity.ps1 -Run C:\guide\runs\apd-parity -ExpectedVersion <ver>"
    The app comes from -AppFolder (the per-user install of the candidate). Nothing is written to the site: Check ConfigMgr reads, Convert only previews.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string[]]$GridSample = @('putty', '7zip', 'git', 'notepadplusplus', 'audacity', 'adobereader', 'firefox', 'temurin-jdk17', 'zulu-jdk17', 'powershell7'),
    [string[]]$SiteApplications = @('7zip', 'audacity'),
    [string[]]$ConvertApplications = @('7-Zip 26.06 (x64 edition)', 'Audacity 3.7.9', 'CMTrace Open'),
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'ApdUiDriver.ps1')
. (Join-Path $here 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$golden = (Get-Content (Join-Path $here '..\..\tests\ApplicationPackagerEngine.Tests\Fixtures\TitleParity\PowerShellTitles.golden.json') -Raw | ConvertFrom-Json).Packagers
$script:Fails = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') { if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" } }
function Run-Check([string]$Name, [scriptblock]$Body) { try { & $Body } catch { $script:Fails++; "FAIL ${Name}: $($_.Exception.Message)" } }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) { Start-Sleep 2 }
}
function Session-Log([string]$Data) {
    $f = Get-ChildItem $Data -Recurse -Filter packr.log -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($f) { Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue } else { '' }
}
# The texts of the grid after a search: every cell of the rows that the filter leaves.
function Grid-Texts($w) {
    $grid = Find-El $w 'PackagerGrid'
    @($grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { try { $_.Current.Name } catch { '' } } | Where-Object { $_ })
}
function Check-Shot-Grid([string]$Theme, [string]$Id) { Move-MouseAway; Save-Shot (Join-Path $Run (Join-Path 'checks' (Join-Path $Theme.ToLower() ($Id + '.png')))) }
function Search-Grid($w, [string]$Text) { Set-ElText (Find-El $w 'txtAppPackagerSearch') $Text; Start-Sleep 2 }
function Check-Row($w, [string]$Name) {
    Search-Grid $w $Name
    $grid = Find-El $w 'PackagerGrid'
    $box = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) |
        Where-Object { $_.Current.AutomationId -ne 'SelectAllCheckBox' } | Select-Object -First 1
    if (-not $box) { throw "The grid has no row for '$Name'." }
    if ($box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
    Search-Grid $w ''
}
function Uncheck-All($w) {
    $all = Find-El $w 'SelectAllCheckBox' -Timeout 5
    if ($all -and $all.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') { Real-Click $all }
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $staging = New-RunStaging $data
    $settings = @{
        DownloadRoot = $staging; FileShareRoot = $FileShareRoot; SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; SiteCodeSaved = $true
        Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false }; CheckForUpdatesAtStartup = $false
    }
    $proc = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Start-Sleep 4
        Run-Check "$theme-01" {
            $v = (Get-Item $exe).VersionInfo.ProductVersion
            Check "$theme-01 the app version is $ExpectedVersion" ($v -like "$ExpectedVersion*") "version: $v"
        }
        Run-Check "$theme-02" {
            $w = Main-Window
            foreach ($id in $GridSample) {
                $p = $golden | Where-Object { $_.Packager -eq $id -or $_.Definition -eq $id } | Select-Object -First 1
                if (-not $p) { throw "No packager '$id' in the golden file." }
                Search-Grid $w $p.App
                $texts = Grid-Texts $w
                Check "$theme-02 the grid shows the title '$($p.App)' of the PowerShell app" ($texts -contains $p.App) ("cells: " + (($texts | Select-Object -First 12) -join ' | '))
                Search-Grid $w $p.CMName
                $texts = Grid-Texts $w
                Check "$theme-02 a search for the ConfigMgr name '$($p.CMName)' finds the row" ($texts -contains $p.App) ("cells: " + (($texts | Select-Object -First 12) -join ' | '))
            }
            Search-Grid $w 'PuTTY'
            Check-Shot-Grid $theme 'grid-putty'
            Search-Grid $w ''
        }
        Run-Check "$theme-03" {
            $w = Main-Window
            foreach ($id in $SiteApplications) { Check-Row $w (($golden | Where-Object { $_.Definition -eq $id }).App) }
            Real-Click (Find-El $w -Name 'Check ConfigMgr' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 240
            $log = Session-Log $data
            Check "$theme-03a Check ConfigMgr ends with its finished line" ($log -match "Check ConfigMgr finished: \d+ of $($SiteApplications.Count) product") "log: $($log.Substring([Math]::Max(0, $log.Length - 400)))"
            Search-Grid $w ''
            $texts = Grid-Texts $w
            Check "$theme-03b Check ConfigMgr found 7-Zip 26.06 and Audacity 3.7.9 by their ConfigMgr names" (($texts -contains '26.06') -and ($texts -contains '3.7.9')) ("cells: " + (($texts | Where-Object { $_ -match '^\d' } | Select-Object -First 12) -join ' | '))
            Save-Shot (Join-Path $Run (Join-Path 'checks' (Join-Path $theme.ToLower() 'check-configmgr.png')))
            Uncheck-All $w
        }
        Run-Check "$theme-04" {
            $w = Main-Window
            foreach ($app in $ConvertApplications) {
                Real-Click (Find-El $w 'btnConvertFromCm' -Timeout 10)
                $name = Find-El $null 'txtCmApplicationName' -Timeout 10
                Set-ElText $name $app
                Invoke-El (Find-El $null 'btnConvertPreview' -Timeout 10)
                $deadline = (Get-Date).AddSeconds(180)
                do { Start-Sleep 2; $s = (Find-El $null 'txtConvertStatus' -Timeout 5).Current.Name } while ((Get-Date) -lt $deadline -and $s -notmatch 'Preview:|rror|not found|No application')
                Check "$theme-04 Convert from ConfigMgr previews '$app'" ($s -match 'Preview:') "status: $s"
                Save-Shot (Join-Path $Run (Join-Path 'checks' (Join-Path $theme.ToLower() ('convert-' + ($app -replace '[^A-Za-z0-9]+', '-') + '.png'))))
                Real-Click (Find-El $null 'btnConvertClose' -Timeout 10)
                Start-Sleep 2
            }
        }
    }
    finally { Stop-Apd }
}
"TITLE-PARITY-FAILS=$script:Fails"
