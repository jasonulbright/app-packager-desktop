<#
.SYNOPSIS
    The lab check of Q-125: the glyph and the text of a button with an icon sit on one line. The probe starts runs that show a Cancel button, copies the pixels of the button, and
    measures the offset between the glyph and the first capital letter of the text. A button that is not busy (Check Latest) gives the reference. The run ends with "ALIGN-FAILS=<n>".

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from a folder that holds the scripts of tools\guide-capture:
        Run-VisibleProbe.ps1 -Worktree <folder with these scripts> -ProbeArgs "& .\Probe-CancelAlignment.ps1 -Run C:\guide\runs\apd-q125"
    The run reads and changes nothing on the site. Per theme and per button it prints one MEASURE line: the rows of the glyph and of the capital letter (y0-y1, device pixels in the
    button), DY = centre of the capital minus centre of the glyph (positive: the glyph is high), GAP = columns between glyph and text, PADL = columns left of the glyph. A button passes
    when |DY| is at most -MaxOffsetDevice device pixels (default 0.5, that is none at 100 percent). The pixels of each button are saved as <Run>\align\<theme>\<name>.png (enlarged four times), and the page as <name>-page.png.
    The display scale of the session decides how many device pixels one logical pixel has (the line SCALE shows it). The session of the lab user keeps the scale that it signed in with:
    the checks at 125 and 150 percent are the render tests of ButtonAlignmentTests, which draw the same buttons at those scales.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    [double]$MaxOffsetDevice = 0.5
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$script:Fails = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') { if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" } }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Run-Check([string]$Name, [scriptblock]$Body) { try { & $Body } catch { $script:Fails++; "FAIL ${Name}: $($_.Exception.Message)" } }

# Copies the pixels of a button from the screen at once (a busy button can go away) and saves the picture enlarged four times.
function Grab-Button($El, [string]$Theme, [string]$Name) {
    $r = $El.Current.BoundingRectangle
    $x0 = [int][math]::Floor($r.X); $y0 = [int][math]::Floor($r.Y); $w = [int][math]::Ceiling($r.Width); $h = [int][math]::Ceiling($r.Height)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x0, $y0, 0, 0, $bmp.Size)
    $g.Dispose()
    $file = Join-Path $Run (Join-Path 'align' (Join-Path $Theme.ToLower() (($Name -replace '[^A-Za-z0-9]', '-') + '.png')))
    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
    $big = New-Object System.Drawing.Bitmap ($w * 4), ($h * 4)
    $gg = [System.Drawing.Graphics]::FromImage($big)
    $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $gg.DrawImage($bmp, 0, 0, $w * 4, $h * 4)
    $gg.Dispose(); $big.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()
    [pscustomobject]@{ Bmp = $bmp; Name = $Name; Theme = $Theme; File = $file; Rect = ('{0},{1} {2}x{3}' -f $r.X, $r.Y, $r.Width, $r.Height) }
}

# Reads the pixels of a button and returns the rows of the glyph and of the first capital letter of the text. The ink is every pixel that is at least half as far from the background
# as the strongest pixel. The glyph is the first group of columns with ink, the text is the next group; a gap of six logical pixels or more ends a group (the gap between glyph and
# text is the 8 px margin of the glyph, the gap inside a text is smaller). The capital letter is the columns of the text up to the first column without ink.
function Measure-Button($Grab) {
    $bmp = $Grab.Bmp; $w = $bmp.Width; $h = $bmp.Height; $Name = $Grab.Name; $Theme = $Grab.Theme
    $scale = [math]::Round($script:Dpi / 96, 2)
    $inset = [int][math]::Ceiling(5 * $scale)
    $bg = $bmp.GetPixel($inset + 2, $inset)
    $d = New-Object 'int[,]' $w, $h
    $max = 0
    for ($x = $inset; $x -lt $w - $inset; $x++) {
        for ($y = $inset; $y -lt $h - $inset; $y++) {
            $p = $bmp.GetPixel($x, $y)
            $v = [math]::Abs($p.R - $bg.R) + [math]::Abs($p.G - $bg.G) + [math]::Abs($p.B - $bg.B)
            $d[$x, $y] = $v
            if ($v -gt $max) { $max = $v }
        }
    }
    $bmp.Dispose()
    $limit = $max / 2
    $rowsOf = @{}
    for ($x = $inset; $x -lt $w - $inset; $x++) {
        for ($y = $inset; $y -lt $h - $inset; $y++) {
            if ($d[$x, $y] -gt $limit) {
                if (-not $rowsOf.ContainsKey($x)) { $rowsOf[$x] = New-Object System.Collections.ArrayList }
                [void]$rowsOf[$x].Add($y)
            }
        }
    }
    $gapMin = [int][math]::Round(6 * $scale)
    $groups = New-Object System.Collections.ArrayList
    $start = -1; $last = -1
    for ($x = $inset; $x -lt $w - $inset; $x++) {
        if (-not $rowsOf.ContainsKey($x)) { continue }
        if ($start -lt 0) { $start = $x } elseif ($x - $last -ge $gapMin) { [void]$groups.Add(@($start, $last)); $start = $x }
        $last = $x
    }
    if ($start -ge 0) { [void]$groups.Add(@($start, $last)) }
    if ($groups.Count -lt 2) { throw "The button '$Name' shows $($groups.Count) group of ink (glyph and text expected). Image: $($Grab.File)" }
    $end = $groups[1][0]
    while ($end + 1 -le $groups[1][1] -and $rowsOf.ContainsKey($end + 1)) { $end++ }
    $iconYs = @(); for ($x = $groups[0][0]; $x -le $groups[0][1]; $x++) { if ($rowsOf.ContainsKey($x)) { $iconYs += $rowsOf[$x] } }
    $textYs = @(); for ($x = $groups[1][0]; $x -le $end; $x++) { if ($rowsOf.ContainsKey($x)) { $textYs += $rowsOf[$x] } }
    $i0 = ($iconYs | Measure-Object -Minimum).Minimum; $i1 = ($iconYs | Measure-Object -Maximum).Maximum
    $t0 = ($textYs | Measure-Object -Minimum).Minimum; $t1 = ($textYs | Measure-Object -Maximum).Maximum
    [pscustomobject]@{
        Name = $Name; Theme = $Theme; W = $w; H = $h; Scale = $scale; Rect = $Grab.Rect
        Icon = "$i0-$i1"; Text = "$t0-$t1"
        DY = (($t0 + $t1) / 2.0) - (($i0 + $i1) / 2.0)
        Gap = $groups[1][0] - $groups[0][1] - 1; PadL = $groups[0][0]
    }
}

function Report([string]$Theme, $M) {
    'MEASURE {0,-5} {1,-34} {2}x{3} scale {4} glyph y {5} capital y {6} DY={7:N1} px ({8:N2} logical) GAP={9} PADL={10} rect {11}' -f $Theme, $M.Name, $M.W, $M.H, $M.Scale, $M.Icon, $M.Text, $M.DY, ($M.DY / $M.Scale), $M.Gap, $M.PadL, $M.Rect
    Check "$Theme $($M.Name): glyph and text share one line (|DY| <= $MaxOffsetDevice device px)" ([math]::Abs($M.DY) -le $MaxOffsetDevice) ("DY=$($M.DY) px")
}

# Measures every button of the page that has a glyph and a text and is 32 px high (the idle buttons of the toolbar of the page); the page does not move any more.
function Measure-PageButtons([string]$Theme, [string]$Page) {
    Start-Sleep 2
    Move-MouseAway
    $cond = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Button)
    $win = Main-Window
    $n = 0
    foreach ($b in @($win.FindAll($script:TS::Descendants, $cond))) {
        $r = $b.Current.BoundingRectangle
        if ($b.Current.IsOffscreen -or $r.IsEmpty -or [math]::Abs($r.Height - 32) -gt 1 -or $r.Width -lt 60 -or $r.X -lt 260 -or $r.Y -lt 100) { continue }
        try {
            $m = Measure-Button (Grab-Button $b $Theme ("$Page - " + $b.Current.Name))
            'PAGE    {0,-5} {1,-26} {2,-30} glyph y {3} capital y {4} DY={5:N1} rect {6}' -f $Theme, $Page, $b.Current.Name, $m.Icon, $m.Text, $m.DY, $m.Rect
            $n++
        } catch { }
    }
}

# Starts the action that makes the Cancel button show, copies the pixels of the button the moment it shows, and ends the run with a real click on Cancel.
function Measure-Busy([string]$Theme, [string]$Name, [scriptblock]$Start, [string]$CancelId, [double]$Seconds = 60) {
    & $Start
    $deadline = (Get-Date).AddSeconds($Seconds)
    $c = $null
    do {
        $c = Find-El $null $CancelId -Timeout 1
        if ($c -and -not $c.Current.IsOffscreen -and -not $c.Current.BoundingRectangle.IsEmpty) { break }
        $c = $null
    } while ((Get-Date) -lt $deadline)
    if (-not $c) { throw "The button $CancelId did not show within $Seconds seconds." }
    Move-MouseAway
    $c = Find-El $null $CancelId -Timeout 2
    if (-not $c) { throw "The busy state of $CancelId ended before the pixels were read." }
    $grab = Grab-Button $c $Theme $Name
    Save-Shot (Join-Path $Run (Join-Path 'align' (Join-Path $Theme.ToLower() (($Name -replace '[^A-Za-z0-9]', '-') + '-page.png'))))
    Report $Theme (Measure-Button $grab)
    $c = Find-El $null $CancelId -Timeout 2
    if ($c -and $c.Current.IsEnabled) { Real-Click $c }
    [void](Wait-Gone $CancelId 90)
}

$script:Dpi = 96
$probe = New-Object System.Drawing.Bitmap 1, 1
$script:Dpi = $probe.HorizontalResolution
$probe.Dispose()
"SCALE work area $((Get-ApdScreen).Width)x$((Get-ApdScreen).Height), $script:Dpi dpi ($([int]($script:Dpi / 96 * 100)) percent), app $((Get-Item $exe).VersionInfo.ProductVersion)"
foreach ($theme in $Themes) {
    $script:GuideTheme = $theme
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $staging = New-RunStaging $data
    $settings = @{
        DownloadRoot = $staging; FileShareRoot = $FileShareRoot; SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; SiteCodeSaved = $true
        Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true }; CheckForUpdatesAtStartup = $false
    }
    $proc = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Start-Sleep 3
        Run-Check "$theme-ref" {
            foreach ($n in 'Check', 'One Click', 'Stage') {
                $b = Find-El $w -Name $n -ControlType 'Button' -Timeout 5
                if (-not $b) { throw "no button $n" }
                Report $theme (Measure-Button (Grab-Button $b $theme "idle $n"))
            }
        }
        Run-Check "$theme-packages-idle" { Measure-PageButtons $theme 'Packages' }
        Run-Check "$theme-packages" {
            Set-HeaderCheck $w 'On'
            Measure-Busy $theme 'Packages Cancel (Check Latest)' { Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest' } 'btnCancelOperation' 30
            Set-HeaderCheck $w 'Off'
        }
        Run-Check "$theme-hygiene" {
            $null = Click-Nav 'Site Hygiene'; Start-Sleep 4
            Measure-Busy $theme 'Site Hygiene Cancel (scan)' { Real-Click (Need-El 'btnHygieneScan') } 'btnHygieneCancel' 60
            Measure-PageButtons $theme 'Site Hygiene'
        }
        # The pages that read the site when they open show their Cancel button while the read runs.
        foreach ($page in @(
                @{ Nav = 'Deployment Helper'; Id = 'btnDeployCancel' },
                @{ Nav = 'DP Content Manager'; Id = 'btnDpContentCancel' },
                @{ Nav = 'Collection and Compliance'; Id = 'btnCancelLoad' })) {
            Run-Check "$theme-$($page.Nav)" {
                $null = Click-Nav 'App Packager'; Start-Sleep 2
                Measure-Busy $theme "$($page.Nav) Cancel (read)" { $null = Click-Nav $page.Nav } $page.Id 30
                Measure-PageButtons $theme $page.Nav
            }
        }
        Run-Check "$theme-detection" {
            $null = Click-Nav 'Detection Tester'; Start-Sleep 3
            Select-Combo (Need-El 'cboClauseType') '*Script*'; Start-Sleep 2
            Set-ElText (Need-El 'txtScript') 'Start-Sleep -Seconds 40; exit 0'
            Measure-Busy $theme 'Detection Tester Cancel (run)' { Real-Click (Need-El 'btnRun') } 'btnCancel' 30
        }
        $null = Click-Nav 'App Packager'
    }
    finally { Stop-Apd }
}
"ALIGN-FAILS=$script:Fails"
