<#
.SYNOPSIS
    Lab check of Check ConfigMgr in APD for a list of packagers (Q-128c): the application exists in ConfigMgr (made by the PowerShell app),
    APD runs Check ConfigMgr for it, and the grid must show the version that ConfigMgr holds. Prints one line per packager:
    "FOUND <packager> <title> <version>" or "MISSING <packager> <title>", then the session log lines of the check, and "CHECK-CM-MISSING=<n>".

.DESCRIPTION
    Run under the capture lock from a folder that holds the repository files:
        Run-VisibleProbe.ps1 -Worktree <repo folder> -ProbeArgs "& .\tools\guide-capture\Probe-CheckConfigMgr.ps1 -Run C:\guide\runs\apd-checkcm -AppFolder C:\guide\cand -Packagers putty,awssamcli -Expected @{ putty='0.85' }"
    The run reads the site only.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string[]]$Packagers,
    [hashtable]$Expected = @{},
    [string]$Theme = 'Light',
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'ApdUiDriver.ps1')
. (Join-Path $here 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$golden = (Get-Content (Join-Path $here '..\..\tests\ApplicationPackagerEngine.Tests\Fixtures\TitleParity\PowerShellTitles.golden.json') -Raw | ConvertFrom-Json).Packagers
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Grid-Texts($w) {
    $grid = Find-El $w 'PackagerGrid'
    @($grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { try { $_.Current.Name } catch { '' } } | Where-Object { $_ })
}
function Search-Grid($w, [string]$Text) { Set-ElText (Find-El $w 'txtAppPackagerSearch') $Text; Start-Sleep -Seconds 2 }
function Check-Row($w, [string]$Name) {
    Search-Grid $w $Name
    $grid = Find-El $w 'PackagerGrid'
    $box = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) |
        Where-Object { $_.Current.AutomationId -ne 'chkSelectAllPackages' } | Select-Object -First 1
    if (-not $box) { throw "The grid has no row for '$Name'." }
    if ($box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
    Search-Grid $w ''
}
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) { Start-Sleep 2 }
}
$data = Join-Path $Run "data-$Theme"
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
$staging = New-RunStaging $data
$settings = @{
    DownloadRoot = $staging; FileShareRoot = $FileShareRoot; SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; SiteCodeSaved = $true
    Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false }; CheckForUpdatesAtStartup = $false
}
$proc = Start-Apd $exe $data $Theme $settings
$missing = 0
try {
    $w = Main-Window
    $w.SetFocus()
    Start-Sleep 4
    foreach ($id in $Packagers) {
        $p = $golden | Where-Object { $_.Packager -eq $id -or $_.Definition -eq $id } | Select-Object -First 1
        if (-not $p) { throw "No packager '$id' in the golden file." }
        Check-Row $w $p.App
    }
    Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckConfigMgr'
    Wait-Idle $w 900
    foreach ($id in $Packagers) {
        $p = $golden | Where-Object { $_.Packager -eq $id -or $_.Definition -eq $id } | Select-Object -First 1
        Search-Grid $w $p.App
        $texts = Grid-Texts $w
        $want = $Expected[$id]
        $hit = if ($want) { $texts -contains $want } else { @($texts | Where-Object { $_ -match '^\d+(\.\d+)+' }).Count -gt 0 }
        if ($hit) { "FOUND $id '$($p.App)' (CMName '$($p.CMName)')" } else { $missing++; "MISSING $id '$($p.App)' (CMName '$($p.CMName)') cells: " + (($texts | Select-Object -First 14) -join ' | ') }
    }
    $log = Get-ChildItem $data -Recurse -Filter packr.log -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($log) { Get-Content $log.FullName | Where-Object { $_ -match 'ConfigMgr|Not found|found' } | Select-Object -Last 60 }
}
finally { Stop-Apd }
"CHECK-CM-MISSING=$missing"
