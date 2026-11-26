<#
.SYNOPSIS
    Helpers that the guide capture scripts share (decision D-21): a run with one theme, the shot of the work area under a guide image id, a combo box choice,
    a wait for an element and a dump of the elements for a step that fails.

.DESCRIPTION
    Dot-source it after ApdUiDriver.ps1:
        . (Join-Path $PSScriptRoot 'ApdUiDriver.ps1'); . (Join-Path $PSScriptRoot 'GuideLib.ps1')
    The images are <Run>\images\<theme>\<id>.png (the work area, 1920 x 1032). Every file of a run lives in the run folder.
#>
$script:GuideRun = $null
$script:GuideTheme = 'Light'
$script:Sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Initialize-Run([string]$Run) {
    $script:GuideRun = $Run
    New-Item -ItemType Directory -Force $Run | Out-Null
}

# The folder that the run uses as the staging folder of a theme: only the user, the administrators and the system can write to it (the staging guard asks for it).
function New-RunStaging([string]$DataRoot) {
    $staging = Join-Path $DataRoot (Join-Path 'Users' (Join-Path $script:Sid 'Staging'))
    New-Item -ItemType Directory -Force $staging | Out-Null
    & icacls $staging /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    $staging
}

# Saves the work area as <Run>\images\<theme>\<id>.png.
function Shot([string]$Id, [int[]]$Rect) {
    Move-MouseAway
    $path = Join-Path $script:GuideRun (Join-Path 'images' (Join-Path $script:GuideTheme.ToLower() ($Id + '.png')))
    if ($Rect) { Save-Shot $path $Rect } else { Save-Shot $path }
    "OK $($script:GuideTheme) $Id"
}

# Runs one step. A step that fails writes FAIL with the reason and the elements that the screen holds, and the run goes on.
function Step([string]$Id, [scriptblock]$Body) {
    try { & $Body }
    catch {
        "FAIL $($script:GuideTheme) ${Id}: $($_.Exception.Message)"
        try { Move-MouseAway; Save-Shot (Join-Path $script:GuideRun (Join-Path 'fail' ($script:GuideTheme.ToLower() + '-' + $Id + '.png'))) } catch { }
        try { foreach ($w in (Get-ApdWindows)) { '  WINDOW ' + $w.Current.Name; Get-ElList $w 60 | ForEach-Object { '    ' + $_ } } } catch { }
        try { Close-OtherWindows } catch { }
    }
}

function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }

# The element with this automation id; throws when it does not show in time.
function Need-El([string]$Id, [double]$Timeout = 15) {
    $e = Find-El $null $Id -Timeout $Timeout
    if (-not $e) { throw "no element with the id $Id" }
    $e
}

function Need-Name([string]$Name, [string]$ControlType, [double]$Timeout = 15) {
    $e = if ($ControlType) { Find-El $null -Name $Name -ControlType $ControlType -Timeout $Timeout } else { Find-El $null -Name $Name -Timeout $Timeout }
    if (-not $e) { throw "no element named $Name" }
    $e
}

# Waits until the element shows (and is enabled when -Enabled); returns it or $null.
function Wait-El([string]$Id, [double]$Seconds = 60, [switch]$Enabled) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $e = Find-El $null $Id -Timeout 1
        if ($e -and -not $e.Current.IsOffscreen -and (-not $Enabled -or $e.Current.IsEnabled)) { return $e }
        Start-Sleep -Milliseconds 700
    } while ((Get-Date) -lt $deadline)
    $null
}

# Waits until the element is gone.
function Wait-Gone([string]$Id, [double]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $e = Find-El $null $Id -Timeout 1
        if (-not $e -or $e.Current.IsOffscreen) { return $true }
        Start-Sleep -Milliseconds 700
    }
    $false
}

# Picks the entry of a combo box whose text matches the pattern (-like).
function Select-Combo($Combo, [string]$Like) {
    $Combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 800
    $cond = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem)
    $item = $null
    foreach ($root in @($Combo) + @(Get-ApdWindows)) {
        foreach ($i in $root.FindAll($script:TS::Descendants, $cond)) {
            $names = @($i.Current.Name) + @($i.FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
            if ($names | Where-Object { $_ -like $Like }) { $item = $i; break }
        }
        if ($item) { break }
    }
    if (-not $item) { $Combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); throw "no combo entry like $Like" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 800
    # The list stays open after Select: close it, or the next click only closes the list.
    try { $Combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
    Start-Sleep -Milliseconds 500
}

function Set-Check($El, [bool]$On) {
    $t = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if (($t.Current.ToggleState -eq 'On') -ne $On) { $t.Toggle(); Start-Sleep -Milliseconds 500 }
}

# Types a path into the common file dialog that is open and confirms it.
function Send-DialogPath([string]$Path) {
    Start-Sleep -Seconds 2
    Send-ApdKeys ($Path.Replace('+', '{+}').Replace('^', '{^}').Replace('%', '{%}').Replace('~', '{~}') + '{ENTER}')
    Start-Sleep -Seconds 2
}

# Starts the app for a theme with the settings of the run; returns the data root.
function Start-RunApp([string]$Exe, [string]$Theme, [hashtable]$Settings = @{}, [string[]]$Arguments = @()) {
    $script:GuideTheme = $Theme
    $data = Join-Path $script:GuideRun "data-$Theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $staging = New-RunStaging $data
    $all = @{ DownloadRoot = $staging }
    foreach ($k in $Settings.Keys) { $all[$k] = $Settings[$k] }
    $null = Start-Apd $Exe $data $Theme $all $Arguments
    (Main-Window).SetFocus()
    $data
}

# Scrolls the page with the mouse wheel until the element with this automation id is in the visible part of the window (between the header and the footer).
function Scroll-ToId([string]$Id, [int]$X = 1000, [int]$Y = 600, [int]$Max = 40) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $X, $Y
    for ($i = 0; $i -lt $Max; $i++) {
        $e = Find-El $null $Id -Timeout 1
        if ($e) {
            $r = $e.Current.BoundingRectangle
            if (-not $e.Current.IsOffscreen -and $r.Y -gt 180 -and ($r.Y + $r.Height) -lt 900) { return $e }
        }
        Send-ApdWheel ([uint32]4294967176); Start-Sleep -Milliseconds 400
    }
    throw "the element $Id did not come into view"
}

# Closes every window of the app except the main window (a dialog that a failed step left open).
function Close-OtherWindows {
    # The dialogs of the app are owned windows: UI Automation lists them below the main window, not at the top. Close the innermost first.
    $isWindow = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Window)
    foreach ($top in @(Get-ApdWindows)) {
        $nested = @($top.FindAll($script:TS::Descendants, $isWindow))
        [array]::Reverse($nested)
        foreach ($n in $nested) {
            $p = $null
            if ($n.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$p)) { try { $p.Close() } catch { } }
            Start-Sleep -Milliseconds 700
        }
    }
    foreach ($w in @(Get-ApdWindows)) {
        if ($w.Current.Name -eq 'App Packager Desktop') { continue }
        $p = $null
        if ($w.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$p)) { try { $p.Close() } catch { } }
    }
    Start-Sleep 2
}

# Selects the row of a grid or a list whose text matches (-like): the first visible element below the scope with the name, then the row (data item) that holds it. The row is selected
# with the selection pattern and clicked; a click alone loses the selection when the page reloads the list. Returns the element.
function Pick-Row([string]$ScopeId, [string]$Like, [double]$Timeout = 20) {
    $scope = Need-El $ScopeId
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $deadline = (Get-Date).AddSeconds($Timeout)
    do {
        foreach ($e in $scope.FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($e.Current.Name -like $Like -and -not $e.Current.IsOffscreen -and -not $e.Current.BoundingRectangle.IsEmpty -and $e.Current.AutomationId -ne $ScopeId) {
                $row = $e
                for ($p = $e; $p -and -not [System.Windows.Automation.Automation]::Compare($p, $scope); $p = $walker.GetParent($p)) {
                    if ($p.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataItem -or $p.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) { $row = $p; break }
                }
                Click-El $row; Start-Sleep -Milliseconds 800
                $sp = $null
                if ($row.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { try { $sp.Select() } catch { } }
                Start-Sleep -Milliseconds 800
                return $row
            }
        }
        Start-Sleep 1
    } while ((Get-Date) -lt $deadline)
    throw "no row like '$Like' in $ScopeId"
}

# Selects the n-th visible row (data item) of a grid; throws when the grid has no such row.
function Pick-First([string]$ScopeId, [int]$Index = 0, [double]$Timeout = 20) {
    $scope = Need-El $ScopeId
    $deadline = (Get-Date).AddSeconds($Timeout)
    do {
        $rows = @($scope.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::DataItem))) |
            Where-Object { -not $_.Current.IsOffscreen -and -not $_.Current.BoundingRectangle.IsEmpty })
        if ($rows.Count -gt $Index) {
            $row = $rows[$Index]
            Click-El $row; Start-Sleep -Milliseconds 800
            $sp = $null
            if ($row.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { try { $sp.Select() } catch { } }
            Start-Sleep -Milliseconds 800
            return $row
        }
        Start-Sleep 1
    } while ((Get-Date) -lt $deadline)
    throw "$ScopeId has no row number $Index"
}

# The header check box of the Packages grid cycles through three states (every visible row, the rows with an update, none): clicks until it shows the wanted state ('On' or 'Off').
function Set-HeaderCheck($Window, [string]$State) {
    $box = Find-El $Window 'chkSelectAllPackages' -Timeout 10
    if (-not $box) { throw 'The grid has no header check box.' }
    $toggle = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    for ($i = 0; $i -lt 4 -and "$($toggle.Current.ToggleState)" -ne $State; $i++) { Click-El $box; Start-Sleep -Milliseconds 800 }
    if ("$($toggle.Current.ToggleState)" -ne $State) { throw "The header check box did not reach the state $State." }
}
