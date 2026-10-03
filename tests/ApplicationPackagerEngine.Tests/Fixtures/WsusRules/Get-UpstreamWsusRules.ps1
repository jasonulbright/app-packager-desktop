#Requires -Version 5.1
<#
.SYNOPSIS
    Writes the WSUS applicability rules the PowerShell publisher builds for
    every case in cases.json to upstream-rules.json.

.DESCRIPTION
    Runs ConvertTo-WsusApplicabilityRules under Windows PowerShell 5.1
    (powershell.exe). ModuleRoot is the folder that holds
    Packagers\AppPackagerWsus.psm1. A case with ContentFolder true passes this
    folder as the content folder; seven-zip.msi is created here when it is
    missing. Each output entry carries IsInstalled, IsInstallable, Source and
    the ArpEntry for an MsiEntry source, or the Error text the function threw.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Get-UpstreamWsusRules.ps1 -ModuleRoot <folder>
#>
param(
    [Parameter(Mandatory)][string]$ModuleRoot,
    [string]$FixtureFolder
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($FixtureFolder)) { $FixtureFolder = Split-Path -Parent $MyInvocation.MyCommand.Path }

$msiPath = [string](Join-Path $FixtureFolder 'seven-zip.msi')
if (-not (Test-Path -LiteralPath $msiPath)) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msiPath, 3))
    foreach ($sql in @(
            'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)'
            "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('ProductName', '7-Zip 24.08 (x64 edition)')"
            "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('ProductVersion', '24.08.00.0')"
            "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('Manufacturer', 'Igor Pavlov')"
            "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('ProductCode', '{23170F69-40C1-2702-2408-000001000000}')")) {
        $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
        [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
    $summary = $db.GetType().InvokeMember('SummaryInformation', 'GetProperty', $null, $db, 4)
    [void]$summary.GetType().InvokeMember('Property', 'SetProperty', $null, $summary, @(7, 'x64;1033'))
    [void]$summary.GetType().InvokeMember('Persist', 'InvokeMethod', $null, $summary, $null)
    [void]$db.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $db, $null)
    foreach ($o in @($summary, $db, $installer)) { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($o) }
}

$module = Import-Module (Join-Path $ModuleRoot 'Packagers\AppPackagerWsus.psm1') -Force -PassThru -DisableNameChecking
$cases = [System.IO.File]::ReadAllText((Join-Path $FixtureFolder 'cases.json')) | ConvertFrom-Json
$results = foreach ($case in $cases) {
    $content = if ($case.PSObject.Properties['ContentFolder'] -and $case.ContentFolder) { $FixtureFolder } else { '' }
    $entry = [ordered]@{ Name = $case.Name }
    try {
        $rules = ConvertTo-WsusApplicabilityRules -Manifest $case.Manifest -ContentFolder $content
        $entry.IsInstalled = $rules.IsInstalled
        $entry.IsInstallable = $rules.IsInstallable
        $entry.Source = $rules.Source
        if ($rules.Entry -and $rules.Source -eq 'MsiEntry') {
            $entry.Entry = [ordered]@{}
            foreach ($property in $rules.Entry.PSObject.Properties) { $entry.Entry[$property.Name] = [string]$property.Value }
        }
    }
    catch { $entry.Error = $_.Exception.Message }
    [pscustomobject]$entry
}
$json = ConvertTo-Json -InputObject @($results) -Depth 6
[System.IO.File]::WriteAllText((Join-Path $FixtureFolder 'upstream-rules.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
