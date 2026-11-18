<#
.SYNOPSIS
    The lab capture run of the guide images of the supersedence views of the App Packager page (decision D-21): the context menu with "Use as old application...",
    the Old Application window (list, confirmation, linked state), the log and status after "Apply supersedence", and the One Click plan with the step Supersedence.
    It drives the real app with UI Automation (ApdUiDriver.ps1), maximized on the 1080p work area (1920 x 1032), one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Capture-ApdPages.ps1 shows the session and the lock), from the folder of the
    published app, with every file inside one run folder (C:\guide\runs\<owner>-<yyyyMMdd-HHmmss>, testing policy "Lab hygiene"):
        .\Capture-PackagesSupersedence.ps1 -Run <run folder> -SampleMsi <msi> -SeedDll <SeedPendingSupersedence.dll> -Themes Light
    The images are <Run>\images\<theme>\<guide image id>.png. The status bar, which names the site server, is painted over in each image.

    What the site must hold first (the run does not create or remove site objects):
      - A published application with the title that the stage of the sample gets, at the version of the sample. The sample is an MSI that has
        the product name <SearchText>, the version 2.0.0 and the architecture x86 (the stage names it "<name> - 2.0.0 (x86)").
      - An older application with the title -OldTitle and a lower version, with no keywords, with one deployment type (the one to link).
      - Applications that show the other states of the list: one with keywords, one that is not older, one that is retired. Their titles contain
        <SearchText>.
    The first theme stages the sample with the app itself (Add from file, Stage now). The run ends each theme with "Remove Link", so the next theme starts
    unlinked; the relation of the supersedence step stays on the site, so remove it (or publish the application again) between themes for equal logs.
    -SeedDll is the build of SeedPendingSupersedence (copy it with its runtimeconfig next to the app files); it writes the pending supersedence state that
    the plan reads. The run closes every window that it opens and ends the app.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$SampleMsi,
    [Parameter(Mandatory)][string]$SeedDll,
    [string]$AppFolder = $PSScriptRoot,
    [string]$DotnetExe = 'C:\tools\dotnet\dotnet.exe',
    [string]$SearchText = 'Sample Viewer',
    [string]$OldTitle = 'Sample Viewer 1.0',
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$OutRoot = (Join-Path $Run 'images')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')

$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$data = Join-Path $Run 'capture-data'
$baseSettings = @{
    FileShareRoot = Join-Path $Run 'share'; DownloadRoot = Join-Path $Run 'staging'
    Supersedence = @{ Mode = 'Replace' }; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false }
}

function Wait-For([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { if (& $Condition) { return }; Start-Sleep -Milliseconds 700 }
    throw "Timed out: $What"
}

function Shot([string]$Theme, [string]$Id) {
    Save-Shot (Join-Path $OutRoot (Join-Path $Theme.ToLower() ($Id + '.png'))) -MaskFooter
    "OK $Theme $Id"
}

function Main-Window { Get-ApdWindows | Select-Object -First 1 }
function Status-Text($w) { (Find-El $w 'StatusLabel' -Timeout 2).Current.Name }

# Add from file with the sample, then Stage now (the Configure dialog is confirmed with Continue). Leaves the app on the App Packager page.
function Add-AndStage {
    $w = Main-Window
    Click-El (Find-El $w 'nav-addfile'); Start-Sleep 3
    Invoke-El (Find-El $w 'btnAddFileOpen'); Start-Sleep 3
    [System.Windows.Forms.SendKeys]::SendWait($SampleMsi.Replace('+', '{+}').Replace('^', '{^}').Replace('%', '{%}').Replace('~', '{~}') + '{ENTER}')
    Wait-For { Find-El $w 'btnAddFileSave' -Timeout 1 } 60 'the review of the sample'
    $accept = Find-El $w 'chkAddFileAcceptPredicted' -Timeout 2
    if ($accept -and $accept.Current.IsEnabled) {
        $toggle = $accept.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
    }
    Invoke-El (Find-El $w 'btnAddFileSave')
    Wait-For { Find-El $w 'btnAddResultStage' -Timeout 1 } 30 'the result panel'
    Invoke-El (Find-El $w 'btnAddResultStage')
    $continue = Find-El $w -Name 'Continue' -ControlType 'Button' -Timeout 20
    if ($continue) { Invoke-El $continue }
    Wait-For { (Find-El $w 'txtAddResultStage' -Timeout 1).Current.Name -like 'Stage complete*' } 180 'the stage'
    Invoke-El (Find-El $w 'btnAddResultDone')
    Click-El (Find-El $w 'nav-packages'); Start-Sleep 3
}

function Filter-Grid($w) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $SearchText
    Start-Sleep 2
}

function Select-Row($w) {
    $cell = Find-El $w -Name $SearchText -ControlType 'Custom' -Timeout 15
    if (-not $cell) { throw "The grid has no row named '$SearchText'." }
    Click-El $cell
    Start-Sleep 1
    # A row of a new start is not checked, and "Apply supersedence" works on the checked rows.
    $grid = Find-El $w 'PackagerGrid'
    $boxes = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox)))
    foreach ($box in $boxes) {
        if ($box.Current.AutomationId -eq 'SelectAllCheckBox') { continue }
        $toggle = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
    }
    $cell
}

# The Apply supersedence action for the checked row; returns the status of the row when the run ends.
function Apply-Supersedence($w) {
    Invoke-El (Find-El $w 'btnApplySupersedence')
    Start-Sleep 4
    Wait-For { (Find-El $w 'btnApplySupersedence' -Timeout 1).Current.IsEnabled } 120 'Apply supersedence'
    Start-Sleep 2
    Status-Text $w
}

foreach ($theme in $Themes) {
    "== $theme"
    $null = Start-Apd $exe $data $theme $baseSettings
    $w = Main-Window
    if (-not (Get-ChildItem (Join-Path $Run 'staging') -Recurse -Filter stage-manifest.json -ErrorAction SilentlyContinue)) { Add-AndStage; $w = Main-Window }
    Filter-Grid $w
    $null = Select-Row $w

    # The log of a new start and of a stage names the folder of the run: three runs of the action fill the pane with lines of its own before an
    # image is taken. With no link yet they end with "No older version", and change nothing on the site.
    1..3 | ForEach-Object { "apply (no link yet): $(Apply-Supersedence $w)" }

    # The context menu with the new item.
    $cell = Select-Row $w
    Click-El $cell -Right; Start-Sleep 2
    $item = Find-El ([System.Windows.Automation.AutomationElement]::RootElement) 'menuUseAsOldApplication' -Timeout 5
    if (-not $item) { throw 'The context menu has no item "Use as old application...".' }
    [System.Windows.Forms.Cursor]::Position = Get-ElCenter $item
    Start-Sleep -Milliseconds 350
    Shot $theme 'core-packages-context-menu'

    # The Old Application window: the list, the confirmation, the linked state.
    Click-El $item; Start-Sleep 4
    if (-not (Find-El $null 'txtOldApplicationIntro' -Timeout 10)) { throw 'The Old Application window did not open.' }
    Set-ElText (Find-El $null 'txtOldApplicationSearch') $SearchText
    Invoke-El (Find-El $null 'btnOldApplicationSearch')
    Wait-For { (Find-El $null 'txtOldApplicationStatus').Current.Name -match 'found|No application' } 90 'the search of the site'
    Start-Sleep 1
    $grid = Find-El $null 'dgOldApplications'
    Click-El (Find-El $grid -Name $OldTitle -ControlType 'Custom')
    Start-Sleep 1
    Move-MouseAway
    Shot $theme 'core-packages-old-application'

    Invoke-El (Find-El $null 'btnOldApplicationLink'); Start-Sleep 2
    Wait-For { Find-El $null 'PrimaryButton' -Timeout 1 } 20 'the confirmation'
    Move-MouseAway
    Shot $theme 'core-packages-old-application-confirm'
    Invoke-El (Find-El $null 'PrimaryButton')
    Wait-For { (Find-El $null 'txtOldApplicationStatus').Current.Name -match 'is the old application' } 90 'the link'
    Start-Sleep 1
    Click-El (Find-El $grid -Name $OldTitle -ControlType 'Custom')
    Start-Sleep 1
    Move-MouseAway
    Shot $theme 'core-packages-old-application-linked'

    # Remove Link at the end of the theme, after the Apply supersedence below needs the link: the window closes first.
    Invoke-El (Find-El $null 'btnOldApplicationClose'); Start-Sleep 2

    # Apply supersedence with the link: the status of the row and the lines of the log.
    $w = Main-Window
    $cell = Select-Row $w
    "apply (linked): $(Apply-Supersedence $w)"
    Move-MouseAway
    Shot $theme 'core-packages-apply-supersedence'

    # The One Click plan with the pending supersedence step.
    $definitionFile = Get-ChildItem $data -Recurse -Filter *.json | Where-Object { $_.DirectoryName -like '*\Workbench\definitions' } | Select-Object -First 1
    $definition = Get-Content $definitionFile.FullName -Raw | ConvertFrom-Json
    $stageManifest = Get-Content (Get-ChildItem (Join-Path $Run 'staging') -Recurse -Filter stage-manifest.json | Select-Object -First 1).FullName -Raw | ConvertFrom-Json
    $siteApp = $stageManifest.AppName
    & $DotnetExe $SeedDll $data $definition.Id $stageManifest.ApplicationId $siteApp $stageManifest.SoftwareVersion 'MCM' 'cm01.contoso.com'
    Update-ApdSettings $data @{ OneClick = @{ Action = 'StageAndPackage'; TrackedApplications = @($definition.Id); DefaultDestinations = @{ ConfigMgr = $true; WSUS = $false; Intune = $false } } }
    Invoke-El (Find-El $w 'btnOneClick')
    Wait-For { Find-El $null 'gridPlan' -Timeout 1 } 30 'the One Click plan'
    Start-Sleep 2
    Move-MouseAway
    Shot $theme 'core-one-click-plan-supersedence'
    $close = Find-El $null -Name 'Close' -ControlType 'Button' -Timeout 3
    if ($close) { Invoke-El $close; Start-Sleep 2 }

    # Remove Link, so the next theme starts as this one did.
    $cell = Select-Row (Main-Window)
    Click-El $cell -Right; Start-Sleep 2
    Click-El (Find-El ([System.Windows.Automation.AutomationElement]::RootElement) 'menuUseAsOldApplication' -Timeout 5); Start-Sleep 4
    Set-ElText (Find-El $null 'txtOldApplicationSearch') $SearchText
    Invoke-El (Find-El $null 'btnOldApplicationSearch')
    Wait-For { (Find-El $null 'txtOldApplicationStatus').Current.Name -match 'found|No application' } 90 'the search of the site'
    $grid = Find-El $null 'dgOldApplications'
    Click-El (Find-El $grid -Name $OldTitle -ControlType 'Custom'); Start-Sleep 1
    Invoke-El (Find-El $null 'btnOldApplicationClear'); Start-Sleep 2
    Wait-For { Find-El $null 'PrimaryButton' -Timeout 1 } 20 'the confirmation'
    Invoke-El (Find-El $null 'PrimaryButton')
    Wait-For { (Find-El $null 'txtOldApplicationStatus').Current.Name -match 'marker is removed' } 90 'Remove Link'
    "removed: $((Find-El $null 'txtOldApplicationStatus').Current.Name)"
    Invoke-El (Find-El $null 'btnOldApplicationClose'); Start-Sleep 2
    Stop-Apd
}
