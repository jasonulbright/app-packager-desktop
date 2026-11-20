<#
.SYNOPSIS
    The lab run of the supersedence redesign (Q-93): drives the real app on the lab client against the ConfigMgr site, one scenario and one theme at a time.
    It turns supersedence on from the Supersedence menu of the Packages page, publishes, answers the confirmation window (Yes, Skip, No, or Yes with
    "Remember this answer" for two applications) and saves the guide images of the menu, the confirmation window, the locked title in the Workbench and the
    Options page.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the installed app or a copy
    of this folder; every file lives inside one run folder:
        .\Capture-PackagesSupersedence.ps1 -Run C:\guide\runs\apd-<timestamp> -Scenario Yes -Theme Light
    The images are <Run>\images\<theme>\<id>.png. The status bar, which names the site server, is painted over in each image.

    What the site must hold first (the run creates no older application and removes nothing):
      - for each application of the scenario an older version with the title "<base title> - <older version>", the same publisher and one deployment type
        (throw-away applications, made and removed by the person who runs the check). The run stages the applications with the command-line build.
    The scenario writes the settings of the isolated data folder (the settings of the lab user stay as they are) and ends the app when it is done.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][ValidateSet('Yes', 'Skip', 'No', 'Remember')][string]$Scenario,
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light',
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string[]]$Applications = @('Everything'),
    [hashtable]$ApplicationIds = @{ 'Everything' = 'catalog:67cc2246f034493e'; 'WizTree' = 'catalog:06915f526f2e443f' },
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    [string]$OutRoot = (Join-Path $Run 'images')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')

$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$cli = Join-Path $AppFolder 'AppPackagerBuild.exe'
$data = Join-Path $Run "data-$Scenario-$Theme"
# The staging folder sits under the user folder of the data folder: a folder that other accounts can write to is refused.
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$staging = Join-Path $data (Join-Path 'Users' (Join-Path $sid 'Staging'))
New-Item -ItemType Directory -Force $staging | Out-Null
# The run folder inherits write access for Authenticated Users: the staging folder keeps only this account and the administrators.
& icacls $staging /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null

function Wait-For([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { if (& $Condition) { return }; Start-Sleep -Milliseconds 700 }
    throw "Timed out: $What"
}

function Shot([string]$Id) {
    Save-Shot (Join-Path $OutRoot (Join-Path $Theme.ToLower() ($Id + '.png'))) -MaskFooter
    "OK $Theme $Id"
}

function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Status-Text($w) { (Find-El $w 'StatusLabel' -Timeout 2).Current.Name }

# Real mouse clicks: a WPF control does not always answer the Invoke pattern.
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 700 }

# The Everything or WizTree row: filter the grid by the name, and check the row.
function Check-Row($w, [string]$Name) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Name
    Start-Sleep 2
    $cell = Find-El $w -Name $Name -ControlType 'Custom' -Timeout 15
    if (-not $cell) { $cell = Find-El $w -Name "$Name (x64)" -ControlType 'Custom' -Timeout 5 }
    if (-not $cell) { throw "The grid has no row named '$Name'." }
    Click-El $cell; Start-Sleep 1
    $grid = Find-El $w 'PackagerGrid'
    $boxes = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox)))
    foreach ($box in $boxes) {
        if ($box.Current.AutomationId -eq 'SelectAllCheckBox') { continue }
        $toggle = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne 'On') { Real-Click $box }
    }
    $cell
}

# Stage the applications with the command-line build into the data folder (the grid finds the stage).
foreach ($name in $Applications) {
    $ErrorActionPreference = 'Continue'
    $out = & $cli --application $ApplicationIds[$name] --stage --data-root $data --download-root $staging 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    "stage ${name}: exit $exit"
    if ($exit -ne 0) { $out; throw "Stage of $name failed" }
}

$settings = @{
    FileShareRoot = $FileShareRoot; DownloadRoot = $staging; RemoveVersionFromTitle = $true
    Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false }
}
$null = Start-Apd $exe $data $Theme $settings
try {
    $w = Main-Window
    $w.SetFocus()

    # Check the application rows; the default is Off, so the Supersedence menu turns it on for the checked ones only.
    foreach ($name in $Applications) { $null = Check-Row $w $name }
    Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
    Start-Sleep 1

    # The grid publishes the staged version that it knows, so Check Latest comes first (it reads the vendor version of the checked rows).
    Real-Click (Find-El $w -Name 'Check Latest' -ControlType 'Button' -Timeout 5)
    $latestDeadline = (Get-Date).AddSeconds(180)
    Start-Sleep 5
    while ((Get-Date) -lt $latestDeadline -and (Status-Text $w) -match 'Check|Checking') { Start-Sleep 2 }
    "status after Check Latest: $(Status-Text $w)"

    Real-Click (Find-El $w 'btnSupersedence')
    $menu = Find-El ([System.Windows.Automation.AutomationElement]::RootElement) 'menuSupersedenceCheckedReplace' -Timeout 5
    if (-not $menu) { throw 'The Supersedence menu did not open.' }
    [System.Windows.Forms.Cursor]::Position = Get-ElCenter $menu
    Start-Sleep -Milliseconds 400
    Shot 'core-packages-supersedence-menu'
    Real-Click $menu
    Start-Sleep 2
    Move-MouseAway
    $settingsFile = (Get-ChildItem $data -Recurse -Filter settings.json | Select-Object -First 1).FullName
    $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
    "settings after the menu: default Mode='$($saved.Supersedence.Mode)'; overrides: $($saved.Supersedence.TitleOverrides | ConvertTo-Json -Compress)"
    "status: $(Status-Text $w)"

    # Publish to ConfigMgr for the checked applications.
    $w = Main-Window
    Real-Click (Find-El $w 'btnPublishConfigMgr')
    $prompts = 0
    $deadline = (Get-Date).AddSeconds(420)
    $finished = $false
    $logFile = Join-Path (Split-Path (Get-ChildItem $data -Recurse -Filter settings.json | Select-Object -First 1).FullName) 'SRLPackr\packr.log'
    while ((Get-Date) -lt $deadline -and -not $finished) {
        $yes = Find-El $null 'btnSupersedeYes' -Timeout 2
        if ($yes) {
            $prompts++
            $dialogText = (Find-El $null 'txtSupersedeOld' -Timeout 2).Current.Name + ' / ' + (Find-El $null 'txtSupersedeNew' -Timeout 2).Current.Name
            "confirmation $($prompts): $dialogText"
            if ($prompts -eq 1) {
                Move-MouseAway
                Shot "core-packages-supersedence-confirm"
            }
            if ($Scenario -eq 'Remember') {
                $remember = Find-El $null 'chkSupersedeRemember' -Timeout 2
                $toggle = $remember.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                if ($toggle.Current.ToggleState -ne 'On') { Real-Click $remember }
                if ($prompts -eq 1) { Move-MouseAway; Shot 'core-packages-supersedence-confirm-remember' }
            }
            $id = switch ($Scenario) { 'Skip' { 'btnSupersedeSkip' } 'No' { 'btnSupersedeNo' } default { 'btnSupersedeYes' } }
            Real-Click (Find-El $null $id -Timeout 5)
            Start-Sleep 2
            continue
        }
        $primary = Find-El $null 'PrimaryButton' -Timeout 1
        if ($primary) {
            'a dialog with a primary button is open: ' + ((Get-ElList (Main-Window) 400 | Where-Object { $_ -match 'Text' } | Select-Object -First 8) -join ' // ')
            Real-Click $primary; Start-Sleep 2; continue
        }
        # The run ends when the session log says so (the status bar of the page keeps its own text).
        $text = Get-Content $logFile -Raw -ErrorAction SilentlyContinue
        $packaged = ([regex]::Matches($text, '(?m)^\S+ \S+  Packaged\.')).Count
        $failed = ([regex]::Matches($text, '(?m)Package FAILED')).Count
        $stopped = $text -match 'declined at the confirmation|Cancelled|cancelled'
        if ($packaged + $failed -ge $Applications.Count -or $stopped) { $finished = $true }
        Start-Sleep 3
    }
    if (-not $finished) { Shot 'timeout'; Get-ElList (Main-Window) 80; throw 'The publish did not finish in time.' }
    Start-Sleep 3
    Move-MouseAway
    "confirmation windows shown: $prompts"
    '--- the log lines of the run ---'
    Get-Content $logFile | Where-Object { $_ -match 'Supersedence|Packaged|Package FAILED|Chain check|Application title|Version rule|declined|skipped|Before publish' } | ForEach-Object { if ($_.Length -gt 250) { $_.Substring(0, 250) } else { $_ } }
    '--- end ---'
    Shot 'core-packages-supersedence-log'

    # The locked title in the Workbench and the Options page (once, with the Yes scenario).
    if ($Scenario -eq 'Yes') {
        $w = Main-Window
        Set-ElText (Find-El $w 'txtAppPackagerSearch') $Applications[0]
        Start-Sleep 2
        $cell = Find-El $w -Name $Applications[0] -ControlType 'Custom' -Timeout 10
        Click-El $cell -Right; Start-Sleep 2
        $edit = Find-El ([System.Windows.Automation.AutomationElement]::RootElement) -Name 'Edit application...' -Timeout 5
        if ($edit) {
            Real-Click $edit; Start-Sleep 5
            $combo = Find-El $null 'cboTitleMode' -Timeout 15
            if ($combo) {
                "Workbench title combo enabled: $($combo.Current.IsEnabled)"
                Move-MouseAway
                Shot 'core-workbench-title-locked'
            } else { 'The Workbench has no title combo.' }
            foreach ($win in (Get-ApdWindows | Where-Object { $_.Current.Name -match 'Workbench' })) {
                try { $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
            }
            Start-Sleep 2
        }
        $w = Main-Window
        Real-Click (Find-El $w -Name 'Settings' -ControlType 'DataItem' -Timeout 5)
        Start-Sleep 3
        Real-Click (Find-El $w 'nav_supersedence' -Timeout 10)
        Start-Sleep 2
        Move-MouseAway
        Shot 'tools-settings-supersedence'
    }
}
finally {
    Stop-Apd
}
