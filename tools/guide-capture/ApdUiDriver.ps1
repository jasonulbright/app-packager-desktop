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
[void][ApdW]::SetProcessDPIAware()
$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$script:App = $null

function Get-ApdScreen { [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea }

# Saves the work area (1920 x 1032 on the 1080p display) as a PNG; with -Rect (x, y, width, height) only that part. -MaskFooter paints over the text of the status bar
# with the color next to it: the text names the site server, which a guide image must not show.
function Save-Shot([string]$Path, [int[]]$Rect, [switch]$MaskFooter) {
    $Mask = @()
    if ($MaskFooter) { $f = Get-FooterRect; if ($f) { $Mask = @(,$f) } }
    $b = Get-ApdScreen
    if (-not $Rect) { $Rect = @($b.X, $b.Y, $b.Width, $b.Height) }
    $bmp = New-Object System.Drawing.Bitmap $Rect[2], $Rect[3]
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($Rect[0], $Rect[1], 0, 0, $bmp.Size)
    foreach ($m in $Mask) {
        $x = $m[0] - $Rect[0]; $y = $m[1] - $Rect[1]
        $brush = New-Object System.Drawing.SolidBrush ($bmp.GetPixel([math]::Max(0, $x - 8), [math]::Max(0, $y + [int]($m[3] / 2))))
        $g.FillRectangle($brush, $x - 2, $y - 1, $m[2] + 4, $m[3] + 2)
        $brush.Dispose()
    }
    $g.Dispose()
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Writes settings.json for the isolated data folder and starts the app maximized. Returns the process.
function Start-Apd([string]$Exe, [string]$DataRoot, [string]$Theme, [hashtable]$Settings = @{}) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $userRoot = Join-Path $DataRoot (Join-Path 'Users' $sid)
    New-Item -ItemType Directory -Force $userRoot | Out-Null
    $all = [ordered]@{ Theme = $Theme; FirstRunCompleted = $true; SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; UseConfigMgr = $true }
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
    $env:DOTNET_ROOT = 'C:\tools\dotnet'
    $p = Start-Process -FilePath $Exe -PassThru
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline -and $p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 500; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; throw 'no main window' }
    [void][ApdW]::ShowWindow($p.MainWindowHandle, 3)
    [void][ApdW]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Seconds 4
    $script:App = $p
    $p
}

function Stop-Apd {
    if ($script:App -and -not $script:App.HasExited) { Stop-Process -Id $script:App.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
    $script:App = $null
}

# The top-level windows of the app (the main window, and the windows that it opened; a pop-up menu is one of them).
function Get-ApdWindows {
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
    if ($Right) { [ApdW]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); [ApdW]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero) }
    else { [ApdW]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [ApdW]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
}

function Click-El($El, [switch]$Right) { Click-At (Get-ElCenter $El) -Right:$Right }

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
