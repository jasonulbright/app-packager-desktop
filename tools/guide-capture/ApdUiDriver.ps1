<#
.SYNOPSIS
    UI Automation helpers that drive the real App Packager Desktop window on the lab client (decision D-21): start the published app with an isolated data
    folder, find controls by automation id or name, click, type, open a context menu and save a screenshot of the 1080p work area.

.DESCRIPTION
    Dot-source it in the interactive session of the lab user, under the capture lock (Capture-ApdPages.ps1 shows the session and the lock):
        . .\ApdUiDriver.ps1
    The app runs maximized on the work area of 1920 x 1032. The settings of the run are written into the isolated data folder (the variable
    APP_PACKAGER_DATA_ROOT), so the settings of the lab user stay as they are. Clicks go through the mouse for a context menu and through the Invoke pattern
    for a button, so they do not depend on the position of a control.
#>
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
if (-not ('ApdW' -as [type])) {
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ApdW { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e); }'
}
if (-not ('ApdOwn' -as [type])) {
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ApdOwn { [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int max); public static string ForegroundTitle() { System.Text.StringBuilder s = new System.Text.StringBuilder(512); GetWindowTextW(GetForegroundWindow(), s, s.Capacity); return s.ToString(); } [StructLayout(LayoutKind.Sequential)] public struct PT { public int X; public int Y; } [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p); [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p); [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint id); static int Owner(IntPtr h) { uint id = 0; if (h != IntPtr.Zero) { GetWindowThreadProcessId(h, out id); } return (int)id; } public static int ForegroundProcessId() { return Owner(GetForegroundWindow()); } public static int ProcessIdAtCursor() { PT p; if (!GetCursorPos(out p)) { return 0; } return Owner(WindowFromPoint(p)); } }'
}
[void][ApdW]::SetProcessDPIAware()
$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$script:App = $null
# The processes that this run started and still holds a handle for. The person who is signed in may have windows open: key and mouse input goes only to
# a window of one of these processes.
$script:ApdOwned = New-Object System.Collections.Generic.List[System.Diagnostics.Process]

function Add-ApdOwnedProcess([System.Diagnostics.Process]$Process) {
    $null = $Process.Handle
    $script:ApdOwned.Add($Process)
}

# True when the process id belongs to a process of this run that has not ended.
function Test-ApdOwnedId([int]$Id) {
    foreach ($p in $script:ApdOwned) { if (-not $p.HasExited -and $p.Id -eq $Id) { return $true } }
    $false
}

# Throws unless the window in front belongs to this run.
function Assert-ApdForeground([string]$What) {
    $id = [ApdOwn]::ForegroundProcessId()
    if (-not (Test-ApdOwnedId $id)) { throw "$What was not sent: the window in front belongs to process $id, which this run did not start." }
}

# Throws unless the window under the mouse pointer belongs to this run.
function Assert-ApdPointer([string]$What) {
    $id = [ApdOwn]::ProcessIdAtCursor()
    if (-not (Test-ApdOwnedId $id)) { throw "$What was not sent: the window under the pointer belongs to process $id, which this run did not start." }
}

# The one way to send keys. SendKeys goes to the window in front, so that window must belong to this run.
function Send-ApdKeys([string]$Keys) {
    if ($Keys.Contains('%{F4}')) { throw 'Alt+F4 is sent only by Send-ApdCloseKey, which checks the title of the window in front.' }
    Assert-ApdForeground 'The keys'
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
}

# The fallback for a window of the app that UI Automation does not list: Alt+F4, sent only when the window in front belongs to this run and its title
# matches (-like) the title of the expected dialog. So it can not close the main window or a window of another program.
function Send-ApdCloseKey([string]$Title) {
    Assert-ApdForeground 'Alt+F4'
    $front = [ApdOwn]::ForegroundTitle()
    if ($front -notlike $Title) { throw "Alt+F4 was not sent: the window in front is '$front', not '$Title'." }
    [System.Windows.Forms.SendKeys]::SendWait('%{F4}')
}

# The one way to turn the mouse wheel (at the place of the pointer). Windows sends the wheel to the window under the pointer or to the one in front,
# by a setting of the user, so both must belong to this run.
function Send-ApdWheel([uint32]$Delta) {
    Assert-ApdPointer 'The wheel'
    Assert-ApdForeground 'The wheel'
    [ApdW]::mouse_event(0x0800, 0, 0, $Delta, [UIntPtr]::Zero)
}

function Get-ApdScreen { [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea }

# Saves the work area (1920 x 1032 on the 1080p display) as a PNG; with -Rect (x, y, width, height) only that part. The status bar shows the public names of
# the lab, which are never masked. -MaskFooter is accepted for the scripts that pass it and changes nothing.
function Save-Shot([string]$Path, [int[]]$Rect, [switch]$MaskFooter) {
    $null = $MaskFooter
    $b = Get-ApdScreen
    if (-not $Rect) { $Rect = @($b.X, $b.Y, $b.Width, $b.Height) }
    $bmp = New-Object System.Drawing.Bitmap $Rect[2], $Rect[3]
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($Rect[0], $Rect[1], 0, 0, $bmp.Size)
    $g.Dispose()
    # Bitmap.Save takes a relative path from the folder of the process, not from the location of PowerShell, so the path is made full first.
    $Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Writes settings.json for the isolated data folder and starts the app maximized. Returns the process.
function Start-Apd([string]$Exe, [string]$DataRoot, [string]$Theme, [hashtable]$Settings = @{}, [string[]]$Arguments = @()) {
    if (-not (Test-Path -LiteralPath $Exe)) { throw "no app at $Exe (give -AppFolder)" }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $userRoot = Join-Path $DataRoot (Join-Path 'Users' $sid)
    New-Item -ItemType Directory -Force $userRoot | Out-Null
    # CheckForUpdatesAtStartup is off: the update window of a newer release must not cover a page in the image (Capture-UpdateAndCli opens it with its own button).
    $all = [ordered]@{ Theme = $Theme; FirstRunCompleted = $true; SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; UseConfigMgr = $true; CheckForUpdatesAtStartup = $false }
    # The columns of a new install. The app takes a settings file that is there before its first start as an upgrade and would show the earlier, wider
    # set, so the compact set is written. A caller that needs other columns gives both values in -Settings.
    $all.PackagesShownColumns = @('ConfigMgr version', 'Latest', 'Status', 'CVE check')
    $all.PackagesHiddenColumns = @('Vendor', 'CVEs', 'Max CVSS', 'KEV', 'In latest', 'Newest CVE')
    foreach ($k in $Settings.Keys) { $all[$k] = $Settings[$k] }
    Set-Content -LiteralPath (Join-Path $userRoot 'settings.json') -Value ($all | ConvertTo-Json -Depth 6) -Encoding Ascii
    # The icon pack (C:\guide\icon-pack.zip, the release asset of app-packager-icons) goes into the user definitions folder of the run, so the grids show the packager icons.
    $pack = 'C:\guide\icon-pack.zip'
    $icons = Join-Path $userRoot 'Workbench\definitions\Icons'
    if ((Test-Path -LiteralPath $pack) -and -not (Test-Path -LiteralPath $icons)) {
        New-Item -ItemType Directory -Force $icons | Out-Null
        Expand-Archive -LiteralPath $pack -DestinationPath $icons -Force
    }
    $env:APP_PACKAGER_DATA_ROOT = $DataRoot
    if (Test-Path -LiteralPath 'C:\tools\dotnet') { $env:DOTNET_ROOT = 'C:\tools\dotnet' }
    $p = if ($Arguments) { Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru } else { Start-Process -FilePath $Exe -PassThru }
    Add-ApdOwnedProcess $p
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline -and $p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 500; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { Stop-ApdProcess $p; throw 'no main window' }
    [void][ApdW]::ShowWindow($p.MainWindowHandle, 3)
    [void][ApdW]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Seconds 4
    $script:App = $p
    $p
}

# Ends a process that this run started, through the handle that Start-Process returned. Never stops a process by name or by id.
function Stop-ApdProcess([System.Diagnostics.Process]$Process) {
    if (-not $Process -or $Process.HasExited) { return }
    try { $Process.Kill(); $null = $Process.WaitForExit(10000) } catch { Write-Warning "the process did not end: $($_.Exception.Message)" }
}

function Stop-Apd {
    if ($script:App -and -not $script:App.HasExited) { Stop-ApdProcess $script:App; Start-Sleep -Seconds 2 }
    $script:App = $null
}

# The top-level windows of the app (the main window, and the windows that it opened; a pop-up menu is one of them).
function Get-ApdWindows {
    if (-not $script:App -or $script:App.HasExited) { return @() }
    $c = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ProcessIdProperty), $script:App.Id
    @($script:AE::RootElement.FindAll($script:TS::Children, $c))
}

function Find-El($Scope, [string]$AutomationId, [string]$Name, [string]$ControlType, [double]$Timeout = 10) {
    $conds = @()
    if ($AutomationId) { $conds += New-Object System.Windows.Automation.PropertyCondition ($script:AE::AutomationIdProperty), $AutomationId }
    if ($Name) { $conds += New-Object System.Windows.Automation.PropertyCondition ($script:AE::NameProperty), $Name }
    if ($ControlType) { $conds += New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::($ControlType)) }
    $cond = if ($conds.Count -eq 1) { $conds[0] } else { New-Object System.Windows.Automation.AndCondition ([System.Windows.Automation.Condition[]]$conds) }
    $deadline = (Get-Date).AddSeconds($Timeout)
    do {
        $roots = if ($Scope) { @($Scope) } else { Get-ApdWindows }
        foreach ($r in $roots) { $e = $r.FindFirst($script:TS::Descendants, $cond); if ($e) { return $e } }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    $null
}

# Clicks an entry of the sidebar by its name (the sidebar has changed its entries over time, so a click by position goes stale). The entry is the element with the name
# in the left 240 px of the work area. Returns $false when the sidebar has no such entry.
function Click-Nav([string]$Name, [double]$Timeout = 10) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ($script:AE::NameProperty), $Name
    $deadline = (Get-Date).AddSeconds($Timeout)
    do {
        foreach ($w in (Get-ApdWindows)) {
            foreach ($e in $w.FindAll($script:TS::Descendants, $cond)) {
                $r = $e.Current.BoundingRectangle
                if ($r.IsEmpty -or $e.Current.IsOffscreen -or $r.X -gt 240) { continue }
                Click-El $e
                return $true
            }
        }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    $false
}

function Invoke-El($El) { $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

function Set-ElText($El, [string]$Text) { $El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text) }

function Get-ElCenter($El) {
    $r = $El.Current.BoundingRectangle
    New-Object System.Drawing.Point ([int]($r.X + $r.Width / 2)), ([int]($r.Y + $r.Height / 2))
}

function Click-At($Point, [switch]$Right) {
    [System.Windows.Forms.Cursor]::Position = $Point
    Start-Sleep -Milliseconds 300
    Assert-ApdPointer "The click at $($Point.X),$($Point.Y)"
    if ($Right) { [ApdW]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); [ApdW]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero) }
    else { [ApdW]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [ApdW]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
}

# A missing element has no rectangle, so its center would be the corner of the screen and the click would do nothing without a message.
function Click-El($El, [switch]$Right) {
    if (-not $El) { throw 'Click-El: there is no element to click.' }
    Click-At (Get-ElCenter $El) -Right:$Right
}

# Starts a Site Hygiene scan with -Click and waits for its end. The page keeps the summary of an earlier scan while a scan runs, so the end is read from the
# status line under the page: a scan clears it at its start and writes 'The scan ended: ...' at its end. A status that never changed and a Scan button that was
# never disabled do not count, because the click can be lost.
function Invoke-ApdHygieneScan([scriptblock]$Click, [int]$Minutes = 8) {
    $readText = { param($Id) $e = Find-El $null $Id -Timeout 1; if ($e) { [string]$e.Current.Name } else { '' } }
    $statusBefore = & $readText 'txtHygieneStatus'
    & $Click
    $sawStart = $false
    $deadline = (Get-Date).AddMinutes($Minutes)
    do {
        Start-Sleep -Milliseconds 700
        $status = & $readText 'txtHygieneStatus'
        $button = Find-El $null 'btnHygieneScan' -Timeout 1
        $idle = [bool]($button -and $button.Current.IsEnabled)
        if ($status -cne $statusBefore -or ($button -and -not $idle)) { $sawStart = $true }
    } while ((Get-Date) -lt $deadline -and -not ($sawStart -and $idle -and $status))
    [pscustomobject]@{ Ended = [bool]($sawStart -and $idle -and $status -like 'The scan ended*'); Status = $status; Summary = (& $readText 'txtHygieneSummary') }
}

# The window of the app (the main window, or a window that it opened) whose title matches (-like), or $null.
function Find-ApdWindow([string]$Like) {
    $isWindow = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Window)
    foreach ($top in @(Get-ApdWindows)) {
        if ($top.Current.Name -like $Like) { return $top }
        foreach ($n in $top.FindAll($script:TS::Descendants, $isWindow)) { if ($n.Current.Name -like $Like) { return $n } }
    }
    $null
}

# Closes a window of the app through its own UI Automation element; no key is sent. $false when the app shows no such window or it cannot be closed so.
function Close-ApdWindow([string]$Like) {
    $w = Find-ApdWindow $Like
    if (-not $w -or -not (Test-ApdOwnedId $w.Current.ProcessId)) { return $false }
    $p = $null
    if (-not $w.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$p)) { return $false }
    $p.Close()
    Start-Sleep -Seconds 2
    $true
}

# For a run whose publish must not meet an existing application: looks for a content dialog with a primary button. 'Application Already Exists' has
# Overwrite as its primary button, so it is looked for first: Skip is clicked and the run fails. A primary button is clicked only in a dialog whose
# title is one of -Expected; for any other dialog nothing is clicked and the run fails.
function Resolve-ApdContentDialog($Window, [string[]]$Expected) {
    $primary = Find-El $null 'PrimaryButton' -Timeout 1
    if (Find-El $null -Name 'Application Already Exists' -Timeout 0.3) {
        $skip = Find-El $Window -Name 'Skip' -ControlType 'Button' -Timeout 5
        if (-not $skip) { throw 'The Application Already Exists dialog is open and shows no Skip button. Nothing was clicked.' }
        Click-El $skip; Start-Sleep -Seconds 3
        throw 'The site already holds the application. Skip was clicked: nothing was overwritten.'
    }
    if (-not $primary -or $primary.Current.IsOffscreen) { return }
    foreach ($title in $Expected) {
        if (Find-El $null -Name $title -Timeout 0.3) { "the dialog '$title': its primary button"; Click-El $primary; Start-Sleep -Seconds 2; return }
    }
    throw "A dialog with a primary button and a title that this run does not expect is open (expected: $($Expected -join ', ')). Nothing was clicked."
}

# A list of the elements below a scope that have a name or an automation id, for finding the right control: type, id, name, position.
function Get-ElList($Scope, [int]$Max = 150) {
    $all = $Scope.FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $n = 0
    foreach ($e in $all) {
        $c = $e.Current
        if (-not $c.Name -and -not $c.AutomationId) { continue }
        if ($c.IsOffscreen) { continue }
        $r = $c.BoundingRectangle
        if ($r.IsEmpty) { continue }
        '{0,-12} id={1,-28} name={2} [{3},{4} {5}x{6}]' -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.AutomationId, $c.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height
        if (++$n -ge $Max) { break }
    }
}

function Move-MouseAway { [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 1060; Start-Sleep -Milliseconds 400 }

# Adds or replaces top-level values of the settings file of the isolated data folder (the running app reads the file when it needs a value).
function Update-ApdSettings([string]$DataRoot, [hashtable]$Values) {
    $file = (Get-ChildItem $DataRoot -Recurse -Filter settings.json | Select-Object -First 1).FullName
    $json = Get-Content $file -Raw | ConvertFrom-Json
    foreach ($k in $Values.Keys) { $json | Add-Member -Force -NotePropertyName $k -NotePropertyValue ([pscustomobject]$Values[$k]) }
    $json | ConvertTo-Json -Depth 8 | Set-Content $file -Encoding Ascii
}

# The bounds of the text of the status bar that names the site and the site server (x, y, width, height), or $null.
function Get-FooterRect {
    $e = Find-El $null 'txtSiteFooter' -Timeout 3
    if (-not $e) { return $null }
    $r = $e.Current.BoundingRectangle
    [int[]]@([int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
}

# The checks, the reports and the ways to add an application are menu items since the V1 overhaul (phase 3c): open the menu, then click the item.
function Open-ApdMenuItem($Window, [string]$Menu, [string]$Item) {
    $button = Find-El $Window $Menu -Timeout 5
    if (-not $button) { throw "There is no menu button $Menu." }
    Click-El $button
    Start-Sleep -Milliseconds 600
    $found = Find-El $null $Item -Timeout 5
    if (-not $found) { throw "The menu $Menu shows no item $Item." }
    if (-not $found.Current.IsEnabled) { Send-ApdKeys '{ESC}'; throw "The item $Item of the menu $Menu is disabled." }
    Click-El $found
    Start-Sleep -Milliseconds 800
}

# The check boxes of the rows of the Packages grid, without the check box of the header.
function Get-ApdRowBoxes($Grid) {
    $c = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox)
    @($Grid.FindAll($script:TS::Descendants, $c) | Where-Object { $_.Current.AutomationId -notin 'chkSelectAllPackages', 'SelectAllCheckBox' })
}

# ---------------- grids of the Settings page
# The row of a grid whose name starts with -Name. The grid shows only the rows on screen, so it is scrolled from the top in small steps.
function Find-ApdGridRow($Grid, [string]$Name) {
    $scroll = $Grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $items = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::DataItem)
    foreach ($percent in @(0..50 | ForEach-Object { $_ * 2 })) {
        $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $percent)
        Start-Sleep -Milliseconds 400
        foreach ($item in $Grid.FindAll([System.Windows.Automation.TreeScope]::Children, $items)) {
            if ($item.Current.Name -like "$Name*") { return $item }
        }
    }
    $null
}

# Opens a drop-down list with the mouse (at its right edge) and takes the entry of the list with the text.
function Select-ApdItem($Combo, [string]$Text) {
    # Bring the cell into view first: the grid scrolls, and a click on a cell below the visible part misses.
    try { $Combo.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 500 } catch { $null = $_ }
    $r = $Combo.Current.BoundingRectangle
    Click-At (New-Object System.Drawing.Point ([int]($r.X + $r.Width - 14)), ([int]($r.Y + $r.Height / 2)))
    Start-Sleep -Milliseconds 900
    $entry = Find-El $null $null $Text 'ListItem' -Timeout 3
    if (-not $entry) {
        $names = foreach ($w2 in (Get-ApdWindows)) { $w2.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object { $_.Current.Name } }
        throw "The list has no entry '$Text'. The list items on screen: $($names -join ' | ')"
    }
    if ($entry.Current.IsOffscreen) {
        # The list opens over the combo and the first entries can sit above the work area, where a mouse click misses: press the keys instead.
        # The list is open: Home goes to its first entry, Down goes to the entry by its place among the entries of the list, Enter takes it.
        $listItems = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem)
        $left = $entry.Current.BoundingRectangle.X
        $places = @(foreach ($w2 in (Get-ApdWindows)) { foreach ($li in $w2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItems)) { $b2 = $li.Current.BoundingRectangle; if ([Math]::Abs($b2.X - $left) -lt 2 -and $b2.Width -gt 0) { [pscustomobject]@{ Name = $li.Current.Name; Y = $b2.Y } } } }) | Sort-Object Y -Unique
        $index = [Array]::IndexOf(@($places | ForEach-Object { $_.Name }), $Text)
        if ($index -lt 0) { throw "The list has no place for the entry '$Text'." }
        Send-ApdKeys '{HOME}'
        if ($index -gt 0) { Send-ApdKeys ('{DOWN ' + $index + '}') }
        Send-ApdKeys '{ENTER}'
    }
    else { Click-El $entry }
    Start-Sleep -Milliseconds 700
}

# Checks the check box of the row (the first cell) with the mouse, reads it back, and returns the row.
function Set-ApdGridRowCheck($Grid, [string]$Name) {
    $row = Find-ApdGridRow $Grid $Name
    if (-not $row) { throw "The grid has no row for '$Name'." }
    try { $row.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 700 } catch { $null = $_ }
    $cell = $row.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), ('Item: ' + $row.Current.Name + ', Column Display Index: 0')))
    if (-not $cell) { throw "The row of '$Name' has no check box cell." }
    # A click on a row that shows only in part selects it and scrolls the grid, so the box can stay empty. The box is read, and one more click follows.
    $box = $cell.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox)))
    if (-not $box) { throw "The row of '$Name' has no check box." }
    foreach ($attempt in 1, 2) {
        if ($box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') { break }
        Click-El $box
        Start-Sleep -Milliseconds 700
    }
    if ($box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { throw "Two clicks did not check the box of '$Name'." }
    $r = $row.Current.BoundingRectangle
    "     clicked the check box of '$Name' at {0},{1}; the page says: {2}" -f [int]$r.X, [int]$r.Y, (Find-El $null 'txtCheckedCount' -Timeout 2).Current.Name
    $row
}

# True while a run of the App Packager page is active. Cancel shows only then: it keeps its room, hidden, while no run is active.
function Test-ApdBusy($Window) {
    $c = Find-El $Window 'btnCancelOperation' -Timeout 1
    [bool]($c -and -not $c.Current.IsOffscreen)
}

# Opens the Logs window with the button under the grid and returns it. Close-ApdLogs closes it with its own Close button.
function Open-ApdLogs($Window) {
    $button = Find-El $Window 'btnLogs' -Timeout 10
    if (-not $button) { throw 'The App Packager page has no Logs button.' }
    Click-El $button
    if (-not (Find-El $null 'lstLog' -Timeout 20)) { throw 'The Logs window did not open.' }
    Start-Sleep -Seconds 2
    Find-ApdWindow 'Log'
}
function Close-ApdLogs {
    $close = Find-El $null 'btnLogClose' -Timeout 5
    if (-not $close) { return }
    Invoke-El $close
    Start-Sleep -Seconds 1
}

# True for a text that is only the glyph of an icon (a character of the private use area), which UI Automation lists as a text.
function Test-ApdGlyph([string]$Text) { [bool]($Text.Length -ge 1 -and $Text.Length -le 2 -and [int][char]$Text[0] -ge 0xD800 -and [int][char]$Text[0] -le 0xF8FF) }

# Throws unless the selection line of the App Packager page says that exactly -Count applications are checked, none of them outside the view. The search
# box of the grid must be empty or show every checked row.
function Assert-ApdSelection($Window, [int]$Count) {
    $e = Find-El $Window 'txtSelectionCount' -Timeout 5
    $text = if ($e) { [string]$e.Current.Name } else { '' }
    if ($text -cne "$Count selected") { throw "The selection line says '$text', not '$Count selected'. Nothing was published." }
}

# What the open review differs in from the expected publish: exactly the rows of -Applications (the names of the Application column), exactly the system
# columns of -Systems, and in each cell one of the allowed texts of its system. -CellTexts gives the allowed texts of a system; without an entry only the
# plain create action is allowed. So a Skip, a blocked or unsupported system, an application on the site or a failed read does not pass unless its text
# is given. No output means that the review matches.
function Get-ApdReviewProblems([string[]]$Applications, [string[]]$Systems, [hashtable]$CellTexts = @{}) {
    $read = { param($Id) $e = Find-El $null $Id -Timeout 2; if ($e) { [string]$e.Current.Name } else { '' } }
    $create = @{ ConfigMgr = 'Create application and deployment type'; Intune = 'Create Win32 app'; WSUS = 'Create software update' }
    # The order of the system columns of the app.
    $ordered = @('ConfigMgr', 'Intune', 'WSUS' | Where-Object { $Systems -contains $_ })
    $n = $ordered.Count
    $count = $Applications.Count
    if ($n -eq 0 -or $n -ne $Systems.Count -or $count -eq 0) { "the caller names $count application(s) and the systems '$($Systems -join ', ')'"; return }

    $heading = & $read 'txtReviewHeading'
    $wantHeading = "Review $count $(if ($count -eq 1) { 'application' } else { 'applications' })"
    if ($heading -cne $wantHeading) { "the heading is '$heading', not '$wantHeading'" }
    $writes = $count * $n
    $counts = & $read 'txtReviewCounts'
    $wantCounts = "$count $(if ($count -eq 1) { 'application' } else { 'applications' }), $n $(if ($n -eq 1) { 'system' } else { 'systems' }): $writes $(if ($writes -eq 1) { 'publish is' } else { 'publishes are' }) planned."
    if ($counts -cne $wantCounts) { "the counts line is '$counts', not '$wantCounts'" }
    # "Publish All: ConfigMgr and WSUS - Site MCM - from Settings, Systems in Use" (the parts are joined by a middle dot).
    $names = if ($n -gt 1) { ($ordered[0..($n - 2)] -join ', ') + ' and ' + $ordered[-1] } else { $ordered[0] }
    $scope = & $read 'txtReviewScope'
    if ($scope -cnotlike "Publish All: $names *from Settings, Systems in Use") { "the scope line is '$scope', not a Publish All to $names" }
    $blocked = & $read 'txtReviewBlocked'
    if ($blocked) { "the review names a blocked system: $blocked" }

    $grid = Find-El $null 'gridReviewMatrix' -Timeout 10
    if (-not $grid) { 'the review shows no matrix'; return }
    $ofType = { param($Scope, $Type) @($Scope.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), $Type))) }
    $headers = @(& $ofType $grid ([System.Windows.Automation.ControlType]::HeaderItem) |
        Where-Object { $_.Current.Name -and $_.Current.Name -notlike 'System.*' -and $_.Current.BoundingRectangle.Width -gt 0 } | ForEach-Object { [string]$_.Current.Name })
    $wantHeaders = @('Application', 'Version', 'Checked') + $ordered
    if (($headers -join '|') -cne ($wantHeaders -join '|')) { "the columns are '$($headers -join ' | ')', not '$($wantHeaders -join ' | ')'" }

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $seen = New-Object System.Collections.Generic.List[string]
    foreach ($row in @(& $ofType $grid ([System.Windows.Automation.ControlType]::DataItem) | Where-Object { -not $_.Current.IsOffscreen -and -not $_.Current.BoundingRectangle.IsEmpty })) {
        $cells = New-Object System.Collections.Generic.List[string]
        for ($c = $walker.GetFirstChild($row); $c; $c = $walker.GetNextSibling($c)) {
            if ($c.Current.ControlType -eq [System.Windows.Automation.ControlType]::HeaderItem) { continue }
            # A matrix cell holds an icon beside the text, and the glyph of the icon is listed as a text too.
            $all = @(& $ofType $c ([System.Windows.Automation.ControlType]::Text))
            $texts = @($all | ForEach-Object { [string]$_.Current.Name } | Where-Object { -not (Test-ApdGlyph $_) })
            if ($texts) { $cells.Add([string](@($texts | Where-Object { $_ } | Select-Object -First 1) -join '')) }
            elseif ($all) { $cells.Add('') }
            else { $cells.Add([string]$c.Current.Name) }
        }
        if ($cells.Count -ne 3 + $n) { "a row has $($cells.Count) cells, not $(3 + $n): $($cells -join ' | ')"; $seen.Add('(unreadable row)'); continue }
        $seen.Add($cells[0])
        if (-not $cells[1] -or $cells[1] -ceq 'Not checked') { "the row '$($cells[0])' has no version" }
        for ($i = 0; $i -lt $n; $i++) {
            $system = $ordered[$i]
            $allowed = @(if ($CellTexts.ContainsKey($system)) { $CellTexts[$system] } else { $create[$system] })
            if ($allowed -cnotcontains $cells[3 + $i]) { "the $system cell of '$($cells[0])' is '$($cells[3 + $i])'; allowed: '$($allowed -join ''' or ''')'" }
        }
    }
    $got = @($seen | Sort-Object -CaseSensitive) -join "', '"
    $want = @($Applications | Sort-Object -CaseSensitive) -join "', '"
    if ($seen.Count -ne $count -or $got -cne $want) { "the review lists $($seen.Count) row(s) '$got'; expected $count row(s) '$want'" }
}

# Confirms the open review only when it matches the expected publish (Get-ApdReviewProblems) and takes the comment. Else it selects Back and throws, so
# nothing is published. This is the one place of these scripts, beside the guard of Probe-ReleaseE2E.ps1, that clicks btnReviewConfirm.
function Confirm-ApdReview {
    param(
        [Parameter(Mandatory)][string[]]$Applications,
        [Parameter(Mandatory)][ValidateSet('ConfigMgr', 'Intune', 'WSUS')][string[]]$Systems,
        [hashtable]$CellTexts = @{},
        [string]$Comment = ''
    )
    Start-Sleep -Seconds 2
    $problems = @()
    $confirm = $null
    try {
        $problems = @(Get-ApdReviewProblems $Applications $Systems $CellTexts)
        # The comment box shows only when ConfigMgr or Intune is in the plan.
        if (-not $problems -and $Comment -and @($Systems | Where-Object { $_ -ne 'WSUS' })) {
            $box = Find-El $null 'txtReviewComment' -Timeout 5
            if ($box) { Set-ElText $box $Comment; Start-Sleep -Milliseconds 500 }
            $typed = if ($box) { [string]$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } else { '(no box)' }
            if ($typed -cne $Comment) { $problems += "the comment box holds '$typed', not the comment of the run" }
        }
        $confirm = Find-El $null 'btnReviewConfirm' -Timeout 3
        if (-not $confirm -or $confirm.Current.IsOffscreen -or -not $confirm.Current.IsEnabled) { $problems += 'the confirm button is not available' }
    }
    catch { $problems += "the review could not be read: $($_.Exception.Message)" }
    if ($problems) {
        $back = Find-El $null 'btnReviewBack' -Timeout 3
        if ($back) { Click-El $back; Start-Sleep -Seconds 1 }
        $how = if ($back) { 'Back was selected' } else { 'no Back button was found' }
        throw "The publish review is not the expected one; $how and nothing was confirmed. $($problems -join '; ')"
    }
    $line = Find-El $null 'txtReviewCounts' -Timeout 1
    "publish review matches '$($Applications -join ''', ''')' for $($Systems -join ', '): $(if ($line) { $line.Current.Name }) Confirm."
    Click-El $confirm
    Start-Sleep -Seconds 3
}

# Follows a run after Publish All until it ends, and writes what it answered. Every publish opens the review first (it opens again when a fresh read
# differs from the plan) and the results window after the run. The run counts as active while the review is open. Each review is confirmed only through
# Confirm-ApdReview, with the expected -Applications and -Systems (and -CellTexts for a cell that is not the plain create action). -Comment goes into the
# comment box of the review. -OnConflict Skip answers the Application Already Exists dialog with Skip; Return leaves it open and returns. -Expected names
# the other content dialogs whose primary button may be clicked; the app asks none by default.
function Complete-ApdPublish {
    param(
        $Window,
        [int]$Seconds = 420,
        [Parameter(Mandatory)][string[]]$Applications,
        [Parameter(Mandatory)][ValidateSet('ConfigMgr', 'Intune', 'WSUS')][string[]]$Systems,
        [hashtable]$CellTexts = @{},
        [ValidateSet('Skip', 'Return')][string]$OnConflict = 'Skip',
        [string[]]$Expected = @(),
        [string]$Comment = ''
    )
    Start-Sleep -Seconds 3
    $reviews = 0
    $idle = 0
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $confirm = Find-El $null 'btnReviewConfirm' -Timeout 1
        if ($confirm -and -not $confirm.Current.IsOffscreen) {
            if (-not $confirm.Current.IsEnabled) {
                $blocked = Find-El $null 'txtReviewBlocked' -Timeout 1
                $why = if ($blocked) { $blocked.Current.Name } else { '(no text)' }
                $back = Find-El $null 'btnReviewBack' -Timeout 2
                if ($back) { Click-El $back; Start-Sleep -Seconds 1 }
                throw "The publish review cannot run: $why"
            }
            if (++$reviews -gt 3) {
                $back = Find-El $null 'btnReviewBack' -Timeout 2
                if ($back) { Click-El $back; Start-Sleep -Seconds 1 }
                throw 'The publish review opened more than three times.'
            }
            Confirm-ApdReview -Applications $Applications -Systems $Systems -CellTexts $CellTexts -Comment $Comment
            $idle = 0
            continue
        }
        $done = Find-El $null 'btnResultsDone' -Timeout 1
        if ($done -and -not $done.Current.IsOffscreen) {
            $headline = Find-El $null 'txtResultsHeadline' -Timeout 1
            "publish results: $(if ($headline) { $headline.Current.Name })"
            Click-El $done; Start-Sleep -Seconds 2
            return
        }
        $skip = Find-El $Window -Name 'Skip' -ControlType 'Button' -Timeout 0.1
        if ($skip -and -not $skip.Current.IsOffscreen) {
            if ($OnConflict -eq 'Return') { 'the Application Already Exists dialog is open'; return }
            'the Application Already Exists dialog: Skip'
            Click-El $skip; Start-Sleep -Seconds 3
            $idle = 0
            continue
        }
        # Any other content dialog with a primary button: clicked only when its title is one of -Expected, else the run fails and nothing is clicked.
        $primary = Find-El $null 'PrimaryButton' -Timeout 0.1
        if ($primary -and -not $primary.Current.IsOffscreen) { Resolve-ApdContentDialog $Window $Expected; $idle = 0; continue }
        if (Test-ApdBusy $Window) { $idle = 0 } elseif (++$idle -ge 3) { return }
        Start-Sleep -Seconds 1
    }
    throw "The publish run did not end in $Seconds seconds."
}
