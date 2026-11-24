<#
.SYNOPSIS
    Compares the titles of App Packager Desktop with the golden file of the PowerShell app, for every packager.
.DESCRIPTION
    For each packager it computes, with the default settings and with "Add the architecture to application names" off:
      grid title      the Application column of the grid, and the first key of the search (PowerShell: App header)
      CMName          the name that Check ConfigMgr, Discover and the conversion use to find an existing application
      stage name      the application name that a stage writes to ConfigMgr ({version} is the version; 'installer' when
                      both apps read the name from the installer, which the file cannot show)
    and writes one row per packager. With -Markdown it writes the table as a markdown file. With -BeforeDefinitions
    (a folder with the definitions as they were) the table also shows whether the row matched before.
    Exit code 1 when a row differs and the packager is not listed under Defects in known-differences.json.
.PARAMETER Golden            Golden file made by Export-PowerShellTitles.ps1.
.PARAMETER Definitions       Folder of the APD definitions.
.PARAMETER BeforeDefinitions Folder of the definitions as they were before a fix (optional).
.PARAMETER Markdown          Output markdown table (optional).
#>
[CmdletBinding()]
param(
    [string]$Golden = (Join-Path $PSScriptRoot '..\..\tests\ApplicationPackagerEngine.Tests\Fixtures\TitleParity\PowerShellTitles.golden.json'),
    [string]$Definitions = (Join-Path $PSScriptRoot '..\..\src\ApplicationPackagerEngine\Definitions'),
    [string]$BeforeDefinitions,
    [string]$Markdown
)
$ErrorActionPreference = 'Stop'
$goldenData = Get-Content -LiteralPath $Golden -Raw | ConvertFrom-Json
$tag = $goldenData.ArchitectureTagPattern
function Format-Arch([string]$Title, [bool]$Append) {
    $r = [regex]::Replace($Title, '(' + $tag + ')(\s+\1)+', '$1', 'IgnoreCase')
    if (-not $Append) { $r = [regex]::Replace($r, '\s*' + $tag + '\s*$', '', 'IgnoreCase') }
    $r.Trim()
}
function Read-Definitions([string]$Folder) {
    $map = @{}
    foreach ($f in Get-ChildItem -LiteralPath $Folder -Filter *.json) { $map[$f.BaseName.ToLowerInvariant()] = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json -AsHashtable }
    $map
}
function Get-Differences($Packager, $Definition) {
    $why = @()
    if ($null -eq $Definition) { return @('no APD definition') }
    if ([string]$Definition['Application'] -cne [string]$Packager.App) { $why += 'grid title' }
    if ([string]$Definition['CMName'] -cne [string]$Packager.CMName) { $why += 'CMName' }
    if ($Packager.StageKind -eq 'template' -and @($Packager.StageNames).Count -eq 1 -and [string]$Definition['AppName'] -cne @($Packager.StageNames)[0]) { $why += 'stage name' }
    $why
}
$knownFile = Join-Path (Split-Path -Parent $Golden) 'known-differences.json'
$known = Get-Content -LiteralPath $knownFile -Raw | ConvertFrom-Json
$defects = @{}; foreach ($d in @($known.Defects)) { $defects[$d.Packager] = $d.Reason }
$powerShellOnly = @{}; foreach ($d in @($known.PowerShellOnly)) { $powerShellOnly[$d.Packager] = $d.Reason }
$apdOnly = @{}; foreach ($d in @($known.ApdOnly)) { $apdOnly[$d.Definition.ToLowerInvariant()] = $d.Reason }

$now = Read-Definitions $Definitions
$before = if ($BeforeDefinitions) { Read-Definitions $BeforeDefinitions } else { $null }
$rows = New-Object System.Collections.Generic.List[object]
$used = @{}
foreach ($p in $goldenData.Packagers) {
    $key = if ($p.Definition) { $p.Definition.ToLowerInvariant() } else { '' }
    $d = if ($key) { $now[$key] } else { $null }
    if ($key) { $used[$key] = $true }
    $why = @(Get-Differences $p $d)
    $beforeWhy = if ($before -and $key) { @(Get-Differences $p $before[$key]) } else { @() }
    $psStage = if ($p.StageKind -eq 'template' -and @($p.StageNames).Count -ge 1) { @($p.StageNames) -join ' / ' } else { 'installer' }
    $apdStage = if ($d -and $d['AppName']) { [string]$d['AppName'] } else { 'installer' }
    $match = if ($why.Count -eq 0) { 'yes' } elseif ($defects.ContainsKey($p.Packager)) { 'defect' } elseif ($powerShellOnly.ContainsKey($p.Packager)) { 'n/a' } else { 'no' }
    $reason = if ($why.Count) { ($why -join ', ') + $(if ($defects.ContainsKey($p.Packager)) { ': PowerShell defect, ' + $defects[$p.Packager] } elseif ($powerShellOnly.ContainsKey($p.Packager)) { ': ' + $powerShellOnly[$p.Packager] } else { '' }) } else { '' }
    $rows.Add([pscustomobject]@{
        Packager = $p.Packager; Definition = $p.Definition
        PsGrid = [string]$p.App; ApdGridBefore = $(if ($before -and $key) { [string]$before[$key]['Application'] } else { '' }); ApdGrid = $(if ($d) { [string]$d['Application'] } else { '' })
        PsCMName = [string]$p.CMName; ApdCMName = $(if ($d) { [string]$d['CMName'] } else { '' })
        PsStage = $psStage; ApdStage = $apdStage
        PsGridOff = Format-Arch ([string]$p.App) $false; ApdGridOff = $(if ($d) { Format-Arch ([string]$d['Application']) $false } else { '' })
        MatchBefore = $(if ($before -and $key) { if ($beforeWhy.Count) { 'no' } else { 'yes' } } else { '' })
        Match = $match; Reason = $reason
    })
}
foreach ($k in $now.Keys | Sort-Object) {
    if ($used.ContainsKey($k)) { continue }
    $rows.Add([pscustomobject]@{ Packager = ''; Definition = $k; PsGrid = ''; ApdGridBefore = $(if ($before -and $before.ContainsKey($k)) { [string]$before[$k]['Application'] } else { '' }); ApdGrid = [string]$now[$k]['Application']
            PsCMName = ''; ApdCMName = [string]$now[$k]['CMName']; PsStage = ''; ApdStage = $(if ($now[$k]['AppName']) { [string]$now[$k]['AppName'] } else { 'installer' }); PsGridOff = ''; ApdGridOff = Format-Arch ([string]$now[$k]['Application']) $false
            MatchBefore = ''; Match = 'n/a'; Reason = $(if ($apdOnly.ContainsKey($k)) { 'APD only: ' + $apdOnly[$k] } else { 'APD only, not listed' }) })
}
$compared = @($rows | Where-Object { $_.Packager -and $_.Definition })
$counts = [ordered]@{
    Compared = $compared.Count
    MatchNow = @($compared | Where-Object Match -eq 'yes').Count
    MatchBefore = @($compared | Where-Object MatchBefore -eq 'yes').Count
    Defect = @($rows | Where-Object Match -eq 'defect').Count
    Differ = @($rows | Where-Object Match -eq 'no').Count
    PowerShellOnly = @($rows | Where-Object { $_.Packager -and -not $_.Definition }).Count
    ApdOnly = @($rows | Where-Object { -not $_.Packager }).Count
}
$counts.GetEnumerator() | ForEach-Object { Write-Host ('{0,-15} {1}' -f $_.Key, $_.Value) }
if ($Markdown) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('| Packager | APpwsh title | APD grid title before | APD grid title now | APpwsh CMName | APD CM name now | APpwsh stage name | APD stage name now | Title, architecture option off (APpwsh / APD) | Match before | Match now | Reason |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|---|---|---|---|---|---|')
    foreach ($r in $rows) {
        $off = if ($r.PsGridOff -or $r.ApdGridOff) { ($r.PsGridOff + ' / ' + $r.ApdGridOff) } else { '' }
        $cells = @($(if ($r.Packager) { $r.Packager } else { '(' + $r.Definition + ')' }), $r.PsGrid, $r.ApdGridBefore, $r.ApdGrid, $r.PsCMName, $r.ApdCMName, $r.PsStage, $r.ApdStage, $off, $r.MatchBefore, $r.Match, $r.Reason) | ForEach-Object { ([string]$_).Replace('|', '/') }
        [void]$sb.AppendLine('| ' + ($cells -join ' | ') + ' |')
    }
    [System.IO.File]::WriteAllText($Markdown, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
}
$rows
if ($counts.Differ -gt 0) { exit 1 }
