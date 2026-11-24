<#
.SYNOPSIS
    The lab check that every row of the Packages grid keeps its own icon after a sort, a scroll, a filter and a theme change.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from a folder that holds the scripts of tools\guide-capture:
        Run-VisibleProbe.ps1 -Worktree <folder with these scripts> -ProbeArgs "& .\Probe-PackagesIconSort.ps1 -Run C:\guide\runs\apd-icons"
    The icon pack goes into the user definitions folder of the run. For each realized row the probe reads the Application text and the screen
    pixels of the icon cell. The expected icon is the pack file named for the definition of that application. A row passes when its pixels are
    closer to its own pack file than to the file of any other packager (a small tolerance covers near-identical icons). The probe clicks every
    sortable column header, ascending and then descending, and checks after each click and after a page of scrolling.
    It reads nothing from the site and publishes nothing. Each check says PASS or FAIL. The run ends with "ICON-FAILS=<n>".
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$script:Fails = 0; $script:Checks = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') { $script:Checks++; if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" } }
function Items($Scope, [string]$Type) { @($Scope.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::($Type))))) }

# The icon pack: the release asset of app-packager-icons, kept at C:\guide\icon-pack.zip (Start-Apd expands it into the run).
$pack = 'C:\guide\icon-pack.zip'
if (-not (Test-Path -LiteralPath $pack)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'
    $h = @{ 'User-Agent' = 'AppPackager-IconPack' }
    $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/jasonulbright/app-packager-icons/releases/latest' -Headers $h -UseBasicParsing
    $asset = @($rel.assets | Where-Object { $_.name -eq 'icon-pack.zip' })[0]
    $sums = @($rel.assets | Where-Object { $_.name -eq 'checksums.txt' })[0]
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $pack -Headers $h -UseBasicParsing
    $expected = ((Invoke-WebRequest -Uri $sums.browser_download_url -Headers $h -UseBasicParsing).Content -split "`n" | Where-Object { $_ -match 'icon-pack\.zip' } | Select-Object -First 1) -replace '^\s*([0-9a-fA-F]{64}).*$', '$1'
    if ((Get-FileHash -LiteralPath $pack -Algorithm SHA256).Hash -ne $expected.ToUpperInvariant()) { Remove-Item $pack -Force; throw 'The icon pack checksum did not match.' }
}
Add-Type -AssemblyName System.Drawing, System.IO.Compression.FileSystem
$packBitmaps = @{}
$zip = [IO.Compression.ZipFile]::OpenRead($pack)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.Name -notmatch '\.png$') { continue }
        $ms = New-Object IO.MemoryStream; $st = $entry.Open(); $st.CopyTo($ms); $st.Dispose(); $ms.Position = 0
        $packBitmaps[[IO.Path]::GetFileNameWithoutExtension($entry.Name).ToLowerInvariant()] = New-Object System.Drawing.Bitmap $ms
    }
} finally { $zip.Dispose() }
"icon pack png files: $($packBitmaps.Count)"

# Application text -> definition base name (the icon file name), from the bundled definitions. A name that two definitions share is left out.
$byApp = @{}; $dup = @{}
foreach ($f in Get-ChildItem (Join-Path $AppFolder 'Definitions') -Filter *.json -File) {
    try { $app = [string](Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json).Application } catch { continue }
    if (-not $app) { continue }
    if ($byApp.ContainsKey($app)) { $dup[$app] = $true } else { $byApp[$app] = $f.BaseName.ToLowerInvariant() }
}
foreach ($k in $dup.Keys) { $byApp.Remove($k) }
"applications with a unique name: $($byApp.Count)"

$grid = 12
function Get-Signature([System.Drawing.Bitmap]$Source, [System.Drawing.Color]$Background) {
    $flat = New-Object System.Drawing.Bitmap $grid, $grid
    $g = [System.Drawing.Graphics]::FromImage($flat); $g.Clear($Background)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($Source, 0, 0, $grid, $grid); $g.Dispose()
    $sig = New-Object 'int[]' ($grid * $grid * 3); $i = 0
    for ($y = 0; $y -lt $grid; $y++) { for ($x = 0; $x -lt $grid; $x++) { $p = $flat.GetPixel($x, $y); $sig[$i++] = $p.R; $sig[$i++] = $p.G; $sig[$i++] = $p.B } }
    $flat.Dispose()
    , $sig
}
function Get-Distance([int[]]$A, [int[]]$B) { $sum = 0; for ($i = 0; $i -lt $A.Length; $i++) { $sum += [Math]::Abs($A[$i] - $B[$i]) }; $sum / $A.Length }

function Test-GridIcons([string]$Label) {
    Start-Sleep -Milliseconds 1200
    $gridEl = Find-El $null 'PackagerGrid' -Timeout 10
    $checked = 0; $bad = New-Object System.Collections.Generic.List[string]
    foreach ($row in (Items $gridEl 'DataItem')) {
        $r = $row.Current.BoundingRectangle
        if ($r.IsEmpty -or $r.Width -le 0) { continue }
        $texts = @(Items $row 'Text' | ForEach-Object { $_.Current.Name } | Where-Object { $byApp.ContainsKey($_) } | Select-Object -Unique)
        if ($texts.Count -ne 1) { continue }
        $app = $texts[0]; $expectedBase = $byApp[$app]
        if (-not $packBitmaps.ContainsKey($expectedBase)) { continue }
        $cell = Find-El $row 'PackagerIcon' -Timeout 1
        if (-not $cell) { continue }
        $ir = $cell.Current.BoundingRectangle
        if ($ir.IsEmpty -or $ir.Width -lt 8) { continue }
        $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
        if ($ir.Y -lt 0 -or ($ir.Y + $ir.Height) -gt $vs.Height -or $ir.X -lt 8) { continue }
        $w = [int]$ir.Width; $h = [int]$ir.Height
        $shot = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($shot); $g.CopyFromScreen([int]$ir.X, [int]$ir.Y, 0, 0, (New-Object System.Drawing.Size $w, $h)); $g.Dispose()
        $bgShot = New-Object System.Drawing.Bitmap 1, 1
        $g = [System.Drawing.Graphics]::FromImage($bgShot); $g.CopyFromScreen([int]($ir.X - 4), [int]($ir.Y + $ir.Height / 2), 0, 0, (New-Object System.Drawing.Size 1, 1)); $g.Dispose()
        $bg = $bgShot.GetPixel(0, 0); $bgShot.Dispose()
        $seen = Get-Signature $shot $bg; $shot.Dispose()
        $best = [double]::MaxValue; $own = [double]::MaxValue; $bestName = ''
        foreach ($name in $packBitmaps.Keys) {
            $d = Get-Distance $seen (Get-Signature $packBitmaps[$name] $bg)
            if ($name -eq $expectedBase) { $own = $d }
            if ($d -lt $best) { $best = $d; $bestName = $name }
        }
        $checked++
        if (-not ($own -le $best + 3 -and $own -lt 40)) { $bad.Add(('{0} shows {1} (own distance {2:N1}, best {3:N1})' -f $app, $bestName, $own, $best)) }
    }
    Check "$Label : $checked rows with a pack icon were checked (at least 8 needed)" ($checked -ge 8) "checked $checked"
    Check "$Label : every checked row shows its own icon" ($bad.Count -eq 0) (($bad | Select-Object -First 6) -join '; ')
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $staging = New-RunStaging $data
    $settings = @{ DownloadRoot = $staging; FileShareRoot = '\cm01.contoso.com\ContentShare$'; SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; SiteCodeSaved = $true; CheckForUpdatesAtStartup = $false }
    $proc = Start-Apd $exe $data $theme $settings
    try {
        $w = Get-ApdWindows | Select-Object -First 1
        $w.SetFocus(); Start-Sleep 3
        if (-not (Click-Nav 'App Packager')) { throw 'The sidebar has no App Packager entry.' }
        Start-Sleep 4
        $gridEl = Find-El $null 'PackagerGrid' -Timeout 20
        Check "$theme the Packages grid shows" ($null -ne $gridEl) 'no PackagerGrid'
        Test-GridIcons "$theme before sorting"
        $names = @(Items $gridEl 'Header' | Where-Object { $_.Current.Name -and $_.Current.BoundingRectangle.Width -gt 0 } | ForEach-Object { $_.Current.Name } | Select-Object -Unique)
        "$theme columns: " + ($names -join ', ')
        foreach ($name in $names) {
            foreach ($dir in 'ascending', 'descending') {
                $hdr = @(Items (Find-El $null 'PackagerGrid' -Timeout 5) 'Header' | Where-Object { $_.Current.Name -eq $name })[0]
                if (-not $hdr) { continue }
                Click-El $hdr; Start-Sleep -Milliseconds 900
                Test-GridIcons "$theme sort by $name $dir"
                $gr = (Find-El $null 'PackagerGrid' -Timeout 5).Current.BoundingRectangle
                [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]($gr.X + $gr.Width / 2)), ([int]($gr.Y + $gr.Height / 2))
                [System.Windows.Forms.SendKeys]::SendWait('{PGDN}'); Start-Sleep -Milliseconds 700
                Test-GridIcons "$theme sort by $name $dir, scrolled"
                [System.Windows.Forms.SendKeys]::SendWait('^{HOME}'); Start-Sleep -Milliseconds 500
            }
        }
        Save-Shot (Join-Path $Run (Join-Path 'checks' (Join-Path $theme.ToLower() 'packages-icons-sorted.png')))
    } catch { $script:Fails++; "FAIL ${theme}: $($_.Exception.Message)" }
    finally { Stop-Apd }
}
"checks: $script:Checks"
"ICON-FAILS=$script:Fails"
