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
    An image whose text names a state (a selected object, a read, a hash) is saved only after the page shows that state; else the run says FAIL for it and
    saves no image. The run only selects and reads: it opens no plan and writes nothing to the site. The image of the Registry Settings tab needs an NSIS
    installer: the run downloads the latest Notepad++ x64 installer from its GitHub releases into the run folder, analyzes it and never starts it.
#>
param(
    [Parameter(Mandatory)][ValidateSet('InstallerAnalysis', 'SiteHygiene', 'LivePages', 'SettingsScroll')][string]$Scenario,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$Run = (Join-Path $PSScriptRoot 'out'),
    [string]$AppFolder = $PSScriptRoot,
    # Collections of the lab, the first one with a query rule is used for the image of the WQL Editor.
    [string[]]$WqlCollections = @('All Workstations', 'All Servers', 'Copy of All Workstations', 'CH Lab 1.0.4'),
    # A shipped template with a parameter, its category, the label of the parameter and the value that the image shows.
    [string]$TemplateCategory = 'Software',
    [string]$Template = 'Software Installed by Name',
    [string]$TemplateParameter = 'Software Name',
    [string]$TemplateValue = '7-Zip',
    [string]$SavedQuery = 'All Systems with Hardware Inventory Collected',
    [string]$Content = 'Configuration Manager Client Package'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
# GuideLib gives the grid and wait helpers; the function Shot of this script replaces the one of GuideLib.
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
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

# Runs the steps that put a page into the state of its image and sets $script:StateOk. When the state is not there: a FAIL line, and the screen under
# <Run>\fail.
$script:StateOk = $false
function Set-PageState([string]$Theme, [string]$Id, [scriptblock]$State) {
    $script:StateOk = $false
    try { & $State; $script:StateOk = $true }
    catch {
        "FAIL $Theme ${Id}: $($_.Exception.Message)"
        try { Move-MouseAway; Save-Shot (Join-Path $Run (Join-Path 'fail' ($Theme.ToLower() + '-' + $Id + '.png'))) } catch { $null = $_ }
    }
}

# Opens a file in Installer Analysis with the Open button and waits for the end of the analysis.
function Open-Installer([string]$Path) {
    Click-El (Need-El 'btnOpenInstaller')
    Send-DialogPath $Path
    $leaf = Split-Path $Path -Leaf
    Wait-State { $s = Find-El $null 'txtAnalysisStatus' -Timeout 2; $s -and $s.Current.Name -like "Analyzed $leaf*" } 240 "the status line says Analyzed $leaf"
    Start-Sleep -Seconds 3
}

$states = @{
    'tools-installer-analysis-payload' = {
        # A small file of the list: the second row, because the first can be a large cabinet.
        $null = Pick-First 'dgPayload' 1
        Invoke-El (Need-El 'btnPayloadHash')
        Wait-State { $h = Find-El $null 'txtHash' -Timeout 2; $h -and -not $h.Current.IsOffscreen -and (Get-ElValue $h) -match '[0-9A-Fa-f]{64}' } 120 'the SHA-256 box holds a hash'
    }
    'tools-maintenance-windows-list' = {
        $null = Pick-First 'dgWindows'
        Wait-State { $l = Find-El $null 'lstProperties' -Timeout 2; $l -and -not $l.Current.IsOffscreen -and @($l.FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)).Count -gt 2 } 30 'the properties of the selected window'
    }
    'tools-dp-content-content' = {
        $null = Pick-Row 'dgContent' "*$Content*" 60
        Wait-State { @(Get-VisibleRows 'dgContentPairs').Count -ge 1 } 60 "the lower grid holds a distribution point of '$Content'"
    }
    'tools-collections-wql' = {
        $used = $null
        foreach ($name in $WqlCollections) {
            Set-ElText (Need-El 'txtWqlFilter') $name; Start-Sleep -Seconds 3
            $isItem = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::TreeItem)
            $item = @((Need-El 'treeWqlCollections').FindAll($script:TS::Descendants, $isItem) | Where-Object { $_.Current.Name -like "$name (*" }) | Select-Object -First 1
            if (-not $item) { "no collection '$name' in the tree"; continue }
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Seconds 8
            # The rules of the collection are read after the selection. The first query rule is chosen.
            $combo = Need-El 'cboWqlRule'
            $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 800
            $isEntry = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem)
            $rule = $combo.FindFirst($script:TS::Descendants, $isEntry)
            if ($rule) { $rule.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800 }
            try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { $null = $_ }
            if ($rule -and (Get-ElValue (Need-El 'txtWql')).Trim()) { $used = $name; break }
            "the collection '$name' shows no query rule"
        }
        if (-not $used) { throw "None of these collections shows a query rule: $($WqlCollections -join ', ')." }
        "WQL Editor: collection $used"
        # Preview runs the query and shows the devices; it changes nothing on the site.
        Invoke-El (Need-El 'btnPreviewWql')
        Wait-State { @(Get-VisibleRows 'dgPreview').Count -ge 1 } 120 'the preview lists a device'
    }
    'tools-collections-site-queries' = {
        $null = Pick-Row 'dgSavedQueries' $SavedQuery 60
        Wait-State { (Get-ElValue (Need-El 'txtSavedQuery')).Trim().Length -gt 0 } 30 'the text of the selected query'
    }
}
# The parameter box of a template has the label of the parameter as its name.
$states['tools-collections-templates'] = {
    Select-Combo (Need-El 'cboCategory') $TemplateCategory
    $null = Pick-Row 'dgTemplates' $Template
    $box = Find-El $null -Name $TemplateParameter -ControlType 'Edit' -Timeout 15
    if (-not $box) { throw "The template '$Template' shows no box '$TemplateParameter'." }
    Set-ElText $box $TemplateValue
    Wait-State { (Get-ElValue (Need-El 'txtTemplateWql')) -like "*$TemplateValue*" } 30 'the final query holds the value of the parameter'
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    $script:GuideTheme = $theme
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
                'tools-installer-analysis-strings' = 'tabStrings'
            }
            foreach ($id in $tabs.Keys) {
                # The guide image of the Inner Installers tab shows a bundle with its breadcrumb; Capture-Tools makes it.
                if ($id -eq 'tools-installer-analysis-inner') { continue }
                if (-not (Open-Tab $tabs[$id])) { "REFUSED $id.png: no tab $($tabs[$id])"; continue }
                Start-Sleep -Seconds 3
                if ($states.ContainsKey($id)) { Set-PageState $theme $id $states[$id]; if (-not $script:StateOk) { continue } }
                Move-MouseAway
                Shot (Join-Path $dir ($id + '.png'))
            }
            # The Registry Settings image shows an NSIS installer. It is the last step, because it replaces the analyzed file.
            $registry = {
                $nsis = Join-Path $Run 'sample\npp-installer.exe'
                if (-not (Test-Path -LiteralPath $nsis)) {
                    $release = Invoke-RestMethod -UseBasicParsing 'https://api.github.com/repos/notepad-plus-plus/notepad-plus-plus/releases/latest' -Headers @{ 'User-Agent' = 'guide-capture' }
                    $asset = @($release.assets | Where-Object { $_.name -like 'npp.*.Installer.x64.exe' }) | Select-Object -First 1
                    if (-not $asset) { throw 'The latest Notepad++ release holds no x64 installer.' }
                    Invoke-WebRequest -UseBasicParsing $asset.browser_download_url -OutFile $nsis
                }
                Open-Installer $nsis
                if (-not (Open-Tab 'tabRegistry')) { throw 'The page shows no Registry Settings tab.' }
                Start-Sleep -Seconds 3
                $isText = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Text)
                Wait-State { @(Get-VisibleRows 'dgRegistry' | ForEach-Object { $_.FindAll($script:TS::Descendants, $isText) } | Where-Object { $_.Current.Name -like 'NSIS*' }).Count -ge 1 } 60 'a registry write of the NSIS script'
            }
            Set-PageState $theme 'tools-installer-analysis-registry' $registry
            if ($script:StateOk) { Move-MouseAway; Shot (Join-Path $dir 'tools-installer-analysis-registry.png') }
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
            # A selected finding shows its details and its fix text. Selecting a row changes nothing; no button of the findings is clicked.
            $finding = {
                foreach ($index in 0..7) {
                    $null = Pick-First 'dgFindings' $index
                    $text = Find-El $null 'txtFixText' -Timeout 3
                    if ($text -and -not $text.Current.IsOffscreen -and (Get-ElValue $text).Trim()) { return }
                }
                throw 'None of the first 8 findings shows a fix text.'
            }
            Set-PageState $theme 'tools-site-hygiene-findings' $finding
            if ($script:StateOk) { Move-MouseAway; Shot (Join-Path $dir 'tools-site-hygiene-findings.png') }
            $tabs = [ordered]@{ 'tools-site-hygiene-relationships' = 'tabHygieneRelations'; 'tools-site-hygiene-summary' = 'tabHygieneSummary'; 'tools-site-hygiene-history' = 'tabHygieneHistory'; 'tools-site-hygiene-options' = 'tabHygieneOptions' }
            foreach ($id in $tabs.Keys) {
                if (-not (Open-Tab $tabs[$id])) { "REFUSED $id.png: no tab $($tabs[$id])"; continue }
                Start-Sleep -Seconds 5
                if ($id -eq 'tools-site-hygiene-relationships') {
                    # Read relationships only reads the site.
                    $relations = {
                        Invoke-El (Need-El 'btnReadRelations')
                        Wait-State { @(Get-VisibleRows 'dgEdges').Count -ge 1 } 300 'the inventory lists a supersedence or dependency edge'
                        Start-Sleep -Seconds 3
                    }
                    Set-PageState $theme $id $relations
                    if (-not $script:StateOk) { continue }
                }
                Move-MouseAway
                Shot (Join-Path $dir ($id + '.png'))
            }
        }
        if ($Scenario -eq 'LivePages') {
            # Pages that read the lab site when they open: (sidebar name, wait, then the tabs: image id, automation id of the tab; $null is the page as it opens).
            $pages = @(
                @{ Nav = 'Maintenance Windows'; Wait = 12; Tabs = @(@('tools-maintenance-windows-list', $null), @('tools-maintenance-windows-coverage', 'tabCoverage'), @('tools-maintenance-windows-templates', 'tabTemplates')) }
                @{ Nav = 'Collection and Compliance'; Wait = 15; Tabs = @(@('tools-collections-list', $null), @('tools-collections-wql', 'tabWqlEditor'), @('tools-collections-templates', 'tabTemplates'), @('tools-collections-site-queries', 'tabSiteQueries')) }
                @{ Nav = 'Deployment Helper'; Wait = 15; Tabs = @(, @('tools-deployment-helper-options', 'tabOptions')) }
                @{ Nav = 'DP Content Manager'; Wait = 15; Tabs = @(@('tools-dp-content-content', 'tabContent'), @('tools-dp-content-issues', 'tabIssues')) }
            )
            # The image of the Deploy tab shows the result of an Undo, which needs a deployment: Capture-ToolsDeploy.ps1 makes it.
            # Three more images are not made here. The two History tabs list the records of the data folder, and the data folder of this run is new, so they are empty until
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
                    if ($states.ContainsKey($tab[0])) { Set-PageState $theme $tab[0] $states[$tab[0]]; if (-not $script:StateOk) { continue } }
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
