<#
.SYNOPSIS
    The lab capture run of the guide images that need a state (decision D-21): starts the published app maximized on the 1080p work area (1920 x 1032), in both
    themes, drives it through a scenario and saves each image under its guide image id.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from the folder of the published app (like Capture-ApdPages.ps1):
        Run-VisibleProbe.ps1 -Worktree <published folder with this script> -ProbeArgs "& .\Capture-Scenarios.ps1 -Scenario InstallerAnalysis"
    The images are <Run>\images\<theme>\<id>.png. An image whose body has no more content than an empty page is not kept: the run says REFUSED for it (the guard
    that stops a capture run from replacing a guide image with an empty page; the test GuideImageContentTests checks the committed images the same way).
    The sidebar entries are found by name and the tabs and buttons by automation id, so no click depends on a position.
#>
param(
    [Parameter(Mandatory)][ValidateSet('InstallerAnalysis', 'SiteHygiene', 'LivePages', 'SettingsScroll')][string]$Scenario,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$Run = (Join-Path $PSScriptRoot 'out'),
    [string]$AppFolder = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'

# Clicks the tab with this automation id; $false when the page does not show it in time.
function Open-Tab([string]$Id, [double]$Timeout = 20) {
    $el = Find-El $null $Id -Timeout $Timeout
    if (-not $el) { return $false }
    Click-El $el
    $true
}

# The share of the pixels of the body of the window (right of the sidebar, below the header and the toolbars) that differ from the most common color.
function BodyInk($bmp) {
    $left = [int]($bmp.Width * 0.18); $top = [int]($bmp.Height * 0.34); $bottom = [int]($bmp.Height * 0.95)
    $counts = @{}; $total = 0
    for ($y = $top; $y -lt $bottom; $y += 2) { for ($x = $left; $x -lt $bmp.Width; $x += 2) { $c = $bmp.GetPixel($x, $y).ToArgb(); $counts[$c] = 1 + [int]$counts[$c]; $total++ } }
    $max = ($counts.Values | Measure-Object -Maximum).Maximum
    return 1.0 - ($max / $total)
}

function Shot($path, [double]$minimumInk = 0.01) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $g.Dispose()
    $ink = BodyInk $bmp
    if ($ink -lt $minimumInk) { "REFUSED $(Split-Path $path -Leaf): the body has ink $([math]::Round($ink, 4)), like an empty page" }
    else { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); "OK $(Split-Path $path -Leaf) ink $([math]::Round($ink, 4))" }
    $bmp.Dispose()
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    try { $null = Start-Apd $exe $data $theme } catch { "FAIL $theme : $($_.Exception.Message)"; continue }
    try {
        $dir = Join-Path $Run (Join-Path 'images' $theme.ToLower())
        New-Item -ItemType Directory -Force $dir | Out-Null

        if ($Scenario -eq 'InstallerAnalysis') {
            # A sample installer: the largest MSI of the Windows installer cache, copied so that nothing locks it.
            $sample = Get-ChildItem C:\Windows\Installer -Filter *.msi -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
            if (-not $sample) { 'FAIL no MSI in C:\Windows\Installer'; continue }
            New-Item -ItemType Directory -Force (Join-Path $Run 'sample') | Out-Null
            $copy = Copy-Item $sample.FullName (Join-Path $Run 'sample\sample.msi') -PassThru -Force
            if (-not (Click-Nav 'Installer Analysis')) { "FAIL $theme : the sidebar has no Installer Analysis entry"; continue }
            Start-Sleep -Seconds 3
            $open = Find-El $null 'btnOpenInstaller' -Timeout 15
            if (-not $open) { "FAIL $theme : no btnOpenInstaller"; continue }
            Click-El $open
            Start-Sleep -Seconds 2
            Send-ApdKeys ($copy.FullName.Replace('+', '{+}').Replace('^', '{^}').Replace('%', '{%}').Replace('~', '{~}') + '{ENTER}')
            Start-Sleep -Seconds 12
            $tabs = [ordered]@{
                'tools-installer-analysis-overview' = 'tabOverview'; 'tools-installer-analysis-msi-properties' = 'tabMsiProperties'; 'tools-installer-analysis-payload' = 'tabPayload'
                'tools-installer-analysis-inner' = 'tabInnerInstallers'; 'tools-installer-analysis-file-browser' = 'tabFileBrowser'
                'tools-installer-analysis-registry' = 'tabRegistry'; 'tools-installer-analysis-strings' = 'tabStrings'
            }
            foreach ($id in $tabs.Keys) {
                # The guide image of the Inner Installers tab shows a bundle with its breadcrumb; Capture-Tools makes it.
                if ($id -eq 'tools-installer-analysis-inner') { continue }
                if (-not (Open-Tab $tabs[$id])) { "REFUSED $id.png: no tab $($tabs[$id])"; continue }
                Start-Sleep -Seconds 3
                Move-MouseAway
                Shot (Join-Path $dir ($id + '.png'))
            }
        }
        if ($Scenario -eq 'SiteHygiene') {
            # Scan reads the site (CM01); the findings and the other tabs are shown after it.
            if (-not (Click-Nav 'Site Hygiene')) { "FAIL $theme : the sidebar has no Site Hygiene entry"; continue }
            Start-Sleep -Seconds 4
            $scan = Find-El $null 'btnHygieneScan' -Timeout 20
            if (-not $scan) { "FAIL $theme : no btnHygieneScan"; continue }
            $result = Invoke-ApdHygieneScan { Click-El $scan }
            "scan: $($result.Status) $($result.Summary)"
            if (-not $result.Ended) { "FAIL $theme : the scan did not end"; continue }
            Start-Sleep -Seconds 3
            Move-MouseAway
            Shot (Join-Path $dir 'tools-site-hygiene-findings.png')
            $tabs = [ordered]@{ 'tools-site-hygiene-relationships' = 'tabHygieneRelations'; 'tools-site-hygiene-summary' = 'tabHygieneSummary'; 'tools-site-hygiene-history' = 'tabHygieneHistory'; 'tools-site-hygiene-options' = 'tabHygieneOptions' }
            foreach ($id in $tabs.Keys) {
                if (-not (Open-Tab $tabs[$id])) { "REFUSED $id.png: no tab $($tabs[$id])"; continue }
                Start-Sleep -Seconds 5
                Move-MouseAway
                Shot (Join-Path $dir ($id + '.png'))
            }
        }
        if ($Scenario -eq 'LivePages') {
            # Pages that read the lab site when they open: (sidebar name, wait, then the tabs: image id, automation id of the tab; $null is the page as it opens).
            $pages = @(
                @{ Nav = 'Maintenance Windows'; Wait = 12; Tabs = @(@('tools-maintenance-windows-list', $null), @('tools-maintenance-windows-coverage', 'tabCoverage'), @('tools-maintenance-windows-templates', 'tabTemplates')) }
                @{ Nav = 'Collection and Compliance'; Wait = 15; Tabs = @(@('tools-collections-list', $null), @('tools-collections-wql', 'tabWqlEditor'), @('tools-collections-templates', 'tabTemplates'), @('tools-collections-site-queries', 'tabSiteQueries')) }
                @{ Nav = 'Deployment Helper'; Wait = 15; Tabs = @(@('tools-deployment-helper-deploy', $null), @('tools-deployment-helper-options', 'tabOptions')) }
                @{ Nav = 'DP Content Manager'; Wait = 15; Tabs = @(@('tools-dp-content-content', 'tabContent'), @('tools-dp-content-issues', 'tabIssues')) }
            )
            # Three images are not made here. The two History tabs list the records of the data folder, and the data folder of this run is new, so they are empty until
            # a run writes: Capture-ToolsDeploy.ps1 makes tools-deployment-helper-history and Capture-ToolsDp.ps1 makes tools-dp-content-history, each after a real
            # change on the lab site. The DPs tab of the lab has one row and shows its content only for a selected row: Capture-Tools.ps1 makes tools-dp-content-dps.
            foreach ($page in $pages) {
                if (-not (Click-Nav $page.Nav)) { "FAIL $theme : the sidebar has no $($page.Nav) entry"; continue }
                Start-Sleep -Seconds $page.Wait
                foreach ($tab in $page.Tabs) {
                    if ($tab[1]) {
                        if (-not (Open-Tab $tab[1])) { "REFUSED $($tab[0]).png: no tab $($tab[1])"; continue }
                        Start-Sleep -Seconds 6
                    }
                    Move-MouseAway
                    Shot (Join-Path $dir ($tab[0] + '.png'))
                }
            }
        }
        if ($Scenario -eq 'SettingsScroll') {
            # The page that Settings opens, from the top, one screen at a time (the wheel scrolls three lines of 40 px for each notch at 100 percent).
            $null = Click-Nav 'Settings'; Start-Sleep -Seconds 5
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 600
            $previous = $null
            for ($i = 0; $i -lt 30; $i++) {
                $file = Join-Path $dir ('settings-scroll-{0:00}.png' -f $i)
                Shot $file
                if (Test-Path -LiteralPath $file) {
                    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
                    # The same picture again: the page is at its end, or it does not scroll.
                    if ($hash -eq $previous) { Remove-Item -LiteralPath $file; "the page did not move after $i image(s)"; break }
                    $previous = $hash
                }
                for ($n = 0; $n -lt 18; $n++) { Send-ApdWheel ([uint32]4294966576); Start-Sleep -Milliseconds 120 }
                Start-Sleep -Milliseconds 500
            }
        }
    }
    finally { Stop-Apd }
}
