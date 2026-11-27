<#
.SYNOPSIS
    The lab run of the supersedence redesign (Q-93): drives the real app on the lab client against the ConfigMgr site, one scenario and one theme at a time.
    It turns supersedence on for the applications in Settings, Supersedence (the grid and the buttons of the checked applications), publishes, answers the confirmation window (Yes, Skip, No, or Yes with
    "Remember this answer" for two applications) and saves the guide images of the confirmation window and of the log.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-InSession.ps1 takes it), from the folder of the published app:
        & .\Capture-PackagesSupersedence.ps1 -Run <work folder> -Scenario Yes -Theme Light -AppFolder $PWD
    The images are <Run>\images\<theme>\<id>.png. The applications are the guide definitions zzapd-everything and zzapd-wiztree (the scenario Remember takes both).
    The site must hold, for each application of the scenario, exactly one older version "<name> (x64) - <older version>" with the publisher ZZAPD Lab and one
    deployment type (tools/guide-capture/lab-seed, part Supersedence). The run publishes the newer version, and at its end it removes that new application and its
    new content folders, only as far as PublishCleanup.ps1 can prove that the run created them. The older version stays for the next scenario.
    A publish review is confirmed only when it lists exactly the applications of the scenario ("<name> (x64)") for ConfigMgr, each with the create action and
    the supersedence step, before or after the read of the older version on the site; else the run selects Back.
    The guide image of the Settings grid comes from Capture-SettingsSupersedence.ps1.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][ValidateSet('Yes', 'Skip', 'No', 'Remember')][string]$Scenario,
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light',
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string[]]$Applications = @('ZZAPD Everything'),
    [hashtable]$ApplicationIds = @{ 'ZZAPD Everything' = 'zzapd00000000001'; 'ZZAPD WizTree' = 'zzapd00000000002' },
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    [string]$OutRoot = (Join-Path $Run 'images')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
. (Join-Path $PSScriptRoot 'PublishCleanup.ps1')
if ($Scenario -eq 'Remember' -and $Applications.Count -lt 2) { $Applications = @('ZZAPD Everything', 'ZZAPD WizTree') }
Connect-GuideSite
$before = @(Get-GuideApplications)
# The site holds one older version of each application (lab-seed, part Supersedence) and no other version.
foreach ($name in $Applications) {
    $found = @($before | Where-Object { $_.LocalizedDisplayName -like "$name (x64) - *" })
    if ($found.Count -ne 1) { $found | ForEach-Object { "on the site before the run: $(Format-GuideApp $_)" }; throw "The site must hold exactly one older version of '$name' before the run; it holds $($found.Count). The run does not start." }
}
$beforeModels = @($before | ForEach-Object { [string]$_.ModelName })
# What the publish review must show: the names of the Application column, and the ConfigMgr cell before the read of the site or after it, which finds the
# older version.
$reviewNames = @($Applications | ForEach-Object { "$_ (x64)" })
$createText = 'Create application and deployment type'
$reviewCells = @{ ConfigMgr = @("$createText. Then: apply supersedence") + @($Applications | ForEach-Object {
            $name = $_
            $older = [string]@($before | Where-Object { $_.LocalizedDisplayName -like "$name (x64) - *" })[0].SoftwareVersion
            if (-not $older) { throw "The older version of '$name' on the site has no software version. The run does not start." }
            "$createText (ConfigMgr has $older; if the title matches, the run asks before it replaces it). Then: apply supersedence"
        }) }
$beforeFolders = @(Get-GuideShareFolders $FileShareRoot)

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
    foreach ($box in (Get-ApdRowBoxes $grid)) {
        $toggle = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne 'On') { Real-Click $box }
    }
    $cell
}

# Stage the applications with the command-line build into the data folder (the grid finds the stage).
Install-GuideDefinition $data @('zzapd-everything', 'zzapd-wiztree')
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

    # Check the application rows of the App Packager page.
    foreach ($name in $Applications) { $null = Check-Row $w $name }
    Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
    Start-Sleep 1
    Assert-ApdSelection $w $Applications.Count

    # The grid publishes the staged version that it knows, so Check Latest comes first (it reads the vendor version of the checked rows).
    Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
    $latestDeadline = (Get-Date).AddSeconds(180)
    Start-Sleep 5
    while ((Get-Date) -lt $latestDeadline -and (Status-Text $w) -match 'Check|Checking') { Start-Sleep 2 }
    "status after Check Latest: $(Status-Text $w)"

    # The mode of an application is a setting of Settings, Supersedence (the toolbar has no button for it): check the rows of the applications in the grid, give them
    # Replace with the button of the checked applications, save, and go back to the App Packager page.
    if (-not (Click-Nav 'Settings')) { throw 'The sidebar has no Settings entry.' }
    Start-Sleep 3
    Real-Click (Find-El $w 'nav_supersedence' -Timeout 10)
    Start-Sleep 2
    $grid = Find-El $w 'dgSupersedenceApps' -Timeout 10
    if (-not $grid) { throw 'Settings, Application Settings has no grid of applications.' }
    foreach ($name in $Applications) { $null = Set-ApdGridRowCheck $grid $name }
    # The bulk bar sets the chosen column to the chosen value for the checked applications: choose the column Supersedence and the value Replace, then press the button.
    Select-ApdItem (Find-El $w 'cboBulkColumn' -Timeout 5) 'Supersedence'
    Select-ApdItem (Find-El $w 'cboBulkValue' -Timeout 5) 'Replace'
    Real-Click (Find-El $w 'btnBulkApply' -Timeout 5)
    Start-Sleep 1
    Real-Click (Find-El $w 'btnSaveSettings' -Timeout 5)
    Start-Sleep 2
    if (-not (Click-Nav 'App Packager')) { throw 'The sidebar has no App Packager entry.' }
    Start-Sleep 3
    $settingsFile = (Get-ChildItem $data -Recurse -Filter settings.json | Select-Object -First 1).FullName
    $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
    "settings after the menu: default Mode='$($saved.Supersedence.Mode)'; overrides: $($saved.Supersedence.TitleOverrides | ConvertTo-Json -Compress)"
    "status: $(Status-Text $w)"

    # Publish to ConfigMgr for the checked applications.
    $w = Main-Window
    Assert-ApdSelection $w $Applications.Count
    Real-Click (Find-El $w 'btnPublishAll')
    $prompts = 0
    $reviews = 0
    $deadline = (Get-Date).AddSeconds(420)
    $finished = $false
    while ((Get-Date) -lt $deadline -and -not $finished) {
        # Publish All opens the review first, and again when ConfigMgr, read again, differs from the plan.
        $review = Find-El $null 'btnReviewConfirm' -Timeout 1
        if ($review -and -not $review.Current.IsOffscreen) {
            if (++$reviews -gt 3) { $back = Find-El $null 'btnReviewBack' -Timeout 2; if ($back) { Real-Click $back }; throw 'The publish review opened more than three times.' }
            Confirm-ApdReview -Applications $reviewNames -Systems 'ConfigMgr' -CellTexts $reviewCells
            continue
        }
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
        # Application Already Exists is answered with Skip and fails the run; another dialog is clicked only when its title is expected.
        Resolve-ApdContentDialog (Main-Window) @()
        # The run ends when the session log says so (the status bar of the page keeps its own text).
        $text = Get-GuideRunLog $data
        $packaged = ([regex]::Matches($text, '(?m)^\S+ \S+  Packaged\.')).Count
        $failed = ([regex]::Matches($text, '(?m)Package FAILED')).Count
        $stopped = $text -match 'declined at the confirmation|Cancelled|cancelled'
        if ($packaged + $failed -ge $Applications.Count -or $stopped) { $finished = $true }
        Start-Sleep 3
    }
    if (-not $finished) { Shot 'timeout'; Get-ElList (Main-Window) 80; throw 'The publish did not finish in time.' }
    # The results window of the run covers the log: close it before the image.
    $done = Find-El $null 'btnResultsDone' -Timeout 20
    if ($done) { Real-Click $done }
    Start-Sleep 3
    Move-MouseAway
    "confirmation windows shown: $prompts"
    '--- the log lines of the run ---'
    (Get-GuideRunLog $data) -split "`r?`n" | Where-Object { $_ -match 'Supersedence|Packaged|Package FAILED|Chain check|Application title|Version rule|declined|skipped|Before publish' } | ForEach-Object { if ($_.Length -gt 250) { $_.Substring(0, 250) } else { $_ } }
    '--- end ---'
    $null = Open-ApdLogs (Main-Window)
    Move-MouseAway
    Shot 'core-packages-supersedence-log'
    Close-ApdLogs
}
finally {
    Stop-Apd
    try { Complete-GuideSiteCleanup $data $beforeModels $FileShareRoot $beforeFolders }
    catch { "cleanup of the site failed, nothing more was removed: $($_.Exception.Message)" }
}
