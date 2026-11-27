<#
.SYNOPSIS
    The keyboard pass of the installed app (plan section 4.0): on each page and window, Tab through the controls and record each focus stop with its name,
    and check the keyboard rules of the Packages grid and of the Pivots and Scripts editors.

.DESCRIPTION
    Run it in the signed-in session of the lab user through tools\lab-tests\Run-InSession.ps1, from a folder with ApdUiDriver.ps1 and GuideLib.ps1.
    The run writes nothing to a site, to WSUS or to Intune. It walks with Tab, Shift+Tab, Ctrl+Tab and Escape, only in windows of the app that it started.
    It types one letter into the query editor of Pivots and Scripts (the editor rule; nothing is run). It never sends Enter or Space during a walk, and it
    closes each window with Escape, its Back or Cancel button, or the close of the window.

    Per page and window it reports: the focus stops in order (control type, name, automation id), stops without a name, and enabled controls that can take
    the keyboard focus, are visible, and were never reached.
    Rules that it checks:
      - Packages grid: one Tab leaves the grid (to the detail pane or the batch bar), and Shift+Tab goes back to the search row (Columns or More);
      - Pivots and Scripts, query editor: Tab leaves an editor whose text did not change in this visit; after a change Tab types a tab; Esc then Tab
        leaves; Ctrl+Tab leaves.
    Windows: the Workbench and WSUS Updates (owned windows of the main window, which the list of top windows does not hold), the One Click plan (opened
    with One Click; closed without Run and without Plan only), the publish review (opened for one checked application; closed with Back), Setup and Logs.
    The publish results window shows only after a publish, which writes, so this run does not open it.

    The run ends with KEYBOARD-ISSUES=<n>.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string[]]$Pages = @('App Packager', 'Add from web', 'Add from file', 'Installer Analysis', 'Detection Tester',
        'Deployment Helper', 'Pivots and Scripts', 'Collection and Compliance', 'Maintenance Windows', 'DP Content Manager', 'Site Hygiene', 'Settings'),
    [string[]]$Windows = @('WSUS Updates', 'Workbench', 'One Click', 'Review Publish All', 'Setup', 'Log'),
    [int]$MaximumStops = 250,
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    # The definition id of the one tracked application of the One Click plan (7-Zip: the plan only reads), and its name in the grid.
    [string]$TrackedId = 'fa505d1415214f14',
    [string]$ReviewApplication = '7-Zip'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
$script:Issues = 0
$Run = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Run)
Initialize-Run $Run
$ae = [System.Windows.Automation.AutomationElement]
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

function Issue([string]$Text) { $script:Issues++; Write-Host "ISSUE $Text" }
function Describe($e) {
    $c = $e.Current
    '{0} | name="{1}" | id={2}' -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name, $c.AutomationId
}
function Key-Of($e) { try { ($e.GetRuntimeId() -join '.') } catch { "gone-$([guid]::NewGuid())" } }
function Grid-Of($e) {
    $p = $e
    for ($i = 0; $i -lt 12 -and $p; $i++) {
        if ($p.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataGrid) { return $p }
        $p = $walker.GetParent($p)
    }
    $null
}
# The grid of the App Packager page is named "Applications".
function Test-PackagesGrid($Grid) { [bool]($Grid -and $Grid.Current.Name -ceq 'Applications') }
function Get-FocusNow { try { $ae::FocusedElement } catch { $null } }
function Send-Walk([string]$Keys) {
    if ($Keys -notin '{TAB}', '+{TAB}', '^{TAB}', '{ESC}') { throw "A walk sends no key '$Keys'." }
    Send-ApdKeys $Keys
    Start-Sleep -Milliseconds 200
}

# Walks one window from where the focus is until the stops come round, then reports what was never reached.
function Measure-Walk([string]$Label, $Window) {
    $seen = [ordered]@{}
    $gridStops = 0; $lastGrid = ''; $leftGrid = 0; $repeat = 0; $outside = $false; $stops = 0; $same = 0
    while ($stops -lt $MaximumStops) {
        $now = Get-FocusNow
        $grid = if ($now) { try { Grid-Of $now } catch { $null } } else { $null }
        $gridKey = if ($grid) { Key-Of $grid } else { '' }
        if ($gridKey -and $gridKey -eq $lastGrid) { $gridStops++ } else { $gridStops = 0 }
        $lastGrid = $gridKey
        $before = if ($now) { Key-Of $now } else { '' }
        # Tab walks the cells of the other grids; Ctrl+Tab is the standard key that leaves them. A control that keeps Tab (an editor) is also left so.
        if ($gridStops -ge 12 -or $same -ge 2) { Send-Walk '^{TAB}'; $leftGrid++; $gridStops = 0; $same = 0 } else { Send-Walk '{TAB}' }
        $stops++
        $f = Get-FocusNow
        if (-not $f) { continue }
        if (-not (Test-ApdOwnedId $f.Current.ProcessId)) { $outside = $true; Issue "$Label : the focus left the app: $(Describe $f)"; break }
        $key = Key-Of $f
        if ($key -eq $before) { $same++ } else { $same = 0 }
        if (Test-PackagesGrid $grid) {
            $after = try { Grid-Of $f } catch { $null }
            if (Test-PackagesGrid $after) { Issue "$Label : Tab did not leave the Packages grid: $(Describe $f)" }
        }
        if ($seen.Contains($key)) { $repeat++ } else { $repeat = 0; $seen[$key] = Describe $f }
        # A full circle: many stops in a row that were all seen before.
        if ($repeat -ge 15) { break }
    }
    $n = 0; foreach ($v in $seen.Values) { $n++; "  stop $n : $v" }
    "  stops=$($seen.Count) tabs=$stops leftWithCtrlTab=$leftGrid circleClosed=$($repeat -ge 15)"
    if ($repeat -lt 15 -and -not $outside) { Issue "$Label : Tab did not come round in $MaximumStops presses" }
    if ($seen.Count -lt 2) { Issue "$Label : only $($seen.Count) focus stop(s)" }
    foreach ($v in @($seen.Values | Where-Object { $_ -match 'name="" ' -and $_ -notmatch '^(DataItem|Custom|Pane|Group) ' })) { Issue "$Label : a focus stop has no name: $v" }

    # Shift+Tab goes back: the focus must move.
    try { $before = Key-Of (Get-FocusNow); Send-Walk '+{TAB}'; if ((Key-Of (Get-FocusNow)) -eq $before) { Issue "$Label : Shift+Tab did not move the focus" } }
    catch { "  Shift+Tab not read: $($_.Exception.Message)" }

    $types = 'Button', 'Edit', 'ComboBox', 'CheckBox', 'RadioButton', 'Hyperlink', 'Slider'
    $all = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $missed = foreach ($e in $all) {
        try {
            $c = $e.Current
            $type = $c.ControlType.ProgrammaticName.Replace('ControlType.', '')
            if ($types -contains $type -and $c.IsEnabled -and $c.IsKeyboardFocusable -and -not $c.IsOffscreen -and -not $seen.Contains((Key-Of $e)) -and -not (Grid-Of $e)) { Describe $e }
        } catch { }
    }
    # A radio group has one Tab stop; the arrow keys reach the other buttons, so they are listed apart.
    foreach ($m in @($missed)) { if ($m -like 'RadioButton*') { "  note, radio button reached by arrows: $m" } else { Issue "$Label : not reached by Tab: $m" } }
    Save-Shot (Join-Path $Run ('images\keyboard-' + ($Label -replace '[^A-Za-z]', '') + '.png'))
}

# Tab leaves the Packages grid, and Shift+Tab goes back to the search row. The focus is put on a cell with a click on the name of the first row.
function Test-GridKeys {
    '---- rule: Packages grid'
    $w = Main-Window
    $grid = Find-El $w 'PackagerGrid' -Timeout 10
    $row = $grid.FindFirst($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::DataItem)))
    if (-not $row) { Issue 'Packages grid : there is no row'; return }
    foreach ($case in @(@('{TAB}', @('btnDetailPaneOpen', 'splDetailPane', 'btnDetailPaneClose', 'btnDetailSystems', 'btnDetailWorkbench', 'btnDetailMoreActions', 'btnStage', 'btnPublishAll', 'btnSelectUpdates', 'btnLogs')),
                        @('+{TAB}', @('btnMore', 'btnColumns')))) {
        Click-El $row; Start-Sleep -Milliseconds 700
        $start = Get-FocusNow
        if (-not (Test-PackagesGrid (Grid-Of $start))) { Issue "Packages grid : a click on a row did not put the focus in the grid: $(Describe $start)"; continue }
        Send-Walk $case[0]
        $f = Get-FocusNow
        "  $($case[0]) from the grid: $(Describe $f)"
        if (Test-PackagesGrid (Grid-Of $f)) { Issue "Packages grid : $($case[0]) did not leave the grid" }
        elseif ($case[1] -notcontains $f.Current.AutomationId) { Issue "Packages grid : $($case[0]) went to '$($f.Current.AutomationId)', not to one of $($case[1] -join ', ')" }
    }
}

# The editor rule of Pivots and Scripts on the query editor. The text is changed and never run.
function Test-EditorKeys {
    '---- rule: Pivots and Scripts editor'
    $editor = Find-El $null 'txtQuery' -Timeout 10
    if (-not $editor) { Issue 'Pivots editor : there is no query editor'; return }
    $text = { [string]$editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
    $inEditor = { $f = Get-FocusNow; [bool]($f -and $f.Current.AutomationId -ceq 'txtQuery') }
    # A new visit comes from another control of the page: Shift+Tab goes to it, and Tab comes back into the unchanged editor.
    $enter = { Click-El $editor; Start-Sleep -Milliseconds 500; Send-Walk '+{TAB}'; if (& $inEditor) { Send-Walk '+{TAB}' }; Send-Walk '{TAB}' }

    & $enter
    if (-not (& $inEditor)) { Issue "Pivots editor : the editor did not get the focus back: $(Describe (Get-FocusNow))"; return }
    $before = & $text
    Send-Walk '{TAB}'
    if (& $inEditor) { Issue 'Pivots editor : Tab did not leave the unchanged editor' } else { '  Tab leaves the unchanged editor' }
    if ((& $text) -cne $before) { Issue 'Pivots editor : Tab changed the text of an unchanged editor' }

    & $enter
    if (-not (& $inEditor)) { Issue 'Pivots editor : the editor did not get the focus back, so no letter is typed'; return }
    Send-ApdKeys 'a'; Start-Sleep -Milliseconds 500
    $changed = & $text
    Send-Walk '{TAB}'
    $after = & $text
    if ((& $inEditor) -and $after.Length -eq $changed.Length + 1 -and $after.Contains("`t")) { '  after a change, Tab types a tab' } else { Issue "Pivots editor : after a change Tab did not type a tab (focus in the editor: $(& $inEditor))" }
    Send-Walk '{ESC}'; Send-Walk '{TAB}'
    if (& $inEditor) { Issue 'Pivots editor : Esc and then Tab did not leave the editor' } else { '  Esc, then Tab, leaves the editor' }

    & $enter
    if (-not (& $inEditor)) { Issue 'Pivots editor : the editor did not get the focus back, so no letter is typed'; return }
    Send-ApdKeys 'a'; Start-Sleep -Milliseconds 500
    Send-Walk '^{TAB}'; Start-Sleep -Milliseconds 500
    $f = Get-FocusNow
    if ((& $inEditor) -or -not (Test-ApdOwnedId $f.Current.ProcessId)) { Issue 'Pivots editor : Ctrl+Tab did not leave the changed editor' } else { "  Ctrl+Tab leaves the changed editor: $(Describe $f)" }
}

# Closes the window with Escape; when it stays, through the close of the window. Never another key and never a button of a dialog.
function Close-Walked([string]$Title) {
    if (-not (Find-ApdWindow $Title)) { return }
    try { (Find-ApdWindow $Title).SetFocus(); Send-Walk '{ESC}'; Start-Sleep -Seconds 1 } catch { }
    if (Find-ApdWindow $Title) { $null = Close-ApdWindow $Title; Start-Sleep -Seconds 2 }
    if (Find-ApdWindow $Title) { throw "The window '$Title' did not close with Escape or with its close. The run stops here and clicks nothing." }
}
function Wait-Window([string]$Title, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do { $w = Find-ApdWindow $Title; if ($w) { Start-Sleep -Seconds 3; return (Find-ApdWindow $Title) }; Start-Sleep -Seconds 1 } while ((Get-Date) -lt $deadline)
    $null
}

$data = Join-Path $Run 'data'
$settings = @{
    FileShareRoot = $FileShareRoot; DownloadRoot = (New-RunStaging $data); SiteCodeSaved = $true
    # WSUS is in use, so the sidebar holds WSUS Updates.
    Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true }; Wsus = @{ ServerName = 'cm01.contoso.com'; PortNumber = 8530; UseSsl = $false }
    OneClick = @{ Action = 'Report'; TrackedApplications = @($TrackedId) }
}
$null = Start-Apd (Join-Path $AppFolder 'ApplicationPackagerUI.exe') $data 'Light' $settings
try {
    if (-not (Wait-El 'btnMore' 90 -Enabled)) { throw 'the App Packager page did not load' }
    foreach ($page in $Pages) {
        "---- page $page"
        if (-not (Click-Nav $page 20)) { Issue "$page : the sidebar has no entry"; continue }
        Start-Sleep -Seconds 4
        try { (Main-Window).SetFocus() } catch { }
        Measure-Walk $page (Main-Window)
        if ($page -eq 'App Packager') { try { Test-GridKeys } catch { Issue "Packages grid : $($_.Exception.Message)" } }
        if ($page -eq 'Pivots and Scripts') {
            try {
                Click-El (Need-El 'tabPivot'); Start-Sleep -Seconds 2
                Test-EditorKeys
            } catch { Issue "Pivots editor : $($_.Exception.Message)" }
        }
    }

    foreach ($title in $Windows) {
        "---- window $title"
        $opened = $null
        try {
            $null = Click-Nav 'App Packager'; Start-Sleep -Seconds 3
            switch ($title) {
                'WSUS Updates' { if (-not (Click-Nav 'WSUS Updates' 20)) { throw 'the sidebar has no entry WSUS Updates' } }
                'Workbench' { if (-not (Click-Nav 'Workbench' 20)) { throw 'the sidebar has no entry Workbench' } }
                'One Click' { Click-El (Need-El 'btnOneClick') }
                'Review Publish All' {
                    Set-ElText (Need-El 'txtAppPackagerSearch') $ReviewApplication; Start-Sleep -Seconds 3
                    $box = Get-ApdRowBoxes (Need-El 'PackagerGrid') | Select-Object -First 1
                    if (-not $box) { throw "the grid shows no row for $ReviewApplication" }
                    if ($box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Click-El $box; Start-Sleep -Seconds 1 }
                    Click-El (Need-El 'btnPublishAll')
                }
                'Setup' { Open-ApdMenuItem (Main-Window) 'btnMore' 'miSetup' }
                'Log' { Click-El (Need-El 'btnLogs') }
            }
            # One Click checks the tracked application before its window opens.
            $opened = Wait-Window $title $(if ($title -eq 'One Click') { 600 } else { 120 })
            if (-not $opened) { Issue "$title : the window did not open"; continue }
            try { $opened.SetFocus() } catch { }
            Start-Sleep -Seconds $(if ($title -eq 'WSUS Updates') { 10 } else { 1 })
            Measure-Walk $title $opened
        }
        catch { Issue "$title : $($_.Exception.Message)" }
        finally {
            # The review closes with Back only; every other window with Escape or its close. Nothing is confirmed.
            if ($title -eq 'Review Publish All') {
                $back = Find-El $null 'btnReviewBack' -Timeout 3
                if ($back) { Invoke-El $back; Start-Sleep -Seconds 2 }
                $clear = Find-El $null 'btnClearSelection' -Timeout 2
                if ($clear -and -not $clear.Current.IsOffscreen) { Invoke-El $clear }
                $search = Find-El $null 'txtAppPackagerSearch' -Timeout 2
                if ($search) { Set-ElText $search '' }
            }
            Close-Walked $title
        }
    }
    '---- not opened: Publish results (it shows only after a publish)'
}
finally { Stop-Apd }
"KEYBOARD-ISSUES=$script:Issues"
