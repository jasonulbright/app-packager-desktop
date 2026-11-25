<#
Removes the applications and source folders that a run of batch.ps1 created (Q-128c lab test). Run on CM01.

Marker: the list in results.csv (rows with Status Created, column Created = application name). An application is removed only when
  - its name is in that list,
  - its name is not in -Keep (applications that existed before the run: 7-Zip, PuTTY),
  - it has no deployment (a deployed application is listed and left alone).
The application and its deployment types go through Remove-CMApplication, so the site removes the content package and the content library
files itself. The source folders come from the Location of each deployment type, read BEFORE the removal, and only folders under -SourceRoot
are deleted (the version folder, then its empty product and vendor folders). A record of what was removed is written to -RecordDir.
Use -WhatIf to see the list without removing anything.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$ResultsCsv = 'C:\guide\ps-batch\results.csv',
  [string[]]$Keep = @('PuTTY*', '7-Zip*'),
  [string]$SourceRoot = 'C:\ContentShare\Applications',
  [string]$RecordDir = 'C:\guide\lab-test-cleanup',
  [string]$SiteCode = 'MCM',
  [string[]]$StagePaths = @('C:	empp', 'C:	empp-0909'),   # packager staging folders (not AppData, not C:\APLab)
  [int]$StageAgeDays = 2,                                          # only items older than this (a session may be using newer ones)
  [switch]$CleanRunFiles   # also delete the batch logs and the download folder (results.csv stays)
)
$ErrorActionPreference = 'Continue'
if (-not (Test-Path $ResultsCsv)) { Write-Warning "No results list at $ResultsCsv, nothing to remove."; return }
Import-Module "$($env:SMS_ADMIN_UI_PATH)\..\ConfigurationManager.psd1" -ErrorAction Stop
if (-not (Get-PSDrive -Name $SiteCode -ErrorAction SilentlyContinue)) { New-PSDrive -Name $SiteCode -PSProvider CMSite -Root 'cm01.contoso.com' | Out-Null }
Push-Location "${SiteCode}:"
try {
  $names = @(Import-Csv $ResultsCsv | Where-Object Status -eq 'Created' | ForEach-Object { $_.Created } | Where-Object { $_ })
  $skipped = @(); $targets = @()
  foreach ($a in (Get-CMApplication -Fast)) {
    $n = $a.LocalizedDisplayName
    if ($n -notin $names) { continue }
    if ($Keep | Where-Object { $n -like $_ }) { $skipped += "$n (kept: pre-existing)"; continue }
    if ($a.NumberOfDeployments -gt 0) { $skipped += "$n (kept: has deployments)"; continue }
    $targets += $a
  }
  $srcRoot = $SourceRoot.TrimEnd('\')
  $folders = [System.Collections.Generic.List[string]]::new()
  foreach ($a in $targets) {
    foreach ($dt in (Get-CMDeploymentType -InputObject $a -ErrorAction SilentlyContinue)) {
      $x = [xml]$dt.SDMPackageXML
      foreach ($c in $x.AppMgmtDigest.DeploymentType.Installer.Contents.Content) {
        $loc = $c.Location
        if ($loc -match '(?i)^\\[^\]+\contentshare\$\Applications\(.+)$') { $p = Join-Path $srcRoot $Matches[1].TrimEnd('\') }
        elseif ($loc -like "$srcRoot\*") { $p = $loc.TrimEnd('\') } else { continue }
        if ($p -and -not $folders.Contains($p)) { $folders.Add($p) }
      }
    }
  }
  New-Item -ItemType Directory $RecordDir -Force -WhatIf:$false | Out-Null
  $stamp = Get-Date -Format 'yyyy-MM-dd-HHmm'
  $targets | Select-Object CI_ID, LocalizedDisplayName, SoftwareVersion, PackageID | Export-Csv "$RecordDir\removed-apps-$stamp.csv" -NoTypeInformation -WhatIf:$false
  $folders | Set-Content "$RecordDir\removed-source-folders-$stamp.txt" -WhatIf:$false
  $ok = 0; $fail = 0
  foreach ($a in $targets) {
    if ($PSCmdlet.ShouldProcess($a.LocalizedDisplayName, 'Remove-CMApplication')) {
      try { Remove-CMApplication -Id $a.CI_ID -Force -ErrorAction Stop; $ok++ } catch { $fail++; Write-Warning "$($a.LocalizedDisplayName): $($_.Exception.Message)" }
    }
  }
  $bytes = 0; $gone = 0
  foreach ($f in $folders) {
    if (-not (Test-Path -LiteralPath $f)) { continue }
    $bytes += (Get-ChildItem -LiteralPath $f -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($PSCmdlet.ShouldProcess($f, 'Remove source folder')) {
      Remove-Item -LiteralPath $f -Recurse -Force -ErrorAction SilentlyContinue; $gone++
      # empty parents up to, not including, the source root
      $parent = Split-Path $f -Parent
      while ($parent.Length -gt $srcRoot.Length -and $parent -like "$srcRoot\*" -and -not (Get-ChildItem -LiteralPath $parent -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $parent -Force -ErrorAction SilentlyContinue; $parent = Split-Path $parent -Parent
      }
    }
  }
  if ($CleanRunFiles -and $PSCmdlet.ShouldProcess('batch logs and download folder', 'Remove')) {
    Get-ChildItem (Split-Path $ResultsCsv) -File | Where-Object { $_.Extension -in '.log', '.err' } | Remove-Item -Force -ErrorAction SilentlyContinue
    Remove-Item 'C:\guide\ap-dl\*' -Recurse -Force -ErrorAction SilentlyContinue
  }
  $stageBytes = 0
  foreach ($r in $StagePaths) {
    if (-not (Test-Path -LiteralPath $r)) { continue }
    foreach ($d in Get-ChildItem -LiteralPath $r -Force | Where-Object LastWriteTime -lt (Get-Date).AddDays(-$StageAgeDays)) {
      $stageBytes += (Get-ChildItem -LiteralPath $d.FullName -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
      if ($PSCmdlet.ShouldProcess($d.FullName, 'Remove staging item')) { Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if (-not (Get-ChildItem -LiteralPath $r -Force -ErrorAction SilentlyContinue) -and $PSCmdlet.ShouldProcess($r, 'Remove empty staging folder')) { Remove-Item -LiteralPath $r -Force -ErrorAction SilentlyContinue }
  }
  [pscustomobject]@{
    StageGB = [math]::Round($stageBytes / 1GB, 2)
    Listed = $names.Count; Removed = $ok; Failed = $fail; Left = $skipped.Count
    SourceFolders = $gone; SourceGB = [math]::Round($bytes / 1GB, 2); Record = $RecordDir
  }
  $skipped | ForEach-Object { "left alone: $_" }
  'The site removes the content library files in the background (distmgr.log); free space rises over the next minutes.'
} finally { Pop-Location }
